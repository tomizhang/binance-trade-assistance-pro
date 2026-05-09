using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingTerminal.Hubs;
using TradingTerminal.Models;
using TradingTerminal.Utils;

namespace TradingTerminal.Services
{
    /// <summary>
    /// 所有量化策略的基类，提供行情订阅、下单、风控、格式化等公共能力
    /// </summary>
    public abstract class StrategyBase : BackgroundService
    {
        protected readonly ILogger _logger;
        protected readonly IHubContext<MarketHub> _hubContext;
        protected readonly MarketEventBus _eventBus;
        protected readonly BinanceWebSocketService _wsService;
        protected readonly OrderChannel _orderChannel;
        protected readonly BinanceTradeWsService _tradeWsService;

        // 子类共用的行情监控名单
        protected readonly HashSet<string> _watchList = new();
        protected readonly string[] _timeframes = { "1m" };
        protected readonly SemaphoreSlim _lock = new(1, 1);

        protected StrategyBase(
            ILogger logger,
            IHubContext<MarketHub> hubContext,
            MarketEventBus eventBus,
            BinanceWebSocketService wsService,
            OrderChannel orderChannel,
            BinanceTradeWsService tradeWsService)
        {
            _logger = logger;
            _hubContext = hubContext;
            _eventBus = eventBus;
            _wsService = wsService;
            _orderChannel = orderChannel;
            _tradeWsService = tradeWsService;

            // 监听全局行情事件
            _eventBus.OnKlineReceived += HandleKlineInternal;
        }

        // ==========================================
        // 🌟 行情与订阅流控
        // ==========================================
        private void HandleKlineInternal(KlineMessage msg)
        {
            if (!_watchList.Contains(msg.Symbol)) return;
            if (!_timeframes.Contains(msg.Interval)) return;

            // 调用子类实现的具体策略逻辑
            OnKlineReceived(msg);
        }

        /// <summary>
        /// 子类需实现的行情处理核心逻辑
        /// </summary>
        protected abstract void OnKlineReceived(KlineMessage msg);

        public virtual async Task UpdateWatchListAsync(IEnumerable<string> symbols)
        {
            var requested = symbols.Select(s => s.ToUpper()).ToList();
            List<string> toAdd, toRemove;

            await _lock.WaitAsync();
            try
            {
                toAdd = requested.Except(_watchList).ToList();
                toRemove = _watchList.Except(requested).ToList();

                foreach (var sym in toAdd) _watchList.Add(sym);
                foreach (var sym in toRemove) _watchList.Remove(sym);
            }
            finally { _lock.Release(); }

            if (toRemove.Any())
            {
                var streamsToRemove = toRemove.SelectMany(sym => _timeframes.Select(tf => $"{sym.ToLower()}@kline_{tf}")).ToList();
                await _wsService.UnsubscribeBackendAsync(streamsToRemove);
            }

            if (toAdd.Any())
            {
                _logger.LogInformation($"📡 [{GetType().Name}] 锁定新目标: {string.Join(", ", toAdd)}，正在初始化数据...");
                await InitializeStrategyDataAsync(toAdd);
                var streamsToAdd = toAdd.SelectMany(sym => _timeframes.Select(tf => $"{sym.ToLower()}@kline_{tf}")).ToList();
                await _wsService.SubscribeBackendAsync(streamsToAdd);
            }
        }

        /// <summary>
        /// 策略启动时的历史数据拉取与引擎预热
        /// </summary>
        protected abstract Task InitializeStrategyDataAsync(List<string> symbols);

        // ==========================================
        // 🌟 公共交易执行工具
        // ==========================================
        protected async Task PlaceOrderWithProtectionAsync(
            string symbol,
            bool isLong,
            decimal entryPrice,
            decimal stopLossPrice,
            decimal takeProfitPrice,
            decimal marginUsdt = 1.5m,
            decimal leverage = 5m,
            string strategyName = "BaseStrategy")
        {
            try
            {
                string side = isLong ? "BUY" : "SELL";

                var comboSignal = new OrderSignal
                {
                    Symbol = symbol,
                    Action = OrderAction.OpenMarket,
                    Side = side,
                    IsUsdtMargin = true,
                    UsdtAmount = marginUsdt,
                    Leverage = leverage,
                    StopLossPrice = stopLossPrice,
                    TakeProfitPrice = takeProfitPrice,
                    StrategyName = strategyName,
                    Reason = $"入场 {entryPrice:F4}, SL: {stopLossPrice:F4}, TP: {takeProfitPrice:F4}",
                    Message = "执行标准化组合下单策略"
                };

                await _orderChannel.WriteAsync(comboSignal);
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ [{strategyName}] 下单投递失败: {ex.Message}");
            }
        }

        // ==========================================
        // 🌟 辅助计算工具
        // ==========================================
        protected List<decimal> SmoothData(List<decimal> rawData, int period = 3)
        {
            var smoothed = new List<decimal>(rawData.Count);
            for (int i = 0; i < rawData.Count; i++)
            {
                if (i < period - 1) { smoothed.Add(rawData[i]); continue; }
                decimal sum = 0;
                for (int j = 0; j < period; j++) sum += rawData[i - j];
                smoothed.Add(sum / period);
            }
            return smoothed;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            List<string> list = null;
            do
            {
                try
                {
                    list = await _wsService.RefreshTopSymbolsAsync(stoppingToken);
                }
                catch { await Task.Delay(500, stoppingToken); }
            } while (list is null && !stoppingToken.IsCancellationRequested);

            if (list != null) await UpdateWatchListAsync(list);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }
}