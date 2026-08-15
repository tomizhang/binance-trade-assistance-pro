using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WinFormsApp2
{
    /// <summary>
    /// 最大容量为 5 的 FIFO 流式数据队列管理器 (Producer-Consumer Batch Queue Pipeline)
    /// 核心规则：
    /// 1. 后台抓取数据完全独立，无需等待当前 K 线/帧是否回放完成，仅关注队列是否达到上限 (5 批次)；
    /// 2. 多线程并发抓取后，强行按 OpenTime/Time 进行升序精准重排，绝对保证时间先后顺序正确。
    /// </summary>
    public class BatchQueueManager
    {
        private readonly List<(DateTime Start, DateTime End)> _batchTimeWindows = new List<(DateTime, DateTime)>();
        private readonly ConcurrentQueue<BatchDataChunk> _preloadedQueue = new ConcurrentQueue<BatchDataChunk>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private readonly string _symbol;
        private readonly Binance.Net.Enums.KlineInterval _interval;
        private readonly bool _enableTickPush;
        private readonly Action<string>? _logger;

        private int _nextBatchToLoadIndex = 0;

        public int MaxQueueCapacity { get; set; } = 5; // 默认队列最大容量为 5 批次
        public int BatchDays { get; set; } = 3;        // 默认每批次 3 天
        public int TotalBatches => _batchTimeWindows.Count;
        public int QueueCount => _preloadedQueue.Count;

        public BatchQueueManager(
            string symbol,
            Binance.Net.Enums.KlineInterval interval,
            DateTime startDate,
            DateTime endDate,
            bool enableTickPush,
            int maxQueueCapacity =2,
            int batchDays = 3,
            Action<string>? logger = null)
        {
            _symbol = symbol;
            _interval = interval;
            _enableTickPush = enableTickPush;
            MaxQueueCapacity = maxQueueCapacity;
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
        /// 启动 FIFO 队列管道后台生产者，完全独立常驻运行，不依赖回放进度
        /// </summary>
        public async Task<BatchDataChunk?> StartQueuePipelineAsync()
        {
            if (_batchTimeWindows.Count == 0) return null;

            _nextBatchToLoadIndex = 0;
            _logger?.Invoke($"[FIFO 队列管道] 划分为 {TotalBatches} 个批次，队列上限限制为 {MaxQueueCapacity} 批 (独立后台抓取，无需等待 K线回放完成)...");

            // 启动生产者独立常驻循环
            _ = Task.Run(() => ProducerLoopAsync(_cts.Token));

            // 等待队列填充首个批次
            while (_preloadedQueue.IsEmpty && _nextBatchToLoadIndex <= _batchTimeWindows.Count)
            {
                await Task.Delay(15);
            }

            if (_preloadedQueue.TryDequeue(out var firstChunk))
            {
                _logger?.Invoke($"[首批出队成功] 弹出 [批次 1/{TotalBatches}] 启动回放 (队列剩余: {_preloadedQueue.Count}/{MaxQueueCapacity})");
                return firstChunk;
            }

            return null;
        }

        /// <summary>
        /// 消费者出队获取下一批次数据 (0 延迟出队，队列不满 5 生产者独立自动恢复后台抓取)
        /// </summary>
        public async Task<BatchDataChunk?> DequeueNextBatchAsync()
        {
            if (_preloadedQueue.TryDequeue(out var chunk))
            {
                _logger?.Invoke($"[队列出队成功] 弹出 [批次 {chunk.BatchIndex + 1}/{TotalBatches}] (队列剩余: {_preloadedQueue.Count}/{MaxQueueCapacity})");
                return chunk;
            }

            if (_nextBatchToLoadIndex >= _batchTimeWindows.Count && _preloadedQueue.IsEmpty)
            {
                _logger?.Invoke($"[FIFO 队列管道完成] 所有 {TotalBatches} 个批次数据已全部播放完毕。");
                return null;
            }

            // 若队列临时为空，等待后台生产者填充
            while (_preloadedQueue.IsEmpty && _nextBatchToLoadIndex < _batchTimeWindows.Count)
            {
                await Task.Delay(15);
            }

            if (_preloadedQueue.TryDequeue(out chunk))
            {
                _logger?.Invoke($"[队列出队成功] 弹出 [批次 {chunk.BatchIndex + 1}/{TotalBatches}] (队列剩余: {_preloadedQueue.Count}/{MaxQueueCapacity})");
                return chunk;
            }

            return null;
        }

        /// <summary>
        /// 独立后台生产者循环：只关心队列是否达到 5 批次上限，完全不依赖、不等待 K 线回放进度
        /// </summary>
        private async Task ProducerLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _nextBatchToLoadIndex < _batchTimeWindows.Count)
            {
                try
                {
                    // 仅关心队列是否满 5 批次：满 5 批次才暂停，不满 5 无视播放进度极速后台抓取！
                    while (_preloadedQueue.Count >= MaxQueueCapacity && !token.IsCancellationRequested)
                    {
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }

                    if (token.IsCancellationRequested || _nextBatchToLoadIndex >= _batchTimeWindows.Count)
                    {
                        break;
                    }

                    int loadingIndex = _nextBatchToLoadIndex;
                    _nextBatchToLoadIndex++;

                    _logger?.Invoke($"[FIFO 后台生产者] 极速抓取 [批次 {loadingIndex + 1}/{TotalBatches}] 数据中 (无需等待 K线回放完成，当前队列: {_preloadedQueue.Count}/{MaxQueueCapacity})...");

                    var chunk = await LoadChunkForBatchIndexAsync(loadingIndex).ConfigureAwait(false);

                    _preloadedQueue.Enqueue(chunk);
                    _logger?.Invoke($"[FIFO 队列已填充] [批次 {loadingIndex + 1}/{TotalBatches}] 成功入列 (当前队列: {_preloadedQueue.Count}/{MaxQueueCapacity})");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[FIFO 生产者异常] 加载批次失败: {ex.Message}");
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
            }

            _logger?.Invoke($"[FIFO 生产者完成] 所有批次数据已成功预读入列。");
        }

        /// <summary>
        /// 多线程并发加载批次数据，并强制进行严密的 OpenTime 与 Time 升序重排，100% 保证时间顺序正确
        /// </summary>
        private async Task<BatchDataChunk> LoadChunkForBatchIndexAsync(int batchIndex)
        {
            var win = _batchTimeWindows[batchIndex];

            Task<Kline[]> klineTask = MultiThreadDownloader.DownloadKlinesParallelAsync(
                _symbol, _interval, win.Start, win.End, maxDegreeOfParallelism: 4, logger: null);

            Task<Tick[]> tickTask = _enableTickPush
                ? MultiThreadDownloader.DownloadTicksInSlicesParallelAsync(_symbol, win.Start, win.End, maxDegreeOfParallelism: 4, logger: null)
                : Task.FromResult(Array.Empty<Tick>());

            await Task.WhenAll(klineTask, tickTask).ConfigureAwait(false);

            Kline[] klines = await klineTask.ConfigureAwait(false);
            Tick[] ticks = await tickTask.ConfigureAwait(false);

            // 多线程抓取核心顺序保障：强行按照 OpenTime 与 Time 进行升序快速排序，绝对保证时间先后顺序正确
            if (klines != null && klines.Length > 1)
            {
                Array.Sort(klines, (a, b) => a.OpenTime.CompareTo(b.OpenTime));
            }

            if (ticks != null && ticks.Length > 1)
            {
                Array.Sort(ticks, (a, b) => a.Time.CompareTo(b.Time));
            }

            return new BatchDataChunk
            {
                BatchIndex = batchIndex,
                StartDate = win.Start,
                EndDate = win.End,
                Klines = klines ?? Array.Empty<Kline>(),
                Ticks = ticks ?? Array.Empty<Tick>()
            };
        }

        public void Stop()
        {
            _cts.Cancel();
        }
    }
}
