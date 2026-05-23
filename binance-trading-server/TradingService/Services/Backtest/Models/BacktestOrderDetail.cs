using System;

namespace TradingTerminal.Services.Backtest.Models
{
    public class BacktestOrderDetail
    {
        public string Id { get; set; }
        public string StrategyName { get; set; }
        public string OrderNumber { get; set; }
        public string Symbol { get; set; }
        public string Direction { get; set; } // LONG / SHORT
        
        // Open details
        public DateTime OpenTime { get; set; }
        public decimal OpenPrice { get; set; }
        public string OpenType { get; set; } // taker / maker
        public decimal OpenFee { get; set; }
        public decimal OpenSlippage { get; set; }
        public string OpenReason { get; set; }

        // Close details
        public DateTime? CloseTime { get; set; }
        public decimal? ClosePrice { get; set; }
        public string CloseType { get; set; } // taker / maker
        public decimal? CloseFee { get; set; }
        public decimal? CloseSlippage { get; set; }
        public string CloseReason { get; set; } // TakeProfit / StopLoss / BreakEven / Liquidation / EndOfBacktest

        // Position & PnL metrics
        public decimal Quantity { get; set; }
        public decimal FundingFeePaid { get; set; }
        public double HoldDurationMinutes { get; set; }
        public decimal CurrentBalance { get; set; }
        public decimal TradePnL { get; set; }
        public decimal TradeROI { get; set; } // ROI % based on initial margin

        // TP/SL & Volatility
        public decimal? TakeProfitPrice { get; set; }
        public decimal? StopLossPrice { get; set; }
        public decimal HoldPeriodVolatility { get; set; }
    }
}
