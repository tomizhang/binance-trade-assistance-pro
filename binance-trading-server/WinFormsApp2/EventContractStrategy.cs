using System;
using System.Collections.Generic;
using System.Linq;

namespace WinFormsApp2
{
    /// <summary>
    /// 事件合约到期判定周期枚举
    /// </summary>
    public enum EventContractDuration
    {
        TenMinutes = 10,       // 10 分钟到期判定
        ThirtyMinutes = 30,    // 30 分钟到期判定
        OneHour = 60           // 1 小时到期判定
    }

    /// <summary>
    /// 事件合约结算原因
    /// </summary>
    public enum EventContractExitReason
    {
        TimeExpired,    // 时间窗口到期自动结算 (10m / 30m / 1h)
        TakeProfit,     // 达到止盈比例提前离场
        StopLoss,       // 达到止损比例提前离场
        Manual          // 手动平仓/终止
    }

    /// <summary>
    /// 单笔事件合约记录模型
    /// </summary>
    public class EventContractRecord
    {
        public int Id { get; set; }
        public string Symbol { get; set; } = "BTCUSDT";
        public PositionType Position { get; set; } = PositionType.Long; // 买多 / 卖空
        public decimal EntryPrice { get; set; }
        public DateTime EntryTime { get; set; }
        public int EntryKlineIndex { get; set; }
        public DateTime ExpectedExpiryTime { get; set; }
        public EventContractDuration Duration { get; set; } = EventContractDuration.TenMinutes;

        // 详细 Tick 与 K 线时间属性 (与趋势线策略完全对齐)
        public DateTime EntryTickTime { get; set; }
        public decimal EntryTickPrice { get; set; }
        public DateTime EntryKlineOpenTime { get; set; }
        public DateTime EntryKlineCloseTime { get; set; }
        public decimal EntryTrendLinePrice { get; set; }

        public decimal ExitPrice { get; set; }
        public DateTime ExitTime { get; set; }
        public int ExitKlineIndex { get; set; }
        public DateTime ExitKlineOpenTime { get; set; }
        public EventContractExitReason ExitReason { get; set; } = EventContractExitReason.TimeExpired;

        public decimal ProfitPct { get; set; } // 最终收益率 (%)
        public bool IsWin { get; set; }        // 是否盈利 (多单: ExitPrice > EntryPrice, 空单: ExitPrice < EntryPrice)
        public string Comment { get; set; } = string.Empty;
    }

    /// <summary>
    /// 事件合约参数配置模型
    /// </summary>
    public class EventContractParameters
    {
        public bool Enabled { get; set; } = false;
        public EventContractDuration Duration { get; set; } = EventContractDuration.TenMinutes;
        public int MinLineX1X2 { get; set; } = 40;
        public int MinLineAge { get; set; } = 80;
        public bool EnableEarlyExit { get; set; } = false; // 是否允许在到期前触发止盈止损提前离场
        public decimal TakeProfitPct { get; set; } = 1.5m;
        public decimal StopLossPct { get; set; } = 0.8m;
        public bool IsLiveTrading { get; set; } = false;

        public int DurationMinutes => (int)Duration;

        public EventContractParameters Clone()
        {
            return new EventContractParameters
            {
                Enabled = this.Enabled,
                Duration = this.Duration,
                MinLineX1X2 = this.MinLineX1X2,
                MinLineAge = this.MinLineAge,
                EnableEarlyExit = this.EnableEarlyExit,
                TakeProfitPct = this.TakeProfitPct,
                StopLossPct = this.StopLossPct,
                IsLiveTrading = this.IsLiveTrading
            };
        }
    }

    /// <summary>
    /// 高性能事件合约策略引擎 (开仓逻辑与趋势线假突破/假跌破 100% 严格一致，平仓基于 10m / 30m / 1h 固定时间到期判定与交割)
    /// </summary>
    public class EventContractStrategy
    {
        public EventContractParameters Params { get; set; } = new EventContractParameters();
        public List<EventContractRecord> Contracts { get; } = new List<EventContractRecord>();

