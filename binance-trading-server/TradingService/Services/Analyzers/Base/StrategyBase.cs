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
        /// <summary>
        /// 杠杆自适应：根据目标 ROE 自动计算并下发带保护的订单
        /// </summary>
        /// <param name="targetRoeTp">目标止盈 ROE (如 0.04 代表本金盈利 4%)</param>
        /// <param name="riskRoeSl">目标止损 ROE (如 0.02 代表本金亏损 2%)</param>
        protected async Task PlaceOrderWithLeverageRiskAsync(
            string symbol,
            bool isLong,
            decimal entryPrice,
            decimal marginUsdt,
            decimal leverage,
            decimal targetRoeTp = 0.04m,
            decimal riskRoeSl = 0.02m,
            string strategyName = "Breakout_Leverage")
        {
            try
            {
                string side = isLong ? "BUY" : "SELL";

                // 🌟 1. 计算标的资产实际需要变动的百分比 = ROE / 杠杆
                decimal priceChangeTp = targetRoeTp / leverage;
                decimal priceChangeSl = riskRoeSl / leverage;

                // 🌟 2. 根据方向计算绝对价格位
                decimal rawTp = isLong ? entryPrice * (1 + priceChangeTp) : entryPrice * (1 - priceChangeTp);
                decimal rawSl = isLong ? entryPrice * (1 - priceChangeSl) : entryPrice * (1 + priceChangeSl);

                // 🌟 3. 严格格式化价格与数量，对齐币安精度规则
                decimal tpPrice = _tradeWsService.FormatPrice(symbol, rawTp);
                decimal slPrice = _tradeWsService.FormatPrice(symbol, rawSl);

                // 计算下单数量 (名义价值 / 入场价)
                decimal rawQty = (marginUsdt * leverage) / entryPrice;
                decimal qty = _tradeWsService.FormatQuantity(symbol, rawQty);

                var comboSignal = new OrderSignal
                {
                    Symbol = symbol,
                    Action = OrderAction.OpenMarket,
                    Side = side,
                    IsUsdtMargin = true,
                    UsdtAmount = marginUsdt,
                    Leverage = leverage,
                    StopLossPrice = slPrice,
                    TakeProfitPrice = tpPrice,
                    StrategyName = strategyName,
                    Reason = $"杠杆:{leverage}X, 入场:{entryPrice:F4}, 预设止损ROE:-{riskRoeSl:P1}, 预设止盈ROE:+{targetRoeTp:P1}"
                };

                await _orderChannel.WriteAsync(comboSignal);
                _logger.LogInformation($"🚀 [{strategyName}] 已投递{leverage}X杠杆订单: {symbol} {side}, SL: {slPrice}, TP: {tpPrice}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ [{strategyName}] 杠杆风险计算下单失败: {ex.Message}");
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