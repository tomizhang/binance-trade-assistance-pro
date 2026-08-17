using System;
using System.Collections.Generic;
using System.Linq;

namespace WinFormsApp2
{
    public enum PositionType
    {
        None,
        Long,
        Short
    }

    public enum TradeExitReason
    {
        TakeProfit,
        StopLoss
    }

    public class TradeRecord
    {
        public int Id { get; set; }
        public PositionType Position { get; set; }
        public decimal EntryPrice { get; set; }
        public DateTime EntryTime { get; set; }
        public int EntryKlineIndex { get; set; }

        // 详细 Tick 与 K 线时间属性 (用于策略开仓详细日志输出与异步落盘)
        public DateTime EntryTickTime { get; set; }
        public decimal EntryTickPrice { get; set; }
        public DateTime EntryKlineOpenTime { get; set; }
        public DateTime EntryKlineCloseTime { get; set; }
        public decimal EntryTrendLinePrice { get; set; }

        public decimal ExitPrice { get; set; }
        public DateTime ExitTime { get; set; }
        public int ExitKlineIndex { get; set; }
        public TradeExitReason ExitReason { get; set; }

        public decimal ProfitPct { get; set; } // 收益率 (%)
        public bool IsWin => ProfitPct > 0;
    }

    public class StrategyParameters
    {
        public bool Enabled { get; set; } = true;
        public int MinLineX1X2 { get; set; } = 40;
        public int MinLineAge { get; set; } = 80;
        public decimal TakeProfitPct { get; set; } = 1m; // 止盈 1.5%
        public decimal StopLossPct { get; set; } = 0.3m;   // 止损 0.8%
    }

    /// <summary>
    /// 趋势线假突破/假跌破回调交易策略引擎
    /// </summary>
    public class TrendLineStrategy
    {
        public StrategyParameters Params { get; set; } = new StrategyParameters();
        public List<TradeRecord> Trades { get; } = new List<TradeRecord>();

        public PositionType CurrentPosition { get; private set; } = PositionType.None;
        public decimal CurrentEntryPrice { get; private set; }
        public DateTime CurrentEntryTime { get; private set; }
        public int CurrentEntryKlineIndex { get; private set; }

        // 假突破/假跌破 3-Tick 状态跟踪
        private TrendLine? _pendingTargetLine = null;
        private bool _isPenetrated = false;
        private int _ticksSincePenetration = 0;

        public event Action<TradeRecord>? OnTradeClosed;
        public event Action<TradeRecord>? OnTradeOpened;

        private int _winCount = 0;
        private decimal _totalProfitPct = 0m;

        public void Reset()
        {
            Trades.Clear();
            CurrentPosition = PositionType.None;
            CurrentEntryPrice = 0m;
            _winCount = 0;
            _totalProfitPct = 0m;
            ResetPenetrationState();
        }

        private void ClosePosition(decimal exitPrice, DateTime exitTime, int klineIndex, TradeExitReason reason, decimal profitPct)
        {
            var trade = new TradeRecord
            {
                Id = Trades.Count + 1,
                Position = CurrentPosition,
                EntryPrice = CurrentEntryPrice,
                EntryTime = CurrentEntryTime,
                EntryKlineIndex = CurrentEntryKlineIndex,
                ExitPrice = exitPrice,
                ExitTime = exitTime,
                ExitKlineIndex = klineIndex,
                ExitReason = reason,
                ProfitPct = profitPct
            };

            Trades.Add(trade);
            if (trade.IsWin) _winCount++;
            _totalProfitPct += profitPct;

            CurrentPosition = PositionType.None;
            CurrentEntryPrice = 0m;

            OnTradeClosed?.Invoke(trade);
        }

        public decimal GetWinRate()
        {
            if (Trades.Count == 0) return 0m;
            return (decimal)_winCount / Trades.Count * 100m;
        }

        public decimal GetTotalProfitPct()
        {
            return _totalProfitPct;
        }

        private void ResetPenetrationState()
        {
            _pendingTargetLine = null;
            _isPenetrated = false;
            _ticksSincePenetration = 0;
        }

