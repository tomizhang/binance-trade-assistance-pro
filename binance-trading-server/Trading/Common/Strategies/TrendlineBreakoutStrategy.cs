using Common.Helper;
using Common.Models;
using System.Collections.Generic;

namespace Common.Strategies
{
    /// <summary>
    /// 趋势线突破交易策略 (演示集成 StrategyBase、PivotHelper 与 TrendLineHelper)
    /// </summary>
    public class TrendlineBreakoutStrategy : StrategyBase
    {
        public int LeftLen { get; set; } = 3;
        public int RightLen { get; set; } = 3;
        public int MaxSpan { get; set; } = 50;

        private bool _hasPosition = false;
        private TrendLine? _lastBreakoutLine;

        public TrendlineBreakoutStrategy(
            string symbol = "BTCUSDT",
            string interval = "30m",
            int leftLen = 3,
            int rightLen = 3,
            int maxSpan = 50)
            : base("趋势线突破策略", symbol, interval, bufferCapacity: 100)
        {
            LeftLen = leftLen;
            RightLen = rightLen;
            MaxSpan = maxSpan;
        }

        protected override void OnKline(MarketKline kline, IReadOnlyList<MarketKline> klineHistory)
        {
            if (klineHistory.Count < LeftLen + RightLen + 5)
            {
                return;
            }

            int currentIndex = klineHistory.Count - 1;

            // 1. 一键提取当前 100 根历史中的所有有效活跃趋势线
            var (resistanceLines, supportLines) = TrendLineHelper.FindActiveTrendLines(klineHistory, LeftLen, RightLen, MaxSpan);

            // 2. 检查向上突破阻力趋势线 (做多入场)
            if (!_hasPosition)
            {
                foreach (var line in resistanceLines)
                {
                    // 仅当趋势线向后延伸未被提前破坏，且在当前 K 线发生有效突破时
                    if (line.CollidedKlineIndex == -1 || line.CollidedKlineIndex == currentIndex)
                    {
                        if (TrendLineHelper.CheckBreakout(line, kline, currentIndex) == 1)
                        {
                            decimal stopLoss = line.Y2 * 0.99m;
                            decimal takeProfit = kline.Close * 1.03m;

                            EmitSignal(
                                SignalType.Buy,
                                kline.Close,
                                quantity: 1,
                                reason: $"向上突破阻力趋势线 ({line.X1} -> {line.X2}, 斜率: {line.K:F2}%/bar)",
                                stopLoss: stopLoss,
                                takeProfit: takeProfit,
                                extra: line);

                            _hasPosition = true;
                            _lastBreakoutLine = line;
                            break;
                        }
                    }
                }
            }
            // 3. 检查向下跌破支撑趋势线 (平多离场)
            else
            {
                foreach (var line in supportLines)
                {
                    if (line.CollidedKlineIndex == -1 || line.CollidedKlineIndex == currentIndex)
                    {
                        if (TrendLineHelper.CheckBreakout(line, kline, currentIndex) == -1)
                        {
                            EmitSignal(
                                SignalType.CloseLong,
                                kline.Close,
                                reason: $"向下跌破支撑趋势线 ({line.X1} -> {line.X2}, 斜率: {line.K:F2}%/bar)",
                                extra: line);

                            _hasPosition = false;
                            break;
                        }
                    }
                }
            }
        }

        protected override void OnReset()
        {
            _hasPosition = false;
            _lastBreakoutLine = null;
        }
    }
}
