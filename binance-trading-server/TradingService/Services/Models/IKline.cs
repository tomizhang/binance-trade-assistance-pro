using System;

namespace TradingTerminal.Models
{
    public interface IKline
    {
        string Symbol { get; }
        string Interval { get; }
        bool IsClosed { get; }
        decimal Open { get; }
        decimal Close { get; }
        decimal High { get; }
        decimal Low { get; }
        decimal Volume { get; }
        long OpenTime { get; }
        int TradeCount { get; }
        decimal TakerBuyBaseVolume { get; }
    }
}
