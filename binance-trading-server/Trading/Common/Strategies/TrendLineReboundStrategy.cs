using Common.Helper;
using Common.Models;
using System;
using System.Collections.Generic;

namespace Common.Strategies
{
    /// <summary>
    /// 趋势线 Tick 级别穿透回弹交易策略 (在 Tick 中精准计算穿透、回弹与即刻消失逻辑)
    /// 规则：
    /// 1. 每当周期 K 线结束/更新时，使用 PivotPoint 计算高低点并由 TrendLineHelper 拟合生成趋势线；
    /// 2. 趋势线必须满足：跨度 LineX1X2 > 40 且 寿命 LineAge > 4，且在历史 K 线中未被穿过；
    /// 3. 在 Tick 中实时计算穿过与消失逻辑：
    ///    - 若 Tick 向下穿过支撑/阻力趋势线：
    ///      * 5 个 Tick 内回弹向上 -> 触发 做多 (Buy) 信号，并将该趋势线【立即删除消失】；
    ///      * 超过 5 个 Tick 未回弹 -> 判定为有效击穿失效，将该趋势线【立即删除消失】；
    ///    - 若 Tick 向上穿过趋势线：
    ///      * 5 个 Tick 内回落向下 -> 触发 做空 (Sell) 信号，并将该趋势线【立即删除消失】；
    ///      * 超过 5 个 Tick 未回落 -> 判定为有效击穿失效，将该趋势线【立即删除消失】；
    ///    - 被 Tick 穿透删除后的趋势线会被永久记录，在后续 K 线周期中不会再次重新生成复活；
    /// 4. 提供 GetAllValidTrendLines() 方法与 OnTrendLinesUpdated 事件，实时发回所有有效趋势线供图表展示。
    /// </summary>
    public class TrendLineReboundStrategy : StrategyBase
    {
        #region 策略可配置参数

        /// <summary>
        /// 趋势线最小跨度约束 (X2 - X1 > MinLineX1X2)
        /// </summary>
        public int MinLineX1X2 { get; set; } = 10;

        /// <summary>
        /// 趋势线最小寿命约束 (latestIndex - X2 > MinLineAge)
        /// </summary>
        public int MinLineAge { get; set; } = 3;

        /// <summary>
        /// 穿透后要求回弹的最大 Tick 计数窗口 (默认 5 个 tick 内)
        /// </summary>
        public int ReboundTicksWindow { get; set; } = 5;

        /// <summary>
        /// 波峰波谷左侧分形臂长
        /// </summary>
        public int PivotLeftLen { get; set; } = 3;

        /// <summary>
        /// 波峰波谷右侧分形臂长
        /// </summary>
        public int PivotRightLen { get; set; } = 3;

        /// <summary>
        /// 生成趋势线时的最大跨度 (至少保留500根K线跨度)
        /// </summary>
        public int MaxSpan { get; set; } = 500;

        #endregion

        #region 外部通知事件与公共访问方法 (发回所有有效趋势线供图表显示)

        /// <summary>
        /// 当有效趋势线列表发生变化时触发 (K线更新计算后，或Tick穿透消除后)
        /// </summary>
        public event Action<IReadOnlyList<TrendLine>>? OnTrendLinesUpdated;

        /// <summary>
        /// 🌟 获取当前策略监控中的所有有效存活趋势线列表 (供图表直接呈现)
        /// </summary>
        /// <returns>当前未被穿透且符合跨度/寿命约束的所有有效趋势线快照</returns>
        public IReadOnlyList<TrendLine> GetAllValidTrendLines()
        {
            lock (_stateLock)
            {
                var list = new List<TrendLine>(_activeTrackers.Count);
                foreach (var tracker in _activeTrackers)
                {
                    list.Add(tracker.Line);
                }
                return list;
            }
        }

