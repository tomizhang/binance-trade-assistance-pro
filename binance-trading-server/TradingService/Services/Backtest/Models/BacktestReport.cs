using System;
using System.Collections.Generic;

namespace TradingTerminal.Services.Backtest.Models
{
    public class BacktestReport
    {
        public decimal WinRate { get; set; }
        public decimal TotalProfit { get; set; }
        public decimal TotalProfitPct { get; set; }
        public int TotalTradesCount { get; set; }
        public decimal TotalFees { get; set; }
        public decimal MaxDrawdown { get; set; } // Max drawdown percentage
        public List<BacktestOrderDetail> OrderDetails { get; set; } = new();
        public List<EquityPoint> EquityCurve { get; set; } = new();
        public decimal SymbolVolatility { get; set; } // 币种在回测区间的最大波动幅度 (%)
        public decimal Leverage { get; set; }         // 回测所使用的杠杆倍数
    }

    public class EquityPoint
    {
        public DateTime Time { get; set; }
        public decimal Balance { get; set; }
    }
}
