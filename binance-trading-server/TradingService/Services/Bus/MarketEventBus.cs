using System;

namespace TradingTerminal.Services
{
    /// <summary>
    /// 全局行情事件总线 (发布/订阅中心)
    /// </summary>
    public class MarketEventBus
    {
        // 1. 定义 OI 接收事件
        public event Action<string, decimal> OnOpenInterestReceived;

        // 2. 定义 K线 接收事件
        public event Action<KlineMessage> OnKlineReceived;

        // WebSocket 服务调用这些方法来发布数据
        public void PublishOpenInterest(string symbol, decimal oi)
        {
            OnOpenInterestReceived?.Invoke(symbol, oi);
        }

       

        public void PublishKline(KlineMessage msg)
        {
            OnKlineReceived?.Invoke(msg);
        }
    }
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
    }
}