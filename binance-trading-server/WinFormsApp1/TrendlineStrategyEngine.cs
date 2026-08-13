using System;
using System.Collections.Generic;

namespace WinFormsApp1
{
    public enum StrategyPositionType
    {
        None,
        Long,
        Short
    }

    public class TradeRecord
    {
        public StrategyPositionType Type { get; set; }
        public double EntryPrice { get; set; }
        public int EntryKlineIndex { get; set; } // 对应 _historyKlines 中的绝对全局索引
        public double ExitPrice { get; set; }
        public int ExitKlineIndex { get; set; }  // 对应 _historyKlines 中的绝对全局索引
        public bool IsProfit { get; set; }
        public double ProfitPct { get; set; }
        public string ExitReason { get; set; } = string.Empty;
    }

    public class TrendlineStrategyEngine
    {
        public StrategyPositionType CurrentPosition { get; private set; } = StrategyPositionType.None;
        public double EntryPrice { get; private set; }
        public int EntryKlineIndex { get; private set; }
        public double TakeProfitPrice { get; private set; }
        public double StopLossPrice { get; private set; }

        public double TakeProfitPct { get; set; } = 1.0;
        public double StopLossPct { get; set; } = 1.0;
        public double ExpectedProfitPct { get; set; } = 3.0;

        public event Action<TradeRecord>? OnTradeClosed;
        public event Action<StrategyPositionType, double, int>? OnTradeOpened;

        // 趋势延伸线触碰后反弹确认状态机
        private bool _isTestingSupport = false;
        private double _supportLowestPrice = double.MaxValue;
        private int _supportTouchStartBar = -1;

        private bool _isTestingResistance = false;
        private double _resistanceHighestPrice = double.MinValue;
        private int _resistanceTouchStartBar = -1;

        public List<TradeRecord> CompletedTrades { get; } = new();

        public void Reset()
        {
            CurrentPosition = StrategyPositionType.None;
            EntryPrice = 0;
            EntryKlineIndex = 0;
            TakeProfitPrice = 0;
            StopLossPrice = 0;
            _isTestingSupport = false;
            _supportLowestPrice = double.MaxValue;
            _supportTouchStartBar = -1;
            _isTestingResistance = false;
            _resistanceHighestPrice = double.MinValue;
            _resistanceTouchStartBar = -1;
            CompletedTrades.Clear();
        }

