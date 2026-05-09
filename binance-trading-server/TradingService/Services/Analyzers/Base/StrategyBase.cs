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
    /// 所有量化策略的基类，提供行情订阅、下单开关控制、风控计算、格式化及多通道通知推送等公共能力
    /// </summary>
    public abstract class StrategyBase : BackgroundService
    {
        protected readonly ILogger _logger;
        protected readonly IHubContext<MarketHub> _hubContext;
        protected readonly MarketEventBus _eventBus;
        protected readonly BinanceWebSocketService _wsService;
        protected readonly OrderChannel _orderChannel;
        protected readonly BinanceTradeWsService _tradeWsService;

        // 🌟 策略核心控制参数
        protected bool IsOrderEnabled { get; set; } = false; // 默认关闭真实下单，仅开启信号观察
        protected readonly HashSet<string> _watchList = new();
        protected string[] _timeframes = { "1m" }; // 统一使用 1 分钟作为标准探测周期
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

            // 监听全局行情事件总线
            _eventBus.OnKlineReceived += HandleKlineInternal;
        }

        // ==========================================
        // 🌟 通用通知接口：SignalR 前端推送 + 本地分级日志
        // ==========================================
        /// <summary>
        /// 发送策略告警或状态通知
        /// </summary>
        protected async Task NotifyAlertAsync(string symbol, string type, string message, bool isImportant = false)
        {
            var alert = new
            {
                symbol = symbol,
                strategy = GetType().Name,
                type = type, // 如: "量能监控", "技术突破", "下单执行"
                message = message,
                time = DateTime.Now.ToString("HH:mm:ss"),
                isImportant = isImportant,
                isOrderEnabled = IsOrderEnabled // 告知前端当前策略是否处于实盘运行状态
            };

            // 1. 推送到前端 SignalR Hub (Vue 终端接收)
            await _hubContext.Clients.All.SendAsync("ReceiveHaAlert", alert);

            // 2. 本地分级日志记录
            string logPrefix = IsOrderEnabled ? "[实盘模式]" : "[观察模式]";
            if (isImportant)
                _logger.LogWarning($"🔔 {logPrefix} {symbol} {type}: {message}");
            else
                _logger.LogInformation($"ℹ️ {logPrefix} {symbol} {type}: {message}");
        }

        // ==========================================
        // 🌟 核心交易执行工具 (集成开关控制与杠杆 ROE 计算)
        // ==========================================
        /// <summary>
        /// 杠杆自适应下单：根据本金盈亏率 (ROE) 自动反推止盈止损价格并执行
        /// </summary>
        /// <param name="targetRoeTp">目标止盈 ROE (如 0.05 代表本金盈利 5%)</param>
        /// <param name="riskRoeSl">风险止损 ROE (如 0.025 代表本金亏损 2.5%)</param>
        protected async Task PlaceOrderWithLeverageRiskAsync(
            string symbol,
            bool isLong,
            decimal entryPrice,
            decimal marginUsdt,
            decimal leverage,
            decimal targetRoeTp = 0.04m,
            decimal riskRoeSl = 0.02m,
            string strategyName = "BaseStrategy")
        {
            try
            {
                string side = isLong ? "BUY" : "SELL";

                // 1. 计算标的资产实际需要变动的价格百分比 = 目标 ROE / 杠杆倍数
                decimal priceChangeTp = targetRoeTp / leverage;
                decimal priceChangeSl = riskRoeSl / leverage;

                // 2. 根据多空方向计算绝对止盈止损价
                decimal rawTp = isLong ? entryPrice * (1 + priceChangeTp) : entryPrice * (1 - priceChangeTp);
                decimal rawSl = isLong ? entryPrice * (1 - priceChangeSl) : entryPrice * (1 + priceChangeSl);

                // 3. 严格对齐币安每个币种独特的精度规则
                decimal tpPrice = _tradeWsService.FormatPrice(symbol, rawTp);
                decimal slPrice = _tradeWsService.FormatPrice(symbol, rawSl);
                decimal qty = _tradeWsService.FormatQuantity(symbol, (marginUsdt * leverage) / entryPrice);

                string logDetail = $"杠杆:{leverage}X, 入场:{entryPrice:F4}, SL:{slPrice}(-{riskRoeSl:P1}), TP:{tpPrice}(+{targetRoeTp:P1})";

                // 🌟 核心优化：即便未开放交易，也要发送信号通知
                if (!IsOrderEnabled)
                {
                    await NotifyAlertAsync(symbol, "模拟信号", $"[逻辑触发] 满足 {side} 条件。参数: {logDetail}", true);
                    return;
                }

                // 4. 构建订单信号并推送至执行通道
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
                    StrategyName = strategyName
                };

                await _orderChannel.WriteAsync(comboSignal);

                // 5. 实盘下单通知
                await NotifyAlertAsync(symbol, "下单执行", $"[实盘开仓] {side} 执行。{logDetail}", true);
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ [{strategyName}] 杠杆风险计算或指令投递失败: {ex.Message}");
            }
        }

        // ==========================================
        // 🌟 行情分发与生命周期管理
        // ==========================================
        private void HandleKlineInternal(KlineMessage msg)
        {
            if (!_watchList.Contains(msg.Symbol)) return;
            if (!_timeframes.Contains(msg.Interval)) return;

            // 分发给子类实现的特定策略逻辑
            OnKlineReceived(msg);
        }

        /// <summary>
        /// 子类需实现的 K 线实时处理逻辑
        /// </summary>
        protected abstract void OnKlineReceived(KlineMessage msg);

        /// <summary>
        /// 策略启动时的数据初始化（如拉取历史 K 线）
        /// </summary>
        protected abstract Task InitializeStrategyDataAsync(List<string> symbols);

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
                var streams = toRemove.SelectMany(sym => _timeframes.Select(tf => $"{sym.ToLower()}@kline_{tf}")).ToList();
                await _wsService.UnsubscribeBackendAsync(streams);
            }

            if (toAdd.Any())
            {
                _logger.LogInformation($"📡 [{GetType().Name}] 激活监控: {string.Join(", ", toAdd)}");
                await InitializeStrategyDataAsync(toAdd);
                var streams = toAdd.SelectMany(sym => _timeframes.Select(tf => $"{sym.ToLower()}@kline_{tf}")).ToList();
                await _wsService.SubscribeBackendAsync(streams);
            }
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

        // 辅助工具：SMA 平滑处理
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
    }
}