        public PositionType CurrentPosition { get; private set; } = PositionType.None;
        public decimal CurrentEntryPrice { get; private set; }
        public DateTime CurrentEntryTime { get; private set; }
        public int CurrentEntryKlineIndex { get; private set; }
        public DateTime CurrentExpectedExpiryTime { get; private set; }
        public EventContractRecord? CurrentContract { get; private set; }

        public event Action<EventContractRecord>? OnContractOpened;
        public event Action<EventContractRecord>? OnContractSettled;
        public event Action<string>? OnLog;

        private int _winCount = 0;
        private decimal _totalProfitPct = 0m;

        // 假突破/假跌破 5-Tick 状态跟踪 (与趋势线策略完全一致)
        private TrendLine? _pendingTargetLine = null;
        private bool _isPenetrated = false;
        private int _ticksSincePenetration = 0;

        public void Reset()
        {
            Contracts.Clear();
            CurrentPosition = PositionType.None;
            CurrentEntryPrice = 0m;
            CurrentEntryTime = DateTime.MinValue;
            CurrentEntryKlineIndex = 0;
            CurrentExpectedExpiryTime = DateTime.MinValue;
            CurrentContract = null;
            _winCount = 0;
            _totalProfitPct = 0m;
            ResetPenetrationState();
        }

