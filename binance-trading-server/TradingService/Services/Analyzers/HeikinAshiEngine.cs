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

        // ==========================================
        // 模块 1：主调度器 (Orchestrator)
        // ==========================================
        public LiveHaResult ProcessLiveKlineAndCheckReversal(string symbol, string timeframe, decimal open, decimal close, decimal high, decimal low, long openTime, bool isClosed)
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

            // 1. 调用独立模块：计算当前 HA 数值
            var (haOpen, haClose, haHigh, haLow, isCurrentGreen) = CalculateLiveHeikinAshi(open, close, high, low, prevHa);

            bool isVisualReversal = prevHa.IsBullish != isCurrentGreen;
            bool isValidSignal = false;

            // 🌟 绝对屏障：必须等 K 线完全收盘，才进行风控计算与账本更新
            if (isClosed && openTime > prevHa.OpenTime)
            {
                decimal rawCandleLength = high - low;

                lock (historyList)
                {
                    // 2. 调用独立模块：如果视觉发生反转，执行严苛的策略风控校验
                    if (isVisualReversal)
                    {
                        isValidSignal = EvaluateReversalStrategy(
                            symbol, timeframe, close, rawCandleLength,
                            haOpen, haClose, prevHa, historyList);
                    }

                    // 3. 将这根收盘的 K 线推入历史 List
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

        // ==========================================
        // 模块 2：纯数学计算器 (Calculator)
        // ==========================================
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

        // ==========================================
        // 模块 3：策略与风控校验 (Strategy & Risk Management)
        // ==========================================
        private bool EvaluateReversalStrategy(
            string symbol, string timeframe,
            decimal closePrice, decimal rawCandleLength,
            decimal haOpen, decimal haClose,
            HaState prevHa, List<HaState> historyList)
        {
            // 1. 检查历史连击数 (核心趋势过滤)
            int requiredStreak = 6;
            if (historyList.Count < requiredStreak) return false;

            bool expectedPrevDir = prevHa.IsBullish;
            var lastNStates = historyList.Skip(historyList.Count - requiredStreak).ToList();
            bool isStreakValid = lastNStates.All(h => h.IsBullish == expectedPrevDir);

            if (!isStreakValid) return false;

            // ==========================================
            // 🌟 新增风控：反弹/单边趋势过度过滤
            // ==========================================
            decimal streakMax = lastNStates.Max(h => h.HaHigh);
            decimal streakMin = lastNStates.Min(h => h.HaLow);
            decimal streakMovementPercentage = streakMin > 0 ? (streakMax - streakMin) / streakMin : 0;

            if (streakMovementPercentage > 0.015m) // 如果前方连续 K 线波动超过 1.5%
            {
                _logger.LogDebug($"🛡️ [过度反弹过滤] {symbol} {timeframe} 前期趋势总波动达 {streakMovementPercentage:P2} (大于 1.5%)，结构疑似破坏，拒绝逆势！");
                return false;
            }

            // 2. 动态计算平均波动率
            int volCount = Math.Min(VOLATILITY_WINDOW_SIZE, historyList.Count);
            decimal avgVolatility = volCount > 0
                ? historyList.Skip(historyList.Count - volCount).Average(h => h.RawVolatility)
                : rawCandleLength;

            decimal currentHaBodySize = Math.Abs(haClose - haOpen);

            // 3. 核心风控指标校验
            bool isVolatileEnough = rawCandleLength > (avgVolatility * 0.5m);
            bool isNotDoji = rawCandleLength > 0 && (currentHaBodySize / rawCandleLength) > 0.3m;
            decimal avgVolatilityPercentage = closePrice > 0 ? (avgVolatility / closePrice) : 0;
            bool isMinVolatilityMet = avgVolatilityPercentage >= MIN_VOLATILITY_PERCENTAGE;

            if (isVolatileEnough && isNotDoji && isMinVolatilityMet)
            {
                _logger.LogWarning($"🔥 [极品HA反转] {symbol} {timeframe} 验证达成连续 {requiredStreak} 根{(expectedPrevDir ? "阳" : "阴")}线 (波幅:{streakMovementPercentage:P2}) 后反转! (均波率: {avgVolatilityPercentage:P2})");
                return true;
            }

            return false;
        }

        // ==========================================
        // 辅助方法
        // ==========================================
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