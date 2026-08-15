using Binance.Net.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WinFormsApp2
{
    public enum ExecutionMode
    {
        Idle,
        BacktestReplay,
        LiveStream
    }

    /// <summary>
    /// 独立无界面核心交易与回演引擎 (Headless Trading & Replay Engine)
    /// 0 依赖 Windows GUI / WinForms，可独立在 Linux 各种无界面 Server 系统下直接部署运行
    /// </summary>
    public class TradingServerEngine
    {
        private readonly MarketReplayer _replayer = new MarketReplayer();
        private readonly LiveFeedManager _liveFeedManager = new LiveFeedManager();
        private readonly TrendLineStrategy _strategy = new TrendLineStrategy();
        private BatchQueueManager? _batchQueueManager;

        private readonly List<Kline> _replayKlines = new List<Kline>(5000);
        private readonly List<Kline> _combinedHistoryList = new List<Kline>(1500);
        private Kline[] _warmupKlinesBuffer = Array.Empty<Kline>();
        private readonly Kline[] _displayKlinesBuffer = new Kline[500];

        private List<PivotPoint> _currentActivePivots = new List<PivotPoint>();
        private List<TrendLine> _currentActiveTrendLines = new List<TrendLine>();

        private int _currentKlineIndex = 0;
        private string _currentSymbol = "BTCUSDT";

        public ExecutionMode Mode { get; private set; } = ExecutionMode.Idle;
        public TrendLineStrategy Strategy => _strategy;
        public MarketReplayer Replayer => _replayer;
        public LiveFeedManager LiveFeedManager => _liveFeedManager;
        public bool IsRunning => Mode != ExecutionMode.Idle;

        public IReadOnlyList<Kline> DisplayKlinesBuffer => _displayKlinesBuffer;
        public IReadOnlyList<PivotPoint> ActivePivots => _currentActivePivots;
        public IReadOnlyList<TrendLine> ActiveTrendLines => _currentActiveTrendLines;
        public string CurrentSymbol => _currentSymbol;

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
            _liveFeedManager.OnLiveKlinePushed += Core_OnLiveKlinePushed;
            _liveFeedManager.OnLiveTickPushed += Core_OnLiveTickPushed;

            _strategy.OnTradeOpened += trade => OnTradeOpened?.Invoke(trade);
            _strategy.OnTradeClosed += trade => OnTradeClosed?.Invoke(trade);
        }

        public void Log(string msg)
        {
            OnLog?.Invoke(msg);
        }

        /// <summary>
        /// 更新与提取当前画图视口枢轴点与趋势线
        /// </summary>
        public void UpdateDisplayPivotsAndTrendLines()
        {
            int sampleSize = 0;
            lock (_replayKlines)
            {
                _combinedHistoryList.Clear();
                if (_warmupKlinesBuffer != null && _warmupKlinesBuffer.Length > 0)
                {
                    _combinedHistoryList.AddRange(_warmupKlinesBuffer);
                }
                _combinedHistoryList.AddRange(_replayKlines);

                int totalCombined = _combinedHistoryList.Count;
                if (totalCombined < 7)
                {
                    _currentActivePivots.Clear();
                    _currentActiveTrendLines.Clear();
                    return;
                }

                sampleSize = Math.Min(totalCombined, 500);
                _combinedHistoryList.CopyTo(totalCombined - sampleSize, _displayKlinesBuffer, 0, sampleSize);
            }

            Kline[] sampleSlice = new Kline[sampleSize];
            Array.Copy(_displayKlinesBuffer, 0, sampleSlice, 0, sampleSize);

            _currentActivePivots = PivotHelper.CalculatePeaksCombinedFast(sampleSlice, leftBars: 3, rightBars: 3);
            _currentActiveTrendLines = TrendLineHelper.GenerateTrendLinesFromPivots(sampleSlice, _currentActivePivots, filterPenetrated: true);
        }

        /// <summary>
        /// 启动历史数据回演引擎 (可被 WinForms 或 Linux 命令行/Web 控制器调用)
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

            _currentSymbol = symbol;
            Mode = ExecutionMode.BacktestReplay;
            _strategy.Reset();

            Log($"▶ [引擎启动] 开始初始化行情回放: {symbol} | {interval} | {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}");

            // 1. 初始化 FIFO 多线程后台下载管道
            _batchQueueManager = new BatchQueueManager(symbol, interval, startDate, endDate, batchDays: 1, prefetchQueueCapacity: 5);
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
            _warmupKlinesBuffer = Array.Empty<Kline>();
            if (enableWarmup)
            {
                try
                {
                    DateTime warmupEndDate = startDate.AddTicks(-1);
                    Log($"[API 预热开启] 准备获取 [{symbol}] [{interval}] 起点前 1000 根历史预热 K 线 (截止 {warmupEndDate:yyyy-MM-dd HH:mm:ss})...");
                    var fetchedWarmup = await DataHelper.FetchKlinesFromApiAsync(symbol, interval, endTime: warmupEndDate, limit: 1000);
                    if (fetchedWarmup != null && fetchedWarmup.Length > 0)
                    {
                        _warmupKlinesBuffer = fetchedWarmup;
                        Log($"[API 预热成功] 成功装载 {fetchedWarmup.Length} 根历史预热 K 线。");
                    }
                }
                catch (Exception apiEx)
                {
                    Log($"[API 预热提示] 获取预热 K 线未成功 ({apiEx.Message})，自动回退。");
                }
            }

            lock (_replayKlines)
            {
                _replayKlines.Clear();
            }

            UpdateDisplayPivotsAndTrendLines();
            OnChartRefreshRequired?.Invoke();

            _replayer.StartPlayback(klines, ticks, enableTickPush, intervalMs);
        }

        /// <summary>
        /// 启动币安实盘 WebSocket 行情接口 (可被 Linux 命令行/Web 控制器直接部署运行)
        /// </summary>
        public async Task StartLiveStreamAsync(string symbol, KlineInterval interval)
        {
            Stop();

            _currentSymbol = symbol;
            Mode = ExecutionMode.LiveStream;
            _strategy.Reset();

            Log($"📡 [实盘引擎启动] 正在连接币安 WebSocket 实盘流 [{symbol}] [{interval}]...");

            // 1. 优先预加载 1000 根最新 K 线
            var initialKlines = await DataHelper.FetchKlinesFromApiAsync(symbol, interval, limit: 1000);
            lock (_replayKlines)
            {
                _replayKlines.Clear();
                if (initialKlines != null && initialKlines.Length > 0)
                {
                    _replayKlines.AddRange(initialKlines);
                    _currentKlineIndex = _replayKlines.Count - 1;
                }
            }

            _warmupKlinesBuffer = Array.Empty<Kline>();
            UpdateDisplayPivotsAndTrendLines();
            OnChartRefreshRequired?.Invoke();

            // 2. 建立实盘连接
            await _liveFeedManager.StartLiveFeedAsync(symbol, interval);
        }

        /// <summary>
        /// 安全停止回放或实盘
        /// </summary>
        public void Stop()
        {
            _replayer.StopPlayback();
            _liveFeedManager.StopLiveFeedAsync().Wait();
            _batchQueueManager?.Dispose();
            _batchQueueManager = null;
            Mode = ExecutionMode.Idle;
        }

        #region 底层引擎回调

        private void Core_OnTickPushed(Tick tick)
        {
            if (_strategy.Params.Enabled && _currentActiveTrendLines != null && _currentActiveTrendLines.Count > 0)
            {
                Kline currentKline = default;
                int currentSampleIndex = 0;
                lock (_replayKlines)
                {
                    if (_currentKlineIndex >= 0 && _currentKlineIndex < _replayKlines.Count)
                    {
                        currentKline = _replayKlines[_currentKlineIndex];
                    }
                    currentSampleIndex = Math.Max(0, Math.Min(_combinedHistoryList.Count, 500) - 1);
                }
                _strategy.ProcessTick(tick, currentSampleIndex, _currentActiveTrendLines, currentKline);
            }
            OnTickPushed?.Invoke(tick);
        }

        private void Core_OnKlinePushed(Kline currentKline, int currentFrameIndex, int totalFrames)
        {
            lock (_replayKlines)
            {
                _replayKlines.Add(currentKline);
                _currentKlineIndex = _replayKlines.Count - 1;
            }

            UpdateDisplayPivotsAndTrendLines();
            OnKlinePushed?.Invoke(currentKline);
            OnChartRefreshRequired?.Invoke();
        }

        private void Core_OnPlaybackCompleted()
        {
            Log("🎉 行情回演已全部完成！");
            Mode = ExecutionMode.Idle;
        }

        private void Core_OnLiveKlinePushed(Kline liveKline)
        {
            lock (_replayKlines)
            {
                if (_replayKlines.Count > 0 && _replayKlines.Last().OpenTime == liveKline.OpenTime)
                {
                    _replayKlines[_replayKlines.Count - 1] = liveKline;
                }
                else
                {
                    _replayKlines.Add(liveKline);
                    if (_replayKlines.Count > 500)
                    {
                        _replayKlines.RemoveAt(0);
                    }
                }
                _currentKlineIndex = _replayKlines.Count - 1;
            }

            UpdateDisplayPivotsAndTrendLines();
            OnKlinePushed?.Invoke(liveKline);
            OnChartRefreshRequired?.Invoke();
        }

        private void Core_OnLiveTickPushed(Tick tick)
        {
            Core_OnTickPushed(tick);
        }

        #endregion
    }
}
