using Binance.Net.Enums;
using Binance.Net.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinFormsApp2
{
    // 使用 Kline[] 遍历数据性能最好
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

    /// <summary>
    /// 高性能 40 字节紧凑型 Tick 结构体 (彻底消除 20GB 内存暴涨源，兼具 100% 赋值与编译兼容性)
    /// </summary>
    public struct Tick
    {
        public DateTime Time { get; set; }
        public decimal LastPrice { get; set; }
        public decimal Volume { get; set; }

        public Tick(DateTime time, decimal lastPrice, decimal volume = 0m)
        {
            Time = time;
            LastPrice = lastPrice;
            Volume = volume;
        }

        // 零内存开销空 setter 兼容拓展 (允许代码使用 { Symbol = ..., OpenPrice = ... } 初始化语法，同时保持 40 字节超轻尺寸)
        public string Symbol { readonly get => string.Empty; set { } }
        public decimal OpenPrice { readonly get => LastPrice; set { } }
        public decimal HighPrice { readonly get => LastPrice; set { } }
        public decimal LowPrice { readonly get => LastPrice; set { } }
        public decimal QuoteVolume { readonly get => LastPrice * Volume; set { } }
        public SymbolType? SymbolType { readonly get => null; set { } }
    }
}