        /// <summary>
        /// 🌟 分类获取当前策略监控中的有效阻力趋势线与有效支撑趋势线
        /// </summary>
        /// <returns>元组：(有效阻力趋势线列表, 有效支撑趋势线列表)</returns>
        public (IReadOnlyList<TrendLine> ResistanceLines, IReadOnlyList<TrendLine> SupportLines) GetValidTrendLinesCategorized()
        {
            lock (_stateLock)
            {
                var resList = new List<TrendLine>();
                var supList = new List<TrendLine>();

                foreach (var tracker in _activeTrackers)
                {
                    if (tracker.Line.IsResistance)
                    {
                        resList.Add(tracker.Line);
                    }
                    else
                    {
                        supList.Add(tracker.Line);
                    }
                }

                return (resList, supList);
            }
        }

        #endregion

        #region 内部状态管理

        private readonly List<TrendLineTracker> _activeTrackers = new List<TrendLineTracker>();
        private readonly HashSet<string> _destroyedLineKeys = new HashSet<string>();
        private readonly object _stateLock = new object();
        private decimal? _lastTickPrice;

        /// <summary>
        /// 当前受监控的活跃趋势线数量
        /// </summary>
        public int ActiveLinesCount
        {
            get
            {
                lock (_stateLock)
                {
                    return _activeTrackers.Count;
                }
            }
        }

        /// <summary>
        /// 获取当前所有存活的活跃趋势线快照 (已被 Tick 穿过消失的趋势线不会包含在内)
        /// </summary>
        public IReadOnlyList<TrendLine> ActiveLines => GetAllValidTrendLines();

        #endregion

        public TrendLineReboundStrategy(
            string symbol = "BTCUSDT",
            string interval = "30m",
            int minLineX1X2 = 10,
            int minLineAge = 3,
            int reboundTicksWindow = 5,
            int bufferCapacity = 500,
            int maxSpan = 500)
            : base("趋势线Tick回弹策略", symbol, interval, bufferCapacity: bufferCapacity)
        {
            MinLineX1X2 = minLineX1X2;
            MinLineAge = minLineAge;
            ReboundTicksWindow = reboundTicksWindow;
            MaxSpan = maxSpan;
        }

        #region 1. 周期 K 线结束/更新时：计算 PivotPoint 并生成未被穿透/销毁的有效趋势线

        protected override void OnKline(MarketKline kline, IReadOnlyList<MarketKline> klineHistory)
        {
            if (klineHistory.Count < MinLineX1X2 + MinLineAge + PivotLeftLen + PivotRightLen)
            {
                return;
            }

            int currentIndex = klineHistory.Count - 1;

            // 1. 使用 PivotPoint 计算高低点 (波峰与波谷)
            var (peaks, valleys) = PivotHelper.CalculatePeaks(klineHistory, PivotLeftLen, PivotRightLen);

            // 2. 使用 TrendLineHelper 拟合生成趋势线
            var (resistanceLines, supportLines) = TrendLineHelper.GenerateTrendLines(klineHistory, peaks, valleys, maxSpan: MaxSpan);

            // 3. 筛选满足条件 (LineX1X2 > 40 且 LineAge > 4 且 历史未被穿过且未被Tick穿透销毁) 的趋势线
            var qualifiedLines = new List<TrendLine>();

            lock (_stateLock)
            {
                foreach (var line in resistanceLines)
                {
                    string lineKey = GetLineKey(line);
                    // 若已被 Tick 穿透销毁，或在历史 K 线中已被穿过，则直接丢弃
                    if (_destroyedLineKeys.Contains(lineKey) || line.CollidedKlineIndex != -1)
                    {
                        continue;
                    }

                    if (line.LineX1X2 > MinLineX1X2 && line.LineAge > MinLineAge)
                    {
                        qualifiedLines.Add(line);
                    }
                }

                foreach (var line in supportLines)
                {
                    string lineKey = GetLineKey(line);
                    if (_destroyedLineKeys.Contains(lineKey) || line.CollidedKlineIndex != -1)
                    {
                        continue;
                    }

                    if (line.LineX1X2 > MinLineX1X2 && line.LineAge > MinLineAge)
                    {
                        qualifiedLines.Add(line);
                    }
                }

                // 4. 同步更新 Tick 跟踪器状态列表 (保留已有跟踪器的状态，追加新趋势线)
                var existingMap = new Dictionary<string, TrendLineTracker>();
                foreach (var t in _activeTrackers)
                {
                    existingMap[GetLineKey(t.Line)] = t;
                }

                _activeTrackers.Clear();
                foreach (var line in qualifiedLines)
                {
                    string key = GetLineKey(line);
                    if (existingMap.TryGetValue(key, out var existingTracker))
                    {
                        _activeTrackers.Add(existingTracker);
                    }
                    else
                    {
                        _activeTrackers.Add(new TrendLineTracker(line));
                    }
                }
            }

            Log($"[K线周期更新] 识别波峰:{peaks.Count}个, 波谷:{valleys.Count}个 | 保留存活趋势线: {qualifiedLines.Count} 条 (已排除被穿过销毁的线)");

            // 🌟 触发趋势线列表更新事件，发回最新所有有效趋势线
            NotifyTrendLinesUpdated();
        }

