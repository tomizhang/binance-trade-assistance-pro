using System;
using System.Collections.Generic;

namespace WinFormsApp1
{
    /// <summary>
    /// 币安合约 K 线时间周期枚举
    /// </summary>
    public enum FuturesKlineInterval
    {
        Min1,
        Min3,
        Min5,
        Min15,
        Min30,
        Hour1,
        Hour2,
        Hour4,
        Hour6,
        Hour8,
        Hour12,
        Day1,
        Day3,
        Week1,
        Month1
    }

    /// <summary>
    /// K线周期枚举扩展方法
    /// </summary>
    public static class FuturesKlineIntervalExtensions
    {
        /// <summary>
        /// 将周期枚举转换为币安 API 要求的字符串 (如 "1m", "1h", "1d")
        /// </summary>
        public static string ToIntervalString(this FuturesKlineInterval interval) => interval switch
        {
            FuturesKlineInterval.Min1 => "1m",
            FuturesKlineInterval.Min3 => "3m",
            FuturesKlineInterval.Min5 => "5m",
            FuturesKlineInterval.Min15 => "15m",
            FuturesKlineInterval.Min30 => "30m",
            FuturesKlineInterval.Hour1 => "1h",
            FuturesKlineInterval.Hour2 => "2h",
            FuturesKlineInterval.Hour4 => "4h",
            FuturesKlineInterval.Hour6 => "6h",
            FuturesKlineInterval.Hour8 => "8h",
            FuturesKlineInterval.Hour12 => "12h",
            FuturesKlineInterval.Day1 => "1d",
            FuturesKlineInterval.Day3 => "3d",
            FuturesKlineInterval.Week1 => "1w",
            FuturesKlineInterval.Month1 => "1M",
            _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, "未知的周期枚举类型")
        };

        /// <summary>
        /// 从周期字符串解析为周期枚举
        /// </summary>
        public static bool TryParseInterval(string intervalStr, out FuturesKlineInterval interval)
        {
            switch (intervalStr?.Trim().ToLowerInvariant())
            {
                case "1m": interval = FuturesKlineInterval.Min1; return true;
                case "3m": interval = FuturesKlineInterval.Min3; return true;
                case "5m": interval = FuturesKlineInterval.Min5; return true;
                case "15m": interval = FuturesKlineInterval.Min15; return true;
                case "30m": interval = FuturesKlineInterval.Min30; return true;
                case "1h": interval = FuturesKlineInterval.Hour1; return true;
                case "2h": interval = FuturesKlineInterval.Hour2; return true;
                case "4h": interval = FuturesKlineInterval.Hour4; return true;
                case "6h": interval = FuturesKlineInterval.Hour6; return true;
                case "8h": interval = FuturesKlineInterval.Hour8; return true;
                case "12h": interval = FuturesKlineInterval.Hour12; return true;
                case "1d": interval = FuturesKlineInterval.Day1; return true;
                case "3d": interval = FuturesKlineInterval.Day3; return true;
                case "1w": interval = FuturesKlineInterval.Week1; return true;
                case "1m_month":
                case "1M": interval = FuturesKlineInterval.Month1; return true;
                default:
                    interval = FuturesKlineInterval.Min1;
                    return false;
            }
        }

        /// <summary>
        /// 获取周期的大约 TimeSpan 间隔（便于计算与预估）
        /// </summary>
        public static TimeSpan ToTimeSpan(this FuturesKlineInterval interval) => interval switch
        {
            FuturesKlineInterval.Min1 => TimeSpan.FromMinutes(1),
            FuturesKlineInterval.Min3 => TimeSpan.FromMinutes(3),
            FuturesKlineInterval.Min5 => TimeSpan.FromMinutes(5),
            FuturesKlineInterval.Min15 => TimeSpan.FromMinutes(15),
            FuturesKlineInterval.Min30 => TimeSpan.FromMinutes(30),
            FuturesKlineInterval.Hour1 => TimeSpan.FromHours(1),
            FuturesKlineInterval.Hour2 => TimeSpan.FromHours(2),
            FuturesKlineInterval.Hour4 => TimeSpan.FromHours(4),
            FuturesKlineInterval.Hour6 => TimeSpan.FromHours(6),
            FuturesKlineInterval.Hour8 => TimeSpan.FromHours(8),
            FuturesKlineInterval.Hour12 => TimeSpan.FromHours(12),
            FuturesKlineInterval.Day1 => TimeSpan.FromDays(1),
            FuturesKlineInterval.Day3 => TimeSpan.FromDays(3),
            FuturesKlineInterval.Week1 => TimeSpan.FromDays(7),
            FuturesKlineInterval.Month1 => TimeSpan.FromDays(30),
            _ => TimeSpan.FromMinutes(1)
        };
    }

    /// <summary>
    /// 币安合约 K 线/蜡烛图数据模型
    /// </summary>
    public class BinanceFuturesKlineItem
    {
        /// <summary>
        /// 开盘时间戳 (毫秒)
        /// </summary>
        public long OpenTimeMs { get; set; }

        /// <summary>
        /// 开盘时间 (UTC)
        /// </summary>
        public DateTime OpenTimeUtc => DateTimeOffset.FromUnixTimeMilliseconds(OpenTimeMs).UtcDateTime;

        /// <summary>
        /// 开盘时间 (本地时间)
        /// </summary>
        public DateTime OpenTimeLocal => DateTimeOffset.FromUnixTimeMilliseconds(OpenTimeMs).LocalDateTime;

        /// <summary>
        /// 开盘价
        /// </summary>
        public decimal Open { get; set; }

        /// <summary>
        /// 最高价
        /// </summary>
        public decimal High { get; set; }

        /// <summary>
        /// 最低价
        /// </summary>
        public decimal Low { get; set; }

        /// <summary>
        /// 收盘价
        /// </summary>
        public decimal Close { get; set; }

        /// <summary>
        /// 成交量 (基础资产)
        /// </summary>
        public decimal Volume { get; set; }

        /// <summary>
        /// 收盘时间戳 (毫秒)
        /// </summary>
        public long CloseTimeMs { get; set; }

        /// <summary>
        /// 收盘时间 (UTC)
        /// </summary>
        public DateTime CloseTimeUtc => DateTimeOffset.FromUnixTimeMilliseconds(CloseTimeMs).UtcDateTime;

        /// <summary>
        /// 收盘时间 (本地时间)
        /// </summary>
        public DateTime CloseTimeLocal => DateTimeOffset.FromUnixTimeMilliseconds(CloseTimeMs).LocalDateTime;

        /// <summary>
        /// 成交额 (计价资产)
        /// </summary>
        public decimal QuoteVolume { get; set; }

        /// <summary>
        /// 成交笔数
        /// </summary>
        public long TradeCount { get; set; }

        /// <summary>
        /// 主动买入基础资产成交量
        /// </summary>
        public decimal TakerBuyBaseVolume { get; set; }

        /// <summary>
        /// 主动买入计价资产成交额
        /// </summary>
        public decimal TakerBuyQuoteVolume { get; set; }

        public override string ToString()
        {
            return $"[{OpenTimeLocal:yyyy-MM-dd HH:mm:ss}] O:{Open} H:{High} L:{Low} C:{Close} Vol:{Volume}";
        }
    }
}