        public decimal GetWinRate()
        {
            if (Contracts.Count == 0) return 0m;
            return (decimal)_winCount / Contracts.Count * 100m;
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
        /// 双模式趋势线延长线价格计算：实盘 LiveStream 模式下按 K 线时间戳插值，离线模式下按数组索引计算
        /// </summary>
        private decimal GetLinePriceAt(TrendLine line, int currentIndex, DateTime klineOpenTime)
        {
            if (klineOpenTime > DateTime.MinValue && line.Time1 > DateTime.MinValue && line.Time2 > line.Time1)
            {
                return line.GetPriceAtTime(klineOpenTime);
            }
            return line.GetPriceAt(currentIndex);
        }

        /// <summary>
        /// 核心 Tick 驱动判定引擎：检查到期时间、提前风控与新事件合约入场 (开仓逻辑与趋势线策略 100% 一致)
        /// </summary>
        public void ProcessTick(Tick tick, int currentIndex, List<TrendLine>? activeTrendLines, Kline currentKline, string symbol = "BTCUSDT")
        {
            if (!Params.Enabled) return;

            decimal price = tick.LastPrice;
            DateTime currentTime = tick.Time > DateTime.MinValue ? tick.Time : DateTime.Now;

            // 1. 如果已有持仓中事件合约，检查是否已达指定到期时间 (10m / 30m / 1h)
            if (CurrentPosition != PositionType.None && CurrentContract != null)
            {
                // A. 检查时间窗口到期结算
                if (currentTime >= CurrentExpectedExpiryTime)
                {
                    SettleContract(price, currentTime, currentIndex, currentKline.OpenTime, EventContractExitReason.TimeExpired, symbol);
                    return;
                }

                // B. 如果开启了提前止盈止损风控
                if (Params.EnableEarlyExit)
                {
                    CheckEarlyExitRisk(price, currentTime, currentIndex, currentKline.OpenTime, symbol);
                }
                return;
            }

            if (activeTrendLines == null || activeTrendLines.Count == 0)
                return;

            // 2. 检查处于假突破/假跌破等待状态中的趋势线 (与趋势线策略 100% 严格一致)
            if (_isPenetrated && _pendingTargetLine.HasValue)
            {
                _ticksSincePenetration++;
                var targetLine = _pendingTargetLine.Value;
                decimal linePrice = GetLinePriceAt(targetLine, currentIndex, currentKline.OpenTime);

                // A. 支撑趋势线 (PivotType.Low): 跌破趋势线后，在 5 个 Tick 内收复回到趋势线之上 (Price > LinePrice) -> 开多单 (BUY LONG)
                if (targetLine.Type == PivotType.Low)
                {
                    if (price > linePrice && _ticksSincePenetration <= 5)
                    {
                        OpenContract(PositionType.Long, price, tick.Time, currentIndex, currentKline.OpenTime, currentKline.CloseTime, linePrice, symbol, $"支撑线假跌破收复-开多({Params.DurationMinutes}m)");
                        ResetPenetrationState();
                        return;
                    }
                }
                // B. 阻力趋势线 (PivotType.High): 突破趋势线后，在 5 个 Tick 内回落回到趋势线之下 (Price < LinePrice) -> 开空单 (SELL SHORT)
                else if (targetLine.Type == PivotType.High)
                {
                    if (price < linePrice && _ticksSincePenetration <= 5)
                    {
                        OpenContract(PositionType.Short, price, tick.Time, currentIndex, currentKline.OpenTime, currentKline.CloseTime, linePrice, symbol, $"阻力线假突破回落-开空({Params.DurationMinutes}m)");
                        ResetPenetrationState();
                        return;
                    }
                }

                // 超过 3-5 个 Tick 未回到趋势线另一侧，说明是有效破位/突破，放弃开仓并复位等待状态
                if (_ticksSincePenetration > 3)
                {
                    ResetPenetrationState();
                }
            }

            // 3. 扫描活动趋势线，检测是否有 Tick 穿透趋势线 (Phase 1 触发，与趋势线策略 100% 严格一致)
            if (!_isPenetrated)
            {
                for (int i = 0; i < activeTrendLines.Count; i++)
                {
                    var tl = activeTrendLines[i];
                    if (tl.LineX1X2 < Params.MinLineX1X2 || tl.LineAge < Params.MinLineAge)
                        continue;

                    decimal linePrice = GetLinePriceAt(tl, currentIndex, currentKline.OpenTime);
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

        /// <summary>
        /// 开立新的事件合约
        /// </summary>
        public void OpenContract(
            PositionType pos,
            decimal price,
            DateTime time,
            int klineIndex,
            DateTime klineOpenTime,
            DateTime klineCloseTime,
            decimal trendLinePrice,
            string symbol = "BTCUSDT",
            string comment = "")
        {
            if (CurrentPosition != PositionType.None) return;

            int durationMins = Params.DurationMinutes;
            DateTime expiryTime = time.AddMinutes(durationMins);

            CurrentPosition = pos;
            CurrentEntryPrice = price;
            CurrentEntryTime = time;
            CurrentEntryKlineIndex = klineIndex;
            CurrentExpectedExpiryTime = expiryTime;

            var contract = new EventContractRecord
            {
                Id = Contracts.Count + 1,
                Symbol = symbol,
                Position = pos,
                EntryPrice = price,
                EntryTime = time,
                EntryKlineIndex = klineIndex,
                EntryTickTime = time,
                EntryTickPrice = price,
                EntryKlineOpenTime = klineOpenTime,
                EntryKlineCloseTime = klineCloseTime,
                EntryTrendLinePrice = trendLinePrice,
                ExpectedExpiryTime = expiryTime,
                Duration = Params.Duration,
                Comment = string.IsNullOrEmpty(comment) ? $"{durationMins}分钟事件合约" : comment
            };

            CurrentContract = contract;
            OnContractOpened?.Invoke(contract);

            string posText = pos == PositionType.Long ? "🟢 [事件合约-买多 BUY]" : "🔴 [事件合约-卖空 SELL]";
            OnLog?.Invoke($"{posText} #{contract.Id} [{symbol}] 入场价: {price} | 周期: {durationMins}分钟 | 预计到期时间: {expiryTime:yyyy-MM-dd HH:mm:ss} | 关键趋势线: {trendLinePrice:F2}");
        }

        /// <summary>
        /// 结算当前事件合约 (计算多空胜负与盈亏百分比)
        /// </summary>
        private void SettleContract(
            decimal exitPrice,
            DateTime exitTime,
            int klineIndex,
            DateTime klineOpenTime,
            EventContractExitReason reason,
            string symbol)
        {
            if (CurrentPosition == PositionType.None || CurrentContract == null) return;

            decimal profitPct = 0m;
            bool isWin = false;

            if (CurrentPosition == PositionType.Long)
            {
                profitPct = CurrentEntryPrice > 0 ? (exitPrice - CurrentEntryPrice) / CurrentEntryPrice * 100m : 0m;
                isWin = exitPrice > CurrentEntryPrice;
            }
            else if (CurrentPosition == PositionType.Short)
            {
                profitPct = CurrentEntryPrice > 0 ? (CurrentEntryPrice - exitPrice) / CurrentEntryPrice * 100m : 0m;
                isWin = exitPrice < CurrentEntryPrice;
            }

            CurrentContract.ExitPrice = exitPrice;
            CurrentContract.ExitTime = exitTime;
            CurrentContract.ExitKlineIndex = klineIndex;
            CurrentContract.ExitKlineOpenTime = klineOpenTime;
            CurrentContract.ExitReason = reason;
            CurrentContract.ProfitPct = profitPct;
            CurrentContract.IsWin = isWin;

            Contracts.Add(CurrentContract);
            if (isWin) _winCount++;
            _totalProfitPct += profitPct;

            var settled = CurrentContract;

            CurrentPosition = PositionType.None;
            CurrentEntryPrice = 0m;
            CurrentEntryTime = DateTime.MinValue;
            CurrentExpectedExpiryTime = DateTime.MinValue;
            CurrentContract = null;

            OnContractSettled?.Invoke(settled);

            string reasonText = reason switch
            {
                EventContractExitReason.TimeExpired => $"⏱ [时间到期结算 ({Params.DurationMinutes}m)]",
                EventContractExitReason.TakeProfit => "🎯 [提前止盈离场]",
                EventContractExitReason.StopLoss => "🛡 [提前止损离场]",
                _ => "⏹ [人工结算]"
            };

            string resultText = isWin ? "🏆 判定: 盈利 (WIN)" : "❌ 判定: 亏损 (LOSS)";
            OnLog?.Invoke($"📋 #{settled.Id} [{symbol}] {reasonText} {resultText} | 入场: {settled.EntryPrice} -> 结算: {exitPrice} | 收益: {profitPct:+0.00;-0.00;0.00}% | 持续: {(exitTime - settled.EntryTime).TotalMinutes:F1}分钟");
        }

        private void CheckEarlyExitRisk(decimal currentPrice, DateTime currentTime, int klineIndex, DateTime klineOpenTime, string symbol)
        {
            if (CurrentPosition == PositionType.Long)
            {
                decimal tpPrice = CurrentEntryPrice * (1m + Params.TakeProfitPct / 100m);
                decimal slPrice = CurrentEntryPrice * (1m - Params.StopLossPct / 100m);

                if (currentPrice >= tpPrice)
                {
                    SettleContract(currentPrice, currentTime, klineIndex, klineOpenTime, EventContractExitReason.TakeProfit, symbol);
                }
                else if (currentPrice <= slPrice)
                {
                    SettleContract(currentPrice, currentTime, klineIndex, klineOpenTime, EventContractExitReason.StopLoss, symbol);
                }
            }
            else if (CurrentPosition == PositionType.Short)
            {
                decimal tpPrice = CurrentEntryPrice * (1m - Params.TakeProfitPct / 100m);
                decimal slPrice = CurrentEntryPrice * (1m + Params.StopLossPct / 100m);

                if (currentPrice <= tpPrice)
                {
                    SettleContract(currentPrice, currentTime, klineIndex, klineOpenTime, EventContractExitReason.TakeProfit, symbol);
                }
                else if (currentPrice >= slPrice)
                {
                    SettleContract(currentPrice, currentTime, klineIndex, klineOpenTime, EventContractExitReason.StopLoss, symbol);
                }
            }
        }
    }
}
