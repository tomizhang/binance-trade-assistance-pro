using Common.Helper;
using System;

namespace Common.Models
{
    /// <summary>
    /// 策略信号类型
    /// </summary>
    public enum SignalType
    {
        /// <summary>
        /// 开多 / 买入 (Open Long / Buy)
        /// </summary>
        Buy = 1,

        /// <summary>
        /// 开空 / 卖出 (Open Short / Sell)
        /// </summary>
        Sell = 2,

        /// <summary>
        /// 平多 (Close Long)
        /// </summary>
        CloseLong = 3,

        /// <summary>
        /// 平空 (Close Short)
        /// </summary>
        CloseShort = 4,

        /// <summary>
        /// 全平 (Close All)
        /// </summary>
        CloseAll = 5,

        /// <summary>
        /// 仅告警/提示 (Alert Only)
        /// </summary>
        Alert = 6
    }

    /// <summary>
    /// 标准统一策略交易信号模型 (UTC+0 时间，原生强类型)
    /// </summary>
    public class StrategySignal
    {
        /// <summary>
        /// 策略唯一标识
        /// </summary>
        public string StrategyId { get; set; } = string.Empty;

        /// <summary>
        /// 策略名称
        /// </summary>
        public string StrategyName { get; set; } = string.Empty;

        /// <summary>
        /// 交易对 (如 BTCUSDT)
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// 信号触发时间 (UTC+0)
        /// </summary>
        public DateTime Time { get; set; }

        /// <summary>
        /// 信号类型
        /// </summary>
        public SignalType Type { get; set; }

        /// <summary>
        /// 触发价格
        /// </summary>
        public decimal Price { get; set; }

        /// <summary>
        /// 建议委托数量 (0 为按默认资金比例执行)
        /// </summary>
        public decimal Quantity { get; set; }

        /// <summary>
        /// 止损价格 (可选)
        /// </summary>
        public decimal? StopLossPrice { get; set; }

        /// <summary>
        /// 止盈价格 (可选)
        /// </summary>
        public decimal? TakeProfitPrice { get; set; }

        /// <summary>
        /// 信号产生原因/描述 (如 "MA金叉买入", "突破前高")
        /// </summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>
        /// 扩展数据
        /// </summary>
        public object? ExtraData { get; set; }

        #region UI/日志按需格式化

        public string FormattedTime => Time.ToUtc0String();

        #endregion

        public override string ToString()
        {
            return $"[{FormattedTime}] 策略: {StrategyName} | 信号: {Type} | 交易对: {Symbol} | 价格: {Price:F2} | 原因: {Reason}";
        }
    }
}
