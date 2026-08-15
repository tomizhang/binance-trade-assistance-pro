using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WinFormsApp2
{
    public class BatchDataChunk
    {
        public int BatchIndex { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public Kline[] Klines { get; set; } = Array.Empty<Kline>();
        public Tick[] Ticks { get; set; } = Array.Empty<Tick>();
    }

    /// <summary>
    /// 分批次流式数据缓冲管理器 (Chunked Stream Data Manager)
    /// 按 3 天切片分批次提前异步预加载，双缓冲区无缝衔接，解决大内存溢出与卡顿问题
    /// </summary>
    public class ChunkedDataManager
    {
        private readonly List<(DateTime Start, DateTime End)> _batchTimeWindows = new List<(DateTime, DateTime)>();
        private readonly string _symbol;
        private readonly Binance.Net.Enums.KlineInterval _interval;
        private readonly bool _enableTickPush;
        private readonly Action<string>? _logger;

        private int _currentBatchIndex = 0;
        private BatchDataChunk? _currentChunk = null;
        private Task<BatchDataChunk>? _prefetchTask = null;

        public int BatchDays { get; set; } = 3; // 默认每 3 天为一个加载批次
        public int TotalBatches => _batchTimeWindows.Count;
        public int CurrentBatchIndex => _currentBatchIndex;

        public ChunkedDataManager(
            string symbol,
            Binance.Net.Enums.KlineInterval interval,
            DateTime startDate,
            DateTime endDate,
            bool enableTickPush,
            int batchDays = 3,
            Action<string>? logger = null)
        {
            _symbol = symbol;
            _interval = interval;
            _enableTickPush = enableTickPush;
            BatchDays = batchDays;
            _logger = logger;

            startDate = startDate.Date;
            endDate = endDate.Date;
            if (endDate < startDate)
            {
                var temp = startDate;
                startDate = endDate;
                endDate = temp;
            }

            // 按 batchDays (3天) 切割批次窗口
            DateTime cur = startDate;
            while (cur <= endDate)
            {
                DateTime bStart = cur;
                DateTime bEnd = cur.AddDays(batchDays - 1);
                if (bEnd > endDate) bEnd = endDate;

                _batchTimeWindows.Add((bStart, bEnd));
                cur = bEnd.AddDays(1);
            }
        }

        /// <summary>
        /// 初始化加载首批 3 天数据，并后台立即启动预加载第二批数据
        /// </summary>
        public async Task<BatchDataChunk?> InitializeAsync()
        {
            _currentBatchIndex = 0;
            if (_batchTimeWindows.Count == 0) return null;

            _logger?.Invoke($"[分批流式预读引擎] 划分为 {TotalBatches} 个批次 (每批次 {BatchDays} 天)，开始加载 [批次 1/{TotalBatches}]...");

            // 1. 同步载入首批 3 天数据
            _currentChunk = await LoadChunkForBatchIndexAsync(0);

            // 2. 立即启动后台预加载第二批 3 天数据 (双缓冲区机制)
            StartPrefetchNextBatch(1);

            return _currentChunk;
        }

        /// <summary>
        /// 提前预读触发器：当当前批次播放达到门槛 (如 60%) 时，确保下一批次已提前在后台加载中
        /// </summary>
        public void EnsurePrefetchNextBatch()
        {
            int nextIndex = _currentBatchIndex + 1;
            if (nextIndex < _batchTimeWindows.Count && _prefetchTask == null)
            {
                _logger?.Invoke($"[提前预读门槛触发] 提前启动后台线程预读 [批次 {nextIndex + 1}/{TotalBatches}] 数据至内存缓冲区...");
                _prefetchTask = Task.Run(() => LoadChunkForBatchIndexAsync(nextIndex));
            }
        }

        /// <summary>
        /// 当上一批次播放完毕时，无缝切换获取下一批次数据，并自动预加载下下一批次
        /// </summary>
        public async Task<BatchDataChunk?> MoveToNextBatchAsync()
        {
            _currentBatchIndex++;
            if (_currentBatchIndex >= _batchTimeWindows.Count)
            {
                _logger?.Invoke($"[分批流式预读引擎] 所有 {TotalBatches} 个批次数据已全部完成播放。");
                return null;
            }

            _logger?.Invoke($"[分批无缝接力] 切换至 [批次 {_currentBatchIndex + 1}/{TotalBatches}] 数据...");

            if (_prefetchTask != null)
            {
                _currentChunk = await _prefetchTask;
                _prefetchTask = null;
            }
            else
            {
                _currentChunk = await LoadChunkForBatchIndexAsync(_currentBatchIndex);
            }

            // 立即启动后续批次预加载
            StartPrefetchNextBatch(_currentBatchIndex + 1);

            return _currentChunk;
        }

        private void StartPrefetchNextBatch(int nextIndex)
        {
            if (nextIndex < _batchTimeWindows.Count)
            {
                _logger?.Invoke($"[双缓冲区提前预读] 启动后台线程提取下一批次 [批次 {nextIndex + 1}/{TotalBatches}] 数据至内存预读区...");
                _prefetchTask = Task.Run(() => LoadChunkForBatchIndexAsync(nextIndex));
            }
            else
            {
                _prefetchTask = null;
            }
        }

        private async Task<BatchDataChunk> LoadChunkForBatchIndexAsync(int batchIndex)
        {
            if (batchIndex < 0 || batchIndex >= _batchTimeWindows.Count)
            {
                return new BatchDataChunk { BatchIndex = batchIndex };
            }

            var win = _batchTimeWindows[batchIndex];

            // 并发获取该 3 天切片内的 K线 与 Tick 数据
            Task<Kline[]> klineTask = MultiThreadDownloader.DownloadKlinesParallelAsync(
                _symbol, _interval, win.Start, win.End, maxDegreeOfParallelism: 4, logger: null);

            Task<Tick[]> tickTask = _enableTickPush
                ? MultiThreadDownloader.DownloadTicksInSlicesParallelAsync(_symbol, win.Start, win.End, maxDegreeOfParallelism: 4, logger: null)
                : Task.FromResult(Array.Empty<Tick>());

            await Task.WhenAll(klineTask, tickTask);

            Kline[] klines = await klineTask;
            Tick[] ticks = await tickTask;

            _logger?.Invoke($"[批次 {batchIndex + 1}/{TotalBatches} 预读就绪] 窗口 [{win.Start:yyyy-MM-dd} ~ {win.End:yyyy-MM-dd}] K线: {klines.Length} 帧, Tick: {ticks.Length} 条 (已在内存中等待，0 延迟切换)。");

            return new BatchDataChunk
            {
                BatchIndex = batchIndex,
                StartDate = win.Start,
                EndDate = win.End,
                Klines = klines,
                Ticks = ticks
            };
        }
    }
}
