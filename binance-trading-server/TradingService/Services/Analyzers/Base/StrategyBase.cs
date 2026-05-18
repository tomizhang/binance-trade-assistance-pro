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
        public bool IsStrategyEnabled { get; set; }
#if !DEBUG
        = true; // 控制是否开启该策略的运算
#endif
        protected bool IsOrderEnabled { get; set; } = false; // 默认关闭真实下单，仅开启信号观察
        protected readonly HashSet<string> _watchList = new();
        protected string[] _timeframes = { "1m" }; // 统一使用 1 分钟作为标准探测周期
        protected readonly SemaphoreSlim _lock = new(1, 1);
        private readonly SemaphoreSlim _initSemaphore = new(1, 3); // 新增：用于控制并发初始化，防止请求过多被币安拒绝

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
        private void HandleKlineInternal(IKline msg)
        {
            if (!IsStrategyEnabled) return; // 如果策略处于关闭状态，直接忽略数据推送
            if (!_watchList.Contains(msg.Symbol)) return;
            if (!_timeframes.Contains(msg.Interval)) return;

            // 分发给子类实现的特定策略逻辑
            OnKlineReceived(msg);
        }

        /// <summary>
        /// 子类需实现的 K 线实时处理逻辑
        /// </summary>
        protected abstract void OnKlineReceived(IKline msg);

        /// <summary>
        /// 策略启动时的数据初始化（如拉取历史 K 线，改为针对单一币种独立进行）
        /// </summary>
        protected abstract Task InitializeStrategyDataAsync(string symbol);

        public virtual async Task UpdateWatchListAsync(IEnumerable<string> symbols)
        {
            var requested = symbols.Select(s => s.ToUpper()).ToList();

            if (!IsStrategyEnabled)
            {
                // 如果策略处于关闭状态，仅仅静默更新列表，不发起任何 HTTP 请求拉取历史，也不订阅 WS
                await _lock.WaitAsync();
                try
                {
                    _watchList.Clear();
                    foreach (var sym in requested) _watchList.Add(sym);
                }
                finally { _lock.Release(); }
                return;
            }

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

                // 优化：针对每个新增的币种独立开启后台任务
                // 那个币种的历史数据拉取完成，就立刻为其开启 WS 流，无需等待其它币种
                foreach (var sym in toAdd)
                {
                    _ = Task.Run(async () =>
                    {
                        await _initSemaphore.WaitAsync(); // 获取并发锁，保证不会同时发起大量 API 请求
                        try
                        {
                            await InitializeStrategyDataAsync(sym);
                            var streams = _timeframes.Select(tf => $"{sym.ToLower()}@kline_{tf}").ToList();
                            await _wsService.SubscribeBackendAsync(streams);
                            _logger.LogInformation($"✅ [{GetType().Name}] 币种 {sym} 历史数据就绪，已启动实时策略订阅。");

                            // 增加 1 秒延迟，严格避免触发币安 REST API 频次限制 (TooManyRequests)
                            await Task.Delay(1000);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"❌ [{GetType().Name}] 币种 {sym} 初始化或订阅失败");
                        }
                        finally
                        {
                            _initSemaphore.Release(); // 释放锁，允许下一个币种开始初始化
                        }
                    });
                }
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

        // 切换策略的启停状态
        public async Task SetStrategyEnabledAsync(bool enable)
        {
            if (IsStrategyEnabled == enable) return;
            IsStrategyEnabled = enable;

            if (enable)
            {
                _logger.LogInformation($"🚀 [{GetType().Name}] 策略已被重新开启，开始拉取历史数据并初始化...");

                // 将当前的 watchList 提出来重新触发一次完整的 UpdateWatchListAsync 以实现热启动初始化
                List<string> currentSymbols;
                await _lock.WaitAsync();
                try
                {
                    currentSymbols = _watchList.ToList();
                    _watchList.Clear(); // 清空旧列表，使其判定为全部是“新增”币种从而触发初始化
                }
                finally { _lock.Release(); }

                await UpdateWatchListAsync(currentSymbols);
            }
            else
            {
                _logger.LogInformation($"⏸️ [{GetType().Name}] 策略已被关闭，停止一切计算。");
                // 可选：在这里调用一个 virtual 方法让子类去清空内存中的历史 K 线 buffer
                OnStrategyDisabled();
            }
        }

        // 子类可重写此方法，在策略关闭时清空内存释放资源
        protected virtual void OnStrategyDisabled() { }

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