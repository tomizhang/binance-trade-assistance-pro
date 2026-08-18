using Common.Helper;
using Common.Models;
using System;
using System.Collections.Generic;

namespace Common.Strategies
{
    /// <summary>
    /// 趋势线 Tick 级别穿透回弹交易策略 (支持趋势线被穿过/使用后自动删除机制)
    /// 规则：
    /// 1. 每当周期 K 线结束/更新时，使用 PivotPoint 计算高低点并由 TrendLineHelper 拟合生成趋势线；
    /// 2. 趋势线必须满足：跨度 LineX1X2 > 40 且 寿命 LineAge > 4，且在历史 K 线中未被穿透 (CollidedKlineIndex == -1)；
    /// 3. 处理实时 Tick 逐笔数据时：
    ///    - 若 Tick 向下穿过趋势线，并在 5 个 Tick 内发生回弹向上，则触发 做多 (Buy) 信号，并将该趋势线删除；
    ///    - 若 Tick 向上穿过趋势线，并在 5 个 Tick 内发生回落向下，则触发 做空 (Sell) 信号，并将该趋势线删除；
    ///    - 若 Tick 穿过趋势线后超过 5 个 Tick 仍未回弹（有效击穿/失效），则自动将该趋势线直接删除。
    /// </summary>
    public class TrendLineReboundStrategy : StrategyBase
    {
        #region 策略可配置参数

        /// <summary>
        /// 趋势线最小跨度约束 (X2 - X1 > 40)
        /// </summary>
        public int MinLineX1X2 { get; set; } = 40;

        /// <summary>
        /// 趋势线最小寿命约束 (latestIndex - X2 > 4)
        /// </summary>
        public int MinLineAge { get; set; } = 4;

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
        /// 生成趋势线时的最大跨度
        /// </summary>
        public int MaxSpan { get; set; } = 100;

        #endregion

        #region 内部状态管理

        private readonly List<TrendLineTracker> _activeTrackers = new List<TrendLineTracker>();
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
        /// 获取当前所有存活的活跃趋势线快照
        /// </summary>
        public IReadOnlyList<TrendLine> ActiveLines
        {
            get
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
        }

        #endregion

        public TrendLineReboundStrategy(
            string symbol = "BTCUSDT",
            string interval = "30m",
            int minLineX1X2 = 40,
            int minLineAge = 4,
            int reboundTicksWindow = 5)
            : base("趋势线Tick回弹策略", symbol, interval, bufferCapacity: 100)
        {
            MinLineX1X2 = minLineX1X2;
            MinLineAge = minLineAge;
            ReboundTicksWindow = reboundTicksWindow;
        }

        #region 1. 周期 K 线结束/更新时：计算 PivotPoint 并生成未被穿透的有效趋势线

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

            // 3. 筛选满足条件 (LineX1X2 > 40 且 LineAge > 4 且 历史未被穿过 CollidedKlineIndex == -1) 的趋势线
            var qualifiedLines = new List<TrendLine>();

            foreach (var line in resistanceLines)
            {
                // 若趋势线在历史 K 线中已被穿过，则直接剔除/删除
                if (line.CollidedKlineIndex != -1) continue;

                if (line.LineX1X2 > MinLineX1X2 && line.LineAge > MinLineAge)
                {
                    qualifiedLines.Add(line);
                }
            }

            foreach (var line in supportLines)
            {
                // 若趋势线在历史 K 线中已被穿过，则直接剔除/删除
                if (line.CollidedKlineIndex != -1) continue;

                if (line.LineX1X2 > MinLineX1X2 && line.LineAge > MinLineAge)
                {
                    qualifiedLines.Add(line);
                }
            }

            // 4. 同步更新 Tick 跟踪器状态列表
            lock (_stateLock)
            {
                _activeTrackers.Clear();
                foreach (var line in qualifiedLines)
                {
                    _activeTrackers.Add(new TrendLineTracker(line));
                }
            }

