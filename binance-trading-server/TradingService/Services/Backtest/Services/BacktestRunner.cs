using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingTerminal.Hubs;
using TradingTerminal.Models;
using TradingTerminal.Services;
using TradingTerminal.Services.Backtest.Models;

namespace TradingTerminal.Services.Backtest.Services
{
    public class BacktestRunner
    {
        private readonly ILogger<BacktestRunner> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly BinanceDataDownloadService _downloadService;
        private readonly IHubContext<BacktestHub> _backtestHubContext;

        public BacktestRunner(
            ILogger<BacktestRunner> logger,
            IServiceProvider serviceProvider,
            BinanceDataDownloadService downloadService,
            IHubContext<BacktestHub> backtestHubContext)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _downloadService = downloadService;
            _backtestHubContext = backtestHubContext;
        }

        public async Task<BacktestReport> RunBacktestAsync(BacktestConfig config, BacktestTaskState taskState, CancellationToken ct)
        {
            _logger.LogInformation($"🚀 [回测运行器] 开始执行任务 {taskState.TaskId} ({config.Symbol} | {config.StrategyName})");
            
            // 1. 获取回测周期与预热数据
            // 下载起止时间往前推 7 天以进行预热
            DateTime downloadStart = config.StartTime.AddDays(-7);
            
            // 缓存下载 1m 数据
            var klines1mRaw = await _downloadService.GetKlinesAsync(config.Symbol, "1m", downloadStart, config.EndTime);
            if (!klines1mRaw.Any())
            {
                throw new Exception("未获取到任何历史 K 线数据。");
            }

            // 2. 预先合成所有需要的大周期数据
            _logger.LogInformation("🔄 [回测运行器] 正在内存中合成多周期数据...");
            var klines3m = Aggregate1mToMtf(klines1mRaw, "3m");
            var klines5m = Aggregate1mToMtf(klines1mRaw, "5m");
            var klines10m = Aggregate1mToMtf(klines1mRaw, "10m");
            var klines15m = Aggregate1mToMtf(klines1mRaw, "15m");
            var klines30m = Aggregate1mToMtf(klines1mRaw, "30m");
            var klines1h = Aggregate1mToMtf(klines1mRaw, "1h");
            var klines1d = Aggregate1mToMtf(klines1mRaw, "1d");

            var allSyntheticKlines = new List<IKline>();
            allSyntheticKlines.AddRange(klines1mRaw);
            allSyntheticKlines.AddRange(klines3m);
            allSyntheticKlines.AddRange(klines5m);
            allSyntheticKlines.AddRange(klines10m);
            allSyntheticKlines.AddRange(klines15m);
            allSyntheticKlines.AddRange(klines30m);
            allSyntheticKlines.AddRange(klines1h);
            allSyntheticKlines.AddRange(klines1d);

            // 3. 构造 Mock 依赖服务
            var mockWs = new MockBinanceWebSocketService(async (symbol, interval, limit) =>
            {
                long startMs = new DateTimeOffset(config.StartTime).ToUnixTimeMilliseconds();
                var history = allSyntheticKlines
                    .Where(k => k.Interval == interval && k.OpenTime < startMs)
                    .OrderBy(k => k.OpenTime)
                    .TakeLast(limit)
                    .ToList();

                if (history.Count < limit)
                {
                    int daysNeeded = interval switch
                    {
                        "1d" => limit + 5,
                        "1h" => (limit / 24) + 2,
                        "30m" => (limit / 48) + 2,
                        "15m" => (limit / 96) + 2,
                        _ => 7
                    };
                    DateTime preWarmStart = config.StartTime.AddDays(-daysNeeded);
                    try
                    {
                        var extraKlines = await _downloadService.GetKlinesAsync(symbol, interval, preWarmStart, config.StartTime);
                        var extraFiltered = extraKlines
                            .Where(k => k.OpenTime < startMs)
                            .OrderBy(k => k.OpenTime)
                            .TakeLast(limit)
                            .ToList();

                        if (extraFiltered.Count > history.Count)
                        {
                            history = extraFiltered;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"[回测运行器] 预热拉取大周期 {interval} 历史数据失败: {ex.Message}");
                    }
                }
                
                return SerializeKlinesToBinanceJson(history);
            });

            var mockConfig = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
            var mockTradeWs = new MockBinanceTradeWsService(mockConfig);
            var customEventBus = new MarketEventBus();
            var customOrderChannel = new OrderChannel();
            var mockPositionManager = new BacktestPositionManager();

            // 4. 实例化策略
            Type strategyType = GetStrategyType(config.StrategyName);
            var strategyInstance = ActivatorUtilities.CreateInstance(
                _serviceProvider,
                strategyType,
                mockWs,
                customEventBus,
                customOrderChannel,
                mockTradeWs,
                mockPositionManager
            ) as StrategyBase;

            if (strategyInstance == null)
            {
                throw new Exception($"无法实例化策略类: {config.StrategyName}");
            }

            // 强制启用订单和策略并应用回测杠杆
            strategyInstance.IsStrategyEnabled = true;
            strategyInstance.BacktestLeverage = config.Leverage;
            var isOrderEnabledProp = typeof(StrategyBase).GetProperty("IsOrderEnabled", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            isOrderEnabledProp?.SetValue(strategyInstance, true);

            // 将回测币种手动加入 watchList
            var watchListField = typeof(StrategyBase).GetField("_watchList", BindingFlags.Instance | BindingFlags.NonPublic);
            var watchList = (HashSet<string>)watchListField?.GetValue(strategyInstance);
            watchList?.Add(config.Symbol.ToUpper());

            // 5. 初始化策略历史数据
            _logger.LogInformation("🔥 [回测运行器] 正在执行策略热启动初始化...");
            var initMethod = strategyInstance.GetType().GetMethod("InitializeStrategyDataAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            await (Task)initMethod.Invoke(strategyInstance, new object[] { config.Symbol });

            // 6. 开始回测 K 线步进循环
            long startMs = new DateTimeOffset(config.StartTime).ToUnixTimeMilliseconds();
            var testKlines = klines1mRaw.Where(k => k.OpenTime >= startMs).OrderBy(k => k.OpenTime).ToList();

            decimal balance = config.InitialBalance;
            var closedOrders = new List<BacktestOrderDetail>();
            var pendingLimitOrders = new List<PendingLimitOrder>();
            
            var equityCurve = new List<EquityPoint>
            {
                new EquityPoint { Time = config.StartTime, Balance = balance }
            };

            // 缓存 1m 的历史数据，以合成 closing MTF K线
            var klines1mHistory = klines1mRaw.Where(k => k.OpenTime < startMs).ToList();

            int totalSteps = testKlines.Count;
            _logger.LogInformation($"📈 [回测运行器] 回测主循环启动，总计 {totalSteps} 根 1m K线。");

            for (int i = 0; i < totalSteps; i++)
            {
                // 检测取消
                if (ct.IsCancellationRequested)
                {
                    taskState.Status = BacktestStatus.Cancelled;
                    throw new OperationCanceledException();
                }

                // 检测暂停
                taskState.PauseEvent.Wait(ct);

                var k1m = testKlines[i];
                klines1mHistory.Add(k1m);

                // 更新虚拟时钟
                var currentTime = DateTimeOffset.FromUnixTimeMilliseconds(k1m.OpenTime + 59999).UtcDateTime;
                strategyInstance.BacktestTime = currentTime;

                // --- A. 资金费率检查 ---
                // 资金费在 UTC 时间 00:00, 08:00, 16:00 结算 (即 K 线开始时间刚好是 8 小时的整数倍整点)
                var klineStartTime = DateTimeOffset.FromUnixTimeMilliseconds(k1m.OpenTime).UtcDateTime;
                if (klineStartTime.Hour % 8 == 0 && klineStartTime.Minute == 0)
                {
                    if (mockPositionManager.TryGetPosition(config.Symbol, out var pos))
                    {
                        decimal fundingFee = Math.Abs(pos.Quantity) * k1m.Close * config.FundingRate;
                        balance -= fundingFee;
                        
                        // 寻找对应未平仓订单明细，累计资金费
                        var activeLog = closedOrders.FirstOrDefault(o => o.Symbol == config.Symbol && o.CloseTime == null);
                        if (activeLog != null)
                        {
                            activeLog.FundingFeePaid += fundingFee;
                        }
                        _logger.LogInformation($"💸 [资金费扣除] 时间 {currentTime:yyyy-MM-dd HH:mm:ss} | 仓位 {pos.Side} | 金额 {fundingFee:F4} USDT");
                    }
                }

                // --- B. 挂单撮合 (Limit Order Matching) ---
                if (pendingLimitOrders.Any())
                {
                    var filledOrders = new List<PendingLimitOrder>();
                    foreach (var order in pendingLimitOrders)
                    {
                        bool isFilled = false;
                        decimal fillPrice = order.Price;

                        if (order.Side == "BUY")
                        {
                            if (k1m.Low <= order.Price)
                            {
                                isFilled = true;
                                fillPrice = order.Price * (1 + config.MakerSlippage); // 考虑滑点
                            }
                        }
                        else // SELL
                        {
                            if (k1m.High >= order.Price)
                            {
                                isFilled = true;
                                fillPrice = order.Price * (1 - config.MakerSlippage); // 考虑滑点
                            }
                        }

                        if (isFilled)
                        {
                            filledOrders.Add(order);
                            
                            // 开仓
                            decimal nominalValue = order.UsdtAmount * order.Leverage;
                            decimal quantity = nominalValue / fillPrice;
                            decimal entryFee = nominalValue * config.FeeRate;

                            balance -= entryFee;

                            var tracker = new PositionTracker
                            {
                                Symbol = config.Symbol,
                                Side = order.Side,
                                Quantity = order.Side == "BUY" ? quantity : -quantity,
                                EntryPrice = fillPrice,
                                TakeProfitPrice = order.TakeProfitPrice ?? 0m,
                                OpenTime = currentTime,
                                IsStopMovedToBE = false,
                                StrategyName = order.StrategyName
                            };

                            mockPositionManager.SetPosition(config.Symbol, tracker);

                            var orderLog = new BacktestOrderDetail
                            {
                                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                                StrategyName = order.StrategyName,
                                OrderNumber = "BT_L_" + Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper(),
                                Symbol = config.Symbol,
                                Direction = order.Side == "BUY" ? "LONG" : "SHORT",
                                OpenTime = currentTime,
                                OpenPrice = fillPrice,
                                OpenType = "maker",
                                OpenFee = entryFee,
                                OpenSlippage = fillPrice - order.Price,
                                OpenReason = "Limit Order Filled",
                                Quantity = quantity,
                                TakeProfitPrice = order.TakeProfitPrice ?? (order.Side == "BUY" 
                                    ? fillPrice * (1 + 0.04m / config.Leverage) 
                                    : fillPrice * (1 - 0.04m / config.Leverage)),
                                StopLossPrice = order.StopLossPrice ?? (order.Side == "BUY" 
                                    ? fillPrice * (1 - 0.02m / config.Leverage) 
                                    : fillPrice * (1 + 0.02m / config.Leverage))
                            };

                            closedOrders.Add(orderLog);
                            _logger.LogInformation($"🟢 [挂单成交] {orderLog.Direction} 开仓成功 | 价格: {fillPrice:F4} | 数量: {quantity:F4}");
                        }
                    }

                    foreach (var filled in filledOrders)
                    {
                        pendingLimitOrders.Remove(filled);
                    }
                }

                // --- C. 活跃仓位风控巡检 (SL/TP/强平) ---
                if (mockPositionManager.TryGetPosition(config.Symbol, out var activePos))
                {
                    var orderLog = closedOrders.LastOrDefault(o => o.Symbol == config.Symbol && o.CloseTime == null);
                    
                    if (orderLog != null)
                    {
                        // 1. 强平价格计算
                        // 维持保证金率 MMR = 0.5%
                        decimal mmr = 0.005m;
                        decimal liqPrice = 0m;
                        if (activePos.Side == "BUY") // LONG
                        {
                            liqPrice = activePos.EntryPrice * (1 - 1 / config.Leverage + mmr);
                        }
                        else // SHORT
                        {
                            liqPrice = activePos.EntryPrice * (1 + 1 / config.Leverage - mmr);
                        }

                        // 2. 获取当前持仓对应的止损价与止盈价
                        // 从 activeLog 查找最新的风控线 (如果没有被移动保本，就是初始值)
                        // 初始止损止盈值来自下单信号
                        decimal slPrice = activePos.Side == "BUY" 
                            ? activePos.EntryPrice * (1 - (orderLog.OpenFee / (orderLog.Quantity * activePos.EntryPrice))) // wait, orderLog holds original sl
                            : activePos.EntryPrice * (1 + (orderLog.OpenFee / (orderLog.Quantity * activePos.EntryPrice)));
                        
                        // 我们在开仓时将 SL 和 TP 保存在 Tracker 或本地临时变量中
                        // 我们直接用 activePos 的 TakeProfitPrice，或者我们可以通过 OrderSignal 传递的 SL/TP 变量维护它
                        // 为了确保准确，在开仓记录中保存 Initial SL/TP 并维护
                        
                        // 我们需要知道当前止损价
                        if (!orderLog.ClosePrice.HasValue) // 仍持仓
                        {
                            // 临时附加属性：我们在 orderLog 中动态维护 SL 价
                            // 初始 SL 从 OrderSignal 中提取并存在 Tracker 或 orderLog 扩展字段中
                            // 我们可以直接读取 activePos 上的 IsStopMovedToBE 状态
                            decimal currentSl = activePos.IsStopMovedToBE 
                                ? (activePos.Side == "BUY" ? activePos.EntryPrice * 1.0005m : activePos.EntryPrice * 0.9995m)
                                : (orderLog.Direction == "LONG" ? (orderLog.OpenPrice * (1 - 0.02m / config.Leverage)) : (orderLog.OpenPrice * (1 + 0.02m / config.Leverage))); // fallback default sl
                            
                            // 从下单信号关联的 SL/TP 读取更准确！
                            // 稍后在开仓时直接将 Initial SL 存入 orderLog.OpenReason 字段或自定义属性
                            // 让我们在下面开仓时把 StopLossPrice 存入 orderLog.OpenReason 后面或直接改写其值。
                            // 为了干净，我们可以在 BacktestOrderDetail 中加上 StopLossPrice 和 TakeProfitPrice！
                        }
                    }

                    // 让我们通过更直接的方法：
                    // 在 orderLog 中包含当前 SL/TP：
                    // 我们修改 `BacktestOrderDetail` (或者直接在运行器内部维护活跃风控状态)
                    // 运行器内部用一个字典维护 `ActivePositionRisk`: (StopLoss, TakeProfit, IsStopMovedToBE, OpenMargin)
                }

                // 统一的仓位风控检查 logic：
                if (mockPositionManager.TryGetPosition(config.Symbol, out var posCheck))
                {
                    var orderLog = closedOrders.LastOrDefault(o => o.Symbol == config.Symbol && o.CloseTime == null);
                    if (orderLog != null)
                    {
                        decimal slPrice = orderLog.OpenFee; // wait, let's keep risk states in a dictionary
                        // 我们直接在运行器内用 `ActiveRisk` 结构追踪
                    }
                }

                // --- D. 止盈保本损巡检 ---
                if (config.EnableMoveStopToBE && mockPositionManager.TryGetPosition(config.Symbol, out var posBE) && !posBE.IsStopMovedToBE)
                {
                    var orderLog = closedOrders.LastOrDefault(o => o.Symbol == config.Symbol && o.CloseTime == null);
                    if (orderLog != null)
                    {
                        var duration = currentTime - posBE.OpenTime;

                        if (orderLog.StrategyName == "MinVolumeReversalStrategyService")
                        {
                            // 1分钟成交量反转策略专属规则：盈利期间并且达到3分钟，移动止损位置到开仓价格
                            bool isInProfit = posBE.Side == "BUY"
                                ? k1m.Close > posBE.EntryPrice
                                : k1m.Close < posBE.EntryPrice;

                            bool isTimeReached = duration.TotalMinutes >= 3;

                            if (isInProfit && isTimeReached)
                            {
                                posBE.IsStopMovedToBE = true;
                                _logger.LogWarning($"🎯 [1m成交量反转保本触发] {config.Symbol} 持仓达到3分钟且已盈利(开仓价:{posBE.EntryPrice:F4}, 当前价:{k1m.Close:F4})，移动止损至保本...");
                            }
                        }
                        else if (posBE.TakeProfitPrice > 0)
                        {
                            // 默认规则：触碰半盈位 且 过去2分钟
                            decimal tpDistance = Math.Abs(posBE.TakeProfitPrice - posBE.EntryPrice);
                            decimal halfTpTargetPrice = posBE.Side == "BUY"
                                ? posBE.EntryPrice + (tpDistance * 0.5m)
                                : posBE.EntryPrice - (tpDistance * 0.5m);

                            bool isHalfTpReached = posBE.Side == "BUY"
                                ? k1m.High >= halfTpTargetPrice
                                : k1m.Low <= halfTpTargetPrice;

                            bool isTimeReached = duration.TotalMinutes >= 2;

                            if (isHalfTpReached && isTimeReached)
                            {
                                posBE.IsStopMovedToBE = true;
                                _logger.LogWarning($"🎯 [保本损触发] {config.Symbol} 触碰半盈位 {halfTpTargetPrice:F4} (全盈:{posBE.TakeProfitPrice:F4})，移动止损至保本...");
                            }
                        }
                    }
                }

                // 检查 SL / TP / 强平是否触发
                if (mockPositionManager.TryGetPosition(config.Symbol, out var posRisk))
                {
                    var orderLog = closedOrders.LastOrDefault(o => o.Symbol == config.Symbol && o.CloseTime == null);
                    if (orderLog != null)
                    {
                        // 1. 强平线
                        decimal mmr = 0.005m;
                        decimal liqPrice = posRisk.Side == "BUY"
                            ? posRisk.EntryPrice * (1 - 1 / config.Leverage + mmr)
                            : posRisk.EntryPrice * (1 + 1 / config.Leverage - mmr);

                        // 2. 止损线 (保本或初始)
                        decimal slPrice = 0m;
                        if (posRisk.IsStopMovedToBE)
                        {
                            if (orderLog.StrategyName == "MinVolumeReversalStrategyService")
                            {
                                // 1分钟成交量反转策略使用精确开仓均价作为止损
                                slPrice = posRisk.EntryPrice;
                            }
                            else
                            {
                                slPrice = posRisk.Side == "BUY" ? posRisk.EntryPrice * 1.0005m : posRisk.EntryPrice * 0.9995m;
                            }
                        }
                        else
                        {
                            // 提取开仓时携带的 SL
                            // 如果没有携带，则默认为 -2% / leverage
                            slPrice = orderLog.StopLossPrice ?? (posRisk.Side == "BUY"
                                ? posRisk.EntryPrice * (1 - 0.02m / config.Leverage)
                                : posRisk.EntryPrice * (1 + 0.02m / config.Leverage));
                        }

                        // 3. 止盈线
                        decimal tpPrice = orderLog.TakeProfitPrice ?? (posRisk.Side == "BUY" 
                            ? posRisk.EntryPrice * (1 + 0.04m / config.Leverage)
                            : posRisk.EntryPrice * (1 - 0.04m / config.Leverage));

                        bool isLiq = false;
                        bool isSl = false;
                        bool isTp = false;

                        if (posRisk.Side == "BUY") // LONG
                        {
                            if (k1m.Low <= liqPrice) isLiq = true;
                            else if (k1m.Low <= slPrice) isSl = true;
                            else if (k1m.High >= tpPrice) isTp = true;
                        }
                        else // SHORT
                        {
                            if (k1m.High >= liqPrice) isLiq = true;
                            else if (k1m.High >= slPrice) isSl = true;
                            else if (k1m.Low <= tpPrice) isTp = true;
                        }

                        if (isLiq || isSl || isTp)
                        {
                            // 触发平仓！
                            decimal exitPrice = 0m;
                            string closeReason = "";
                            string closeType = "taker";

                            if (isLiq)
                            {
                                exitPrice = liqPrice;
                                closeReason = "Liquidation";
                            }
                            else if (isSl)
                            {
                                exitPrice = slPrice * (posRisk.Side == "BUY" ? (1 - config.TakerSlippage) : (1 + config.TakerSlippage));
                                closeReason = posRisk.IsStopMovedToBE ? "BreakEven" : "StopLoss";
                            }
                            else if (isTp)
                            {
                                exitPrice = tpPrice * (posRisk.Side == "BUY" ? (1 - config.MakerSlippage) : (1 + config.MakerSlippage));
                                closeReason = "TakeProfit";
                                closeType = "maker"; // 止盈是挂单成交
                            }

                            // 计算盈亏与手续费
                            decimal grossPnL = 0m;
                            if (posRisk.Side == "BUY")
                            {
                                grossPnL = orderLog.Quantity * (exitPrice - posRisk.EntryPrice);
                            }
                            else
                            {
                                grossPnL = orderLog.Quantity * (posRisk.EntryPrice - exitPrice);
                            }

                            if (isLiq)
                            {
                                // 保证金强平，亏损全部开仓本金
                                decimal openMargin = (orderLog.Quantity * posRisk.EntryPrice) / config.Leverage;
                                grossPnL = -openMargin;
                            }

                            decimal exitFee = isLiq ? 0m : (orderLog.Quantity * exitPrice * config.FeeRate);
                            decimal netPnL = grossPnL - exitFee;

                            balance += netPnL;

                            // 写入日志
                            orderLog.CloseTime = currentTime;
                            orderLog.ClosePrice = exitPrice;
                            orderLog.CloseType = closeType;
                            orderLog.CloseFee = exitFee;
                            orderLog.CloseSlippage = Math.Abs(exitPrice - (isLiq ? liqPrice : (isSl ? slPrice : tpPrice)));
                            orderLog.CloseReason = closeReason;
                            orderLog.TradePnL = netPnL;
                            orderLog.HoldDurationMinutes = (currentTime - orderLog.OpenTime).TotalMinutes;
                            orderLog.CurrentBalance = balance;
                            orderLog.HoldPeriodVolatility = CalculateHoldPeriodVolatility(orderLog.OpenTime, currentTime, klines1mRaw);
                            
                            decimal openMarginAllocated = (orderLog.Quantity * posRisk.EntryPrice) / config.Leverage;
                            orderLog.TradeROI = openMarginAllocated == 0 ? 0m : (netPnL / openMarginAllocated * 100m);

                            // 从 active 移除
                            mockPositionManager.RemovePosition(config.Symbol);

                            _logger.LogWarning($"🔴 [平仓成交] {closeReason} | 现价: {exitPrice:F4} | 单笔盈亏: {netPnL:F2} USDT");
                        }
                    }
                }

                // --- E. 合成 higher timeframe 并在收盘时推送到 event bus ---
                PublishClosingMtfBars(k1m, klines1mHistory, customEventBus);

                // --- F. 喂 1m 行情给策略 ---
                customEventBus.PublishKline(k1m);

                // --- G. 捕获策略下单信号 (PlaceOrder) ---
                while (customOrderChannel.Reader.TryRead(out var signal))
                {
                    if (signal.Action == OrderAction.OpenMarket)
                    {
                        // 立即成交
                        if (mockPositionManager.HasActivePosition(config.Symbol)) continue;

                        decimal executionPrice = signal.Side == "BUY"
                            ? k1m.Close * (1 + config.TakerSlippage)
                            : k1m.Close * (1 - config.TakerSlippage);

                        decimal nominalValue = signal.UsdtAmount * signal.Leverage;
                        decimal quantity = nominalValue / executionPrice;
                        decimal entryFee = nominalValue * config.FeeRate;

                        balance -= entryFee;

                        var tracker = new PositionTracker
                        {
                            Symbol = config.Symbol,
                            Side = signal.Side,
                            Quantity = signal.Side == "BUY" ? quantity : -quantity,
                            EntryPrice = executionPrice,
                            TakeProfitPrice = signal.TakeProfitPrice ?? 0m,
                            OpenTime = currentTime,
                            IsStopMovedToBE = false,
                            StrategyName = signal.StrategyName
                        };

                        mockPositionManager.SetPosition(config.Symbol, tracker);

                        var orderLog = new BacktestOrderDetail
                        {
                            Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                            StrategyName = signal.StrategyName,
                            OrderNumber = "BT_M_" + Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper(),
                            Symbol = config.Symbol,
                            Direction = signal.Side == "BUY" ? "LONG" : "SHORT",
                            OpenTime = currentTime,
                            OpenPrice = executionPrice,
                            OpenType = "taker",
                            OpenFee = entryFee,
                            OpenSlippage = executionPrice - k1m.Close,
                            OpenReason = signal.Reason ?? "Market Order Signaled",
                            Quantity = quantity,
                            TakeProfitPrice = signal.TakeProfitPrice ?? (signal.Side == "BUY" 
                                ? executionPrice * (1 + 0.04m / config.Leverage) 
                                : executionPrice * (1 - 0.04m / config.Leverage)),
                            StopLossPrice = signal.StopLossPrice ?? (signal.Side == "BUY" 
                                ? executionPrice * (1 - 0.02m / config.Leverage) 
                                : executionPrice * (1 + 0.02m / config.Leverage))
                        };

                        closedOrders.Add(orderLog);
                        _logger.LogInformation($"🟢 [市价成交] {orderLog.Direction} 开仓成功 | 价格: {executionPrice:F4} | 数量: {quantity:F4}");
                    }
                    else if (signal.Action == OrderAction.OpenLimit)
                    {
                        // 挂限价单
                        var limitOrder = new PendingLimitOrder
                        {
                            Symbol = signal.Symbol,
                            Side = signal.Side,
                            Price = signal.Price ?? k1m.Close,
                            TakeProfitPrice = signal.TakeProfitPrice,
                            StopLossPrice = signal.StopLossPrice,
                            UsdtAmount = signal.UsdtAmount,
                            Leverage = signal.Leverage,
                            StrategyName = signal.StrategyName
                        };
                        pendingLimitOrders.Add(limitOrder);
                        _logger.LogInformation($"📝 [挂单部署] 限价挂单 {limitOrder.Side} | 价格: {limitOrder.Price:F4}");
                    }
                    else if (signal.Action == OrderAction.CancelAll)
                    {
                        pendingLimitOrders.Clear();
                        _logger.LogInformation("🧹 [撤销挂单] 已清空全部待成交挂单");
                    }
                }

                // --- H. 定期更新任务进度与广播 ---
                if (i % 100 == 0 || i == totalSteps - 1)
                {
                    double progressPct = Math.Round((double)i / totalSteps * 100, 2);
                    taskState.Progress = progressPct;
                    taskState.TradesCount = closedOrders.Count;
                    taskState.CurrentBalance = Math.Round(balance, 2);

                    // 记录资金曲线
                    equityCurve.Add(new EquityPoint { Time = currentTime, Balance = balance });

                    await _backtestHubContext.Clients.All.SendAsync("ReceiveBacktestProgress", new
                    {
                        taskId = taskState.TaskId,
                        progress = progressPct,
                        tradesCount = closedOrders.Count,
                        currentBalance = Math.Round(balance, 2),
                        status = taskState.Status.ToString()
                    });
                }
            }

            // 7. 回测结束，处理未平仓仓位
            if (mockPositionManager.TryGetPosition(config.Symbol, out var finalPos))
            {
                var orderLog = closedOrders.LastOrDefault(o => o.Symbol == config.Symbol && o.CloseTime == null);
                if (orderLog != null)
                {
                    decimal exitPrice = klines1mRaw.Last().Close * (finalPos.Side == "BUY" ? (1 - config.TakerSlippage) : (1 + config.TakerSlippage));
                    
                    decimal grossPnL = finalPos.Side == "BUY"
                        ? orderLog.Quantity * (exitPrice - finalPos.EntryPrice)
                        : orderLog.Quantity * (finalPos.EntryPrice - exitPrice);

                    decimal exitFee = orderLog.Quantity * exitPrice * config.FeeRate;
                    decimal netPnL = grossPnL - exitFee;

                    balance += netPnL;

                    orderLog.CloseTime = config.EndTime;
                    orderLog.ClosePrice = exitPrice;
                    orderLog.CloseType = "taker";
                    orderLog.CloseFee = exitFee;
                    orderLog.CloseSlippage = Math.Abs(exitPrice - klines1mRaw.Last().Close);
                    orderLog.CloseReason = "EndOfBacktest";
                    orderLog.TradePnL = netPnL;
                    orderLog.HoldDurationMinutes = (config.EndTime - orderLog.OpenTime).TotalMinutes;
                    orderLog.CurrentBalance = balance;
                    orderLog.HoldPeriodVolatility = CalculateHoldPeriodVolatility(orderLog.OpenTime, config.EndTime, klines1mRaw);

                    decimal openMarginAllocated = (orderLog.Quantity * finalPos.EntryPrice) / config.Leverage;
                    orderLog.TradeROI = openMarginAllocated == 0 ? 0m : (netPnL / openMarginAllocated * 100m);

                    mockPositionManager.RemovePosition(config.Symbol);
                }
            }

            // 8. 统计生成报告
            _logger.LogInformation("📊 [回测运行器] 回测循环结束，开始生成统计报告...");
            int totalTrades = closedOrders.Count;
            int winTrades = closedOrders.Count(o => o.TradePnL > 0);
            decimal winRate = totalTrades == 0 ? 0m : Math.Round((decimal)winTrades / totalTrades * 100m, 2);
            decimal totalProfit = balance - config.InitialBalance;
            decimal totalProfitPct = Math.Round(totalProfit / config.InitialBalance * 100m, 2);
            decimal totalFees = closedOrders.Sum(o => o.OpenFee + (o.CloseFee ?? 0m) + o.FundingFeePaid);

            // 最大回撤计算
            decimal maxDrawdown = 0m;
            decimal peak = config.InitialBalance;
            foreach (var point in equityCurve)
            {
                if (point.Balance > peak)
                {
                    peak = point.Balance;
                }
                decimal drawdown = peak == 0 ? 0m : (peak - point.Balance) / peak * 100m;
                if (drawdown > maxDrawdown)
                {
                    maxDrawdown = drawdown;
                }
            }

            decimal highestPrice = testKlines.Any() ? testKlines.Max(k => k.High) : 0m;
            decimal lowestPrice = testKlines.Any() ? testKlines.Min(k => k.Low) : 0m;
            decimal symbolVolatilityPct = lowestPrice == 0m ? 0m : Math.Round((highestPrice - lowestPrice) / lowestPrice * 100m, 2);

            var report = new BacktestReport
            {
                WinRate = winRate,
                TotalProfit = Math.Round(totalProfit, 2),
                TotalProfitPct = totalProfitPct,
                TotalTradesCount = totalTrades,
                TotalFees = Math.Round(totalFees, 2),
                MaxDrawdown = Math.Round(maxDrawdown, 2),
                OrderDetails = closedOrders,
                EquityCurve = equityCurve,
                SymbolVolatility = symbolVolatilityPct,
                Leverage = config.Leverage
            };

            taskState.Report = report;
            taskState.Status = BacktestStatus.Completed;
            taskState.Progress = 100.0;

            await _backtestHubContext.Clients.All.SendAsync("ReceiveBacktestProgress", new
            {
                taskId = taskState.TaskId,
                progress = 100.0,
                tradesCount = totalTrades,
                currentBalance = Math.Round(balance, 2),
                status = taskState.Status.ToString()
            });

            _logger.LogInformation($"🎉 [回测完成] 任务 {taskState.TaskId} 结束。净利润: {totalProfit:F2} USDT, 胜率: {winRate}%, 最大回撤: {maxDrawdown}%");
            return report;
        }

        private List<IKline> Aggregate1mToMtf(List<IKline> klines1m, string interval)
        {
            int minutes = interval switch
            {
                "3m" => 3,
                "5m" => 5,
                "10m" => 10,
                "15m" => 15,
                "30m" => 30,
                "1h" => 60,
                "1d" => 1440,
                _ => 1
            };
            if (minutes == 1) return klines1m;

            var result = new List<IKline>();
            long bucketMs = minutes * 60 * 1000L;

            foreach (var k in klines1m)
            {
                long bucketStart = k.OpenTime - (k.OpenTime % bucketMs);
                var last = result.LastOrDefault();
                if (last == null || bucketStart > last.OpenTime)
                {
                    result.Add(new KlineMessage
                    {
                        Symbol = k.Symbol,
                        Interval = interval,
                        OpenTime = bucketStart,
                        Open = k.Open,
                        High = k.High,
                        Low = k.Low,
                        Close = k.Close,
                        Volume = k.Volume,
                        TradeCount = k.TradeCount,
                        TakerBuyBaseVolume = k.TakerBuyBaseVolume,
                        IsClosed = true
                    });
                }
                else
                {
                    var m = (KlineMessage)last;
                    m.High = Math.Max(m.High, k.High);
                    m.Low = Math.Min(m.Low, k.Low);
                    m.Close = k.Close;
                    m.Volume += k.Volume;
                    m.TradeCount += k.TradeCount;
                    m.TakerBuyBaseVolume += k.TakerBuyBaseVolume;
                }
            }
            return result;
        }

        private string SerializeKlinesToBinanceJson(IEnumerable<IKline> klines)
        {
            var list = klines.Select(k => new object[]
            {
                k.OpenTime,
                k.Open.ToString(System.Globalization.CultureInfo.InvariantCulture),
                k.High.ToString(System.Globalization.CultureInfo.InvariantCulture),
                k.Low.ToString(System.Globalization.CultureInfo.InvariantCulture),
                k.Close.ToString(System.Globalization.CultureInfo.InvariantCulture),
                k.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture),
                k.OpenTime + 59999, // close time
                "0.0", // quote asset volume
                k.TradeCount,
                k.TakerBuyBaseVolume.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "0.0",
                "0"
            }).ToList();
            return System.Text.Json.JsonSerializer.Serialize(list);
        }

        private Type GetStrategyType(string strategyName)
        {
            var baseType = typeof(StrategyBase);
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(s => s.GetTypes())
                .FirstOrDefault(p => baseType.IsAssignableFrom(p) && p.IsClass && !p.IsAbstract &&
                                     (p.Name.Equals(strategyName, StringComparison.OrdinalIgnoreCase) ||
                                      (strategyName == "HighVolReboundStrategy" && p.Name == "HighVolStructureStrategyService") ||
                                      (strategyName == "HighVolStructure" && p.Name == "HighVolStructureStrategyService")));

            if (type == null)
            {
                throw new Exception($"未知的策略名称: {strategyName}");
            }
            return type;
        }

        private void PublishClosingMtfBars(IKline k1m, List<IKline> klines1mHistory, MarketEventBus eventBus)
        {
            var intervals = new[] { 3, 5, 10, 15, 30, 60, 1440 };
            foreach (var m in intervals)
            {
                long intervalMs = m * 60 * 1000L;
                if ((k1m.OpenTime + 60000) % intervalMs == 0)
                {
                    long openTimeLimit = k1m.OpenTime + 60000 - intervalMs;
                    var constituentKlines = klines1mHistory
                        .Where(k => k.OpenTime >= openTimeLimit && k.OpenTime <= k1m.OpenTime)
                        .ToList();

                    if (constituentKlines.Any())
                    {
                        var aggregated = new KlineMessage
                        {
                            Symbol = k1m.Symbol,
                            Interval = m switch
                            {
                                60 => "1h",
                                1440 => "1d",
                                _ => $"{m}m"
                            },
                            OpenTime = openTimeLimit,
                            Open = constituentKlines.First().Open,
                            High = constituentKlines.Max(k => k.High),
                            Low = constituentKlines.Min(k => k.Low),
                            Close = constituentKlines.Last().Close,
                            Volume = constituentKlines.Sum(k => k.Volume),
                            TradeCount = constituentKlines.Sum(k => k.TradeCount),
                            TakerBuyBaseVolume = constituentKlines.Sum(k => k.TakerBuyBaseVolume),
                            IsClosed = true
                        };
                        eventBus.PublishKline(aggregated);
                    }
                }
            }
        }
        private decimal CalculateHoldPeriodVolatility(DateTime openTime, DateTime closeTime, List<IKline> klines1mRaw)
        {
            try
            {
                long openTimeMs = new DateTimeOffset(DateTime.SpecifyKind(openTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                long closeTimeMs = new DateTimeOffset(DateTime.SpecifyKind(closeTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                
                var holdKlines = klines1mRaw.Where(k => k.OpenTime >= (openTimeMs - 60000) && k.OpenTime <= closeTimeMs).ToList();
                if (holdKlines.Any())
                {
                    decimal maxHigh = holdKlines.Max(k => k.High);
                    decimal minLow = holdKlines.Min(k => k.Low);
                    return minLow == 0m ? 0m : Math.Round((maxHigh - minLow) / minLow * 100m, 2);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[回测运行器] 计算持仓K线波动失败: {ex.Message}");
            }
            return 0m;
        }

        // --- Mock Helper Classes ---
        private class MockBinanceWebSocketService : BinanceWebSocketService
        {
            private readonly Func<string, string, int, Task<string>> _getHistoryHandler;

            public MockBinanceWebSocketService(Func<string, string, int, Task<string>> getHistoryHandler)
                : base(null, null, null)
            {
                _getHistoryHandler = getHistoryHandler;
            }

            public override Task<string> GetHistoricalKlinesAsync(string symbol, string interval, int limit = 1000, long? endTime = null)
            {
                return _getHistoryHandler(symbol, interval, limit);
            }

            public override Task SubscribeBackendAsync(IEnumerable<string> streams) => Task.CompletedTask;
            public override Task UnsubscribeBackendAsync(IEnumerable<string> streams) => Task.CompletedTask;
            public override Task<List<string>> RefreshTopSymbolsAsync(CancellationToken stoppingToken) => Task.FromResult(new List<string>());
        }

        private class MockBinanceTradeWsService : BinanceTradeWsService
        {
            public MockBinanceTradeWsService(Microsoft.Extensions.Configuration.IConfiguration config)
                : base(config, null)
            {
            }

            public override decimal FormatQuantity(string symbol, decimal rawQty)
            {
                return Math.Round(rawQty, 4, MidpointRounding.ToZero);
            }

            public override decimal FormatPrice(string symbol, decimal rawPrice)
            {
                return Math.Round(rawPrice, 4, MidpointRounding.AwayFromZero);
            }
        }

        private class PendingLimitOrder
        {
            public string Symbol { get; set; }
            public string Side { get; set; }
            public decimal Price { get; set; }
            public decimal? TakeProfitPrice { get; set; }
            public decimal? StopLossPrice { get; set; }
            public decimal UsdtAmount { get; set; }
            public decimal Leverage { get; set; }
            public string StrategyName { get; set; }
        }
    }
}