        #endregion

        #region 2. 逐笔 Tick 到达时：精准计算穿过与消失逻辑

        protected override void OnTick(MarketTick tick, IReadOnlyList<MarketTick> tickHistory)
        {
            decimal currentPrice = tick.Price;
            decimal prevPrice = _lastTickPrice ?? currentPrice;
            _lastTickPrice = currentPrice;

            bool linesChanged = false;

            lock (_stateLock)
            {
                if (_activeTrackers.Count == 0) return;

                int currentIndex = KlineHistory.Count > 0 ? KlineHistory.Count - 1 : 0;

                // 倒序遍历以支持在循环中高效删除穿过消失的趋势线
                for (int i = _activeTrackers.Count - 1; i >= 0; i--)
                {
                    var tracker = _activeTrackers[i];
                    var line = tracker.Line;

                    // 计算当前趋势线在当前 K 线索引位置的基准价格
                    decimal linePrice = line.GetPriceAt(currentIndex);

                    if (!tracker.IsPenetrated)
                    {
                        // 场景 A: 之前价格在线上方，当前 Tick 跌破趋势线 (向下穿透)
                        if (prevPrice >= linePrice && currentPrice < linePrice)
                        {
                            tracker.IsPenetrated = true;
                            tracker.PenetrationDirection = -1; // -1 代表向下穿透
                            tracker.TicksSincePenetration = 0;
                            tracker.PenetrationPrice = currentPrice;
                            tracker.HasTriggered = false;

                            Log($"[Tick穿过] 价格 {currentPrice:F2} 向下穿过趋势线 (线价: {linePrice:F2}) -> 开启 5-Tick 回弹监测");
                        }
                        // 场景 B: 之前价格在线下方，当前 Tick 突破趋势线 (向上穿透)
                        else if (prevPrice <= linePrice && currentPrice > linePrice)
                        {
                            tracker.IsPenetrated = true;
                            tracker.PenetrationDirection = 1; // 1 代表向上穿透
                            tracker.TicksSincePenetration = 0;
                            tracker.PenetrationPrice = currentPrice;
                            tracker.HasTriggered = false;

                            Log($"[Tick穿过] 价格 {currentPrice:F2} 向上穿过趋势线 (线价: {linePrice:F2}) -> 开启 5-Tick 回落监测");
                        }
                    }
                    else
                    {
                        // 处于穿透后的回弹监测计数中
                        tracker.TicksSincePenetration++;

                        if (tracker.TicksSincePenetration <= ReboundTicksWindow && !tracker.HasTriggered)
                        {
                            // 规则 1: 向下穿过趋势线，并在 5 个 Tick 内回弹向上 (做多 / Buy)
                            if (tracker.PenetrationDirection == -1 && currentPrice >= linePrice)
                            {
                                decimal stopLoss = tracker.PenetrationPrice * 0.995m;
                                decimal takeProfit = currentPrice * 1.015m;

                                EmitSignal(
                                    SignalType.Buy,
                                    currentPrice,
                                    quantity: 1,
                                    reason: $"Tick向下穿过趋势线并在 {tracker.TicksSincePenetration} 个Tick内回弹向上做多 (LineX1X2:{line.LineX1X2} > {MinLineX1X2}, LineAge:{line.LineAge} > {MinLineAge})",
                                    stopLoss: stopLoss,
                                    takeProfit: takeProfit,
                                    extra: line);

                                tracker.HasTriggered = true;

                                // 🌟 穿过回弹触发信号后，该趋势线【立即从监控列表中删除消失】
                                DeleteTrackerAt(i);
                                linesChanged = true;
                                Log($"[趋势线消失] 趋势线 {line.X1}->{line.X2} 产生做多信号后已完成使命并立即删除消失 (剩余存活: {_activeTrackers.Count} 条)");
                            }
                            // 规则 2: 向上穿过趋势线，并在 5 个 Tick 内回落向下 (做空 / Sell)
                            else if (tracker.PenetrationDirection == 1 && currentPrice <= linePrice)
                            {
                                decimal stopLoss = tracker.PenetrationPrice * 1.005m;
                                decimal takeProfit = currentPrice * 0.985m;

                                EmitSignal(
                                    SignalType.Sell,
                                    currentPrice,
                                    quantity: 1,
                                    reason: $"Tick向上穿过趋势线并在 {tracker.TicksSincePenetration} 个Tick内回落向下做空 (LineX1X2:{line.LineX1X2} > {MinLineX1X2}, LineAge:{line.LineAge} > {MinLineAge})",
                                    stopLoss: stopLoss,
                                    takeProfit: takeProfit,
                                    extra: line);

                                tracker.HasTriggered = true;

                                // 🌟 穿过回落触发信号后，该趋势线【立即从监控列表中删除消失】
                                DeleteTrackerAt(i);
                                linesChanged = true;
                                Log($"[趋势线消失] 趋势线 {line.X1}->{line.X2} 产生做空信号后已完成使命并立即删除消失 (剩余存活: {_activeTrackers.Count} 条)");
                            }
                        }
                        else if (tracker.TicksSincePenetration > ReboundTicksWindow)
                        {
                            // 🌟 超过 5 个 Tick 未发生有效回弹，判定趋势线被有效击穿，【立即从监控列表中删除消失】
                            DeleteTrackerAt(i);
                            linesChanged = true;
                            Log($"[趋势线消失] 趋势线 {line.X1}->{line.X2} 被Tick穿透后超过 {ReboundTicksWindow} 个Tick未回弹(判定有效击穿)，已立即删除消失 (剩余存活: {_activeTrackers.Count} 条)");
                        }
                    }
                }
            }

            if (linesChanged)
            {
                NotifyTrendLinesUpdated();
            }
        }

