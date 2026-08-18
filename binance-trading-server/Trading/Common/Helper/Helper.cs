using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Common.Helper
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


        /// <summary>
        /// 高性能状态机计算 K 线波峰与波谷（内存零分配） 
        /// </summary>
        /// <param name="highs">最高价 ReadOnlySpan 视图</param>
        /// <param name="lows">最低价 ReadOnlySpan 视图</param>
        /// <param name="peaksBuffer">输出波峰索引的 List 缓冲区（需提前初始化）</param>
        /// <param name="valleysBuffer">输出波谷索引的 List 缓冲区（需提前初始化）</param>
        /// <param name="reversalBars">确认极值反转所需的 K 线根数（默认 3）</param>
        public static void CalculatePeaksFastReversal(
            ReadOnlySpan<decimal> highs,
            ReadOnlySpan<decimal> lows,
            List<int> peaksBuffer,
            List<int> valleysBuffer,
            int reversalBars = 3)
        {
            peaksBuffer.Clear();
            valleysBuffer.Clear();

            int length = highs.Length;
            if (length < reversalBars + 1 || lows.Length < length)
            {
                return;
            }

            // 状态变量：0 = 寻找波峰中, 1 = 寻找波谷中
            int state = 0;

            int currentCandidateIdx = 0;
            decimal currentCandidatePrice = highs[0];
            int barsSinceExtreme = 0;

            for (int i = 1; i < length; i++)
            {
                if (state == 0) // 寻找波峰
                {
                    decimal currentHigh = highs[i];

                    // 1. 刷新更高点：零延迟移动候选波峰位置
                    if (currentHigh >= currentCandidatePrice)
                    {
                        currentCandidateIdx = i;
                        currentCandidatePrice = currentHigh;
                        barsSinceExtreme = 0;
                    }
                    else
                    {
                        barsSinceExtreme++;

                        // 2. 连续 N 根未创新高：确认波峰，并将状态切切换为寻找波谷
                        if (barsSinceExtreme >= reversalBars)
                        {
                            peaksBuffer.Add(currentCandidateIdx);

                            state = 1;
                            currentCandidateIdx = i;
                            currentCandidatePrice = lows[i];
                            barsSinceExtreme = 0;
                        }
                    }
                }
                else // 寻找波谷
                {
                    decimal currentLow = lows[i];

                    // 1. 刷新更低点：零延迟移动候选波谷位置
                    if (currentLow <= currentCandidatePrice)
                    {
                        currentCandidateIdx = i;
                        currentCandidatePrice = currentLow;
                        barsSinceExtreme = 0;
                    }
                    else
                    {
                        barsSinceExtreme++;

                        // 2. 连续 N 根未创新低：确认波谷，并将状态切换为寻找波峰
                        if (barsSinceExtreme >= reversalBars)
                        {
                            valleysBuffer.Add(currentCandidateIdx);

                            state = 0;
                            currentCandidateIdx = i;
                            currentCandidatePrice = highs[i];
                            barsSinceExtreme = 0;
                        }
                    }
                }
            }
        }


        /// <summary>
        /// 结合零滞后状态机与分形校验的高性能极值计算
        /// </summary>
        /// <param name="highs">最高价 Span</param>
        /// <param name="lows">最低价 Span</param>
        /// <param name="peaksBuffer">波峰结果缓冲区</param>
        /// <param name="valleysBuffer">波谷结果缓冲区</param>
        /// <param name="reversalBars">零滞后状态机触发反转的 K 线数</param>
        /// <param name="fractalArm">分形二次确认所需的单侧 K 线数（如 2 代表左右各 2 根）</param>
        public static void CalculatePeaksCombinedFast(
            ReadOnlySpan<decimal> highs,
            ReadOnlySpan<decimal> lows,
            List<PeakValleyResult> peaksBuffer,
            List<PeakValleyResult> valleysBuffer,
            int reversalBars = 2,
            int fractalArm = 2)
        {
            peaksBuffer.Clear();
            valleysBuffer.Clear();

            int length = highs.Length;
            if (length < reversalBars + fractalArm + 1) return;

            int state = 0; // 0: 寻找波峰, 1: 寻找波谷
            int candidateIdx = 0;
            decimal candidatePrice = highs[0];
            int barsSinceExtreme = 0;

            for (int i = 1; i < length; i++)
            {
                if (state == 0) // 寻找波峰中
                {
                    if (highs[i] >= candidatePrice)
                    {
                        candidateIdx = i;
                        candidatePrice = highs[i];
                        barsSinceExtreme = 0;
                    }
                    else
                    {
                        barsSinceExtreme++;
                        // 状态机触发：说明右侧出现了回撤
                        if (barsSinceExtreme >= reversalBars)
                        {
                            // 结合分形：二次校验该候选点是否大于其左右两侧各 fractalArm 根 K 线
                            bool isFractal = ValidateFractalHigh(highs, candidateIdx, fractalArm);

                            peaksBuffer.Add(new PeakValleyResult
                            {
                                Index = candidateIdx,
                                Price = candidatePrice,
                                IsFractalConfirmed = isFractal
                            });

                            // 状态切换至寻找波谷
                            state = 1;
                            candidateIdx = i;
                            candidatePrice = lows[i];
                            barsSinceExtreme = 0;
                        }
                    }
                }
                else // 寻找波谷中
                {
                    if (lows[i] <= candidatePrice)
                    {
                        candidateIdx = i;
                        candidatePrice = lows[i];
                        barsSinceExtreme = 0;
                    }
                    else
                    {
                        barsSinceExtreme++;
                        if (barsSinceExtreme >= reversalBars)
                        {
                            // 结合分形：校验波谷
                            bool isFractal = ValidateFractalLow(lows, candidateIdx, fractalArm);

                            valleysBuffer.Add(new PeakValleyResult
                            {
                                Index = candidateIdx,
                                Price = candidatePrice,
                                IsFractalConfirmed = isFractal
                            });

                            state = 0;
                            candidateIdx = i;
                            candidatePrice = highs[i];
                            barsSinceExtreme = 0;
                        }
                    }
                }
            }
        }

        // 内联分形波峰校验（无额外内存开销）
        private static bool ValidateFractalHigh(ReadOnlySpan<decimal> highs, int centerIdx, int arm)
        {
            if (centerIdx - arm < 0 || centerIdx + arm >= highs.Length) return false;

            decimal target = highs[centerIdx];
            for (int j = centerIdx - arm; j <= centerIdx + arm; j++)
            {
                if (j == centerIdx) continue;
                if (highs[j] >= target) return false;
            }
            return true;
        }

        // 内联分形波谷校验
        private static bool ValidateFractalLow(ReadOnlySpan<decimal> lows, int centerIdx, int arm)
        {
            if (centerIdx - arm < 0 || centerIdx + arm >= lows.Length) return false;

            decimal target = lows[centerIdx];
            for (int j = centerIdx - arm; j <= centerIdx + arm; j++)
            {
                if (j == centerIdx) continue;
                if (lows[j] <= target) return false;
            }
            return true;
        }
        public struct PeakValleyResult
        {
            public int Index;
            public decimal Price;
            public bool IsFractalConfirmed; // 是否通过了严格的分形（左右K线）二次确认
        }
    }
}
