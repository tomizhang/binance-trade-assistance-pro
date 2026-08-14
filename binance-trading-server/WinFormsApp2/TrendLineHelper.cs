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
        /// 检查趋势线是否被任意 K 线穿透破位 (从 x1 + 1 到最新 K 线，排除 x2 锚点本身)
        /// 规则：
        /// - 高点阻力趋势线: 只要有任意 K 线的 HighPrice > TrendLinePrice(x)，说明该 K 线在图面上已向上穿透突破，判定为破位被删除；
        /// - 低点支撑趋势线: 只要有任意 K 线的 LowPrice < TrendLinePrice(x)，说明该 K 线在图面上已向下跌破穿透，判定为跌破被删除。
        /// </summary>
        public static bool IsTrendLinePenetrated(Kline[] klines, TrendLine tl)
        {
            if (klines == null || klines.Length == 0) return false;

            int totalKlines = klines.Length;

            // 逐根检查从 x1 + 1 到最新 K 线 (排除锚点 x2 本身)
            for (int x = tl.X1 + 1; x < totalKlines; x++)
            {
                if (x == tl.X2) continue; // 跳过终点锚点 x2 本身

                decimal linePrice = tl.GetPriceAt(x);
                var kline = klines[x];

                if (tl.Type == PivotType.High)
                {
                    // 高点阻力趋势线: 只要 K 线最高价 HighPrice 超过趋势线价格，图面上即为明显向上穿过破位
                    if (kline.HighPrice > linePrice)
                    {
                        return true;
                    }
                }
                else if (tl.Type == PivotType.Low)
                {
                    // 低点支撑趋势线: 只要 K 线最低价 LowPrice 跌破趋势线价格，图面上即为明显向下跌破穿透
                    if (kline.LowPrice < linePrice)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 根据枢轴高低点列表自动匹配生成所有未被后续 K 线穿透破位的“有效存活趋势线”
        /// (若 filterPenetrated 为 true，凡是被 K 线穿过/破位的趋势线一律自动剔除删除)
        /// </summary>
        public static List<TrendLine> GenerateTrendLinesFromPivots(
            Kline[] klines,
            List<PivotPoint> pivots,
            bool filterPenetrated = true)
        {
            List<TrendLine> result = new List<TrendLine>();
            if (klines == null || klines.Length == 0 || pivots == null || pivots.Count < 2)
            {
                return result;
            }

            // 1. 匹配高点阻力趋势线
            var highPivots = pivots.Where(p => p.Type == PivotType.High).OrderBy(p => p.Index).ToList();
            for (int i = 0; i < highPivots.Count - 1; i++)
            {
                for (int j = i + 1; j < highPivots.Count; j++)
                {
                    var p1 = highPivots[i];
                    var p2 = highPivots[j];

                    var tl = CreateTrendLine(klines, p1.Index, p1.Price, p1.Time, p2.Index, p2.Price, p2.Time, PivotType.High);

                    // 交互检查: 若被任意 K 线（中间或延伸段）穿透突破，则自动删除剔除
                    if (filterPenetrated && IsTrendLinePenetrated(klines, tl))
                    {
                        continue;
                    }

                    result.Add(tl);
                }
            }

            // 2. 匹配低点支撑趋势线
            var lowPivots = pivots.Where(p => p.Type == PivotType.Low).OrderBy(p => p.Index).ToList();
            for (int i = 0; i < lowPivots.Count - 1; i++)
            {
                for (int j = i + 1; j < lowPivots.Count; j++)
                {
                    var p1 = lowPivots[i];
                    var p2 = lowPivots[j];

                    var tl = CreateTrendLine(klines, p1.Index, p1.Price, p1.Time, p2.Index, p2.Price, p2.Time, PivotType.Low);

                    // 交互检查: 若被任意 K 线（中间或延伸段）穿透跌破，则自动删除剔除
                    if (filterPenetrated && IsTrendLinePenetrated(klines, tl))
                    {
                        continue;
                    }

                    result.Add(tl);
                }
            }

            return result;
        }
    }
}