            //Log($"[K线周期更新] 识别波峰:{peaks.Count}个, 波谷:{valleys.Count}个 | 保留未穿透合格趋势线: {qualifiedLines.Count} 条 (已自动删除历史被穿过趋势线)");
        }

        #endregion

        #region 2. 逐笔 Tick 到达时：检测穿透，回弹触发或超时未回弹时均删除趋势线

        protected override void OnTick(MarketTick tick, IReadOnlyList<MarketTick> tickHistory)
        {
            decimal currentPrice = tick.Price;
            decimal prevPrice = _lastTickPrice ?? currentPrice;
            _lastTickPrice = currentPrice;

            lock (_stateLock)
            {
                if (_activeTrackers.Count == 0) return;

                int currentIndex = KlineHistory.Count > 0 ? KlineHistory.Count - 1 : 0;

                // 倒序遍历以支持在循环中高效删除失效或已触发的趋势线
                for (int i = _activeTrackers.Count - 1; i >= 0; i--)
                {
                    var tracker = _activeTrackers[i];
                    var line = tracker.Line;

                    // 计算当前趋势线在当前 K 线索引位置的基准价格
                    decimal linePrice = line.GetPriceAt(currentIndex);

                    if (!tracker.IsPenetrated)
                    {
                        // 场景 A: 之前在上方，当前 Tick 向下穿过趋势线 (prev >= linePrice && current < linePrice)
                        if (prevPrice >= linePrice && currentPrice < linePrice)
                        {
                            tracker.IsPenetrated = true;
                            tracker.PenetrationDirection = -1; // -1 代表向下穿透
                            tracker.TicksSincePenetration = 0;
                            tracker.PenetrationPrice = currentPrice;
                            tracker.HasTriggered = false;

                            Log($"[Tick穿透] 价格 {currentPrice:F2} 向下穿过趋势线 (基准线价: {linePrice:F2}) -> 开启 5-Tick 回弹监测");
                        }
                        // 场景 B: 之前在下方，当前 Tick 向上穿过趋势线 (prev <= linePrice && current > linePrice)
                        else if (prevPrice <= linePrice && currentPrice > linePrice)
                        {
                            tracker.IsPenetrated = true;
                            tracker.PenetrationDirection = 1; // 1 代表向上穿透
                            tracker.TicksSincePenetration = 0;
                            tracker.PenetrationPrice = currentPrice;
                            tracker.HasTriggered = false;

                            Log($"[Tick穿透] 价格 {currentPrice:F2} 向上穿过趋势线 (基准线价: {linePrice:F2}) -> 开启 5-Tick 回落监测");
                        }
                    }
                    else
                    {
                        // 处于穿透后的回弹监测窗口中
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

                                // 🌟 触发信号后，该趋势线已被穿过并完成使命，直接删除
                                _activeTrackers.RemoveAt(i);
                                Log($"[趋势线删除] 趋势线 {line.X1}->{line.X2} 产生做多信号后已被删除 (剩余监控趋势线: {_activeTrackers.Count} 条)");
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

                                // 🌟 触发信号后，该趋势线已被穿过并完成使命，直接删除
                                _activeTrackers.RemoveAt(i);
                                Log($"[趋势线删除] 趋势线 {line.X1}->{line.X2} 产生做空信号后已被删除 (剩余监控趋势线: {_activeTrackers.Count} 条)");
                            }
                        }
                        else if (tracker.TicksSincePenetration > ReboundTicksWindow)
                        {
                            // 🌟 超过 5 个 Tick 未发生有效回弹，判定趋势线被有效击穿/失效，直接删除该趋势线
                            _activeTrackers.RemoveAt(i);
                            Log($"[趋势线删除] 趋势线 {line.X1}->{line.X2} 被穿透后超过 {ReboundTicksWindow} 个 Tick 未回弹 (判定有效击穿)，已直接删除 (剩余监控趋势线: {_activeTrackers.Count} 条)");
                        }
                    }
                }
            }
        }

        #endregion

        protected override void OnReset()
        {
            lock (_stateLock)
            {
                _activeTrackers.Clear();
                _lastTickPrice = null;
            }
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
