using System;

namespace TradingTerminal.Models
{
    // 定义支持的订单动作
    public enum OrderAction
    {
        OpenMarket,         // 市价开仓
        OpenLimit,          // 限价开仓
        StopLossMarket,     // 市价止损
        TakeProfitMarket,    // 市价止盈
        CancelAll           // 🌟 新增：撤销该币种的所有挂单
    }

    /// <summary>
    /// 标准化订单通讯载体 (在线程间传递)
    /// </summary>
    public class OrderSignal
    {
        public string Symbol { get; set; }
        public OrderAction Action { get; set; }
        public string Side { get; set; } // "BUY" 或 "SELL"
        public decimal? StopLossPrice { get; set; }   // 🌟 专门用于携带止损价
        public decimal? TakeProfitPrice { get; set; } // 🌟 专门用于携带止盈价
        // 🌟 金额策略 (蓝图 2.3 设计)
        public bool IsUsdtMargin { get; set; } = false; // 是否使用 U本位金额下单
        public decimal UsdtAmount { get; set; }         // 如果是 U 本位，传 U 的数量 (例如 100U)
        public decimal Leverage { get; set; }           // 配合 U 本位使用的杠杆倍数
        public decimal Quantity { get; set; }           // 如果按数量下单，传具体币的数量 (例如 0.5 BTC)

        // 价格参数 (限价或触发价)
        public decimal? Price { get; set; }
        public decimal? StopPrice { get; set; }

        // 追踪溯源
        public string StrategyName { get; set; } = "Manual"; // 是哪个策略发出的指令
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public string? Reason { get; set; }//下单理由
        public string? Message { get; set; }//携带消息
    }
}