        public void Evaluate(int currentKlineIndex, double currentPrice, int activeRedLinesCount, int activeGreenLinesCount, double shortChannelSpreadPct = 0, double longChannelSpreadPct = 0)
        {
            if (currentKlineIndex < 0) return;

            // 1. 校验现有持仓的止盈 / 止损
            if (CurrentPosition == StrategyPositionType.Long)
            {
                if (currentPrice >= TakeProfitPrice)
                {
                    var record = new TradeRecord
                    {
                        Type = StrategyPositionType.Long,
                        EntryPrice = EntryPrice,
                        EntryKlineIndex = EntryKlineIndex,
                        ExitPrice = currentPrice,
                        ExitKlineIndex = currentKlineIndex,
                        IsProfit = true,
                        ProfitPct = (currentPrice - EntryPrice) / EntryPrice * 100,
                        ExitReason = $"[TP (+{TakeProfitPct:F1}%)]"
                    };
                    CompletedTrades.Add(record);
                    CurrentPosition = StrategyPositionType.None;
                    OnTradeClosed?.Invoke(record);
                }
                else if (currentPrice <= StopLossPrice)
                {
                    var record = new TradeRecord
                    {
                        Type = StrategyPositionType.Long,
                        EntryPrice = EntryPrice,
                        EntryKlineIndex = EntryKlineIndex,
                        ExitPrice = currentPrice,
                        ExitKlineIndex = currentKlineIndex,
                        IsProfit = false,
                        ProfitPct = (currentPrice - EntryPrice) / EntryPrice * 100,
                        ExitReason = $"[SL (-{StopLossPct:F1}%)]"
                    };
                    CompletedTrades.Add(record);
                    CurrentPosition = StrategyPositionType.None;
                    OnTradeClosed?.Invoke(record);
                }
            }
            else if (CurrentPosition == StrategyPositionType.Short)
            {
                if (currentPrice <= TakeProfitPrice)
                {
                    var record = new TradeRecord
                    {
                        Type = StrategyPositionType.Short,
                        EntryPrice = EntryPrice,
                        EntryKlineIndex = EntryKlineIndex,
                        ExitPrice = currentPrice,
                        ExitKlineIndex = currentKlineIndex,
                        IsProfit = true,
                        ProfitPct = (EntryPrice - currentPrice) / EntryPrice * 100,
                        ExitReason = $"[TP (+{TakeProfitPct:F1}%)]"
                    };
                    CompletedTrades.Add(record);
                    CurrentPosition = StrategyPositionType.None;
                    OnTradeClosed?.Invoke(record);
                }
                else if (currentPrice >= StopLossPrice)
                {
                    var record = new TradeRecord
                    {
                        Type = StrategyPositionType.Short,
                        EntryPrice = EntryPrice,
                        EntryKlineIndex = EntryKlineIndex,
                        ExitPrice = currentPrice,
                        ExitKlineIndex = currentKlineIndex,
                        IsProfit = false,
                        ProfitPct = (EntryPrice - currentPrice) / EntryPrice * 100,
                        ExitReason = $"[SL (-{StopLossPct:F1}%)]"
                    };
                    CompletedTrades.Add(record);
                    CurrentPosition = StrategyPositionType.None;
                    OnTradeClosed?.Invoke(record);
                }
            }

            // 2. 如果当前无持仓，精准校验【红色阻力趋势线触发开仓做空 SHORT，绿色支撑趋势线触发开仓做多 LONG】：
            if (CurrentPosition == StrategyPositionType.None)
            {
                bool meetsShortProfit = shortChannelSpreadPct == 0 || shortChannelSpreadPct >= ExpectedProfitPct;
                bool meetsLongProfit = longChannelSpreadPct == 0 || longChannelSpreadPct >= ExpectedProfitPct;

                // A. 判定是否触碰【红色趋势线 (Peak 阻力线)】 (≥ 2 条 且 Line_Age > 80) 且做空期望利润满足 -> 准备开仓做空 (SHORT)
                if (activeRedLinesCount >= 2 && meetsShortProfit)
                {
                    if (!_isTestingResistance)
                    {
                        _isTestingResistance = true;
                        _resistanceHighestPrice = currentPrice;
                        _resistanceTouchStartBar = currentKlineIndex;
                    }
                    else
                    {
                        _resistanceHighestPrice = Math.Max(_resistanceHighestPrice, currentPrice);
                    }
                }

                // B. 判定是否触碰【绿色趋势线 (Valley 支撑线)】 (≥ 2 条 且 Line_Age > 80) 且做多期望利润满足 -> 准备开仓做多 (LONG)
                if (activeGreenLinesCount >= 2 && meetsLongProfit)
                {
                    if (!_isTestingSupport)
                    {
                        _isTestingSupport = true;
                        _supportLowestPrice = currentPrice;
                        _supportTouchStartBar = currentKlineIndex;
                    }
                    else
                    {
                        _supportLowestPrice = Math.Min(_supportLowestPrice, currentPrice);
                    }
                }

                // C. 【红色阻力趋势线】：触碰红色阻力下压线，价格受阻回落 ≤ 最高价 * 0.9985 时触发开仓做空 (SHORT)！
                if (_isTestingResistance && currentPrice <= _resistanceHighestPrice * 0.9985)
                {
                    CurrentPosition = StrategyPositionType.Short;
                    EntryPrice = currentPrice;
                    EntryKlineIndex = currentKlineIndex;
                    TakeProfitPrice = currentPrice * (1.0 - TakeProfitPct / 100.0);
                    StopLossPrice = currentPrice * (1.0 + StopLossPct / 100.0);
                    _isTestingResistance = false;
                    _resistanceHighestPrice = double.MinValue;
                    OnTradeOpened?.Invoke(StrategyPositionType.Short, currentPrice, currentKlineIndex);
                }
                // D. 【绿色支撑趋势线】：触碰绿色支撑延伸线，价格获得支撑反弹 ≥ 最低价 * 1.0015 时触发开仓做多 (LONG)！
                else if (_isTestingSupport && currentPrice >= _supportLowestPrice * 1.0015)
                {
                    CurrentPosition = StrategyPositionType.Long;
                    EntryPrice = currentPrice;
                    EntryKlineIndex = currentKlineIndex;
                    TakeProfitPrice = currentPrice * (1.0 + TakeProfitPct / 100.0);
                    StopLossPrice = currentPrice * (1.0 - StopLossPct / 100.0);
                    _isTestingSupport = false;
                    _supportLowestPrice = double.MaxValue;
                    OnTradeOpened?.Invoke(StrategyPositionType.Long, currentPrice, currentKlineIndex);
                }

                // 超时保护
                if (_isTestingSupport && currentKlineIndex - _supportTouchStartBar > 60)
                {
                    _isTestingSupport = false;
                    _supportLowestPrice = double.MaxValue;
                }
                if (_isTestingResistance && currentKlineIndex - _resistanceTouchStartBar > 60)
                {
                    _isTestingResistance = false;
                    _resistanceHighestPrice = double.MinValue;
                }
            }
        }
    }
}
