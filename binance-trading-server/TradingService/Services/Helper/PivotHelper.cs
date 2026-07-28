using System.Collections.Generic;

namespace TradingTerminal.Utils
{
    public static class PivotHelper
    {
        public static (List<int> Peaks, List<int> Valleys) CalculatePeaks(
            IReadOnlyList<decimal> highs,
            IReadOnlyList<decimal> lows,
            int leftLen = 5,
            int rightLen = 5)
        {
            var peaks = new List<int>();
            var valleys = new List<int>();

            if (highs == null || lows == null || highs.Count != lows.Count)
            {
                return (peaks, valleys);
            }

            int length = highs.Count;

            for (int i = leftLen; i < length - rightLen; i++)
            {
                bool isPeak = true;
                bool isValley = true;
                decimal currentHigh = highs[i];
                decimal currentLow = lows[i];

                // 将两次循环合并为一次，减少迭代开销
                for (int j = i - leftLen; j <= i + rightLen; j++)
                {
                    if (j == i) continue;

                    // 同步检查 Peak 和 Valley
                    if (isPeak && highs[j] >= currentHigh)
                        isPeak = false;

                    if (isValley && lows[j] <= currentLow)
                        isValley = false;

                    // 如果既不是高点也不是低点，立刻终止内层循环，不再做无用功
                    if (!isPeak && !isValley)
                        break;
                }

                if (isPeak) peaks.Add(i);
                if (isValley) valleys.Add(i);
            }

            return (peaks, valleys);
        }


        /// <summary>
        /// 极速版计算局部高低点 (零内存分配 + Span 连续内存访问)
        /// </summary>
        /// <param name="highs">最高价连续内存切片</param>
        /// <param name="lows">最低价连续内存切片</param>
        /// <param name="peaksBuffer">用于装载峰值索引的复用缓存区</param>
        /// <param name="valleysBuffer">用于装载谷值索引的复用缓存区</param>
        /// <param name="leftLen">左侧周期</param>
        /// <param name="rightLen">右侧周期</param>
        public static void CalculatePeaksFast(
            ReadOnlySpan<decimal> highs,
            ReadOnlySpan<decimal> lows,
            List<int> peaksBuffer,       // ⚠️ 从外部传入，拒绝 new
            List<int> valleysBuffer,     // ⚠️ 从外部传入，拒绝 new
            int leftLen = 5,
            int rightLen = 5)
        {
            // 1. 清空复用缓存区，复用底层已分配的数组内存
            peaksBuffer.Clear();
            valleysBuffer.Clear();

            int length = highs.Length;

            // 防御性检查
            if (length == 0 || lows.Length != length || length <= leftLen + rightLen)
            {
                return;
            }

            // 2. 提取 Span 的引用，这步能帮助 JIT 更好地消除边界检查
            for (int i = leftLen; i < length - rightLen; i++)
            {
                bool isPeak = true;
                bool isValley = true;
                decimal currentHigh = highs[i];
                decimal currentLow = lows[i];

                int startIdx = i - leftLen;
                int endIdx = i + rightLen;

                for (int j = startIdx; j <= endIdx; j++)
                {
                    if (j == i) continue;

                    if (isPeak && highs[j] >= currentHigh)
                        isPeak = false;

                    if (isValley && lows[j] <= currentLow)
                        isValley = false;

                    if (!isPeak && !isValley)
                        break;
                }

                if (isPeak) peaksBuffer.Add(i);
                if (isValley) valleysBuffer.Add(i);
            }
        }
    }
}