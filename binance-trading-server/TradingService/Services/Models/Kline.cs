using System;

namespace TradingTerminal
{
    public class KlineMessage
    {
        public string Symbol { get; set; }
        public string Interval { get; set; }   // 周期，如 "1m", "15m"
        public bool IsClosed { get; set; }
        public decimal Open { get; set; }
        public decimal Close { get; set; }
        public decimal High { get; set; }      // HA 需要
        public decimal Low { get; set; }       // HA 需要
        public decimal Volume { get; set; }
        public long OpenTime { get; set; }     // HA 需要对齐时间

        public int TradeCount { get; set; }
    }
}
