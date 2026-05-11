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
}