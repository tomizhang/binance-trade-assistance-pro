using System;

namespace TradingTerminal.Services.Backtest.Models
{
    public class BacktestConfig
    {
        public string Symbol { get; set; }
        public string StrategyName { get; set; }
        public string Timeframe { get; set; } = "1m";
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public decimal InitialBalance { get; set; } = 1000m;
        public decimal Leverage { get; set; } = 5m;
        public decimal TakerSlippage { get; set; } = 0.0005m; // 0.05%
        public decimal MakerSlippage { get; set; } = 0.0003m; // 0.03%
        public decimal FeeRate { get; set; } = 0.0005m;       // 0.05%
        public decimal FundingRate { get; set; } = 0.0002m;   // 0.02%
    }
}
