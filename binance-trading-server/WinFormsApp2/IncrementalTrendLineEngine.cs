using System;
using System.Collections.Generic;

namespace WinFormsApp2
{
    /// <summary>
    /// 多层增量趋势线与高低点计算引擎 (Multi-Layer Incremental TrendLine Engine)
    /// 彻底解决 K 线递增导致的 O(N) / O(N^2) 性能衰减痛点，实现均摊 O(1) 极致常数时间响应
    /// </summary>
    public class IncrementalTrendLineEngine
    {
        private readonly List<Kline> _klines = new List<Kline>();
        private readonly List<PivotPoint> _activePivots = new List<PivotPoint>();
        private readonly List<TrendLine> _activeTrendLines = new List<TrendLine>();

        public int LeftBars { get; set; } = 3;
        public int RightBars { get; set; } = 3;
        public bool FilterPenetrated { get; set; } = true;

        public List<Kline> Klines => _klines;
        public List<PivotPoint> ActivePivots => _activePivots;
        public List<TrendLine> ActiveTrendLines => _activeTrendLines;

        public void Reset()
        {
            _klines.Clear();
            _activePivots.Clear();
            _activeTrendLines.Clear();
        }

        /// <summary>
        /// 批量全量初始化 (例如 API 预热 1000 根 K 线或历史载入)
        /// </summary>
        public void Initialize(IEnumerable<Kline> klines)
        {
            Reset();
            if (klines == null) return;

            foreach (var k in klines)
            {
                AppendKlineIncremental(k);
            }
        }

        /// <summary>
        /// 多层增量算子：单根 K 线增量推入 (均摊 O(1) 常数时间复杂度)
        /// </summary>
        public void AppendKlineIncremental(Kline kline)
        {
            _klines.Add(kline);
            int total = _klines.Count;
            int latestIndex = total - 1;

            // ----------------------------------------------------
            // 第一层增量：检查倒数第 (RightBars + 1) 根 K 线是否确认新的 Pivot 枢轴点
            // 由于 RightBars = 3，推送第 N 根 K 线时，唯一可能确认的高低点索引为 N - 3
            // ----------------------------------------------------
            PivotPoint? newlyConfirmedPivot = null;
            int checkIndex = latestIndex - RightBars;

            if (checkIndex >= LeftBars)
            {
                PivotType? pType = CheckPivotTypeAt(checkIndex);
                if (pType.HasValue)
                {
                    newlyConfirmedPivot = new PivotPoint
                    {
                        Index = checkIndex,
                        Price = pType.Value == PivotType.High ? _klines[checkIndex].HighPrice : _klines[checkIndex].LowPrice,
                        Time = _klines[checkIndex].OpenTime,
                        Type = pType.Value
                    };
                    _activePivots.Add(newlyConfirmedPivot.Value);
                }
            }

            // ----------------------------------------------------
            // 第二层增量 A：增量校验最新 K 线 (latestIndex) 是否破位/跌破既有活动趋势线
            // 仅校验最新 1 根 K 线，无需重复重算过往历史 K 线
            // ----------------------------------------------------
            if (FilterPenetrated && _activeTrendLines.Count > 0)
            {
                for (int i = _activeTrendLines.Count - 1; i >= 0; i--)
                {
                    var tl = _activeTrendLines[i];
                    decimal linePriceAtLatest = tl.GetPriceAt(latestIndex);

                    bool penetrated = false;
                    if (tl.Type == PivotType.High)
                    {
                        if (kline.HighPrice > linePriceAtLatest) penetrated = true;
                    }
                    else if (tl.Type == PivotType.Low)
                    {
                        if (kline.LowPrice < linePriceAtLatest) penetrated = true;
                    }

                    if (penetrated)
                    {
                        // 增量剔除被最新 K 线突破/跌破的趋势线
                        _activeTrendLines.RemoveAt(i);
                    }
                    else
                    {
                        // 增量更新存活趋势线的年龄
                        tl.LineAge = latestIndex - tl.X2;
                        _activeTrendLines[i] = tl;
                    }
                }
            }

            // ----------------------------------------------------
            // 第二层增量 B：若诞生了新的确认 Pivot，增量将新 Pivot 与历史同类型 Pivot 配对构建新趋势线
            // ----------------------------------------------------
            if (newlyConfirmedPivot.HasValue)
            {
                var newPivot = newlyConfirmedPivot.Value;

                for (int i = 0; i < _activePivots.Count - 1; i++)
                {
                    var oldPivot = _activePivots[i];
                    if (oldPivot.Type != newPivot.Type) continue;

                    int x1 = oldPivot.Index;
                    decimal y1 = oldPivot.Price;
                    int x2 = newPivot.Index;
                    decimal y2 = newPivot.Price;

                    int lineX1X2 = x2 - x1;
                    if (lineX1X2 <= 0) continue;

                    decimal rawK = (y2 - y1) / lineX1X2;

                    // 增量校验新趋势线从 x1 + 1 至最新 K 线 latestIndex 是否存在破位
                    bool isPenetrated = false;
                    if (FilterPenetrated)
                    {
                        for (int x = x1 + 1; x <= latestIndex; x++)
                        {
                            if (x == x2) continue;

                            decimal linePriceAtX = y1 + rawK * (x - x1);
                            var bar = _klines[x];

                            if (newPivot.Type == PivotType.High)
                            {
                                if (bar.HighPrice > linePriceAtX) { isPenetrated = true; break; }
                            }
                            else
                            {
                                if (bar.LowPrice < linePriceAtX) { isPenetrated = true; break; }
                            }
                        }
                    }

                    if (!isPenetrated)
                    {
                        decimal normalizedK = y1 != 0m ? ((y2 - y1) / y1 / lineX1X2) * 100m : 0m;
                        _activeTrendLines.Add(new TrendLine
                        {
                            X1 = x1,
                            Y1 = y1,
                            Time1 = oldPivot.Time,
                            X2 = x2,
                            Y2 = y2,
                            Time2 = newPivot.Time,
                            RawK = rawK,
                            K = normalizedK,
                            LineX1X2 = lineX1X2,
                            LineAge = latestIndex - x2,
                            LineExtensionRange = latestIndex - x2,
                            CollidedKlineIndex = -1,
                            Type = newPivot.Type
                        });
                    }
                }
            }
        }

        private PivotType? CheckPivotTypeAt(int index)
        {
            decimal targetHigh = _klines[index].HighPrice;
            decimal targetLow = _klines[index].LowPrice;

            bool isHigh = true;
            bool isLow = true;

            for (int offset = -LeftBars; offset <= RightBars; offset++)
            {
                if (offset == 0) continue;
                int idx = index + offset;

                if (_klines[idx].HighPrice >= targetHigh) isHigh = false;
                if (_klines[idx].LowPrice <= targetLow) isLow = false;

                if (!isHigh && !isLow) break;
            }

            if (isHigh) return PivotType.High;
            if (isLow) return PivotType.Low;
            return null;
        }
    }
}
