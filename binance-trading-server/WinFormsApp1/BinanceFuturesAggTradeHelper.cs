using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WinFormsApp1
{
    /// <summary>
    /// 币安合约 AggTrade / Tick 聚合交易数据模型
    /// </summary>
    public class BinanceFuturesAggTradeItem
    {
        public long AggTradeId { get; set; }
        public double Price { get; set; }
        public double Quantity { get; set; }
        public long TradeTimeMs { get; set; }
        public bool IsBuyerMaker { get; set; }
    }

    /// <summary>
    /// 币安合约 Tick / AggTrade 数据拉取与多周期 (1m/3m/5m等) 拟合合成帮助类
    /// 支持【优先秒级加载首批数据 + 后台无缝流式并发下载追加】机制
    /// </summary>
    public class BinanceFuturesAggTradeHelper
    {
        private static readonly HttpClient SharedHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        public BinanceFuturesAggTradeHelper(string baseUrl = "https://fapi.binance.com", HttpClient? customHttpClient = null)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _httpClient = customHttpClient ?? SharedHttpClient;
        }

        /// <summary>
        /// 单次拉取币安合约 AggTrades Tick 数据 (单次上限 1000 条)
        /// Endpoint: GET /fapi/v1/aggTrades
        /// </summary>
        public async Task<List<BinanceFuturesAggTradeItem>> GetAggTradesAsync(
            string symbol,
            int limit = 1000,
            DateTime? startTime = null,
            DateTime? endTime = null,
            long? fromId = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                throw new ArgumentException("交易对 symbol 不能为空", nameof(symbol));

            int realLimit = Math.Clamp(limit, 1, 1000);
            string formattedSymbol = symbol.Trim().ToUpperInvariant();
            string url = $"{_baseUrl}/fapi/v1/aggTrades?symbol={formattedSymbol}&limit={realLimit}";

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

            if (fromId.HasValue)
            {
                url += $"&fromId={fromId.Value}";
            }

            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, default, cancellationToken).ConfigureAwait(false);

            var list = new List<BinanceFuturesAggTradeItem>();
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                long aggId = element.GetProperty("a").GetInt64();
                string pStr = element.GetProperty("p").GetString() ?? "0";
                string qStr = element.GetProperty("q").GetString() ?? "0";
                long timeMs = element.GetProperty("T").GetInt64();
                bool isMaker = element.TryGetProperty("m", out var mProp) && mProp.GetBoolean();

                double price = double.TryParse(pStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0;
                double qty = double.TryParse(qStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var q) ? q : 0;

                list.Add(new BinanceFuturesAggTradeItem
                {
                    AggTradeId = aggId,
                    Price = price,
                    Quantity = qty,
                    TradeTimeMs = timeMs,
                    IsBuyerMaker = isMaker
                });
            }

            return list;
        }

        /// <summary>
        /// 将 Tick / AggTrade 明细数据拟合合成指定周期 (如 1m, 3m, 5m 等) 的 K 线数据
        /// </summary>
        public static List<BinanceFuturesKlineItem> SynthesizeKlinesFromAggTrades(
            IEnumerable<BinanceFuturesAggTradeItem> trades,
            FuturesKlineInterval interval)
        {
            if (trades == null) return new List<BinanceFuturesKlineItem>();

            long intervalMs = (long)interval.ToTimeSpan().TotalMilliseconds;
            if (intervalMs <= 0) intervalMs = 60000;

            var sortedTrades = trades.OrderBy(t => t.TradeTimeMs).ToList();
            if (sortedTrades.Count == 0) return new List<BinanceFuturesKlineItem>();

            var result = new List<BinanceFuturesKlineItem>();
            var grouped = sortedTrades.GroupBy(t => (t.TradeTimeMs / intervalMs) * intervalMs);

            foreach (var group in grouped)
            {
                long windowStartMs = group.Key;
                long windowEndMs = windowStartMs + intervalMs - 1;

                var listInWindow = group.ToList();
                double open = listInWindow.First().Price;
                double close = listInWindow.Last().Price;
                double high = listInWindow.Max(t => t.Price);
                double low = listInWindow.Min(t => t.Price);
                double totalVolume = listInWindow.Sum(t => t.Quantity);

                result.Add(new BinanceFuturesKlineItem
                {
                    OpenTimeMs = windowStartMs,
                    CloseTimeMs = windowEndMs,
                    Open = (decimal)open,
                    High = (decimal)high,
                    Low = (decimal)low,
                    Close = (decimal)close,
                    Volume = (decimal)totalVolume,
                    QuoteVolume = (decimal)(totalVolume * close),
                    TradeCount = listInWindow.Count
                });
            }

            return result;
        }

        /// <summary>
        /// 核心优化架构：【优先秒拉首批 Tick 数据 + 后台无缝并发分段流式下载追加】
        /// 解决 Tick 数据量庞大导致的图表加载卡顿问题
        /// </summary>
        public async Task<List<BinanceFuturesKlineItem>> PriorityStreamingFetchTickKlinesAsync(
            string symbol,
            FuturesKlineInterval interval,
            DateTime startTime,
            DateTime endTime,
            Action<List<BinanceFuturesKlineItem>>? onBackgroundChunkSynthesized = null,
            CancellationToken cancellationToken = default)
        {
            // 1. 首批优先拉取时间范围：优先拉取最近 20 分钟的 Tick 数据 (实现秒级开图)
            DateTime priorityStartTime = endTime.AddMinutes(-20);
            if (priorityStartTime < startTime) priorityStartTime = startTime;

            // 第一阶段：同步优先拉取 Phase 1 数据的 AggTrades 并合成 K 线
            List<BinanceFuturesAggTradeItem> priorityTrades = await FetchAggTradesRangeAsync(symbol, priorityStartTime, endTime, cancellationToken).ConfigureAwait(false);
            List<BinanceFuturesKlineItem> priorityKlines = SynthesizeKlinesFromAggTrades(priorityTrades, interval);

            // 第二阶段：如果需要拉取的历史起点早于 priorityStartTime，启动后台 Task 分段并发拉取，并流式追加
            if (startTime < priorityStartTime)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        DateTime currEnd = priorityStartTime;
                        DateTime targetStart = startTime;

                        // 按 30 分钟为一块进行分段并发下载
                        TimeSpan chunkSize = TimeSpan.FromMinutes(30);
                        List<(DateTime chunkStart, DateTime chunkEnd)> chunks = new();

                        DateTime cursor = currEnd;
                        while (cursor > targetStart)
                        {
                            DateTime chunkS = cursor - chunkSize;
                            if (chunkS < targetStart) chunkS = targetStart;
                            chunks.Add((chunkS, cursor));
                            cursor = chunkS;
                        }

                        foreach (var chunk in chunks)
                        {
                            if (cancellationToken.IsCancellationRequested) break;

                            List<BinanceFuturesAggTradeItem> chunkTrades = await FetchAggTradesRangeAsync(symbol, chunk.chunkStart, chunk.chunkEnd, cancellationToken).ConfigureAwait(false);
                            List<BinanceFuturesKlineItem> chunkKlines = SynthesizeKlinesFromAggTrades(chunkTrades, interval);

                            if (chunkKlines.Count > 0 && onBackgroundChunkSynthesized != null)
                            {
                                onBackgroundChunkSynthesized.Invoke(chunkKlines);
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception) { }
                }, cancellationToken);
            }

            return priorityKlines;
        }

        /// <summary>
        /// 辅助方法：分段拉取指定时间段内所有的 AggTrades
        /// </summary>
        private async Task<List<BinanceFuturesAggTradeItem>> FetchAggTradesRangeAsync(
            string symbol,
            DateTime startTime,
            DateTime endTime,
            CancellationToken cancellationToken)
        {
            var result = new List<BinanceFuturesAggTradeItem>();
            DateTime currStart = startTime;

            while (currStart < endTime && !cancellationToken.IsCancellationRequested)
            {
                List<BinanceFuturesAggTradeItem> batch = await GetAggTradesAsync(symbol, limit: 1000, startTime: currStart, endTime: endTime, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0) break;

                result.AddRange(batch);
                long lastTimeMs = batch.Last().TradeTimeMs;
                DateTime lastDt = DateTimeOffset.FromUnixTimeMilliseconds(lastTimeMs).UtcDateTime;

                if (lastDt <= currStart)
                {
                    currStart = currStart.AddSeconds(1);
                }
                else
                {
                    currStart = lastDt.AddMilliseconds(1);
                }

                if (batch.Count < 1000) break;
            }

            return result;
        }
    }
}
