using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Common.Helper;
using System;

namespace Common.Models
{
    /// <summary>
    /// 标准统一 K 线行情数据模型 (原生强类型，UTC+0 时间)
    /// 可与 Binance.Net 现货/合约 K 线对象无缝转换
    /// </summary>
    public class MarketKline
    {
        public string Symbol { get; set; } = string.Empty;
        public string Interval { get; set; } = string.Empty;

        public DateTime OpenTime { get; set; }
        public DateTime CloseTime { get; set; }

        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }

        public decimal Volume { get; set; }
        public decimal QuoteVolume { get; set; }
        public long TradesCount { get; set; }

        public decimal TakerBuyBaseVolume { get; set; }
        public decimal TakerBuyQuoteVolume { get; set; }

        /// <summary>
        /// 是否为已收盘 K 线 (实盘推流时标记)
        /// </summary>
        public bool IsClosed { get; set; } = true;

        #region UI/用户层按需格式化辅助属性 (零预先格式化开销)

        public string FormattedOpenTime => OpenTime.ToUtc0String();
        public string FormattedCloseTime => CloseTime.ToUtc0String();

        #endregion

        #region Binance.Net 类型快速转换工厂方法

        /// <summary>
        /// 从 Binance.Net IBinanceKline 构造
        /// </summary>
        public static MarketKline FromBinanceKline(string symbol, string interval, IBinanceKline kline)
        {
            return new MarketKline
            {
                Symbol = symbol.ToUpper(),
                Interval = interval,
                OpenTime = TimeHelper.EnsureUtc(kline.OpenTime),
                CloseTime = TimeHelper.EnsureUtc(kline.CloseTime),
                Open = kline.OpenPrice,
                High = kline.HighPrice,
                Low = kline.LowPrice,
                Close = kline.ClosePrice,
                Volume = kline.Volume,
                QuoteVolume = kline.QuoteVolume,
                TradesCount = kline.TradeCount,
                TakerBuyBaseVolume = kline.TakerBuyBaseVolume,
                TakerBuyQuoteVolume = kline.TakerBuyQuoteVolume,
                IsClosed = true
            };
        }

        /// <summary>
        /// 从 Binance.Net WebSocket 实时推流数据构造
        /// </summary>
        public static MarketKline FromBinanceStream(IBinanceStreamKlineData streamData)
        {
            var data = streamData.Data;
            return new MarketKline
            {
                Symbol = streamData.Symbol.ToUpper(),
                Interval = data.Interval.ToIntervalString(),
                OpenTime = TimeHelper.EnsureUtc(data.OpenTime),
                CloseTime = TimeHelper.EnsureUtc(data.CloseTime),
                Open = data.OpenPrice,
                High = data.HighPrice,
                Low = data.LowPrice,
                Close = data.ClosePrice,
                Volume = data.Volume,
                QuoteVolume = data.QuoteVolume,
                TradesCount = data.TradeCount,
                TakerBuyBaseVolume = data.TakerBuyBaseVolume,
                TakerBuyQuoteVolume = data.TakerBuyQuoteVolume,
                IsClosed = data.Final
            };
        }

        #endregion

        public override string ToString()
        {
            return $"[{Symbol} {Interval}] {FormattedOpenTime} O:{Open} H:{High} L:{Low} C:{Close} V:{Volume}";
        }
    }
}
