using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinFormsApp1
{
    /// <summary>
    /// 币安合约历史 K 线数据获取帮助类
    /// 接口规范参考 Binance USDT-M Futures API: GET /fapi/v1/klines
    /// 支持单条/多条/时间段串行、多线程并发获取以及【生产者-消费者模式】有序数据流推送
    /// </summary>
    public class BinanceFuturesHistoryHelper
    {
        private static readonly HttpClient SharedHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        /// <summary>
        /// 默认构造函数，使用币安 USDT-M 合约官方 REST Endpoint (https://fapi.binance.com)
        /// </summary>
        /// <param name="baseUrl">自定义 API 根地址 (如需使用代理节点或测试网可以传入)</param>
        /// <param name="customHttpClient">自定义 HttpClient 实例</param>
        public BinanceFuturesHistoryHelper(string baseUrl = "https://fapi.binance.com", HttpClient? customHttpClient = null)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _httpClient = customHttpClient ?? SharedHttpClient;
        }

        /// <summary>
        /// 单次获取指定币种与周期的 K 线数据 (单次最多 1500 条)
        /// </summary>
        public async Task<List<BinanceFuturesKlineItem>> GetKlinesAsync(
            string symbol,
            FuturesKlineInterval interval,
            int limit = 500,
            DateTime? startTime = null,
            DateTime? endTime = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                throw new ArgumentException("交易对 symbol 不能为空", nameof(symbol));

            int realLimit = Math.Clamp(limit, 1, 1500);
            string intervalStr = interval.ToIntervalString();
            string formattedSymbol = symbol.Trim().ToUpperInvariant();

            string url = $"{_baseUrl}/fapi/v1/klines?symbol={formattedSymbol}&interval={intervalStr}&limit={realLimit}";

            if (startTime.HasValue)
            {
                long startMs = new DateTimeOffset(startTime.Value.ToUniversalTime()).ToUnixTimeMilliseconds();
                url += $"&startTime={startMs}";
            }

            if (endTime.HasValue)
            {
                long endMs = new DateTimeOffset(endTime.Value.ToUniversalTime()).ToUnixTimeMilliseconds();
                url += $"&endTime={endMs}";
            }

            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, default, cancellationToken).ConfigureAwait(false);

            var list = new List<BinanceFuturesKlineItem>();
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() < 11)
                    continue;

                var kline = ParseKlineElement(element);
                if (kline != null)
                {
                    list.Add(kline);
                }
            }

            return list;
        }

        /// <summary>
        /// 串行顺序获取指定时间范围内的全部历史 K 线数据
        /// </summary>
        public async Task<List<BinanceFuturesKlineItem>> GetKlinesRangeAsync(
            string symbol,
            FuturesKlineInterval interval,
            DateTime startTime,
            DateTime endTime,
            int delayBetweenRequestsMs = 50,
            CancellationToken cancellationToken = default)
        {
            if (startTime >= endTime)
                return new List<BinanceFuturesKlineItem>();

            var allKlines = new List<BinanceFuturesKlineItem>();
            DateTime currentStart = startTime;
            long endMs = new DateTimeOffset(endTime.ToUniversalTime()).ToUnixTimeMilliseconds();

            while (currentStart < endTime && !cancellationToken.IsCancellationRequested)
            {
                var batch = await GetKlinesAsync(
                    symbol,
                    interval,
                    limit: 1500,
                    startTime: currentStart,
                    endTime: endTime,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (batch == null || batch.Count == 0)
                {
                    break;
                }

                if (allKlines.Count > 0 && batch[0].OpenTimeMs <= allKlines.Last().OpenTimeMs)
                {
                    batch.RemoveAll(k => k.OpenTimeMs <= allKlines.Last().OpenTimeMs);
                }

                if (batch.Count == 0)
                {
                    break;
                }

                allKlines.AddRange(batch);

                long lastCloseTimeMs = batch.Last().CloseTimeMs;
                if (lastCloseTimeMs >= endMs)
                {
                    break;
                }

                currentStart = DateTimeOffset.FromUnixTimeMilliseconds(lastCloseTimeMs + 1).UtcDateTime;

                if (delayBetweenRequestsMs > 0)
                {
                    await Task.Delay(delayBetweenRequestsMs, cancellationToken).ConfigureAwait(false);
                }
            }

            return allKlines;
        }

        /// <summary>
        /// 【多线程并发】按时间片切分，并发获取大时间跨度的历史 K 线数据
        /// </summary>
        public async Task<List<BinanceFuturesKlineItem>> GetKlinesRangeParallelAsync(
            string symbol,
            FuturesKlineInterval interval,
            DateTime startTime,
            DateTime endTime,
            int maxDegreeOfParallelism = 5,
            CancellationToken cancellationToken = default)
        {
            if (startTime >= endTime)
                return new List<BinanceFuturesKlineItem>();

            double intervalMs = interval.ToTimeSpan().TotalMilliseconds;
            if (intervalMs <= 0) intervalMs = 60_000;
            long chunkSpanMs = (long)(intervalMs * 1500);

            var timeChunks = new List<(DateTime start, DateTime end)>();
            DateTime current = startTime;
            while (current < endTime)
            {
                DateTime chunkEnd = current.AddMilliseconds(chunkSpanMs);
                if (chunkEnd > endTime) chunkEnd = endTime;
                timeChunks.Add((current, chunkEnd));
                current = chunkEnd;
            }

            using var semaphore = new SemaphoreSlim(Math.Max(1, maxDegreeOfParallelism));
            var tasks = timeChunks.Select(async chunk =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await GetKlinesAsync(
                        symbol,
                        interval,
                        limit: 1500,
                        startTime: chunk.start,
                        endTime: chunk.end,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    return new List<BinanceFuturesKlineItem>();
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            return results
                .SelectMany(r => r)
                .GroupBy(k => k.OpenTimeMs)
                .Select(g => g.First())
                .OrderBy(k => k.OpenTimeMs)
                .ToList();
        }

        /// <summary>
        /// 【生产者-消费者模式】：并发多线程生产 (HTTP下载)，严格按【时间顺序】向消费者流式推送 (Stream / IAsyncEnumerable)
        /// 特性：生产者并行下载提高效率，消费者按时间序 (Chunk 0 -> Chunk 1 -> Chunk 2) 依次接收，绝对保证时间顺序不颠倒。
        /// </summary>
        /// <param name="symbol">合约交易对标识，如 "BTCUSDT"</param>
        /// <param name="interval">K线周期</param>
        /// <param name="startTime">起始时间</param>
        /// <param name="endTime">结束时间</param>
        /// <param name="maxDegreeOfParallelism">并发生产者线程数</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>按时间升序流式返回每条 K 线</returns>
        public async IAsyncEnumerable<BinanceFuturesKlineItem> StreamKlinesChronologicalAsync(
            string symbol,
            FuturesKlineInterval interval,
            DateTime startTime,
            DateTime endTime,
            int maxDegreeOfParallelism = 5,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (startTime >= endTime)
                yield break;

            double intervalMs = interval.ToTimeSpan().TotalMilliseconds;
            if (intervalMs <= 0) intervalMs = 60_000;
            long chunkSpanMs = (long)(intervalMs * 1500);

            // 1. 切分有序号的时间块 [Chunk 0, Chunk 1, Chunk 2 ...]
            var timeChunks = new List<(int Index, DateTime start, DateTime end)>();
            DateTime current = startTime;
            int chunkIndex = 0;

            while (current < endTime)
            {
                DateTime chunkEnd = current.AddMilliseconds(chunkSpanMs);
                if (chunkEnd > endTime) chunkEnd = endTime;
                timeChunks.Add((chunkIndex++, current, chunkEnd));
                current = chunkEnd;
            }

            int totalChunks = timeChunks.Count;
            if (totalChunks == 0) yield break;

            // 2. 生产者：多线程并发触发 HTTP 下载任务，存入按索引排序的数组中
            using var semaphore = new SemaphoreSlim(Math.Max(1, maxDegreeOfParallelism));
            var chunkTasks = new Task<List<BinanceFuturesKlineItem>>[totalChunks];

            for (int i = 0; i < totalChunks; i++)
            {
                var chunk = timeChunks[i];
                chunkTasks[i] = Task.Run(async () =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var list = await GetKlinesAsync(
                            symbol,
                            interval,
                            limit: 1500,
                            startTime: chunk.start,
                            endTime: chunk.end,
                            cancellationToken: cancellationToken).ConfigureAwait(false);

                        return list ?? new List<BinanceFuturesKlineItem>();
                    }
                    catch
                    {
                        return new List<BinanceFuturesKlineItem>();
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellationToken);
            }

            // 3. 消费者：严格按 Index 从 0 到 totalChunks - 1 依次等待并吐出数据
            // 确保输出流时间严格递增！
            long lastYieldedOpenTimeMs = -1;

            for (int i = 0; i < totalChunks; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                // 消费者等待第 i 个生产者任务完成 (即便后续的任务先完成，也会在这里被正确按顺序阻塞)
                List<BinanceFuturesKlineItem> chunkKlines = await chunkTasks[i].ConfigureAwait(false);

                // 组内根据开盘时间排序
                var orderedChunk = chunkKlines.OrderBy(k => k.OpenTimeMs);

                foreach (var kline in orderedChunk)
                {
                    // 严格去重及时间递增校验
                    if (kline.OpenTimeMs > lastYieldedOpenTimeMs)
                    {
                        lastYieldedOpenTimeMs = kline.OpenTimeMs;
                        yield return kline;
                    }
                }
            }
        }

        /// <summary>
        /// 【生产者-消费者模式 + 回调通知】：多线程并发生产，按时间顺序推送给回调函数处理
        /// </summary>
        public async Task ConsumeKlinesChronologicalAsync(
            string symbol,
            FuturesKlineInterval interval,
            DateTime startTime,
            DateTime endTime,
            Action<BinanceFuturesKlineItem> onKlineConsumed,
            int maxDegreeOfParallelism = 5,
            CancellationToken cancellationToken = default)
        {
            if (onKlineConsumed == null) return;

            await foreach (var kline in StreamKlinesChronologicalAsync(
                symbol, interval, startTime, endTime, maxDegreeOfParallelism, cancellationToken).ConfigureAwait(false))
            {
                onKlineConsumed(kline);
            }
        }

        /// <summary>
        /// 【多线程并发】并发获取多个不同交易对的历史 K 线数据
        /// </summary>
        public async Task<Dictionary<string, List<BinanceFuturesKlineItem>>> GetKlinesMultiSymbolsAsync(
            IEnumerable<string> symbols,
            FuturesKlineInterval interval,
            int limit = 500,
            DateTime? startTime = null,
            DateTime? endTime = null,
            int maxDegreeOfParallelism = 5,
            CancellationToken cancellationToken = default)
        {
            var symbolList = symbols.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var dict = new ConcurrentDictionary<string, List<BinanceFuturesKlineItem>>(StringComparer.OrdinalIgnoreCase);

            using var semaphore = new SemaphoreSlim(Math.Max(1, maxDegreeOfParallelism));
            var tasks = symbolList.Select(async sym =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var data = await GetKlinesAsync(sym, interval, limit, startTime, endTime, cancellationToken).ConfigureAwait(false);
                    dict[sym] = data;
                }
                catch
                {
                    dict[sym] = new List<BinanceFuturesKlineItem>();
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return new Dictionary<string, List<BinanceFuturesKlineItem>>(dict, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 【多线程并发】并发获取同一个交易对的多个不同周期的 K 线数据
        /// </summary>
        public async Task<Dictionary<FuturesKlineInterval, List<BinanceFuturesKlineItem>>> GetKlinesMultiIntervalsAsync(
            string symbol,
            IEnumerable<FuturesKlineInterval> intervals,
            int limit = 500,
            DateTime? startTime = null,
            DateTime? endTime = null,
            int maxDegreeOfParallelism = 5,
            CancellationToken cancellationToken = default)
        {
            var intervalList = intervals.Distinct().ToList();
            var dict = new ConcurrentDictionary<FuturesKlineInterval, List<BinanceFuturesKlineItem>>();

            using var semaphore = new SemaphoreSlim(Math.Max(1, maxDegreeOfParallelism));
            var tasks = intervalList.Select(async inter =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var data = await GetKlinesAsync(symbol, inter, limit, startTime, endTime, cancellationToken).ConfigureAwait(false);
                    dict[inter] = data;
                }
                catch
                {
                    dict[inter] = new List<BinanceFuturesKlineItem>();
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return new Dictionary<FuturesKlineInterval, List<BinanceFuturesKlineItem>>(dict);
        }

        /// <summary>
        /// 获取最新的 N 条 K 线数据 (若 N > 1500 则自动分页向上追溯)
        /// </summary>
        public async Task<List<BinanceFuturesKlineItem>> GetLatestKlinesAsync(
            string symbol,
            FuturesKlineInterval interval,
            int totalCount,
            CancellationToken cancellationToken = default)
        {
            if (totalCount <= 0)
                return new List<BinanceFuturesKlineItem>();

            if (totalCount <= 1500)
            {
                return await GetKlinesAsync(symbol, interval, limit: totalCount, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var result = new List<BinanceFuturesKlineItem>();
            DateTime? currentEndTime = null;

            while (result.Count < totalCount && !cancellationToken.IsCancellationRequested)
            {
                int fetchCount = Math.Min(1500, totalCount - result.Count);
                var batch = await GetKlinesAsync(
                    symbol,
                    interval,
                    limit: fetchCount,
                    endTime: currentEndTime,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (batch == null || batch.Count == 0)
                {
                    break;
                }

                result.InsertRange(0, batch);

                long earliestOpenTimeMs = batch[0].OpenTimeMs;
                currentEndTime = DateTimeOffset.FromUnixTimeMilliseconds(earliestOpenTimeMs - 1).UtcDateTime;

                if (batch.Count < fetchCount)
                {
                    break;
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }

        private static BinanceFuturesKlineItem? ParseKlineElement(JsonElement element)
        {
            try
            {
                return new BinanceFuturesKlineItem
                {
                    OpenTimeMs = element[0].GetInt64(),
                    Open = ParseDecimal(element[1].GetString()),
                    High = ParseDecimal(element[2].GetString()),
                    Low = ParseDecimal(element[3].GetString()),
                    Close = ParseDecimal(element[4].GetString()),
                    Volume = ParseDecimal(element[5].GetString()),
                    CloseTimeMs = element[6].GetInt64(),
                    QuoteVolume = ParseDecimal(element[7].GetString()),
                    TradeCount = element[8].GetInt64(),
                    TakerBuyBaseVolume = ParseDecimal(element[9].GetString()),
                    TakerBuyQuoteVolume = ParseDecimal(element[10].GetString())
                };
            }
            catch
            {
                return null;
            }
        }

        private static decimal ParseDecimal(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0m;
            return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal result) ? result : 0m;
        }
    }
}
