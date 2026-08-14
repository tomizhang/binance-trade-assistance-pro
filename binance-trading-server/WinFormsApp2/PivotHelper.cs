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
        /// 计算 K 线数组中的所有相对高点与相对低点 (枢轴点)
        /// 规则：相对高点使用 HighPrice，相对低点使用 LowPrice
        /// </summary>
        /// <param name="klines">K线数据数组</param>
        /// <param name="leftBars">左侧比较的 K 线数量 (默认 2)</param>
        /// <param name="rightBars">右侧比较的 K 线数量 (默认 2)</param>
        /// <returns>枢轴高低点列表</returns>
        public static List<PivotPoint> CalculatePivotPoints(Kline[] klines, int leftBars = 2, int rightBars = 2)
        {
            List<PivotPoint> pivots = new List<PivotPoint>();
            if (klines == null || klines.Length < (leftBars + rightBars + 1))
            {
                return pivots;
            }

            for (int i = leftBars; i < klines.Length - rightBars; i++)
            {
                // 1. 计算相对高点 (Pivot High) - 必须使用 HighPrice
                bool isHigh = true;
                decimal currentHigh = klines[i].HighPrice;

                for (int l = i - leftBars; l < i; l++)
                {
                    if (klines[l].HighPrice >= currentHigh)
                    {
                        isHigh = false;
                        break;
                    }
                }

                if (isHigh)
                {
                    for (int r = i + 1; r <= i + rightBars; r++)
                    {
                        if (klines[r].HighPrice > currentHigh)
                        {
                            isHigh = false;
                            break;
                        }
                    }
                }

                if (isHigh)
                {
                    pivots.Add(new PivotPoint
                    {
                        Index = i,
                        Time = klines[i].OpenTime,
                        Price = currentHigh, // 相对高点使用 HighPrice
                        Type = PivotType.High
                    });
                }

                // 2. 计算相对低点 (Pivot Low) - 必须使用 LowPrice
                bool isLow = true;
                decimal currentLow = klines[i].LowPrice;

                for (int l = i - leftBars; l < i; l++)
                {
                    if (klines[l].LowPrice <= currentLow)
                    {
                        isLow = false;
                        break;
                    }
                }

                if (isLow)
                {
                    for (int r = i + 1; r <= i + rightBars; r++)
                    {
                        if (klines[r].LowPrice < currentLow)
                        {
                            isLow = false;
                            break;
                        }
                    }
                }

                if (isLow)
                {
                    pivots.Add(new PivotPoint
                    {
                        Index = i,
                        Time = klines[i].OpenTime,
                        Price = currentLow, // 相对低点使用 LowPrice
                        Type = PivotType.Low
                    });
                }
            }

            return pivots;
        }

        /// <summary>
        /// 只提取相对高点 (Pivot High)，价格使用 HighPrice
        /// </summary>
        public static List<PivotPoint> GetPivotHighs(Kline[] klines, int leftBars = 2, int rightBars = 2)
        {
            return CalculatePivotPoints(klines, leftBars, rightBars)
                .Where(p => p.Type == PivotType.High)
                .ToList();
        }

        /// <summary>
        /// 只提取相对低点 (Pivot Low)，价格使用 LowPrice
        /// </summary>
        public static List<PivotPoint> GetPivotLows(Kline[] klines, int leftBars = 2, int rightBars = 2)
        {
            return CalculatePivotPoints(klines, leftBars, rightBars)
                .Where(p => p.Type == PivotType.Low)
                .ToList();
        }
    }
}
