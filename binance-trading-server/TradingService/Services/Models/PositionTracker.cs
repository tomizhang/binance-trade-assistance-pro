using System;

namespace TradingTerminal.Models
{
    public class PositionTracker
    {
        public string Symbol { get; set; }
        public string Side { get; set; }
        public decimal Quantity { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal TakeProfitPrice { get; set; } // 🌟 新增：记录这笔单子的止盈目标价
        public DateTime OpenTime { get; set; }
        public bool IsStopMovedToBE { get; set; }
    }
}