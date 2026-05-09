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

    public class HaState
    {
        public long OpenTime { get; set; }
        public decimal HaOpen { get; set; }
        public decimal HaClose { get; set; }
        public decimal HaHigh { get; set; }
        public decimal HaLow { get; set; }

        public bool IsBullish { get; set; }
        public decimal RawVolatility { get; set; } // 记录原生真实波动 (High - Low)
        public decimal Volume { get; set; } // 🌟 新增：记录成交量
    }

    public class HeikinAshiEngine
    {
        private readonly ILogger<HeikinAshiEngine> _logger;
        private readonly ConcurrentDictionary<string, List<HaState>> _haHistoryBuffer = new();

        private const decimal MIN_VOLATILITY_PERCENTAGE = 0.005m;
        private const int VOLATILITY_WINDOW_SIZE = 10;
        private const int MAX_HISTORY_SIZE = 30;

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
                decimal volume = k.Volume; // 🌟 提取历史成交量

                var currentHa = new HaState
                {
                    OpenTime = k.OpenTime,
                    HaClose = (open + high + low + close) / 4m,
                    RawVolatility = high - low,
                    Volume = volume
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

        public LiveHaResult ProcessLiveKlineAndCheckReversal(string symbol, string timeframe, decimal open, decimal close, decimal high, decimal low, decimal volume, long openTime, bool isClosed)
        {
            string key = $"{symbol.ToUpper()}_{timeframe}";

            if (!_haHistoryBuffer.TryGetValue(key, out var historyList)) return new LiveHaResult();

            HaState prevHa;
            lock (historyList)
            {
                if (historyList.Count == 0) return new LiveHaResult();
                prevHa = historyList.Last();
            }

            if (openTime < prevHa.OpenTime) return new LiveHaResult();

            var (haOpen, haClose, haHigh, haLow, isCurrentGreen) = CalculateLiveHeikinAshi(open, close, high, low, prevHa);

            bool isVisualReversal = prevHa.IsBullish != isCurrentGreen;
            bool isValidSignal = false;

            if (isClosed && openTime > prevHa.OpenTime)
            {
                decimal rawCandleLength = high - low;

                lock (historyList)
                {
                    if (isVisualReversal)
                    {
                        // 🌟 传入当前这根 K 线的成交量进行策略评估
                        isValidSignal = EvaluateReversalStrategy(
                            symbol, timeframe, close, rawCandleLength, volume,
                            haOpen, haClose, prevHa, historyList);
                    }

                    var newState = new HaState
                    {
                        OpenTime = openTime,
                        HaOpen = haOpen,
                        HaClose = haClose,
                        HaHigh = haHigh,
                        HaLow = haLow,
                        IsBullish = isCurrentGreen,
                        RawVolatility = rawCandleLength,
                        Volume = volume // 🌟 固化这一根 K 线的成交量到账本
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

        private (decimal haOpen, decimal haClose, decimal haHigh, decimal haLow, bool isBullish)
        CalculateLiveHeikinAshi(decimal open, decimal close, decimal high, decimal low, HaState prevHa)
        {
            decimal haClose = (open + high + low + close) / 4m;
            decimal haOpen = (prevHa.HaOpen + prevHa.HaClose) / 2m;
            decimal haHigh = Math.Max(high, Math.Max(haOpen, haClose));
            decimal haLow = Math.Min(low, Math.Min(haOpen, haClose));
            bool isBullish = haClose >= haOpen;

            return (haOpen, haClose, haHigh, haLow, isBullish);
        }

        private bool EvaluateReversalStrategy(
            string symbol, string timeframe,
            decimal closePrice, decimal rawCandleLength, decimal currentVolume,
            decimal haOpen, decimal haClose,
            HaState prevHa, List<HaState> historyList)
        {
            // 1. 检查历史连击数
            int requiredStreak = 6;
            if (historyList.Count < requiredStreak) return false;

            bool expectedPrevDir = prevHa.IsBullish;
            var lastNStates = historyList.Skip(historyList.Count - requiredStreak).ToList();
            bool isStreakValid = lastNStates.All(h => h.IsBullish == expectedPrevDir);

            if (!isStreakValid) return false;

            // ==========================================
            // 🌟 新增风控：成交量爆发过滤 (平常的 3-5 倍)
            // ==========================================
            // 计算过去 10 根 K 线的平均成交量
            int volWindow = Math.Min(10, historyList.Count);
            decimal avgVolume = historyList.Skip(historyList.Count - volWindow).Average(h => h.Volume);

            // 计算成交量放大倍数
            decimal volMultiplier = avgVolume > 0 ? currentVolume / avgVolume : 0;

            if (volMultiplier < 3.0m || volMultiplier > 8.0m)
            {
                _logger.LogDebug($"🛡️ [量能不符] {symbol} {timeframe} 反转发生，但成交量倍数 {volMultiplier:F2} 不在 [3, 5] 区间内，跳过。");
                return false;
            }

            // 2. 反弹/单边趋势过度过滤
            decimal streakMax = lastNStates.Max(h => h.HaHigh);
            decimal streakMin = lastNStates.Min(h => h.HaLow);
            decimal streakMovementPercentage = streakMin > 0 ? (streakMax - streakMin) / streakMin : 0;

            if (streakMovementPercentage > 0.015m)
            {
                _logger.LogDebug($"🛡️ [过度反弹过滤] {symbol} {timeframe} 前期趋势总波动达 {streakMovementPercentage:P2} (大于 1.5%)，结构疑似破坏。");
                return false;
            }

            // 3. 动态计算平均波动率
            int volWindowSize = Math.Min(VOLATILITY_WINDOW_SIZE, historyList.Count);
            decimal avgVolatility = volWindowSize > 0
                ? historyList.Skip(historyList.Count - volWindowSize).Average(h => h.RawVolatility)
                : rawCandleLength;

            decimal currentHaBodySize = Math.Abs(haClose - haOpen);

            // 4. 核心风控指标校验
            bool isVolatileEnough = rawCandleLength > (avgVolatility * 0.5m);
            bool isNotDoji = rawCandleLength > 0 && (currentHaBodySize / rawCandleLength) > 0.3m;
            decimal avgVolatilityPercentage = closePrice > 0 ? (avgVolatility / closePrice) : 0;
            bool isMinVolatilityMet = avgVolatilityPercentage >= MIN_VOLATILITY_PERCENTAGE;

            if (isVolatileEnough && isNotDoji && isMinVolatilityMet)
            {
                _logger.LogWarning($"🔥 [极品HA反转] {symbol} {timeframe} 达成连续 {requiredStreak} 根{(expectedPrevDir ? "阳" : "阴")}线反转! (波幅:{streakMovementPercentage:P2}, 量能放大:{volMultiplier:F2}X)");
                return true;
            }

            return false;
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