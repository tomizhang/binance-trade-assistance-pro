using System;

namespace TradingTerminal.Models
{
    public class PositionTracker
    {
        public string Symbol { get; set; }
        public string Side { get; set; } // BUY (做多) 或 SELL (做空)
        public decimal Quantity { get; set; }
        public decimal EntryPrice { get; set; }
        public DateTime OpenTime { get; set; }
        public bool IsStopMovedToBE { get; set; } // 是否已经执行过保本
    }
}