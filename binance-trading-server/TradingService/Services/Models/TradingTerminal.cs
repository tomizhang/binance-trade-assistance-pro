using System;

namespace TradingTerminal.Models
{
    /// <summary>
    /// 市场微观结构信号类型 (未来可无限扩展)
    /// </summary>
    public enum MarketSignalType
    {
        /// <summary> 无明显信号 </summary>
        None = 0,

        /// <summary> 
        /// 1. 主力做多 (真金白银建仓) 
        /// 特征: 价涨 + 量爆 + OI剧增 
        /// </summary>
        StrongLong = 1,

        /// <summary> 
        /// 2. 主力做空 (真金白银砸盘) 
        /// 特征: 价跌 + 量爆 + OI剧增 
        /// </summary>
        StrongShort = 2,

        /// <summary> 
        /// 3. 空头平仓 (轧空反弹，虚假繁荣) 
        /// 特征: 价涨 + 量爆 + OI锐减 
        /// </summary>
        ShortCovering = 3,

        /// <summary> 
        /// 4. 多头踩踏 (连环爆仓，带血筹码) 
        /// 特征: 价跌 + 量爆 + OI锐减 
        /// </summary>
        LongLiquidation = 4
    }

    /// <summary>
    /// 用于记录某个币种在当前 1 分钟周期内的切片状态
    /// </summary>
    public class SymbolMinuteState
    {
        public decimal StartOi { get; set; } = 0;
        public decimal CurrentOi { get; set; } = 0;
        public decimal PreviousMinuteVolume { get; set; } = 0;
    }

    /// <summary>
    /// 分析引擎输出的完整信号报告
    /// </summary>
    public record StrategyAlert(
        string Symbol,
        MarketSignalType SignalType,
        decimal PriceChangePercent,
        decimal OiChangePercent,
        decimal VolumeMultiplier,
        long Timestamp
    );
}