using Binance.Net.Enums;
using Binance.Net.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinFormsApp2
{
    //使用KlineInfo[]遍历数据性能最好
    public struct Kline
    {
        public DateTime OpenTime { get; set; }
        public decimal OpenPrice { get; set; }
        public decimal HighPrice { get; set; }
        public decimal LowPrice { get; set; }
        public decimal ClosePrice { get; set; }
        public decimal Volume { get; set; }
        public DateTime CloseTime { get; set; }
        public decimal QuoteVolume { get; set; }
        public int TradeCount { get; set; }
        public decimal TakerBuyBaseVolume { get; set; }
        public decimal TakerBuyQuoteVolume { get; set; }
    }

    public struct Tick 
    {
        public string Symbol { get ; set ; }
        public DateTime Time { get; set; }
        public decimal LastPrice { get ; set ; }
        public decimal OpenPrice { get ; set ; }
        public decimal HighPrice { get ; set ; }
        public decimal LowPrice { get ; set ; }
        public decimal Volume { get ; set ; }
        public decimal QuoteVolume { get ; set ; }
        public SymbolType? SymbolType { get ; set ; }
    }
}
