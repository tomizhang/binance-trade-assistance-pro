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
        public decimal TakeProfitPct { get; set; } = 1.5m; // 止盈 1.5%
        public decimal StopLossPct { get; set; } = 0.8m;   // 止损 0.8%
    }

    /// <summary>
    /// 趋势线回调交易策略引擎
    /// </summary>
    public class TrendLineStrategy
    {
        public StrategyParameters Params { get; set; } = new StrategyParameters();
        public List<TradeRecord> Trades { get; } = new List<TradeRecord>();

        public PositionType CurrentPosition { get; private set; } = PositionType.None;
        public decimal CurrentEntryPrice { get; private set; }
        public DateTime CurrentEntryTime { get; private set; }
        public int CurrentEntryKlineIndex { get; private set; }

        // 触及与回调状态跟踪
        private TrendLine? _activeTargetLine = null;
        private decimal _touchedExtremumPrice = 0m;
        private bool _isNearLine = false;

        public event Action<TradeRecord> OnTradeClosed;
        public event Action<TradeRecord> OnTradeOpened;

        public void Reset()
        {
            Trades.Clear();
            CurrentPosition = PositionType.None;
            CurrentEntryPrice = 0m;
            _activeTargetLine = null;
            _touchedExtremumPrice = 0m;
            _isNearLine = false;
        }

        /// <summary>
        /// 策略核心 Tick 级实时处理引擎 (接收当前 K 线索引，0 全量数组分配，0 LINQ 开销)
        /// </summary>
        public void ProcessTick(Tick tick, int currentIndex, List<TrendLine> activeTrendLines)
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

            // 2. 无持仓时：原生 for 循环过滤符合条件的趋势线 (line_x1_x2 >= 40 且 open_age / line_age >= 80)
            List<TrendLine> eligibleLines = new List<TrendLine>();
            for (int i = 0; i < activeTrendLines.Count; i++)
            {
                var tl = activeTrendLines[i];
                if (tl.LineX1X2 >= Params.MinLineX1X2 && tl.LineAge >= Params.MinLineAge)
                {
                    eligibleLines.Add(tl);
                }
            }

            if (eligibleLines.Count == 0)
            {
                return;
            }

            // 3. 检查 Tick 价格是否靠近某条符合条件的趋势线 (触及监测)
            for (int i = 0; i < eligibleLines.Count; i++)
            {
                var tl = eligibleLines[i];
                decimal linePrice = tl.GetPriceAt(currentIndex);
                if (linePrice <= 0m) continue;

                decimal distancePct = Math.Abs(price - linePrice) / linePrice * 100m;

                // 触及门槛: 距离趋势线 <= 0.25% (进入触及准备状态)
                if (distancePct <= 0.25m)
                {
                    bool isDifferentLine = !_activeTargetLine.HasValue || 
                        _activeTargetLine.Value.X1 != tl.X1 || 
                        _activeTargetLine.Value.X2 != tl.X2;

                    if (!_isNearLine || isDifferentLine)
                    {
                        _isNearLine = true;
                        _activeTargetLine = tl;
                        _touchedExtremumPrice = price; // 记录触及极值
                    }
                    else
                    {
                        // 跟踪触及过程中的价格极值
                        if (tl.Type == PivotType.Low && price < _touchedExtremumPrice)
                        {
                            _touchedExtremumPrice = price; // 支撑线寻找最低点
                        }
                        else if (tl.Type == PivotType.High && price > _touchedExtremumPrice)
                        {
                            _touchedExtremumPrice = price; // 阻力线寻找最高点
                        }
                    }
                }
            }

            // 4. 触及后回调确认开仓逻辑 (Touch & Pullback)
            if (_isNearLine && _activeTargetLine.HasValue && _touchedExtremumPrice > 0m)
            {
                var targetLine = _activeTargetLine.Value;

                // 支撑趋势线 -> 触及后反弹回调 (+0.03%) -> 开多单 (Long)
                if (targetLine.Type == PivotType.Low)
                {
                    decimal reboundPct = (price - _touchedExtremumPrice) / _touchedExtremumPrice * 100m;
                    if (reboundPct >= 0.03m)
                    {
                        OpenPosition(PositionType.Long, price, tick.Time, currentIndex);
                        _isNearLine = false;
                        _activeTargetLine = null;
                        _touchedExtremumPrice = 0m;
                    }
                }
                // 阻力趋势线 -> 触及后回落回调 (-0.03%) -> 开空单 (Short)
                else if (targetLine.Type == PivotType.High)
                {
                    decimal dropPct = (_touchedExtremumPrice - price) / _touchedExtremumPrice * 100m;
                    if (dropPct >= 0.03m)
                    {
                        OpenPosition(PositionType.Short, price, tick.Time, currentIndex);
                        _isNearLine = false;
                        _activeTargetLine = null;
                        _touchedExtremumPrice = 0m;
                    }
                }
            }
        }

        private void OpenPosition(PositionType pos, decimal price, DateTime time, int klineIndex)
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
                EntryKlineIndex = klineIndex
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
            CurrentPosition = PositionType.None;
            CurrentEntryPrice = 0m;

            OnTradeClosed?.Invoke(trade);
        }

        public decimal GetWinRate()
        {
            if (Trades.Count == 0) return 0m;
            int winCount = Trades.Count(t => t.IsWin);
            return (decimal)winCount / Trades.Count * 100m;
        }

        public decimal GetTotalProfitPct()
        {
            return Trades.Sum(t => t.ProfitPct);
        }
    }
}
