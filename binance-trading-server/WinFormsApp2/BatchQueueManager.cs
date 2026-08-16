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
    /// 2. 多线程并发抓取后，强行按 OpenTime/Time 进行升序精准重排，绝对保证时间先后顺序正确；
    /// 3. 安全的生产者生命周期管控，杜绝由于网络慢导致提前返回空数据。
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

        private int _nextBatchToSchedule = 0;
        private volatile bool _producerFinished = false;

        public int MaxQueueCapacity { get; set; } = 5; // 默认队列最大容量为 5 批次
        public int BatchDays { get; set; } = 3;        // 默认每批次 3 天
        public int TotalBatches => _batchTimeWindows.Count;
        public int QueueCount => _preloadedQueue.Count;
        public bool IsProducerFinished => _producerFinished;

        public BatchQueueManager(
            string symbol,
            Binance.Net.Enums.KlineInterval interval,
            DateTime startDate,
            DateTime endDate,
            bool enableTickPush,
            int maxQueueCapacity = 5,
            int batchDays = 3,
            Action<string>? logger = null)
        {
            _symbol = symbol;
            _interval = interval;
            _enableTickPush = enableTickPush;
            MaxQueueCapacity = Math.Max(1, maxQueueCapacity);
            BatchDays = Math.Max(1, batchDays);
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
                DateTime bEnd = cur.AddDays(BatchDays - 1);
                if (bEnd > endDate) bEnd = endDate;

                _batchTimeWindows.Add((bStart, bEnd));
                cur = bEnd.AddDays(1);
            }
        }

        /// <summary>
        /// 启动 FIFO 队列管道后台生产者，并安全等待首批切片数据完成装载 (彻底解决网络慢导致提前返回空问题)
        /// </summary>
        public async Task<BatchDataChunk?> StartQueuePipelineAsync()
        {
            if (_batchTimeWindows.Count == 0) return null;

            _nextBatchToSchedule = 0;
            _producerFinished = false;
            _logger?.Invoke($"[FIFO 队列管道] 划分为 {TotalBatches} 个批次 (每批次 {BatchDays} 天)，队列上限限制为 {MaxQueueCapacity} 批 (独立后台抓取，流水线流式回放)...");

            // 启动生产者独立常驻后台循环
            _ = Task.Run(() => ProducerLoopAsync(_cts.Token));

            // 安全阻塞等待首批切片数据完成网络下载并成功入列
            while (!_cts.Token.IsCancellationRequested)
            {
                if (_preloadedQueue.TryDequeue(out var firstChunk))
                {
                    _logger?.Invoke($"[首批出队成功] 弹出 [批次 1/{TotalBatches}] 启动回放 (共 {firstChunk.Klines.Length} 根 K 线, 队列剩余: {_preloadedQueue.Count}/{MaxQueueCapacity})");
                    return firstChunk;
                }

                if (_producerFinished && _preloadedQueue.IsEmpty)
                {
                    _logger?.Invoke("[FIFO 队列提示] 后台生产者已结束，但未获取到任何历史切片数据。");
                    return null;
                }

                await Task.Delay(50, _cts.Token).ConfigureAwait(false);
            }

            return null;
        }

        /// <summary>
        /// 消费者出队获取下一批次数据 (支持流式无缝等待后台生产者装载)
        /// </summary>
        public async Task<BatchDataChunk?> DequeueNextBatchAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                if (_preloadedQueue.TryDequeue(out var chunk))
                {
                    // 内存回收：出队切换时触发 GC 快速回收已被消费的旧 Batch 数组内存，锁定内存平稳运行
                    GC.Collect(2, GCCollectionMode.Optimized, false, false);
                    _logger?.Invoke($"[队列出队成功] 弹出 [批次 {chunk.BatchIndex + 1}/{TotalBatches}] (共 {chunk.Klines.Length} 根 K 线, 队列剩余: {_preloadedQueue.Count}/{MaxQueueCapacity})");
                    return chunk;
                }

                if (_producerFinished && _preloadedQueue.IsEmpty)
                {
                    _logger?.Invoke($"[FIFO 队列管道完成] 所有 {TotalBatches} 个批次数据已全部回放完毕。");
                    return null;
                }

                // 若队列临时为空，等待后台生产者下载完成
                await Task.Delay(50, _cts.Token).ConfigureAwait(false);
            }

            return null;
        }

        /// <summary>
        /// 独立后台生产者循环：只关心队列是否达到 5 批次上限，完全不依赖、不等待 K 线回放进度
        /// </summary>
        private async Task ProducerLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && _nextBatchToSchedule < _batchTimeWindows.Count)
                {
                    // 仅关心队列是否满 5 批次：满 5 批次才暂停，不满 5 无视播放进度极速后台抓取！
                    while (_preloadedQueue.Count >= MaxQueueCapacity && !token.IsCancellationRequested)
                    {
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }

                    if (token.IsCancellationRequested || _nextBatchToSchedule >= _batchTimeWindows.Count)
                    {
                        break;
                    }

                    int loadingIndex = _nextBatchToSchedule;
                    _nextBatchToSchedule++;

                    _logger?.Invoke($"[FIFO 后台生产者] 正在抓取 [批次 {loadingIndex + 1}/{TotalBatches}] 数据中 (当前队列: {_preloadedQueue.Count}/{MaxQueueCapacity})...");

                    var chunk = await LoadChunkForBatchIndexAsync(loadingIndex).ConfigureAwait(false);

                    _preloadedQueue.Enqueue(chunk);
                    _logger?.Invoke($"[FIFO 队列已填充] [批次 {loadingIndex + 1}/{TotalBatches}] 成功入列 (K线: {chunk.Klines.Length} 根, Ticks: {chunk.Ticks.Length} 笔, 当前队列: {_preloadedQueue.Count}/{MaxQueueCapacity})");
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[FIFO 生产者异常] 加载批次失败: {ex.Message}");
            }
            finally
            {
                _producerFinished = true;
                _logger?.Invoke($"[FIFO 生产者完成] 所有批次数据生产流程已全部结束。");
            }
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
