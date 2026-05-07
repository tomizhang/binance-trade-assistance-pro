using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace TradingTerminal.Services
{
    public class LiveHaResult
    {
        public bool IsReversed { get; set; }
        public bool IsBullish { get; set; }
        public decimal HaOpen { get; set; }
        public decimal HaClose { get; set; }
        public decimal HaHigh { get; set; }
        public decimal HaLow { get; set; }
    }

    // 🌟 核心重构 1：HaState 降级为最纯净的数据模型，彻底抛弃所有的 Queue
    public class HaState
    {
        public long OpenTime { get; set; }
        public decimal HaOpen { get; set; }
        public decimal HaClose { get; set; }
        public decimal HaHigh { get; set; }
        public decimal HaLow { get; set; }

        public bool IsBullish { get; set; }
        public decimal RawVolatility { get; set; } // 记录原生真实波动 (High - Low)
    }

    public class HeikinAshiEngine
    {
        private readonly ILogger<HeikinAshiEngine> _logger;

        // 🌟 核心重构 2：引擎维护一个全局的历史 K 线列表字典 (缓冲池)
        private readonly ConcurrentDictionary<string, List<HaState>> _haHistoryBuffer = new();

        private const decimal MIN_VOLATILITY_PERCENTAGE = 0.0015m;
        private const int VOLATILITY_WINDOW_SIZE = 10;
        private const int MAX_HISTORY_SIZE = 30; // 历史列表最大保留容量

        public HeikinAshiEngine(ILogger<HeikinAshiEngine> logger)
        {
            _logger = logger;
        }

        public void InitializeFromHistory(string symbol, string timeframe, List<dynamic> rawKlines)
        {
            if (rawKlines.Count == 0) return;

            string key = $"{symbol.ToUpper()}_{timeframe}";
            var historyList = new List<HaState>();
            HaState prevHa = null;

            foreach (var k in rawKlines)
            {
                decimal open = k.Open; decimal close = k.Close;
                decimal high = k.High; decimal low = k.Low;

                var currentHa = new HaState
                {
                    OpenTime = k.OpenTime,
                    HaClose = (open + high + low + close) / 4m,
                    RawVolatility = high - low
                };

                if (prevHa == null)
                {
                    currentHa.HaOpen = (open + close) / 2m;
                    currentHa.IsBullish = currentHa.HaClose >= currentHa.HaOpen;
                }
                else
                {
                    currentHa.HaOpen = (prevHa.HaOpen + prevHa.HaClose) / 2m;
                    currentHa.IsBullish = currentHa.HaClose >= currentHa.HaOpen;
                }

                currentHa.HaHigh = Math.Max(high, Math.Max(currentHa.HaOpen, currentHa.HaClose));
                currentHa.HaLow = Math.Min(low, Math.Min(currentHa.HaOpen, currentHa.HaClose));

                historyList.Add(currentHa);
                if (historyList.Count > MAX_HISTORY_SIZE) historyList.RemoveAt(0);

                prevHa = currentHa;
            }

            _haHistoryBuffer[key] = historyList;
        }

        public LiveHaResult ProcessLiveKlineAndCheckReversal(string symbol, string timeframe, decimal open, decimal close, decimal high, decimal low, long openTime, bool isClosed)
        {
            string key = $"{symbol.ToUpper()}_{timeframe}";

            if (!_haHistoryBuffer.TryGetValue(key, out var historyList)) return new LiveHaResult();

            HaState prevHa;
            lock (historyList) // 确保线程安全读取
            {
                if (historyList.Count == 0) return new LiveHaResult();
                prevHa = historyList.Last();
            }

            if (openTime < prevHa.OpenTime) return new LiveHaResult();

            decimal haClose = (open + high + low + close) / 4m;
            decimal haOpen = (prevHa.HaOpen + prevHa.HaClose) / 2m;
            decimal haHigh = Math.Max(high, Math.Max(haOpen, haClose));
            decimal haLow = Math.Min(low, Math.Min(haOpen, haClose));

            bool isCurrentGreen = haClose >= haOpen;
            bool isVisualReversal = prevHa.IsBullish != isCurrentGreen;
            bool isValidSignal = false;

            // 🌟 绝对屏障：必须等 K 线完全收盘，才进行历史计算与账本更新
            if (isClosed && openTime > prevHa.OpenTime)
            {
                decimal currentHaBodySize = Math.Abs(haClose - haOpen);
                decimal rawCandleLength = high - low;

                lock (historyList) // 确保在读取历史记录时，列表不会被其他线程篡改
                {
                    // 1. 动态从历史 List 中计算最近的平均波动率
                    int volCount = Math.Min(VOLATILITY_WINDOW_SIZE, historyList.Count);
                    decimal avgVolatility = volCount > 0
                        ? historyList.Skip(historyList.Count - volCount).Average(h => h.RawVolatility)
                        : rawCandleLength;

                    if (isVisualReversal)
                    {
                        // 🌟 核心重构 3：直接从历史 List 中切片提取最后 4 根进行判定！
                        int requiredStreak = 5;
                        if (historyList.Count >= requiredStreak)
                        {
                            bool expectedPrevDir = prevHa.IsBullish;

                            // 动态截取历史最后 4 根 K 线，判断它们的方向是否全部符合预期
                            var last4States = historyList.Skip(historyList.Count - requiredStreak).ToList();
                            bool isStreakValid = last4States.All(h => h.IsBullish == expectedPrevDir);

                            if (isStreakValid)
                            {
                                bool isVolatileEnough = rawCandleLength > (avgVolatility * 0.5m);
                                bool isNotDoji = rawCandleLength > 0 && (currentHaBodySize / rawCandleLength) > 0.3m;
                                decimal avgVolatilityPercentage = close > 0 ? (avgVolatility / close) : 0;
                                bool isMinVolatilityMet = avgVolatilityPercentage >= MIN_VOLATILITY_PERCENTAGE;

                                if (isVolatileEnough && isNotDoji && isMinVolatilityMet)
                                {
                                    isValidSignal = true;
                                    _logger.LogWarning($"🔥 [极品HA反转] {symbol} {timeframe} 历史列表验证达成连续 {requiredStreak} 根{(expectedPrevDir ? "阳" : "阴")}线后完美反转! (均波率: {avgVolatilityPercentage:P2})");
                                }
                            }
                        }
                    }

                    // 2. 将这根收盘的 K 线推入历史 List
                    var newState = new HaState
                    {
                        OpenTime = openTime,
                        HaOpen = haOpen,
                        HaClose = haClose,
                        HaHigh = haHigh,
                        HaLow = haLow,
                        IsBullish = isCurrentGreen,
                        RawVolatility = rawCandleLength
                    };

                    historyList.Add(newState);
                    if (historyList.Count > MAX_HISTORY_SIZE) historyList.RemoveAt(0);
                }
            }

            return new LiveHaResult
            {
                IsReversed = isClosed ? isValidSignal : isVisualReversal,
                IsBullish = isCurrentGreen,
                HaOpen = haOpen,
                HaClose = haClose,
                HaHigh = haHigh,
                HaLow = haLow
            };
        }

        public long GetLastClosedTime(string symbol, string timeframe)
        {
            string key = $"{symbol.ToUpper()}_{timeframe}";
            if (_haHistoryBuffer.TryGetValue(key, out var list))
            {
                lock (list) { return list.Count > 0 ? list.Last().OpenTime : 0; }
            }
            return 0;
        }

        public bool GetCurrentDirection(string symbol, string timeframe)
        {
            string key = $"{symbol.ToUpper()}_{timeframe}";
            if (_haHistoryBuffer.TryGetValue(key, out var list))
            {
                lock (list) { return list.Count > 0 && list.Last().IsBullish; }
            }
            return true;
        }
    }
}