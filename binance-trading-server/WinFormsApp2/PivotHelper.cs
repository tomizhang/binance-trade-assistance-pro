using System;
using System.Collections.Generic;
using System.Linq;

namespace WinFormsApp2
{
    /// <summary>
    /// 枢轴点 (相对高点 / 相对低点) 类型
    /// </summary>
    public enum PivotType
    {
        High,
        Low
    }

    /// <summary>
    /// 枢轴高低点结构体
    /// </summary>
    public struct PivotPoint
    {
        /// <summary>
        /// K 线数组中的索引位置
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// 对应 K 线的开盘时间
        /// </summary>
        public DateTime Time { get; set; }

        /// <summary>
        /// 枢轴点价格 (相对高点使用 HighPrice，相对低点使用 LowPrice)
        /// </summary>
        public decimal Price { get; set; }

        /// <summary>
        /// 枢轴点类型 (High / Low)
        /// </summary>
        public PivotType Type { get; set; }
    }

    /// <summary>
    /// K线相对高点与相对低点计算帮助类
    /// </summary>
    public static class PivotHelper
    {
        /// <summary>
        /// 低延迟单次内存全扫合并算法 (CalculatePeaksCombinedFast)：
        /// 并行并发计算高点与低点，带双重早退剪枝，实现极低延迟高频性能。
        /// 规则：相对高点使用 HighPrice，相对低点使用 LowPrice
        /// </summary>
        public static List<PivotPoint> CalculatePeaksCombinedFast(Kline[] klines, int leftBars = 3, int rightBars = 3)
        {
            List<PivotPoint> pivots = new List<PivotPoint>();
            if (klines == null || klines.Length < (leftBars + rightBars + 1))
            {
                return pivots;
            }

            int count = klines.Length;
            int maxIdx = count - rightBars;

            for (int i = leftBars; i < maxIdx; i++)
            {
                decimal currHigh = klines[i].HighPrice;
                decimal currLow = klines[i].LowPrice;

                bool isHigh = true;
                bool isLow = true;

                // 左侧窗口扫描带双重早退剪枝
                for (int l = i - leftBars; l < i; l++)
                {
                    if (isHigh && klines[l].HighPrice >= currHigh)
                    {
                        isHigh = false;
                    }
                    if (isLow && klines[l].LowPrice <= currLow)
                    {
                        isLow = false;
                    }
                    if (!isHigh && !isLow) break;
                }

                // 右侧窗口扫描带双重早退剪枝
                if (isHigh || isLow)
                {
                    for (int r = i + 1; r <= i + rightBars; r++)
                    {
                        if (isHigh && klines[r].HighPrice > currHigh)
                        {
                            isHigh = false;
                        }
                        if (isLow && klines[r].LowPrice < currLow)
                        {
                            isLow = false;
                        }
                        if (!isHigh && !isLow) break;
                    }
                }

                if (isHigh)
                {
                    pivots.Add(new PivotPoint
                    {
                        Index = i,
                        Time = klines[i].OpenTime,
                        Price = currHigh, // 相对高点使用 HighPrice
                        Type = PivotType.High
                    });
                }

                if (isLow)
                {
                    pivots.Add(new PivotPoint
                    {
                        Index = i,
                        Time = klines[i].OpenTime,
                        Price = currLow, // 相对低点使用 LowPrice
                        Type = PivotType.Low
                    });
                }
            }

            return pivots;
        }

        /// <summary>
        /// 计算 K 线数组中的所有相对高点与相对低点 (调用低延迟算法 CalculatePeaksCombinedFast)
        /// </summary>
        public static List<PivotPoint> CalculatePivotPoints(Kline[] klines, int leftBars = 3, int rightBars = 3)
        {
            return CalculatePeaksCombinedFast(klines, leftBars, rightBars);
        }

        /// <summary>
        /// 只提取相对高点 (Pivot High)，价格使用 HighPrice
        /// </summary>
        public static List<PivotPoint> GetPivotHighs(Kline[] klines, int leftBars = 3, int rightBars = 3)
        {
            return CalculatePeaksCombinedFast(klines, leftBars, rightBars)
                .Where(p => p.Type == PivotType.High)
                .ToList();
        }

        /// <summary>
        /// 只提取相对低点 (Pivot Low)，价格使用 LowPrice
        /// </summary>
        public static List<PivotPoint> GetPivotLows(Kline[] klines, int leftBars = 3, int rightBars = 3)
        {
            return CalculatePeaksCombinedFast(klines, leftBars, rightBars)
                .Where(p => p.Type == PivotType.Low)
                .ToList();
        }
    }
}
