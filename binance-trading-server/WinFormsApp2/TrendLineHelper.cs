using System;
using System.Collections.Generic;
using System.Linq;

namespace WinFormsApp2
{
    /// <summary>
    /// 根据 K线 和点位生成与分析趋势线的帮助类
    /// </summary>
    public static class TrendLineHelper
    {
        /// <summary>
        /// 根据给定的两点坐标在 K线数据上构建包含斜率、年龄与碰撞延伸范围的 TrendLine 对象
        /// </summary>
        public static TrendLine CreateTrendLine(
            Kline[] klines,
            int x1,
            decimal y1,
            DateTime time1,
            int x2,
            decimal y2,
            DateTime time2,
            PivotType type)
        {
            if (klines == null || klines.Length == 0)
            {
                throw new ArgumentException("K线数据不能为空", nameof(klines));
            }

            if (x1 >= x2)
            {
                var tempX = x1; x1 = x2; x2 = tempX;
                var tempY = y1; y1 = y2; y2 = tempY;
                var tempT = time1; time1 = time2; time2 = tempT;
            }

            int lineX1X2 = x2 - x1;
            int totalKlines = klines.Length;
            int latestIndex = totalKlines - 1;
            int lineAge = latestIndex - x2;

            // 1. 绝对价格原始斜率 (RawK)
            decimal rawK = lineX1X2 != 0 ? (y2 - y1) / lineX1X2 : 0m;

            // 2. 归一化百分比斜率 (K): 解决 BTC (90,000) 与微值币 (0.00001) 价格量级过大/过小导致的斜率失真与不可比问题
            // 公式: K = ((y2 - y1) / y1) / (x2 - x1) * 100
            decimal normalizedK = 0m;
            if (y1 != 0m && lineX1X2 != 0)
            {
                normalizedK = ((y2 - y1) / y1 / lineX1X2) * 100m;
            }

            // 3. 趋势线向后延伸穿过/碰撞第一根 K 线的范围 (line_extension_range)
            int lineExtensionRange = latestIndex - x2; // 默认延伸至最新点的距离
            int collidedIndex = -1;

            for (int x = x2 + 1; x < totalKlines; x++)
            {
                decimal linePriceAtX = y1 + rawK * (x - x1);
                var kline = klines[x];

                // 碰撞/穿透判定: 延伸线价格处于该根 K 线的 [LowPrice, HighPrice] 之间
                if (linePriceAtX >= kline.LowPrice && linePriceAtX <= kline.HighPrice)
                {
                    collidedIndex = x;
                    lineExtensionRange = x - x2; // 首次碰撞第一根 K 线的范围
                    break;
                }
            }

            return new TrendLine
            {
                X1 = x1,
                Y1 = y1,
                Time1 = time1,
                X2 = x2,
                Y2 = y2,
                Time2 = time2,
                RawK = rawK,
                K = normalizedK,
                LineX1X2 = lineX1X2,
                LineAge = lineAge,
                LineExtensionRange = lineExtensionRange,
                CollidedKlineIndex = collidedIndex,
                Type = type
            };
        }

        /// <summary>
        /// 根据枢轴高低点列表自动匹配生成所有相对高点阻力趋势线与相对低点支撑趋势线
        /// </summary>
        public static List<TrendLine> GenerateTrendLinesFromPivots(Kline[] klines, List<PivotPoint> pivots)
        {
            List<TrendLine> trendLines = new List<TrendLine>();
            if (klines == null || klines.Length == 0 || pivots == null || pivots.Count < 2)
            {
                return trendLines;
            }

            // 提取所有高点 (Pivot Highs) 生成高点阻力趋势线
            var highPivots = pivots.Where(p => p.Type == PivotType.High).OrderBy(p => p.Index).ToList();
            for (int i = 0; i < highPivots.Count - 1; i++)
            {
                for (int j = i + 1; j < highPivots.Count; j++)
                {
                    var p1 = highPivots[i];
                    var p2 = highPivots[j];
                    trendLines.Add(CreateTrendLine(klines, p1.Index, p1.Price, p1.Time, p2.Index, p2.Price, p2.Time, PivotType.High));
                }
            }

            // 提取所有低点 (Pivot Lows) 生成低点支撑趋势线
            var lowPivots = pivots.Where(p => p.Type == PivotType.Low).OrderBy(p => p.Index).ToList();
            for (int i = 0; i < lowPivots.Count - 1; i++)
            {
                for (int j = i + 1; j < lowPivots.Count; j++)
                {
                    var p1 = lowPivots[i];
                    var p2 = lowPivots[j];
                    trendLines.Add(CreateTrendLine(klines, p1.Index, p1.Price, p1.Time, p2.Index, p2.Price, p2.Time, PivotType.Low));
                }
            }

            return trendLines;
        }
    }
}