        private void DeleteTrackerAt(int index)
        {
            var line = _activeTrackers[index].Line;
            _destroyedLineKeys.Add(GetLineKey(line));
            _activeTrackers.RemoveAt(index);
        }

        private void NotifyTrendLinesUpdated()
        {
            var lines = GetAllValidTrendLines();
            OnTrendLinesUpdated?.Invoke(lines);
        }

        private static string GetLineKey(TrendLine line)
        {
            return $"{line.Time1:yyyyMMddHHmmss}_{line.Time2:yyyyMMddHHmmss}_{line.Type}";
        }

        #endregion

        protected override void OnReset()
        {
            lock (_stateLock)
            {
                _activeTrackers.Clear();
                _destroyedLineKeys.Clear();
                _lastTickPrice = null;
            }
            NotifyTrendLinesUpdated();
        }

        #region 内部趋势线穿透跟踪实体类

        private class TrendLineTracker
        {
            public TrendLine Line { get; }
            public bool IsPenetrated { get; set; }
            public int PenetrationDirection { get; set; } // -1: 向下穿透, 1: 向上穿透
            public int TicksSincePenetration { get; set; }
            public decimal PenetrationPrice { get; set; }
            public bool HasTriggered { get; set; }

            public TrendLineTracker(TrendLine line)
            {
                Line = line;
                IsPenetrated = false;
                PenetrationDirection = 0;
                TicksSincePenetration = 0;
                PenetrationPrice = 0;
                HasTriggered = false;
            }
        }

        #endregion
    }
}
