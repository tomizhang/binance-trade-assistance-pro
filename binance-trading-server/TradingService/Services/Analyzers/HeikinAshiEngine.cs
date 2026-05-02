using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace TradingTerminal.Services
{
    // 记录 HA 的状态
    public class HaState
    {
        public long OpenTime { get; set; }
        public decimal HaOpen { get; set; }
        public decimal HaClose { get; set; }
        public decimal HaHigh { get; set; }
        public decimal HaLow { get; set; }

        public bool IsBullish { get; set; }

        // 滑动窗口：记录原生 K 线的真实波动幅度 (ATR)
        public Queue<decimal> RecentVolatilities { get; set; } = new Queue<decimal>();
    }

    public class HeikinAshiEngine
    {
        private readonly ILogger<HeikinAshiEngine> _logger;
        private readonly ConcurrentDictionary<string, HaState> _lastClosedStates = new();

        // 🌟 核心升级：跨币种自适应的最小百分比波动率 (0.15%)
        private const decimal MIN_VOLATILITY_PERCENTAGE = 0.0015m;

        // 🌟 滑动窗口大小 (记录最近 10 根 K 线的波动)
        private const int VOLATILITY_WINDOW_SIZE = 10;

        public HeikinAshiEngine(ILogger<HeikinAshiEngine> logger)
        {
            _logger = logger;
        }

        // 1. 根据历史数据初始化 HA 状态
        public void InitializeFromHistory(string symbol, string timeframe, List<dynamic> rawKlines)
        {
            if (rawKlines.Count == 0) return;

            string key = $"{symbol.ToUpper()}_{timeframe}";
            HaState prevHa = null;

            foreach (var k in rawKlines)
            {
                decimal open = k.Open; decimal close = k.Close;
                decimal high = k.High; decimal low = k.Low;

                var currentHa = new HaState
                {
                    OpenTime = k.OpenTime,
                    HaClose = (open + high + low + close) / 4m
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
                    currentHa.RecentVolatilities = new Queue<decimal>(prevHa.RecentVolatilities);
                }

                currentHa.HaHigh = Math.Max(high, Math.Max(currentHa.HaOpen, currentHa.HaClose));
                currentHa.HaLow = Math.Min(low, Math.Min(currentHa.HaOpen, currentHa.HaClose));

                // 记录原生 K 线的真实波动 (High - Low)
                decimal rawVolatility = high - low;
                currentHa.RecentVolatilities.Enqueue(rawVolatility);
                if (currentHa.RecentVolatilities.Count > VOLATILITY_WINDOW_SIZE)
                {
                    currentHa.RecentVolatilities.Dequeue();
                }

                prevHa = currentHa;
            }

            _lastClosedStates[key] = prevHa;
        }

        // 2. 处理 WS 推送的实时 K 线，并判断是否反转
        public bool ProcessLiveKlineAndCheckReversal(string symbol, string timeframe, decimal open, decimal close, decimal high, decimal low, long openTime, bool isClosed)
        {
            string key = $"{symbol.ToUpper()}_{timeframe}";

            if (!_lastClosedStates.TryGetValue(key, out var prevHa)) return false;
            if (openTime < prevHa.OpenTime) return false;

            decimal haClose = (open + high + low + close) / 4m;
            decimal haOpen = (prevHa.HaOpen + prevHa.HaClose) / 2m;
            decimal haHigh = Math.Max(high, Math.Max(haOpen, haClose));
            decimal haLow = Math.Min(low, Math.Min(haOpen, haClose));

            bool isCurrentGreen = haClose >= haOpen;
            bool isReversal = prevHa.IsBullish != isCurrentGreen;
            bool isValidSignal = false;

            if (isClosed && openTime > prevHa.OpenTime)
            {
                decimal currentHaBodySize = Math.Abs(haClose - haOpen);
                decimal rawCandleLength = high - low; // 真实的 K 线高低点跨度

                // 计算过去 10 根的原生平均波动幅度
                decimal avgVolatility = prevHa.RecentVolatilities.Any() ? prevHa.RecentVolatilities.Average() : rawCandleLength;

                if (isReversal)
                {
                    // 🛡️ 过滤 A：相对突发波动率 (这根 K 线的真实波动必须大于过去均值的 50%)
                    bool isVolatileEnough = rawCandleLength > (avgVolatility * 0.5m);

                    // 🛡️ 过滤 B：防十字星 (HA 实体必须占整根【真实 K 线】长度的 30% 以上)
                    bool isNotDoji = rawCandleLength > 0 && (currentHaBodySize / rawCandleLength) > 0.3m;

                    // 🛡️ 过滤 C：跨币种绝对冰点过滤 (最近 10 根的平均波动率必须 >= 0.15%) 🌟
                    // 均波百分比 = 平均价格波动 / 当前收盘价
                    decimal avgVolatilityPercentage = close > 0 ? (avgVolatility / close) : 0;
                    bool isMinVolatilityMet = avgVolatilityPercentage >= MIN_VOLATILITY_PERCENTAGE;

                    if (isVolatileEnough && isNotDoji && isMinVolatilityMet)
                    {
                        isValidSignal = true;
                        // 日志里直接打印出计算出的百分比，方便你复盘时观察 (P2 格式化会自动转为百分比并保留两位小数)
                        _logger.LogWarning($"🔄 [HA反转] {symbol} 在 {timeframe} 级别由 {(prevHa.IsBullish ? "多转空📉" : "空转多📈")}! (均波率: {avgVolatilityPercentage:P2})");
                    }
                    else if (isReversal && !isMinVolatilityMet)
                    {
                        // 隐式记录：可以把过滤掉的冰点死水打印出来，方便你调试 0.15% 的阈值是否合适
                        // _logger.LogDebug($"💤 [冰点过滤] {symbol} {timeframe} 发生反转被过滤，当前近期均波率仅为: {avgVolatilityPercentage:P2}");
                    }
                }

                var newState = new HaState
                {
                    OpenTime = openTime,
                    HaOpen = haOpen,
                    HaClose = haClose,
                    HaHigh = haHigh,
                    HaLow = haLow,
                    RecentVolatilities = new Queue<decimal>(prevHa.RecentVolatilities)
                };

                if (isValidSignal || !isReversal)
                {
                    newState.IsBullish = isCurrentGreen;
                }
                else
                {
                    newState.IsBullish = prevHa.IsBullish;
                }

                // 更新历史波动记忆
                newState.RecentVolatilities.Enqueue(rawCandleLength);
                if (newState.RecentVolatilities.Count > VOLATILITY_WINDOW_SIZE) newState.RecentVolatilities.Dequeue();

                _lastClosedStates[key] = newState;
            }

            return isValidSignal;
        }

        public long GetLastClosedTime(string symbol, string timeframe)
        {
            string key = $"{symbol.ToUpper()}_{timeframe}";
            return _lastClosedStates.TryGetValue(key, out var state) ? state.OpenTime : 0;
        }

        // 获取指定币种和周期当前的趋势方向 (true 为多头，false 为空头)
        public bool GetCurrentDirection(string symbol, string timeframe)
        {
            string key = $"{symbol.ToUpper()}_{timeframe}";
            return _lastClosedStates.TryGetValue(key, out var state) && state.IsBullish;
        }
    }
}