using Binance.Net.Enums;
using Binance.Net.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinFormsApp2
{
    public static class Config
    {
        //默认格式 d:\data\[币种]\[时间周期]
        public static string TickDataRoot = "D:\\data";//tick数据根目录
        public static string GetDataPath(string coin,KlineInterval klineInterval)
        {
            return $"{TickDataRoot}\\{coin}\\{klineInterval}";
        }

        public static string GetTradeDataPath(string coin)
        {
            return $"{TickDataRoot}\\{coin}\\Trade";
        }
    }
}
