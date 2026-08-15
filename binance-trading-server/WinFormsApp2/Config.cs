using Binance.Net.Enums;
using System;
using System.IO;

namespace WinFormsApp2
{
    public static class Config
    {
        // 统一数据根目录 (默认 D:\data，具备自动创建与备用回退机制)
        public static string TickDataRoot = "D:\\data"; 

        public static string GetRootPath()
        {
            try
            {
                if (!Directory.Exists(TickDataRoot))
                {
                    Directory.CreateDirectory(TickDataRoot);
                }
                return TickDataRoot;
            }
            catch
            {
                // 安全回退：若系统无 D 盘或写入受限，自动使用应用程序根目录下的 data 文件夹
                string fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (!Directory.Exists(fallback))
                {
                    Directory.CreateDirectory(fallback);
                }
                return fallback;
            }
        }

        public static string GetDataPath(string coin, KlineInterval klineInterval)
        {
            string path = Path.Combine(GetRootPath(), coin.ToUpper(), klineInterval.ToString());
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        public static string GetTradeDataPath(string coin)
        {
            string path = Path.Combine(GetRootPath(), coin.ToUpper(), "Trade");
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        public static string GetParquetRootPath()
        {
            string path = Path.Combine(GetRootPath(), "parquet");
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }
    }
}
