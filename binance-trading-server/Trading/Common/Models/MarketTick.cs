using Binance.Net.Interfaces;
using Binance.Net.Objects.Models.Spot.Socket;
using Common.Helper;
using System;

namespace Common.Models
{
    /// <summary>
    /// 标准统一 Tick / Trade 逐笔成交行情数据模型 (原生强类型，UTC+0 时间)
    /// 可与 Binance.Net 现货/合约 Trade 对象无缝转换
    /// </summary>
    public class MarketTick
    {
        public string Symbol { get; set; } = string.Empty;
        public long TradeId { get; set; }

        public DateTime Time { get; set; }
        public decimal Price { get; set; }
        public decimal Quantity { get; set; }
        public decimal QuoteQuantity { get; set; }

        /// <summary>
        /// 是否为主动作卖方 (true: 卖单挂单被买入吃单，买方为 Maker；false: 买方为 Taker)
        /// </summary>
        public bool IsBuyerMaker { get; set; }

        public bool IsBestMatch { get; set; } = true;

        #region 时间戳属性 (UTC+0 Unix 毫秒/秒时间戳)

        /// <summary>
        /// 成交 Unix 毫秒时间戳 (UTC)
        /// </summary>
        public long TimeMs => TimeHelper.ToUnixTimeMilliseconds(Time);

        /// <summary>
        /// 成交 Unix 秒时间戳 (UTC)
        /// </summary>
        public long TimeSeconds => TimeHelper.ToUnixTimeSeconds(Time);

        #endregion

        #region UI/用户层按需格式化辅助属性 (零预先格式化开销)

        public string FormattedTime => Time.ToUtc0String();

        #endregion

        #region Binance.Net 类型快速转换工厂方法

        /// <summary>
        /// 从 Binance.Net IBinanceRecentTrade 构造
        /// </summary>
        public static MarketTick FromBinanceRecentTrade(string symbol, IBinanceRecentTrade trade)
        {
            return new MarketTick
            {
                Symbol = symbol.ToUpper(),
                TradeId = trade.OrderId,
                Time = TimeHelper.EnsureUtc(trade.TradeTime),
                Price = trade.Price,
                Quantity = trade.BaseQuantity,
                QuoteQuantity = trade.QuoteQuantity,
                IsBuyerMaker = trade.BuyerIsMaker,
                IsBestMatch = trade.IsBestMatch
            };
        }

        /// <summary>
        /// 从 Binance.Net WebSocket 实时 Trade 推流数据构造
        /// </summary>
        public static MarketTick FromBinanceStream(BinanceStreamTrade streamTrade)
        {
            return new MarketTick
            {
                Symbol = streamTrade.Symbol.ToUpper(),
                TradeId = streamTrade.Id,
                Time = TimeHelper.EnsureUtc(streamTrade.TradeTime),
                Price = streamTrade.Price,
                Quantity = streamTrade.Quantity,
                QuoteQuantity = streamTrade.Price * streamTrade.Quantity,
                IsBuyerMaker = streamTrade.BuyerIsMaker,
                IsBestMatch = true
            };
        }

        /// <summary>
        /// 从 Binance.Net WebSocket 实时 Aggregated Trade 推流数据构造
        /// </summary>
        public static MarketTick FromBinanceStream(BinanceStreamAggregatedTrade aggTrade)
        {
            return new MarketTick
            {
                Symbol = aggTrade.Symbol.ToUpper(),
                TradeId = aggTrade.Id,
                Time = TimeHelper.EnsureUtc(aggTrade.TradeTime),
                Price = aggTrade.Price,
                Quantity = aggTrade.Quantity,
                QuoteQuantity = aggTrade.Price * aggTrade.Quantity,
                IsBuyerMaker = aggTrade.BuyerIsMaker,
                IsBestMatch = true
            };
        }

        #endregion

        public override string ToString()
        {
            return $"[{Symbol} Tick] {FormattedTime} P:{Price} Q:{Quantity} Maker:{IsBuyerMaker}";
        }
    }
}
