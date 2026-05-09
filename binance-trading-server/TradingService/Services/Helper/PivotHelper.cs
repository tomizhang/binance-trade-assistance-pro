using System.Collections.Generic;

namespace TradingTerminal.Utils
{
    public static class PivotHelper
    {
        /// <summary>
        /// 计算指定窗口期内的局部高点(Peaks)和低点(Valleys)
        /// 返回值为记录索引位置的 Tuple (Peaks, Valleys)
        /// </summary>
        /// <param name="highs">最高价集合</param>
        /// <param name="lows">最低价集合</param>
        /// <param name="leftLen">左侧比较K线根数</param>
        /// <param name="rightLen">右侧比较K线根数</param>
        public static (List<int> Peaks, List<int> Valleys) CalculatePeaks(
            IReadOnlyList<decimal> highs,
            IReadOnlyList<decimal> lows,
            int leftLen = 5,
            int rightLen = 5)
        {
            var peaks = new List<int>();
            var valleys = new List<int>();

            // 防御性检查
            if (highs == null || lows == null || highs.Count != lows.Count)
            {
                return (peaks, valleys);
            }

            int length = highs.Count; // 替代 JS 里的 times.length

            for (int i = leftLen; i < length - rightLen; i++)
            {
                bool isPeak = true;
                bool isValley = true;

                // 1. 判断是否为局部最高点
                for (int j = i - leftLen; j <= i + rightLen; j++)
                {
                    if (j == i) continue;

                    if (highs[j] >= highs[i])
                    {
                        isPeak = false;
                        break; // 只要发现有一个点比它高，立刻判定不是Peak，跳出内层循环
                    }
                }

                // 2. 判断是否为局部最低点
                for (int j = i - leftLen; j <= i + rightLen; j++)
                {
                    if (j == i) continue;

                    if (lows[j] <= lows[i])
                    {
                        isValley = false;
                        break; // 只要发现有一个点比它低，立刻判定不是Valley，跳出内层循环
                    }
                }

                // 3. 记录极值点的索引
                if (isPeak) peaks.Add(i);
                if (isValley) valleys.Add(i);
            }

            // 使用 C# 元组语法直接返回两个 List
            return (peaks, valleys);
        }
    }
}