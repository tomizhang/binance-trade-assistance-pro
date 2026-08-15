using Binance.Net.Enums;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WinFormsApp2
{
    /// <summary>
    /// 单个币种独立的策略推演与行情状态上下文 (Per-Symbol Trading Engine Context)
    /// </summary>
    public class SymbolEngineContext
    {
        public string Symbol { get; }
        public TrendLineStrategy Strategy { get; } = new TrendLineStrategy();
        public List<Kline> ReplayKlines { get; } = new List<Kline>(5000);
        public List<Kline> CombinedHistoryList { get; } = new List<Kline>(1500);
        public Kline[] WarmupKlinesBuffer { get; set; } = Array.Empty<Kline>();
        public Kline[] DisplayKlinesBuffer { get; } = new Kline[500];

        public List<PivotPoint> ActivePivots { get; set; } = new List<PivotPoint>();
        public List<TrendLine> ActiveTrendLines { get; set; } = new List<TrendLine>();
        public int CurrentKlineIndex { get; set; } = 0;

        public SymbolEngineContext(string symbol)
        {
            Symbol = symbol.Trim().ToUpper();
        }

        public void Reset()
        {
            Strategy.Reset();
            lock (ReplayKlines)
            {
                ReplayKlines.Clear();
            }
            WarmupKlinesBuffer = Array.Empty<Kline>();
            ActivePivots.Clear();
            ActiveTrendLines.Clear();
            CurrentKlineIndex = 0;
        }

        public void UpdateDisplayPivotsAndTrendLines()
        {
            int sampleSize = 0;
            lock (ReplayKlines)
            {
                CombinedHistoryList.Clear();
                if (WarmupKlinesBuffer != null && WarmupKlinesBuffer.Length > 0)
                {
                    CombinedHistoryList.AddRange(WarmupKlinesBuffer);
                }
                CombinedHistoryList.AddRange(ReplayKlines);

                int totalCombined = CombinedHistoryList.Count;
                if (totalCombined < 7)
                {
                    ActivePivots.Clear();
                    ActiveTrendLines.Clear();
                    return;
                }

                sampleSize = Math.Min(totalCombined, 500);
                CombinedHistoryList.CopyTo(totalCombined - sampleSize, DisplayKlinesBuffer, 0, sampleSize);
            }

            Kline[] sampleSlice = new Kline[sampleSize];
            Array.Copy(DisplayKlinesBuffer, 0, sampleSlice, 0, sampleSize);

            ActivePivots = PivotHelper.CalculatePeaksCombinedFast(sampleSlice, leftBars: 3, rightBars: 3);
            ActiveTrendLines = TrendLineHelper.GenerateTrendLinesFromPivots(sampleSlice, ActivePivots, filterPenetrated: true);
        }

        public void ProcessTick(Tick tick)
        {
            if (Strategy.Params.Enabled && ActiveTrendLines != null && ActiveTrendLines.Count > 0)
            {
                Kline currentKline = default;
                int currentSampleIndex = 0;

                lock (ReplayKlines)
                {
                    if (ReplayKlines.Count > 0)
                    {
                        int idx = CurrentKlineIndex;
                        if (idx < 0 || idx >= ReplayKlines.Count)
                        {
                            idx = ReplayKlines.Count - 1; // 兼容在线实盘 LiveStream 模式 (自动锁定最新在建 K线)
                        }
                        currentKline = ReplayKlines[idx];

                        // 在线交易/实盘 Tick 动态更新在建 K线的 OHLC 与最新收盘价
                        if (tick.LastPrice > 0m)
                        {
                            if (currentKline.OpenPrice == 0m) currentKline.OpenPrice = tick.LastPrice;
                            if (currentKline.HighPrice == 0m || tick.LastPrice > currentKline.HighPrice) currentKline.HighPrice = tick.LastPrice;
                            if (currentKline.LowPrice == 0m || tick.LastPrice < currentKline.LowPrice) currentKline.LowPrice = tick.LastPrice;
                            currentKline.ClosePrice = tick.LastPrice;
                            if (currentKline.OpenTime == default) currentKline.OpenTime = tick.Time;
                            ReplayKlines[idx] = currentKline;
                        }
                    }
                    else
                    {
                        // 若在线行情初始为空，依据 Tick 实时合成首根 K线
                        currentKline = new Kline
                        {
                            OpenTime = tick.Time,
                            OpenPrice = tick.LastPrice,
                            HighPrice = tick.LastPrice,
                            LowPrice = tick.LastPrice,
                            ClosePrice = tick.LastPrice
                        };
                    }

                    int totalCount = (WarmupKlinesBuffer != null ? WarmupKlinesBuffer.Length : 0) + ReplayKlines.Count;
                    currentSampleIndex = Math.Max(0, Math.Min(totalCount, 500) - 1);
                }

                Strategy.ProcessTick(tick, currentSampleIndex, ActiveTrendLines, currentKline);
            }
        }
    }

    /// <summary>
    /// 独立无界面多币种并发核心交易与回演引擎 (Headless Multi-Symbol Trading & Replay Engine)
    /// 0 依赖 Windows GUI / WinForms，可独立在 Linux 各种无界面 Server 系统下并发交易多币种
    /// </summary>
    public class TradingServerEngine
    {
        private readonly MarketReplayer _replayer = new MarketReplayer();
        private readonly LiveFeedManager _liveFeedManager = new LiveFeedManager();
        private readonly OrderExecutionQueue _orderQueue = new OrderExecutionQueue();
        private readonly ConcurrentDictionary<string, SymbolEngineContext> _symbolEngines = new ConcurrentDictionary<string, SymbolEngineContext>();
        private BatchQueueManager? _batchQueueManager;

        public ExecutionMode Mode { get; private set; } = ExecutionMode.Idle;
        public MarketReplayer Replayer => _replayer;
        public LiveFeedManager LiveFeedManager => _liveFeedManager;
        public OrderExecutionQueue OrderQueue => _orderQueue;
        public bool IsRunning => Mode != ExecutionMode.Idle;

        public string ActiveSymbol { get; set; } = "BTCUSDT";

        public SymbolEngineContext GetOrCreateContext(string symbol)
        {
            symbol = symbol.Trim().ToUpper();
            return _symbolEngines.GetOrAdd(symbol, s =>
            {
                var ctx = new SymbolEngineContext(s);
                ctx.Strategy.Params.IsLiveTrading = _orderQueue.IsLiveTrading;
                BindStrategyOrderEvents(ctx);
                return ctx;
            });
        }

        public SymbolEngineContext ActiveContext => GetOrCreateContext(ActiveSymbol);
        public IReadOnlyCollection<SymbolEngineContext> AllSymbolContexts => _symbolEngines.Values.ToList();

        public TrendLineStrategy Strategy => ActiveContext.Strategy;
        public IReadOnlyList<Kline> DisplayKlinesBuffer => ActiveContext.DisplayKlinesBuffer;
        public IReadOnlyList<PivotPoint> ActivePivots => ActiveContext.ActivePivots;
        public IReadOnlyList<TrendLine> ActiveTrendLines => ActiveContext.ActiveTrendLines;

        public event Action<string>? OnLog;
        public event Action<Tick>? OnTickPushed;
        public event Action<Kline>? OnKlinePushed;
        public event Action<TradeRecord>? OnTradeOpened;
        public event Action<TradeRecord>? OnTradeClosed;
        public event Action? OnChartRefreshRequired;

        public TradingServerEngine()
        {
            // 绑定底层引擎事件回调
            _replayer.OnLog += Log;
            _replayer.OnTickPushed += Core_OnTickPushed;
            _replayer.OnKlinePushed += Core_OnKlinePushed;
            _replayer.OnPlaybackCompleted += Core_OnPlaybackCompleted;

            _liveFeedManager.OnLog += Log;
            _liveFeedManager.OnMultiLiveKlinePushed += Core_OnMultiLiveKlinePushed;
            _liveFeedManager.OnMultiLiveTickPushed += Core_OnMultiLiveTickPushed;

            _orderQueue.OnLog += Log;
        }

        public decimal OrderQuantityUsdt { get; set; } = 1m;

        public void ConfigureOrderEngine(bool isLiveTrading, string apiKey, string apiSecret, int leverage, decimal orderQuantityUsdt = 1m)
        {
            OrderQuantityUsdt = orderQuantityUsdt > 0 ? orderQuantityUsdt : 1m;
            _orderQueue.ConfigureApi(isLiveTrading, apiKey, apiSecret, leverage, OrderQuantityUsdt);

            foreach (var ctx in _symbolEngines.Values)
            {
                ctx.Strategy.Params.IsLiveTrading = isLiveTrading;
            }
        }

        public Task<bool> SetLeverageAsync(string symbol, int leverage)
        {
            return _orderQueue.SetLeverageAsync(symbol, leverage);
        }

        private void BindStrategyOrderEvents(SymbolEngineContext ctx)
        {
            string sym = ctx.Symbol;
            ctx.Strategy.OnTradeOpened += trade =>
            {
                OnTradeOpened?.Invoke(trade);

                OrderType oType = trade.Position == PositionType.Long ? OrderType.BuyLongOpen : OrderType.SellShortOpen;
                _orderQueue.EnqueueOrder(new OrderRequest
                {
                    TradeId = trade.Id,
                    Symbol = sym,
                    Type = oType,
                    Price = trade.EntryPrice,
                    QuantityUsdt = OrderQuantityUsdt,
                    Timestamp = trade.EntryTime,
                    Comment = "策略信号触发开仓"
                });
            };

            ctx.Strategy.OnTradeClosed += trade =>
            {
                OnTradeClosed?.Invoke(trade);

                OrderType oType = trade.Position == PositionType.Long ? OrderType.CloseLong : OrderType.CloseShort;
                _orderQueue.EnqueueOrder(new OrderRequest
                {
                    TradeId = trade.Id,
                    Symbol = sym,
                    Type = oType,
                    Price = trade.ExitPrice,
                    QuantityUsdt = OrderQuantityUsdt,
                    Timestamp = trade.ExitTime,
                    Comment = "策略止盈/止损平仓"
                });
            };
        }

        public void Log(string msg)
        {
            OnLog?.Invoke(msg);
        }

        public void UpdateDisplayPivotsAndTrendLines()
        {
            ActiveContext.UpdateDisplayPivotsAndTrendLines();
        }

        /// <summary>
        /// 启动历史数据回演引擎 (支持指定单币种测试)
        /// </summary>
        public async Task StartReplayAsync(
            string symbol,
            KlineInterval interval,
            DateTime startDate,
            DateTime endDate,
            bool enableWarmup,
            bool enableTickPush,
            int intervalMs)
        {
            Stop();

            ActiveSymbol = symbol.Trim().ToUpper();
            Mode = ExecutionMode.BacktestReplay;

            var ctx = GetOrCreateContext(ActiveSymbol);
            ctx.Reset();

            Log($"▶ [引擎启动] 开始初始化单币种行情回播: [{ActiveSymbol}] | {interval} | {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}");

            // 1. 初始化 FIFO 多线程后台下载管道
            _batchQueueManager = new BatchQueueManager(ActiveSymbol, interval, startDate, endDate, enableTickPush: enableTickPush, maxQueueCapacity: 5, batchDays: 1, logger: Log);
            _batchQueueManager.OnLog += Log;
            _batchQueueManager.Start();

            // 2. 预载首批数据
            var firstChunk = await _batchQueueManager.GetNextChunkAsync();
            if (firstChunk == null || firstChunk.Klines.Length == 0)
            {
                Log("❌ 未装载到任何 K线数据，引擎终止。");
                Mode = ExecutionMode.Idle;
                return;
            }

            Kline[] klines = firstChunk.Klines;
            Tick[] ticks = firstChunk.Ticks;

            // 3. API 预热控制
            ctx.WarmupKlinesBuffer = Array.Empty<Kline>();
            if (enableWarmup)
            {
                try
                {
                    DateTime warmupEndDate = startDate.AddTicks(-1);
                    Log($"[API 预热开启] 准备获取 [{ActiveSymbol}] [{interval}] 起点前 1000 根历史预热 K 线 (截止 {warmupEndDate:yyyy-MM-dd HH:mm:ss})...");
                    var fetchedWarmup = await DataHelper.FetchKlinesFromApiAsync(ActiveSymbol, interval, endTime: warmupEndDate, limit: 1000);
                    if (fetchedWarmup != null && fetchedWarmup.Length > 0)
                    {
                        ctx.WarmupKlinesBuffer = fetchedWarmup;
                        Log($"[API 预热成功] 成功装载 {fetchedWarmup.Length} 根历史预热 K 线。");
                    }
                }
                catch (Exception apiEx)
                {
                    Log($"[API 预热提示] 获取预热 K 线未成功 ({apiEx.Message})，自动回退。");
                }
            }

            ctx.UpdateDisplayPivotsAndTrendLines();
            OnChartRefreshRequired?.Invoke();

            _replayer.StartPlayback(klines, ticks, enableTickPush, intervalMs);
        }

        /// <summary>
        /// 启动多币种并发实盘 WebSocket 行情接口 (如同时监控 BTCUSDT, ETHUSDT, SOLUSDT, BNBUSDT)
        /// </summary>
        public async Task StartMultiLiveStreamAsync(IEnumerable<string> symbols, KlineInterval interval)
        {
            Stop();

            List<string> symbolList = symbols.Select(s => s.Trim().ToUpper()).Distinct().Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (symbolList.Count == 0)
            {
                throw new ArgumentException("订阅币种列表不能为空！");
            }

            ActiveSymbol = symbolList[0];
            Mode = ExecutionMode.LiveStream;

            Log($"📡 [多币种实盘启动] 正在在线连接币安 WebSocket，并发监控 {symbolList.Count} 个币种 [{string.Join(", ", symbolList)}] [{interval}]...");

            // 1. 在线并发为所有币种预加载 1000 根最新 K 线建立基线
            var preloadTasks = symbolList.Select(async sym =>
            {
                var ctx = GetOrCreateContext(sym);
                ctx.Reset();

                try
                {
                    var initialKlines = await DataHelper.FetchKlinesFromApiAsync(sym, interval, limit: 1000);
                    lock (ctx.ReplayKlines)
                    {
                        ctx.ReplayKlines.Clear();
                        if (initialKlines != null && initialKlines.Length > 0)
                        {
                            ctx.ReplayKlines.AddRange(initialKlines);
                            ctx.CurrentKlineIndex = ctx.ReplayKlines.Count - 1;
                        }
                    }
                    ctx.UpdateDisplayPivotsAndTrendLines();
                }
                catch (Exception ex)
                {
                    Log($"⚠️ 币种 [{sym}] 在线预加载 1000 根 K线失败: {ex.Message}");
                }
            });

            await Task.WhenAll(preloadTasks);
            OnChartRefreshRequired?.Invoke();

            // 2. 建立多币种 WebSocket 长连接
            await _liveFeedManager.StartMultiLiveFeedAsync(symbolList, interval);
        }

        public Task StartLiveStreamAsync(string symbol, KlineInterval interval)
        {
            return StartMultiLiveStreamAsync(new[] { symbol }, interval);
        }

        /// <summary>
        /// 安全停止所有回放与多币种实盘引擎
        /// </summary>
        public void Stop()
        {
            _replayer.StopPlayback();
            _liveFeedManager.StopLiveFeedAsync().Wait();
            _batchQueueManager?.Dispose();
            _batchQueueManager = null;
            Mode = ExecutionMode.Idle;
        }

        #region 多币种实时事件处理回调

        private void Core_OnTickPushed(Tick tick)
        {
            ActiveContext.ProcessTick(tick);
            OnTickPushed?.Invoke(tick);
        }

        private void Core_OnKlinePushed(Kline currentKline, int currentFrameIndex, int totalFrames)
        {
            var ctx = ActiveContext;
            lock (ctx.ReplayKlines)
            {
                ctx.ReplayKlines.Add(currentKline);
                ctx.CurrentKlineIndex = ctx.ReplayKlines.Count - 1;
            }

            ctx.UpdateDisplayPivotsAndTrendLines();
            OnKlinePushed?.Invoke(currentKline);
            OnChartRefreshRequired?.Invoke();
        }

        private void Core_OnPlaybackCompleted()
        {
            Log("🎉 行情回演已全部完成！");
            Mode = ExecutionMode.Idle;
        }

        private void Core_OnMultiLiveKlinePushed(string symbol, Kline liveKline)
        {
            if (_symbolEngines.TryGetValue(symbol, out var ctx))
            {
                lock (ctx.ReplayKlines)
                {
                    if (ctx.ReplayKlines.Count > 0 && ctx.ReplayKlines.Last().OpenTime == liveKline.OpenTime)
                    {
                        ctx.ReplayKlines[ctx.ReplayKlines.Count - 1] = liveKline;
                    }
                    else
                    {
                        ctx.ReplayKlines.Add(liveKline);
                        if (ctx.ReplayKlines.Count > 500)
                        {
                            ctx.ReplayKlines.RemoveAt(0);
                        }
                    }
                    ctx.CurrentKlineIndex = ctx.ReplayKlines.Count - 1;
                }

                ctx.UpdateDisplayPivotsAndTrendLines();

                if (symbol.Equals(ActiveSymbol, StringComparison.OrdinalIgnoreCase))
                {
                    OnKlinePushed?.Invoke(liveKline);
                    OnChartRefreshRequired?.Invoke();
                }
            }
        }

        private void Core_OnMultiLiveTickPushed(string symbol, Tick liveTick)
        {
            if (_symbolEngines.TryGetValue(symbol, out var ctx))
            {
                ctx.ProcessTick(liveTick);

                if (symbol.Equals(ActiveSymbol, StringComparison.OrdinalIgnoreCase))
                {
                    OnTickPushed?.Invoke(liveTick);
                }
            }
        }

        #endregion

        #region 多币种全局统计统计计算

        public int GetTotalTradesCount()
        {
            return _symbolEngines.Values.Sum(ctx => ctx.Strategy.Trades.Count);
        }

        public decimal GetOverallWinRate()
        {
            int totalTrades = GetTotalTradesCount();
            if (totalTrades == 0) return 0m;
            int totalWins = _symbolEngines.Values.Sum(ctx => ctx.Strategy.Trades.Count(t => t.IsWin));
            return (decimal)totalWins / totalTrades * 100m;
        }

        public decimal GetOverallProfitPct()
        {
            return _symbolEngines.Values.Sum(ctx => ctx.Strategy.GetTotalProfitPct());
        }

        #endregion
    }
}
