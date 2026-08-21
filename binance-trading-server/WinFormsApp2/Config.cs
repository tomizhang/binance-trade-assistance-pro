using Binance.Net.Enums;
using System;
using System.IO;

namespace WinFormsApp2
{
    /// <summary>
    /// 系统路径与币安行情数据目录配置管理器 (Binance Market Data Path Configuration)
    /// 遵循标准层级结构：
    ///   {RootDir}/{Symbol}/klines/{Interval}/{Year}/{Month}/{Symbol}-{Interval}-{Date}.parquet
    ///   {RootDir}/{Symbol}/trades/{Year}/{Month}/{Symbol}-trades-{Date}.parquet
    ///   {RootDir}/_cache_extracted/...
    ///   {RootDir}/logs/...
    /// </summary>
    public static class Config
    {
        // 统一数据根目录 (默认 D:\data\binance_market_data，具备智能自适应探测与自动创建机制)
        public static string TickDataRoot = @"D:\data\binance_market_data";

        public static string GetRootPath()
        {
            try
            {
                // 1. 如果当前配置的 TickDataRoot 存在，直接使用
                if (Directory.Exists(TickDataRoot))
                {
                    return TickDataRoot;
                }

                // 2. 检查默认路径 D:\data\binance_market_data
                string defaultPath = @"D:\data\binance_market_data";
                if (Directory.Exists(defaultPath))
                {
                    TickDataRoot = defaultPath;
                    return TickDataRoot;
                }

                // 3. 检查 D:\data，若存在则在其下创建/使用 binance_market_data
                if (Directory.Exists(@"D:\data"))
                {
                    string target = Path.Combine(@"D:\data", "binance_market_data");
                    if (!Directory.Exists(target))
                    {
                        Directory.CreateDirectory(target);
                    }
                    TickDataRoot = target;
                    return TickDataRoot;
                }

                // 4. 安全回退：应用程序目录下的 binance_market_data
                string fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "binance_market_data");
                if (!Directory.Exists(fallback))
                {
                    Directory.CreateDirectory(fallback);
                }
                TickDataRoot = fallback;
                return fallback;
            }
            catch
            {
                string fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "binance_market_data");
                if (!Directory.Exists(fallback))
                {
                    try { Directory.CreateDirectory(fallback); } catch { }
                }
                return fallback;
            }
        }

        public static void SetRootPath(string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                TickDataRoot = path.Trim();
                if (!Directory.Exists(TickDataRoot))
                {
                    try { Directory.CreateDirectory(TickDataRoot); } catch { }
                }
            }
        }

        #region K线目录与文件路径 (按 {Symbol}\klines\{Interval}\{Year}\{Month}\...)

        public static string GetKlineDirectory(string symbol, string interval, DateTime date)
        {
            string dir = Path.Combine(GetRootPath(), symbol.ToUpperInvariant(), "klines", interval.ToLowerInvariant(), date.ToString("yyyy"), date.ToString("MM"));
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return dir;
        }

        public static string GetKlineParquetPath(string symbol, string interval, DateTime date)
        {
            string dir = GetKlineDirectory(symbol, interval, date);
            return Path.Combine(dir, $"{symbol.ToUpperInvariant()}-{interval.ToLowerInvariant()}-{date:yyyy-MM-dd}.parquet");
        }

        public static string GetKlineZipPath(string symbol, string interval, DateTime date)
        {
            string dir = GetKlineDirectory(symbol, interval, date);
            return Path.Combine(dir, $"{symbol.ToUpperInvariant()}-{interval.ToLowerInvariant()}-{date:yyyy-MM-dd}.zip");
        }

        #endregion

        #region Trades 逐笔/归集成交目录与文件路径 (按 {Symbol}\trades\{Year}\{Month}\...)

        public static string GetTradeDirectory(string symbol, DateTime date)
        {
            string dir = Path.Combine(GetRootPath(), symbol.ToUpperInvariant(), "trades", date.ToString("yyyy"), date.ToString("MM"));
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return dir;
        }

        public static string GetTradeParquetPath(string symbol, DateTime date)
        {
            string dir = GetTradeDirectory(symbol, date);
            return Path.Combine(dir, $"{symbol.ToUpperInvariant()}-trades-{date:yyyy-MM-dd}.parquet");
        }

        public static string GetTradeZipPath(string symbol, DateTime date)
        {
            string dir = GetTradeDirectory(symbol, date);
            return Path.Combine(dir, $"{symbol.ToUpperInvariant()}-trades-{date:yyyy-MM-dd}.zip");
        }

        #endregion

        #region 解压缓存与日志路径 (_cache_extracted 与 logs)

        public static string GetExtractedCacheDirectory(string symbol, string subCategory, DateTime date, string? interval = null)
        {
            string baseCache = Path.Combine(GetRootPath(), "_cache_extracted", symbol.ToUpperInvariant(), subCategory);
            if (!string.IsNullOrEmpty(interval))
            {
                baseCache = Path.Combine(baseCache, interval.ToLowerInvariant());
            }
            string dir = Path.Combine(baseCache, date.ToString("yyyy"), date.ToString("MM"));
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return dir;
        }

        public static string GetLogsPath()
        {
            string path = Path.Combine(GetRootPath(), "logs");
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        #endregion

        #region 兼容旧版调用方法

        public static string GetDataPath(string coin, KlineInterval klineInterval)
        {
            string intervalStr = DataHelper.ToBinanceVisionIntervalString(klineInterval);
            string path = Path.Combine(GetRootPath(), coin.ToUpperInvariant(), "klines", intervalStr);
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        public static string GetTradeDataPath(string coin)
        {
            string path = Path.Combine(GetRootPath(), coin.ToUpperInvariant(), "trades");
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        public static string GetParquetRootPath()
        {
            return GetRootPath();
        }

        #endregion
    }
}
