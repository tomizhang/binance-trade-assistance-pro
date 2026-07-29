using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace WinFormsApp1
{
    /// <summary>
    /// 数据获取状态/进度报告
    /// </summary>
    public class FetchStatusReport
    {
        public string Symbol { get; set; } = string.Empty;
        public FuturesKlineInterval Interval { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public bool IsInFlightWaiting { get; set; }
        public string Message { get; set; } = string.Empty;
        public int FetchedCount { get; set; }
    }

    /// <summary>
    /// 币种数据提供助手 (Symbol Data Provider)
    /// 职责：
    /// 1. 提供指定币种、指定周期、指定时间段的 K 线历史数据获取。
    /// 2. 调用 BinanceFuturesHistoryHelper 多线程并发获取。
    /// 3. 支持【生产者-消费者模式】有序流式推送，保障队列时间升序不乱序。
    /// 4. 并发防重/请求合并 (In-Flight Task Coalescing)：若相同参数的数据正在获取中，后续请求自动等待同一 Task 完成。
    /// 5. 支持 Memory Cache (内存缓存) 加速。
    /// </summary>
    public class SymbolDataProvider
    {
        private readonly BinanceFuturesHistoryHelper _historyHelper;
        private readonly int _maxDegreeOfParallelism;

        // 正在进行中的请求 Task 字典 (实现并发合并去重：SingleFlight 模式)
        private readonly ConcurrentDictionary<string, Task<List<BinanceFuturesKlineItem>>> _inFlightTasks = new(StringComparer.OrdinalIgnoreCase);

        // 内存缓存字典: Key 为 "SYMBOL_INTERVAL", Value 为按时间排序的 K线列表
        private readonly ConcurrentDictionary<string, List<BinanceFuturesKlineItem>> _memoryCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 构造函数
        /// </summary>
        public SymbolDataProvider(BinanceFuturesHistoryHelper? historyHelper = null, int maxDegreeOfParallelism = 5)
        {
            _historyHelper = historyHelper ?? new BinanceFuturesHistoryHelper();
            _maxDegreeOfParallelism = maxDegreeOfParallelism;
        }

        /// <summary>
        /// 核心接口：获取币种指定周期和时间段的 K 线数据
        /// </summary>
        public async Task<List<BinanceFuturesKlineItem>> GetSymbolDataAsync(
            string symbol,
            FuturesKlineInterval interval,
            DateTime startTime,
            DateTime endTime,
            bool useCache = true,
            IProgress<FetchStatusReport>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                throw new ArgumentException("Symbol 不能为空", nameof(symbol));

            if (startTime >= endTime)
                return new List<BinanceFuturesKlineItem>();

            string formattedSymbol = symbol.Trim().ToUpperInvariant();

            // 1. 检查内存缓存
            string cacheKey = $"{formattedSymbol}_{interval.ToIntervalString()}";
            if (useCache && TryGetFromMemoryCache(cacheKey, startTime, endTime, out var cachedKlines))
            {
                progress?.Report(new FetchStatusReport
                {
                    Symbol = formattedSymbol,
                    Interval = interval,
                    StartTime = startTime,
                    EndTime = endTime,
                    IsInFlightWaiting = false,
                    Message = "直接从内存缓存命中返回",
                    FetchedCount = cachedKlines.Count
                });
                return cachedKlines;
            }

            // 2. 构造防重 Key
            long startMs = new DateTimeOffset(startTime.ToUniversalTime()).ToUnixTimeMilliseconds();
            long endMs = new DateTimeOffset(endTime.ToUniversalTime()).ToUnixTimeMilliseconds();
            string requestKey = $"{formattedSymbol}_{interval.ToIntervalString()}_{startMs}_{endMs}";

            // 3. 检查是否有相同请求正在下载中
            bool isWaitingInFlight = false;
            Task<List<BinanceFuturesKlineItem>> fetchTask = _inFlightTasks.GetOrAdd(requestKey, key =>
            {
                return FetchDataInternalAsync(formattedSymbol, interval, startTime, endTime, cacheKey, cancellationToken);
            });

            if (_inFlightTasks.TryGetValue(requestKey, out var existingTask) && existingTask != fetchTask)
            {
                isWaitingInFlight = true;
            }

            if (isWaitingInFlight)
            {
                progress?.Report(new FetchStatusReport
                {
                    Symbol = formattedSymbol,
                    Interval = interval,
                    StartTime = startTime,
                    EndTime = endTime,
                    IsInFlightWaiting = true,
                    Message = "相同数据正在由另一个任务下载中，当前请求进入排队等待...",
                    FetchedCount = 0
                });
            }

            try
            {
                List<BinanceFuturesKlineItem> result = await fetchTask.ConfigureAwait(false);

                progress?.Report(new FetchStatusReport
                {
                    Symbol = formattedSymbol,
                    Interval = interval,
                    StartTime = startTime,
                    EndTime = endTime,
                    IsInFlightWaiting = isWaitingInFlight,
                    Message = "获取成功",
                    FetchedCount = result.Count
                });

                return FilterByTimeRange(result, startTime, endTime);
            }
            finally
            {
                _inFlightTasks.TryRemove(requestKey, out _);
            }
        }

        /// <summary>
        /// 【生产者-消费者模式 (流式接口)】多线程并发生产，严格按时间顺序 (时间升序) 向消费者实时 yield 吐出 K 线
        /// 适合在大数据集下载时，边下载边在图表/策略上按时间顺序推进与消费。
        /// </summary>
        public async IAsyncEnumerable<BinanceFuturesKlineItem> StreamSymbolDataChronologicalAsync(
            string symbol,
            FuturesKlineInterval interval,
            DateTime startTime,
            DateTime endTime,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(symbol) || startTime >= endTime)
                yield break;

            string formattedSymbol = symbol.Trim().ToUpperInvariant();

            // 调用帮助类有序生产者-消费者流
            await foreach (var kline in _historyHelper.StreamKlinesChronologicalAsync(
                formattedSymbol, interval, startTime, endTime, _maxDegreeOfParallelism, cancellationToken).ConfigureAwait(false))
            {
                yield return kline;
            }
        }

        /// <summary>
        /// 【生产者-消费者模式 (回调接口)】多线程并发生产，按时间顺序回调 onKlineConsumed 消费者
        /// </summary>
        public async Task ConsumeSymbolDataChronologicalAsync(
            string symbol,
            FuturesKlineInterval interval,
            DateTime startTime,
            DateTime endTime,
            Action<BinanceFuturesKlineItem> onKlineConsumed,
            CancellationToken cancellationToken = default)
        {
            if (onKlineConsumed == null) return;

            await foreach (var kline in StreamSymbolDataChronologicalAsync(symbol, interval, startTime, endTime, cancellationToken).ConfigureAwait(false))
            {
                onKlineConsumed(kline);
            }
        }

        /// <summary>
        /// 回调函数方式接口 (Callback Style)
        /// </summary>
        public void GetSymbolDataWithCallback(
            string symbol,
            FuturesKlineInterval interval,
            DateTime startTime,
            DateTime endTime,
            Action<List<BinanceFuturesKlineItem>> onSuccess,
            Action<Exception>? onError = null,
            bool useCache = true,
            CancellationToken cancellationToken = default)
        {
            Task.Run(async () =>
            {
                try
                {
                    var data = await GetSymbolDataAsync(symbol, interval, startTime, endTime, useCache, null, cancellationToken).ConfigureAwait(false);
                    onSuccess?.Invoke(data);
                }
                catch (Exception ex)
                {
                    onError?.Invoke(ex);
                }
            }, cancellationToken);
        }

        /// <summary>
        /// 清理指定币种和周期的内存缓存
        /// </summary>
        public void ClearCache(string? symbol = null, FuturesKlineInterval? interval = null)
        {
            if (string.IsNullOrEmpty(symbol) && !interval.HasValue)
            {
                _memoryCache.Clear();
                return;
            }

            string filterKey = string.Empty;
            if (!string.IsNullOrEmpty(symbol)) filterKey += symbol.Trim().ToUpperInvariant();
            if (interval.HasValue) filterKey += $"_{interval.Value.ToIntervalString()}";

            var keysToRemove = _memoryCache.Keys.Where(k => k.Contains(filterKey)).ToList();
            foreach (var key in keysToRemove)
            {
                _memoryCache.TryRemove(key, out _);
            }
        }

        private async Task<List<BinanceFuturesKlineItem>> FetchDataInternalAsync(
            string symbol,
            FuturesKlineInterval interval,
            DateTime startTime,
            DateTime endTime,
            string cacheKey,
            CancellationToken cancellationToken)
        {
            List<BinanceFuturesKlineItem> fetchedData = await _historyHelper.GetKlinesRangeParallelAsync(
                symbol,
                interval,
                startTime,
                endTime,
                maxDegreeOfParallelism: _maxDegreeOfParallelism,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (fetchedData != null && fetchedData.Count > 0)
            {
                _memoryCache.AddOrUpdate(cacheKey,
                    fetchedData,
                    (key, existingList) =>
                    {
                        lock (existingList)
                        {
                            return existingList.Concat(fetchedData)
                                .GroupBy(k => k.OpenTimeMs)
                                .Select(g => g.First())
                                .OrderBy(k => k.OpenTimeMs)
                                .ToList();
                        }
                    });
            }

            return fetchedData ?? new List<BinanceFuturesKlineItem>();
        }

        private bool TryGetFromMemoryCache(
            string cacheKey,
            DateTime startTime,
            DateTime endTime,
            out List<BinanceFuturesKlineItem> cachedResult)
        {
            cachedResult = new List<BinanceFuturesKlineItem>();

            if (!_memoryCache.TryGetValue(cacheKey, out var fullList) || fullList == null || fullList.Count == 0)
            {
                return false;
            }

            long startMs = new DateTimeOffset(startTime.ToUniversalTime()).ToUnixTimeMilliseconds();
            long endMs = new DateTimeOffset(endTime.ToUniversalTime()).ToUnixTimeMilliseconds();

            lock (fullList)
            {
                long cacheMinMs = fullList.First().OpenTimeMs;
                long cacheMaxMs = fullList.Last().CloseTimeMs;

                if (cacheMinMs <= startMs && cacheMaxMs >= endMs)
                {
                    cachedResult = fullList.Where(k => k.OpenTimeMs >= startMs && k.OpenTimeMs <= endMs).ToList();
                    return true;
                }
            }

            return false;
        }

        private static List<BinanceFuturesKlineItem> FilterByTimeRange(List<BinanceFuturesKlineItem> items, DateTime startTime, DateTime endTime)
        {
            long startMs = new DateTimeOffset(startTime.ToUniversalTime()).ToUnixTimeMilliseconds();
            long endMs = new DateTimeOffset(endTime.ToUniversalTime()).ToUnixTimeMilliseconds();
            return items.Where(k => k.OpenTimeMs >= startMs && k.OpenTimeMs <= endMs).ToList();
        }
    }
}
