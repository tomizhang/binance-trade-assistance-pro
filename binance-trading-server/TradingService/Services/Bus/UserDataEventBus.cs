using System;

namespace TradingTerminal.Services
{
    // 标准化的订单成交/更新事件模型
    public class OrderTradeUpdateEvent
    {
        public string Symbol { get; set; }
        public string OrderType { get; set; }
        public string OrderStatus { get; set; } // NEW, FILLED, CANCELED 等
        public decimal RealizedPnl { get; set; } // 已实现盈亏
        public decimal Commission { get; set; }  // 手续费
        public string ClientOrderId { get; set; }
    }

    /// <summary>
    /// 账户私有数据总线 (Pub/Sub)
    /// </summary>
    public class UserDataEventBus
    {
        // 1. 订单状态更新事件
        public event Action<OrderTradeUpdateEvent> OnOrderTradeUpdated;

        // 2. 原始 JSON 数据广播 (给前端或者其他只需要原始数据的模块)
        public event Action<string> OnRawUserDataReceived;

        public void PublishOrderTradeUpdate(OrderTradeUpdateEvent tradeEvent)
        {
            OnOrderTradeUpdated?.Invoke(tradeEvent);
        }

        public void PublishRawUserData(string rawJson)
        {
            OnRawUserDataReceived?.Invoke(rawJson);
        }
    }
}