using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        public bool IsBullish => HaClose >= HaOpen;
    }

    public class HeikinAshiEngine
    {
        private readonly ILogger<HeikinAshiEngine> _logger;

        // 存储格式：Key: "BTCUSDT_15m", Value: 最后一根已闭合的 HA 状态
        private readonly ConcurrentDictionary<string, HaState> _lastClosedStates = new();

        public HeikinAshiEngine(ILogger<HeikinAshiEngine> logger)
        {
            _logger = logger;
        }

        // 1. 根据 1000 条历史数据初始化 HA 状态
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
                    currentHa.HaOpen = (open + close) / 2m; // 第一根的初始值
                else
                    currentHa.HaOpen = (prevHa.HaOpen + prevHa.HaClose) / 2m;

                currentHa.HaHigh = Math.Max(high, Math.Max(currentHa.HaOpen, currentHa.HaClose));
                currentHa.HaLow = Math.Min(low, Math.Min(currentHa.HaOpen, currentHa.HaClose));

                prevHa = currentHa;
            }

            // 保存历史计算结果的最后一根
            _lastClosedStates[key] = prevHa;
        }

        // 2. 处理 WS 推送的实时 K 线，并判断是否反转
        // 返回 true 表示发生了方向反转
        public bool ProcessLiveKlineAndCheckReversal(string symbol, string timeframe, decimal open, decimal close, decimal high, decimal low, long openTime, bool isClosed)
        {
            string key = $"{symbol.ToUpper()}_{timeframe}";

            // 如果内存中没有历史状态，说明还没初始化，不处理
            if (!_lastClosedStates.TryGetValue(key, out var prevHa)) return false;

            // 如果推送的是已经处理过的历史 K 线，跳过
            if (openTime < prevHa.OpenTime) return false;

            // 计算当前实时 K 线的 HA 值
            decimal haClose = (open + high + low + close) / 4m;
            decimal haOpen = (prevHa.HaOpen + prevHa.HaClose) / 2m;
            bool currentIsBullish = haClose >= haOpen;

            bool isReversal = false;

            // 只有当这根 K 线闭合时 (x: true)，才进行严格的反转判定并更新状态
            if (isClosed && openTime > prevHa.OpenTime)
            {
                // 反转判定：上一根是阴，这根是阳；或上一根是阳，这根是阴
                if (prevHa.IsBullish != currentIsBullish)
                {
                    isReversal = true;
                    _logger.LogWarning($"🔄 [HA反转] {symbol} 在 {timeframe} 级别由 {(prevHa.IsBullish ? "多转空📉" : "空转多📈")}!");
                }

                // 更新内存中的最后一根闭合状态
                _lastClosedStates[key] = new HaState
                {
                    OpenTime = openTime,
                    HaOpen = haOpen,
                    HaClose = haClose,
                    HaHigh = Math.Max(high, Math.Max(haOpen, haClose)),
                    HaLow = Math.Min(low, Math.Min(haOpen, haClose))
                };
            }

            return isReversal;
        }

        // 获取某个周期最后闭合的时间，用于断线后补齐数据
        public long GetLastClosedTime(string symbol, string timeframe)
        {
            string key = $"{symbol.ToUpper()}_{timeframe}";
            return _lastClosedStates.TryGetValue(key, out var state) ? state.OpenTime : 0;
        }
    }
}