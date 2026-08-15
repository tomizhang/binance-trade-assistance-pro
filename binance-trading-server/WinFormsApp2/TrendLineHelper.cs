using System;
using System.Collections.Generic;

namespace WinFormsApp2
{
    /// <summary>
    /// 根据 K线 和点位生成与分析趋势线的帮助类 (极致性能提前剪枝与 0 LINQ 算法)
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

            // 2. 归一化百分比斜率 (K)
            decimal normalizedK = 0m;
            if (y1 != 0m && lineX1X2 != 0)
            {
                normalizedK = ((y2 - y1) / y1 / lineX1X2) * 100m;
            }

            // 3. 趋势线向后延伸穿过/碰撞第一根 K 线的范围 (line_extension_range)
            int lineExtensionRange = latestIndex - x2;
            int collidedIndex = -1;

            for (int x = x2 + 1; x < totalKlines; x++)
            {
                decimal linePriceAtX = y1 + rawK * (x - x1);
                ref readonly Kline kline = ref klines[x];

                // 碰撞/穿透判定: 延伸线价格处于该根 K 线的 [LowPrice, HighPrice] 之间
                if (linePriceAtX >= kline.LowPrice && linePriceAtX <= kline.HighPrice)
                {
                    collidedIndex = x;
                    lineExtensionRange = x - x2;
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
        /// </summary>
        public static bool IsTrendLinePenetrated(Kline[] klines, TrendLine tl)
        {
            if (klines == null || klines.Length == 0) return false;

            int totalKlines = klines.Length;
            decimal rawK = tl.RawK;
            decimal y1 = tl.Y1;
            int x1 = tl.X1;
            int x2 = tl.X2;

            for (int x = x1 + 1; x < totalKlines; x++)
            {
                if (x == x2) continue; // 跳过终点锚点 x2 本身

                decimal linePrice = y1 + rawK * (x - x1);
                ref readonly Kline kline = ref klines[x];

                if (tl.Type == PivotType.High)
                {
                    if (kline.HighPrice > linePrice)
                    {
                        return true;
                    }
                }
                else if (tl.Type == PivotType.Low)
                {
                    if (kline.LowPrice < linePrice)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 高性能生成未破位有效趋势线算法 (采用提前剪枝 Early-Pruning、单次遍历分组与 0 LINQ 开销)
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

            int totalKlines = klines.Length;
            int latestIndex = totalKlines - 1;

            // 1. 单次遍历拆分高低枢轴点 (免除 LINQ Where / OrderBy，O(N) 极速响应)
            List<PivotPoint> highPivots = new List<PivotPoint>(pivots.Count);
            List<PivotPoint> lowPivots = new List<PivotPoint>(pivots.Count);

            for (int i = 0; i < pivots.Count; i++)
            {
                var p = pivots[i];
                if (p.Type == PivotType.High) highPivots.Add(p);
                else if (p.Type == PivotType.Low) lowPivots.Add(p);
            }

            // 2. 匹配高点阻力趋势线 (提前剪枝)
            ProcessPivotGroup(klines, highPivots, PivotType.High, filterPenetrated, totalKlines, latestIndex, result);

            // 3. 匹配低点支撑趋势线 (提前剪枝)
            ProcessPivotGroup(klines, lowPivots, PivotType.Low, filterPenetrated, totalKlines, latestIndex, result);

            return result;
        }

        private static void ProcessPivotGroup(
            Kline[] klines,
            List<PivotPoint> pivotList,
            PivotType type,
            bool filterPenetrated,
            int totalKlines,
            int latestIndex,
            List<TrendLine> result)
        {
            int count = pivotList.Count;
            for (int i = 0; i < count - 1; i++)
            {
                var p1 = pivotList[i];
                int x1 = p1.Index;
                decimal y1 = p1.Price;

                for (int j = i + 1; j < count; j++)
                {
                    var p2 = pivotList[j];
                    int x2 = p2.Index;
                    decimal y2 = p2.Price;

                    if (x1 >= x2) continue;

                    int lineX1X2 = x2 - x1;
                    decimal rawK = (y2 - y1) / lineX1X2;

                    // A. 破位检测与提前剪枝 (Early-Pruning): 发现首个破位点立即跳出循环，阻断后方所有无用运算
                    bool isPenetrated = false;
                    if (filterPenetrated)
                    {
                        for (int x = x1 + 1; x < totalKlines; x++)
                        {
                            if (x == x2) continue; // 排除锚点 x2

                            decimal linePriceAtX = y1 + rawK * (x - x1);
                            ref readonly Kline k = ref klines[x];

                            if (type == PivotType.High)
                            {
                                if (k.HighPrice > linePriceAtX)
                                {
                                    isPenetrated = true;
                                    break; // 发现突破立刻剪枝早退！
                                }
                            }
                            else // PivotType.Low
                            {
                                if (k.LowPrice < linePriceAtX)
                                {
                                    isPenetrated = true;
                                    break; // 发现跌破立刻剪枝早退！
                                }
                            }
                        }
                    }

                    if (isPenetrated) continue;

                    // B. 计算延伸碰撞范围 (仅针对验证有效的存活趋势线进行延伸计算)
                    int lineExtensionRange = latestIndex - x2;
                    int collidedIndex = -1;

                    for (int x = x2 + 1; x < totalKlines; x++)
                    {
                        decimal linePriceAtX = y1 + rawK * (x - x1);
                        ref readonly Kline k = ref klines[x];

                        if (linePriceAtX >= k.LowPrice && linePriceAtX <= k.HighPrice)
                        {
                            collidedIndex = x;
                            lineExtensionRange = x - x2;
                            break;
                        }
                    }

                    decimal normalizedK = y1 != 0m ? ((y2 - y1) / y1 / lineX1X2) * 100m : 0m;

                    result.Add(new TrendLine
                    {
                        X1 = x1,
                        Y1 = y1,
                        Time1 = p1.Time,
                        X2 = x2,
                        Y2 = y2,
                        Time2 = p2.Time,
                        RawK = rawK,
                        K = normalizedK,
                        LineX1X2 = lineX1X2,
                        LineAge = latestIndex - x2,
                        LineExtensionRange = lineExtensionRange,
                        CollidedKlineIndex = collidedIndex,
                        Type = type
                    });
                }
            }
        }
    }
}
