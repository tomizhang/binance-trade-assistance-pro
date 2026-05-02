using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TradingTerminal.Services
{
    public class CustomKlineAggregator
    {
        // 记录正在构建的虚拟 K 线状态
        private class AggregationState
        {
            public long BucketStartTime { get; set; }
            public decimal Open { get; set; }
            public decimal High { get; set; }
            public decimal Low { get; set; }
            public decimal Close { get; set; }
            public ConcurrentDictionary<long, decimal> VolumesMap { get; set; } = new();
        }

        private readonly ConcurrentDictionary<string, AggregationState> _states = new();

        // 🌟 币安不支持，但我们需要支持的自定义周期 (1m 为基准)
        public static readonly Dictionary<string, int> SupportedCustomIntervals = new()
        {
            { "2m", 2 },
            { "4m", 4 },
            { "6m", 6 },
            { "8m", 8 },
            { "10m", 10 },
            { "20m", 20 },
            { "40m", 40 }
        };

        public bool IsCustomInterval(string interval) => SupportedCustomIntervals.ContainsKey(interval);

        // ==========================================
        // 1. WebSocket 实时数据聚合引擎
        // ==========================================
        public List<KlineMessage> Process1mKline(KlineMessage k1m, IEnumerable<string> activeCustomIntervals)
        {
            var results = new List<KlineMessage>();

            foreach (var interval in activeCustomIntervals)
            {
                if (!SupportedCustomIntervals.TryGetValue(interval, out int multiplier)) continue;

                long bucketDurationMs = multiplier * 60 * 1000L;
                long bucketStartTime = k1m.OpenTime - (k1m.OpenTime % bucketDurationMs);
                string key = $"{k1m.Symbol}_{interval}";

                var state = _states.AddOrUpdate(key,
                    _ => new AggregationState
                    {
                        BucketStartTime = bucketStartTime,
                        Open = k1m.Open,
                        High = k1m.High,
                        Low = k1m.Low,
                        Close = k1m.Close,
                        VolumesMap = new ConcurrentDictionary<long, decimal>(new[] { new KeyValuePair<long, decimal>(k1m.OpenTime, k1m.Volume) })
                    },
                    (_, existing) =>
                    {
                        if (bucketStartTime > existing.BucketStartTime)
                        {
                            return new AggregationState
                            {
                                BucketStartTime = bucketStartTime,
                                Open = k1m.Open,
                                High = k1m.High,
                                Low = k1m.Low,
                                Close = k1m.Close,
                                VolumesMap = new ConcurrentDictionary<long, decimal>(new[] { new KeyValuePair<long, decimal>(k1m.OpenTime, k1m.Volume) })
                            };
                        }

                        existing.High = Math.Max(existing.High, k1m.High);
                        existing.Low = Math.Min(existing.Low, k1m.Low);
                        existing.Close = k1m.Close;
                        existing.VolumesMap[k1m.OpenTime] = k1m.Volume;

                        return existing;
                    });

                long last1mOpenTimeInBucket = bucketStartTime + (multiplier - 1) * 60 * 1000L;
                bool isCustomClosed = (k1m.OpenTime == last1mOpenTimeInBucket) && k1m.IsClosed;

                results.Add(new KlineMessage
                {
                    Symbol = k1m.Symbol,
                    Interval = interval,
                    OpenTime = state.BucketStartTime,
                    Open = state.Open,
                    High = state.High,
                    Low = state.Low,
                    Close = state.Close,
                    Volume = state.VolumesMap.Values.Sum(),
                    IsClosed = isCustomClosed
                });
            }

            return results;
        }

        // ==========================================
        // 🌟 2. REST API 历史数据聚合引擎
        // ==========================================

        /// <summary>
        /// 拦截请求：获取底层依赖周期和需要的安全数量
        /// </summary>
        public (string BaseInterval, int NeededLimit) GetBaseHistoryRequestParams(string customInterval, int requestedLimit)
        {
            if (SupportedCustomIntervals.TryGetValue(customInterval, out int multiplier))
            {
                int neededLimit = requestedLimit * multiplier;
                // 🛡️ 核心防爆：币安单次请求最大上限一般是 1000 (U本位是1500)
                // 即使前端需要 100根 40m 线（实际需要4000根），我们也强制卡在 1000。
                // 返回的数量变少没关系，前端的 loadMoreHistory 会自动识别并再次请求。
                if (neededLimit > 1000) neededLimit = 1000;

                return ("1m", neededLimit);
            }
            return (customInterval, requestedLimit);
        }

        /// <summary>
        /// 拦截响应：将币安返回的 1m 历史数组，揉捏成目标周期的历史数据
        /// </summary>
        public string AggregateHistoricalJson(string rawBinanceJson, string targetInterval)
        {
            if (!SupportedCustomIntervals.TryGetValue(targetInterval, out int multiplier))
            {
                return rawBinanceJson; // 不是自定义周期，直接放行
            }

            
            long bucketDurationMs = multiplier * 60 * 1000L;

            using var doc = JsonDocument.Parse(rawBinanceJson);
            var root = doc.RootElement;

            // 防御性校验
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root[0].ValueKind != JsonValueKind.Array)
            {
                return rawBinanceJson;
            }

            // 提取所有 1m K 线
            var klines = new List<decimal[]>();
            foreach (var item in root.EnumerateArray())
            {
                try
                {
                    klines.Add(new decimal[]
                    {
                        item[0].GetInt64(),                 // 0: OpenTime
                        decimal.Parse(item[1].GetString()), // 1: Open
                        decimal.Parse(item[2].GetString()), // 2: High
                        decimal.Parse(item[3].GetString()), // 3: Low
                        decimal.Parse(item[4].GetString()), // 4: Close
                        decimal.Parse(item[5].GetString())  // 5: Volume
                    });
                }
                catch { continue; }
            }

            // 按照目标周期取模分组
            var grouped = klines
                .GroupBy(k => (long)k[0] - ((long)k[0] % bucketDurationMs))
                .OrderBy(g => g.Key);

            var resultList = new List<object[]>();

            foreach (var group in grouped)
            {
                var list = group.OrderBy(k => k[0]).ToList();
                var first = list.First();
                var last = list.Last();

                // 🎭 完美伪装成币安原始数组格式返回
                resultList.Add(new object[]
                {
                    group.Key,                                    // 0: OpenTime
                    first[1].ToString("0.########"),              // 1: Open
                    list.Max(k => k[2]).ToString("0.########"),   // 2: High
                    list.Min(k => k[3]).ToString("0.########"),   // 3: Low
                    last[4].ToString("0.########"),               // 4: Close
                    list.Sum(k => k[5]).ToString("0.########"),   // 5: Volume
                    group.Key + bucketDurationMs - 1,             // 6: CloseTime
                    "0", "0", "0", "0", "0"                       // 7-11: 废弃占位符，保持数组长度一致
                });
            }

            return JsonSerializer.Serialize(resultList);
        }
    }
}