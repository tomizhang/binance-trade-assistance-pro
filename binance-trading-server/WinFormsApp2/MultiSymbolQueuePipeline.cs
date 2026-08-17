using Binance.Net.Enums;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WinFormsApp2
{
    /// <summary>
    /// 单币种独立推演与策略上下文 (Symbol Processing Context)
    /// </summary>
    public class SingleSymbolContext
    {
        public SymbolConfigItem Config { get; }
        public string Symbol => Config.Symbol;
        public KlineInterval Interval => Config.ParsedInterval;
        public int Leverage => Config.Leverage;
        public decimal OrderQuantityUsdt => Config.OrderQuantityUsdt;

        public List<Kline> Klines { get; } = new List<Kline>();
        public List<PivotPoint> ActivePivots { get; set; } = new List<PivotPoint>();
        public List<TrendLine> ActiveTrendLines { get; set; } = new List<TrendLine>();

        public TrendLineStrategy Strategy { get; } = new TrendLineStrategy();
        public EventContractStrategy EventStrategy { get; } = new EventContractStrategy();

        public bool IsInitialized { get; set; } = false;
        public int CurrentKlineIndex { get; set; } = 0;

        public SingleSymbolContext(SymbolConfigItem config, StrategyParameters? globalParams = null, EventContractParameters? eventParams = null)
        {
            Config = config ?? new SymbolConfigItem();

            if (globalParams != null)
            {
                Strategy.Params = globalParams.Clone();
            }
            Strategy.Params.IsLiveTrading = true;

            if (eventParams != null)
            {
                EventStrategy.Params = eventParams.Clone();
            }
            EventStrategy.Params.IsLiveTrading = true;
        }

        public void UpdateDisplayPivotsAndTrendLines(int minLineX1X2 = 40, int minLineAge = 80)
        {
            lock (Klines)
            {
                int totalCount = Klines.Count;
                if (totalCount == 0) return;

                Kline[] klineArray = Klines.ToArray();

                // A. 增量计算高低点
                var pivots = PivotHelper.CalculatePeaksCombinedFast(klineArray, leftBars: 2, rightBars: 2);
                ActivePivots = pivots;

                // B. 增量计算与过滤延伸趋势线
                var trendLines = TrendLineHelper.GenerateTrendLinesFromPivots(
                    klineArray,
                    pivots,
                    filterPenetrated: true);

                ActiveTrendLines = trendLines;
                CurrentKlineIndex = Math.Max(0, totalCount - 1);
            }
        }

        public void ProcessTick(Tick tick)
        {
            if (!IsInitialized)
                return;

            Kline currentKline = default;
            lock (Klines)
            {
                if (CurrentKlineIndex >= 0 && CurrentKlineIndex < Klines.Count)
                {
                    currentKline = Klines[CurrentKlineIndex];
                }
            }

            // 1. 趋势线回调策略驱动
            if (Strategy.Params.Enabled && ActiveTrendLines != null && ActiveTrendLines.Count > 0)
            {
                Strategy.ProcessTick(tick, CurrentKlineIndex, ActiveTrendLines, currentKline);
            }

            // 2. 事件合约策略驱动 (10m / 30m / 1h 到期判定)
            if (EventStrategy.Params.Enabled)
            {
                EventStrategy.ProcessTick(tick, CurrentKlineIndex, ActiveTrendLines, currentKline, Symbol);
            }
        }
    }

    /// <summary>
    /// 多币种并发排队加载与处理管道管理器 (Queued Multi-Symbol Pipeline Manager)
    /// 核心规则：
    /// 1. 排队限流加载 (Queue-Rate-Limited Initialization)：避免多币种并发同时抓取与计算导致的 CPU 飙高、内存暴涨与币安 API 429 限流；
    /// 2. 独立上线推演：每个币种具备独立的 K 线历史、高低点与趋势线推断，数据隔离不冲突；
    /// 3. 秒级非阻塞 WebSocket 实时分发。
    /// </summary>
    public class MultiSymbolQueuePipeline : IDisposable
    {
        private readonly ConcurrentDictionary<string, SingleSymbolContext> _contexts = new ConcurrentDictionary<string, SingleSymbolContext>();
        private readonly SemaphoreSlim _loadingSemaphore = new SemaphoreSlim(1, 1); // 严格限制最大 1 个并发加载线程，排队平滑装载
        private readonly LiveFeedManager _liveFeedManager = new LiveFeedManager();

        public bool IsRunning => _liveFeedManager.IsRunning;
        public event Action<string>? OnLog;
        public event Action<string, SingleSymbolContext>? OnSymbolInitialized;
        public event Action<string, Kline>? OnSymbolKlineUpdated;
        public event Action<string, Tick>? OnSymbolTickUpdated;

        public OrderExecutionQueue OrderQueue { get; } = new OrderExecutionQueue();
        public StrategyParameters StrategyParams { get; set; } = new StrategyParameters();
        public EventContractParameters EventParams { get; set; } = new EventContractParameters();

        public MultiSymbolQueuePipeline()
        {
            _liveFeedManager.OnLog += Log;
            _liveFeedManager.OnMultiLiveKlinePushed += HandleLiveKlinePushed;
            _liveFeedManager.OnMultiLiveTickPushed += HandleLiveTickPushed;
            OrderQueue.OnLog += Log;
        }

        public void Log(string msg)
        {
            OnLog?.Invoke(msg);
        }

        public SingleSymbolContext? GetContext(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return null;
            _contexts.TryGetValue(symbol.Trim().ToUpperInvariant(), out var ctx);
            return ctx;
        }

        public IReadOnlyCollection<SingleSymbolContext> GetAllContexts()
        {
            return _contexts.Values.ToList();
        }

        /// <summary>
        /// 动态热更新所有正在运行中的币种策略参数 (实时下发界面 TakeProfit / StopLoss 与事件合约周期)
        /// </summary>
        public void UpdateStrategyParameters(StrategyParameters stratParams, EventContractParameters? eventContractParams = null)
        {
            if (stratParams != null)
            {
                StrategyParams.Enabled = stratParams.Enabled;
                StrategyParams.MinLineX1X2 = stratParams.MinLineX1X2;
                StrategyParams.MinLineAge = stratParams.MinLineAge;
                StrategyParams.TakeProfitPct = stratParams.TakeProfitPct;
                StrategyParams.StopLossPct = stratParams.StopLossPct;
            }

            if (eventContractParams != null)
            {
                EventParams.Enabled = eventContractParams.Enabled;
                EventParams.Duration = eventContractParams.Duration;
                EventParams.MinLineX1X2 = eventContractParams.MinLineX1X2;
                EventParams.MinLineAge = eventContractParams.MinLineAge;
                EventParams.EnableEarlyExit = eventContractParams.EnableEarlyExit;
                EventParams.TakeProfitPct = eventContractParams.TakeProfitPct;
                EventParams.StopLossPct = eventContractParams.StopLossPct;
            }

            foreach (var ctx in _contexts.Values)
            {
                if (stratParams != null)
                {
                    ctx.Strategy.Params.Enabled = stratParams.Enabled;
                    ctx.Strategy.Params.MinLineX1X2 = stratParams.MinLineX1X2;
                    ctx.Strategy.Params.MinLineAge = stratParams.MinLineAge;
                    ctx.Strategy.Params.TakeProfitPct = stratParams.TakeProfitPct;
                    ctx.Strategy.Params.StopLossPct = stratParams.StopLossPct;
                }

                if (eventContractParams != null)
                {
                    ctx.EventStrategy.Params.Enabled = eventContractParams.Enabled;
                    ctx.EventStrategy.Params.Duration = eventContractParams.Duration;
                    ctx.EventStrategy.Params.MinLineX1X2 = eventContractParams.MinLineX1X2;
                    ctx.EventStrategy.Params.MinLineAge = eventContractParams.MinLineAge;
                    ctx.EventStrategy.Params.EnableEarlyExit = eventContractParams.EnableEarlyExit;
                    ctx.EventStrategy.Params.TakeProfitPct = eventContractParams.TakeProfitPct;
                    ctx.EventStrategy.Params.StopLossPct = eventContractParams.StopLossPct;
                }
            }
        }

        /// <summary>
        /// 核心方法：排队平滑初始化并启动多币种并发在线交易
        /// </summary>
        public async Task StartPipelineAsync(
            IEnumerable<SymbolConfigItem> configs,
            bool isLiveTrading,
            string apiKey,
            string apiSecret,
            StrategyParameters? strategyParams = null,
            EventContractParameters? eventContractParams = null)
        {
            StopPipeline();

            if (strategyParams != null)
            {
                StrategyParams = strategyParams.Clone();
            }
            if (eventContractParams != null)
            {
                EventParams = eventContractParams.Clone();
            }

            var symbolList = configs.Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Symbol)).ToList();
            if (symbolList.Count == 0)
            {
                throw new ArgumentException("至少需要启用一个有效的配置币种！");
            }

            Log($"🚀 [多币种排队管道启动] 准备平滑加载 {symbolList.Count} 个币种差异化配置 (止盈: +{StrategyParams.TakeProfitPct}%, 止损: -{StrategyParams.StopLossPct}%, 事件合约: {(EventParams.Enabled ? $"启用({EventParams.DurationMinutes}m)" : "未开启")})...");

            // 1. 初始化下单队列引擎
            OrderQueue.ConfigureApi(isLiveTrading, apiKey, apiSecret, leverage: 20, orderQuantityUsdt: 1m);

            // 2. 顺序排队加载每个币种的历史 K 线、高低点与趋势线
            foreach (var cfg in symbolList)
            {
                string sym = cfg.Symbol.Trim().ToUpperInvariant();
                var ctx = new SingleSymbolContext(cfg, StrategyParams, EventParams);
                _contexts[sym] = ctx;

                // 绑定策略下单回调事件
                BindStrategyEvents(ctx);

                // 排队异步装载
                _ = LoadSymbolDataInQueueAsync(ctx);
            }

            // 3. 建立多币种 WebSocket 实盘数据流订阅
            var symbolsToSubscribe = symbolList.Select(c => c.Symbol.Trim().ToUpperInvariant()).Distinct().ToList();
            var primaryInterval = symbolList[0].ParsedInterval;

            await _liveFeedManager.StartMultiLiveFeedAsync(symbolsToSubscribe, primaryInterval);
            Log($"✅ [多币种数据流就绪] 已成功建立币安 WebSocket 实盘长连接，后台队列平滑预热装载中...");
        }

        /// <summary>
        /// 排队限流异步加载单币种历史 K 线与特征计算 (带 150ms API 保护间隔)
        /// </summary>
        private async Task LoadSymbolDataInQueueAsync(SingleSymbolContext ctx)
        {
            await _loadingSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                Log($"⏳ [排队加载中] ({_contexts.Values.Count(c => c.IsInitialized) + 1}/{_contexts.Count}) 正在为 [{ctx.Symbol}] [{ctx.Interval}] 获取历史 1000 根基线 K 线...");

                // A. 抓取 API 历史数据
                var initialKlines = await DataHelper.FetchKlinesFromApiAsync(ctx.Symbol, ctx.Interval, limit: 1000).ConfigureAwait(false);

                lock (ctx.Klines)
                {
                    ctx.Klines.Clear();
                    if (initialKlines != null && initialKlines.Length > 0)
                    {
                        ctx.Klines.AddRange(initialKlines);
                    }
                }

                // B. 平滑计算历史高低点与延伸趋势线
                ctx.UpdateDisplayPivotsAndTrendLines();
                ctx.IsInitialized = true;

                Log($"✅ [装载完毕] [{ctx.Symbol}] 建立 {ctx.Klines.Count} 根 K 线基线，计算高低点: {ctx.ActivePivots.Count} 个，活动趋势线: {ctx.ActiveTrendLines.Count} 条 (杠杆: {ctx.Leverage}x, 资金: {ctx.OrderQuantityUsdt} USDT, 止盈: +{ctx.Strategy.Params.TakeProfitPct}%, 止损: -{ctx.Strategy.Params.StopLossPct}%)");

                OnSymbolInitialized?.Invoke(ctx.Symbol, ctx);

                // C. 间隔 150 毫秒保护币安 API 权重与缓解 CPU 瞬间硬冲
                await Task.Delay(150).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"❌ [装载异常] 币种 [{ctx.Symbol}] 加载失败: {ex.Message}");
            }
            finally
            {
                _loadingSemaphore.Release();
            }
        }

        private void BindStrategyEvents(SingleSymbolContext ctx)
        {
            // 1. 趋势线回调策略事件绑定
            ctx.Strategy.OnTradeOpened += trade =>
            {
                decimal tpPct = ctx.Strategy.Params.TakeProfitPct;
                decimal slPct = ctx.Strategy.Params.StopLossPct;
                decimal tpPrice = trade.Position == PositionType.Long
                    ? trade.EntryPrice * (1m + tpPct / 100m)
                    : trade.EntryPrice * (1m - tpPct / 100m);
                decimal slPrice = trade.Position == PositionType.Long
                    ? trade.EntryPrice * (1m - slPct / 100m)
                    : trade.EntryPrice * (1m + slPct / 100m);

                Log($"🟢 [{ctx.Symbol} 趋势线策略开仓] #{trade.Id} [{(trade.Position == PositionType.Long ? "买入做多" : "卖出做空")}] @ {trade.EntryPrice} ({trade.EntryTime:yyyy-MM-dd HH:mm:ss}) | 🎯 止盈位: {tpPrice} (+{tpPct}%) | 🛡 止损位: {slPrice} (-{slPct}%)");

                OrderType oType = trade.Position == PositionType.Long ? OrderType.BuyLongOpen : OrderType.SellShortOpen;
                OrderQueue.EnqueueOrder(new OrderRequest
                {
                    TradeId = trade.Id,
                    Symbol = ctx.Symbol,
                    Type = oType,
                    Price = trade.EntryPrice,
                    QuantityUsdt = ctx.OrderQuantityUsdt,
                    Timestamp = trade.EntryTime,
                    TakeProfitPrice = tpPrice,
                    StopLossPrice = slPrice,
                    TakeProfitPct = tpPct,
                    StopLossPct = slPct,
                    Comment = $"{ctx.Symbol} 趋势线假突破开仓"
                });
            };

            ctx.Strategy.OnTradeClosed += trade =>
            {
                Log($"🔴 [{ctx.Symbol} 趋势线策略平仓] #{trade.Id} [{(trade.ExitReason == TradeExitReason.TakeProfit ? "止盈" : "止损")}] 收益: {trade.ProfitPct:+0.00;-0.00;0.00}% @ {trade.ExitPrice}");

                OrderType oType = trade.Position == PositionType.Long ? OrderType.CloseLong : OrderType.CloseShort;
                OrderQueue.EnqueueOrder(new OrderRequest
                {
                    TradeId = trade.Id,
                    Symbol = ctx.Symbol,
                    Type = oType,
                    Price = trade.ExitPrice,
                    QuantityUsdt = ctx.OrderQuantityUsdt,
                    Timestamp = trade.ExitTime,
                    Comment = $"{ctx.Symbol} 策略止盈/止损平仓"
                });
            };

            // 2. 事件合约策略事件绑定 (10m / 30m / 1h 到期判定)
            ctx.EventStrategy.OnContractOpened += contract =>
            {
                decimal tpPct = ctx.EventStrategy.Params.TakeProfitPct;
                decimal slPct = ctx.EventStrategy.Params.StopLossPct;
                decimal tpPrice = contract.Position == PositionType.Long
                    ? contract.EntryPrice * (1m + tpPct / 100m)
                    : contract.EntryPrice * (1m - tpPct / 100m);
                decimal slPrice = contract.Position == PositionType.Long
                    ? contract.EntryPrice * (1m - slPct / 100m)
                    : contract.EntryPrice * (1m + slPct / 100m);

                Log($"⏱ [{ctx.Symbol} 事件合约开仓] #{contract.Id} [{(contract.Position == PositionType.Long ? "买多 LONG" : "卖空 SHORT")}] @ {contract.EntryPrice} | 判定周期: {ctx.EventStrategy.Params.DurationMinutes}m | 预计到期: {contract.ExpectedExpiryTime:HH:mm:ss}");

                OrderType oType = contract.Position == PositionType.Long ? OrderType.BuyLongOpen : OrderType.SellShortOpen;
                OrderQueue.EnqueueOrder(new OrderRequest
                {
                    TradeId = contract.Id,
                    Symbol = ctx.Symbol,
                    Type = oType,
                    Price = contract.EntryPrice,
                    QuantityUsdt = ctx.OrderQuantityUsdt,
                    Timestamp = contract.EntryTime,
                    TakeProfitPrice = ctx.EventStrategy.Params.EnableEarlyExit ? tpPrice : 0m,
                    StopLossPrice = ctx.EventStrategy.Params.EnableEarlyExit ? slPrice : 0m,
                    TakeProfitPct = tpPct,
                    StopLossPct = slPct,
                    Comment = $"{ctx.Symbol} {ctx.EventStrategy.Params.DurationMinutes}m事件合约"
                });
            };

            ctx.EventStrategy.OnContractSettled += contract =>
            {
                string resDesc = contract.IsWin ? "🏆 盈利 WIN" : "❌ 亏损 LOSS";
                Log($"⏱ [{ctx.Symbol} 事件合约结算] #{contract.Id} {resDesc} | 原因: {contract.ExitReason} | 收益: {contract.ProfitPct:+0.00;-0.00;0.00}% @ {contract.ExitPrice}");

                OrderType oType = contract.Position == PositionType.Long ? OrderType.CloseLong : OrderType.CloseShort;
                OrderQueue.EnqueueOrder(new OrderRequest
                {
                    TradeId = contract.Id,
                    Symbol = ctx.Symbol,
                    Type = oType,
                    Price = contract.ExitPrice,
                    QuantityUsdt = ctx.OrderQuantityUsdt,
                    Timestamp = contract.ExitTime,
                    Comment = $"{ctx.Symbol} 事件合约到期交割"
                });
            };
        }

        private void HandleLiveKlinePushed(string symbol, Kline liveKline)
        {
            if (_contexts.TryGetValue(symbol, out var ctx))
            {
                lock (ctx.Klines)
                {
                    if (ctx.Klines.Count > 0 && ctx.Klines.Last().OpenTime == liveKline.OpenTime)
                    {
                        ctx.Klines[ctx.Klines.Count - 1] = liveKline;
                    }
                    else
                    {
                        ctx.Klines.Add(liveKline);
                        if (ctx.Klines.Count > 500)
                        {
                            ctx.Klines.RemoveAt(0); // 剔除最旧 K 线，锁定 500 容量
                        }
                    }
                }

                ctx.UpdateDisplayPivotsAndTrendLines();
                OnSymbolKlineUpdated?.Invoke(symbol, liveKline);
            }
        }

        private void HandleLiveTickPushed(string symbol, Tick liveTick)
        {
            if (_contexts.TryGetValue(symbol, out var ctx))
            {
                ctx.ProcessTick(liveTick);
                OnSymbolTickUpdated?.Invoke(symbol, liveTick);
            }
        }

        public void StopPipeline()
        {
            _liveFeedManager.StopLiveFeedAsync().Wait();
            _contexts.Clear();
        }

        public void Dispose()
        {
            StopPipeline();
            _loadingSemaphore.Dispose();
            OrderQueue.Dispose();
        }
    }
}