        /// <summary>
        /// 策略核心 Tick 级实时处理引擎 (接收当前 Tick, K 线索引, 趋势线列表与当前 K 线时间信息)
        /// 规则: Tick 值穿过趋势线后，在后续 3 个 Tick 内回到趋势线之上/之下则开仓
        /// </summary>
        public void ProcessTick(Tick tick, int currentIndex, List<TrendLine> activeTrendLines, Kline currentKline)
        {
            if (!Params.Enabled || activeTrendLines == null || activeTrendLines.Count == 0)
                return;

            decimal price = tick.LastPrice;

            // 1. 如果已有持仓，检查止盈(1.5%)与止损(0.8%)风控条件
            if (CurrentPosition != PositionType.None)
            {
                CheckPositionRisk(price, tick.Time, currentIndex);
                return;
            }

            // 2. 检查处于假突破/假跌破等待状态中的趋势线
            if (_isPenetrated && _pendingTargetLine.HasValue)
            {
                _ticksSincePenetration++;
                var targetLine = _pendingTargetLine.Value;
                decimal linePrice = targetLine.GetPriceAt(currentIndex);

                // A. 支撑趋势线 (PivotType.Low): 跌破趋势线后，在 3 个 Tick 内收复回到趋势线之上 (Price > LinePrice) -> 开多单 (BUY LONG)
                if (targetLine.Type == PivotType.Low)
                {
                    if (price > linePrice && _ticksSincePenetration <= 3)
                    {
                        OpenPosition(PositionType.Long, price, tick.Time, currentIndex, currentKline.OpenTime, currentKline.CloseTime, linePrice);
                        ResetPenetrationState();
                        return;
                    }
                }
                // B. 阻力趋势线 (PivotType.High): 突破趋势线后，在 3 个 Tick 内回落回到趋势线之下 (Price < LinePrice) -> 开空单 (SELL SHORT)
                else if (targetLine.Type == PivotType.High)
                {
                    if (price < linePrice && _ticksSincePenetration <= 3)
                    {
                        OpenPosition(PositionType.Short, price, tick.Time, currentIndex, currentKline.OpenTime, currentKline.CloseTime, linePrice);
                        ResetPenetrationState();
                        return;
                    }
                }

                // 超过 3 个 Tick 未回到趋势线另一侧，说明是有效破位/突破，放弃开仓并复位等待状态
                if (_ticksSincePenetration > 3)
                {
                    ResetPenetrationState();
                }
            }

            // 3. 扫描活动趋势线，检测是否有 Tick 穿透趋势线 (Phase 1 触发)
            if (!_isPenetrated)
            {
                for (int i = 0; i < activeTrendLines.Count; i++)
                {
                    var tl = activeTrendLines[i];
                    if (tl.LineX1X2 < Params.MinLineX1X2 || tl.LineAge < Params.MinLineAge)
                        continue;

                    decimal linePrice = tl.GetPriceAt(currentIndex);
                    if (linePrice <= 0m) continue;

                    // A. 支撑趋势线 (Low): Tick 价格跌破趋势线 (price < linePrice)
                    if (tl.Type == PivotType.Low && price < linePrice)
                    {
                        _isPenetrated = true;
                        _pendingTargetLine = tl;
                        _ticksSincePenetration = 0; // 标记第 0 个 Tick 刚发生穿透跌破
                        break;
                    }
                    // B. 阻力趋势线 (High): Tick 价格突破趋势线 (price > linePrice)
                    else if (tl.Type == PivotType.High && price > linePrice)
                    {
                        _isPenetrated = true;
                        _pendingTargetLine = tl;
                        _ticksSincePenetration = 0; // 标记第 0 个 Tick 刚发生穿透突破
                        break;
                    }
                }
            }
        }

        private void OpenPosition(
            PositionType pos,
            decimal price,
            DateTime time,
            int klineIndex,
            DateTime klineOpenTime,
            DateTime klineCloseTime,
            decimal trendLinePrice)
        {
            CurrentPosition = pos;
            CurrentEntryPrice = price;
            CurrentEntryTime = time;
            CurrentEntryKlineIndex = klineIndex;

            var trade = new TradeRecord
            {
                Id = Trades.Count + 1,
                Position = pos,
                EntryPrice = price,
                EntryTime = time,
                EntryKlineIndex = klineIndex,
                EntryTickTime = time,
                EntryTickPrice = price,
                EntryKlineOpenTime = klineOpenTime,
                EntryKlineCloseTime = klineCloseTime,
                EntryTrendLinePrice = trendLinePrice
            };

            OnTradeOpened?.Invoke(trade);
        }

        private void CheckPositionRisk(decimal currentPrice, DateTime time, int klineIndex)
        {
            if (CurrentPosition == PositionType.Long)
            {
                decimal tpPrice = CurrentEntryPrice * (1m + Params.TakeProfitPct / 100m);
                decimal slPrice = CurrentEntryPrice * (1m - Params.StopLossPct / 100m);

                if (currentPrice >= tpPrice)
                {
                    ClosePosition(currentPrice, time, klineIndex, TradeExitReason.TakeProfit, Params.TakeProfitPct);
                }
                else if (currentPrice <= slPrice)
                {
                    ClosePosition(currentPrice, time, klineIndex, TradeExitReason.StopLoss, -Params.StopLossPct);
                }
            }
            else if (CurrentPosition == PositionType.Short)
            {
                decimal tpPrice = CurrentEntryPrice * (1m - Params.TakeProfitPct / 100m);
                decimal slPrice = CurrentEntryPrice * (1m + Params.StopLossPct / 100m);

                if (currentPrice <= tpPrice)
                {
                    ClosePosition(currentPrice, time, klineIndex, TradeExitReason.TakeProfit, Params.TakeProfitPct);
                }
                else if (currentPrice >= slPrice)
                {
                    ClosePosition(currentPrice, time, klineIndex, TradeExitReason.StopLoss, -Params.StopLossPct);
                }
            }
        }
    }
}
