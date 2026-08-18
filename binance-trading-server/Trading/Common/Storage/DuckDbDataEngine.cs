using Binance.Net.Enums;
using Common.Helper;
using Common.Models;
using DuckDB.NET.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Common.Storage
{
    /// <summary>
    /// DuckDB 高性能市场数据直读引擎
    /// 支持直接读取 CSV / Zip 压缩包并极速加载至内存，供游标与策略进行无延迟访问
    /// </summary>
    public class DuckDbDataEngine : IDisposable
    {
        private readonly string _cacheDirectory;
        private bool _disposed = false;

        public DuckDbDataEngine()
        {
            _cacheDirectory = Path.Combine(Config.GetRootPath(), "_cache_extracted");
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
            }
        }

        #region K 线数据读取

        /// <summary>
        /// 使用 DuckDB 极速批量加载指定日期区间的 K 线数据
        /// </summary>
        public List<MarketKline> LoadKlines(string symbol, string interval, DateTime startUtc, DateTime endUtc)
        {
            var klines = new List<MarketKline>();
            startUtc = TimeHelper.EnsureUtc(startUtc);
            endUtc = TimeHelper.EnsureUtc(endUtc);

            long startMs = TimeHelper.ToUnixTimeMilliseconds(startUtc);
            long endMs = TimeHelper.ToUnixTimeMilliseconds(endUtc);

            var csvFiles = ResolveKlineCsvFiles(symbol, interval, startUtc, endUtc);
            if (csvFiles.Count == 0)
            {
                return klines;
            }

            try
            {
                using var connection = new DuckDBConnection("DataSource=:memory:");
                connection.Open();

                // 使用 DuckDB read_csv 批量并发读取多个 CSV 文件
                string fileListSql = string.Join(", ", csvFiles.Select(f => $"'{f.Replace("\\", "/")}'"));
                string query = $@"
                    SELECT 
                        column00 AS open_time,
                        column01 AS open_p,
                        column02 AS high_p,
                        column03 AS low_p,
                        column04 AS close_p,
                        column05 AS volume,
                        column06 AS close_time,
                        column07 AS quote_vol,
                        column08 AS trades_cnt,
                        column09 AS taker_base_vol,
                        column10 AS taker_quote_vol
                    FROM read_csv([{fileListSql}], header=false, auto_detect=true, union_by_name=false)
                    WHERE TRY_CAST(column00 AS BIGINT) IS NOT NULL
                      AND CAST(column00 AS BIGINT) >= {startMs}
                      AND CAST(column00 AS BIGINT) <= {endMs}
                    ORDER BY CAST(column00 AS BIGINT) ASC;";

                using var command = connection.CreateCommand();
                command.CommandText = query;

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    long openTimeMs = Convert.ToInt64(reader[0]);
                    long closeTimeMs = Convert.ToInt64(reader[6]);

                    var kline = new MarketKline
                    {
                        Symbol = symbol.ToUpper(),
                        Interval = interval,
                        OpenTime = TimeHelper.FromUnixTimeMilliseconds(openTimeMs),
                        CloseTime = TimeHelper.FromUnixTimeMilliseconds(closeTimeMs),
                        Open = Convert.ToDecimal(reader[1]),
                        High = Convert.ToDecimal(reader[2]),
                        Low = Convert.ToDecimal(reader[3]),
                        Close = Convert.ToDecimal(reader[4]),
                        Volume = Convert.ToDecimal(reader[5]),
                        QuoteVolume = Convert.ToDecimal(reader[7]),
                        TradesCount = Convert.ToInt64(reader[8]),
                        TakerBuyBaseVolume = Convert.ToDecimal(reader[9]),
                        TakerBuyQuoteVolume = Convert.ToDecimal(reader[10]),
                        IsClosed = true
                    };
                    klines.Add(kline);
                }
            }
            catch
            {
                // 若 DuckDB 批量查询受特定异常限制，启用鲁棒流式逐文件读取备用回退
                klines = FallbackLoadKlines(symbol, interval, csvFiles, startMs, endMs);
            }

            return klines;
        }

        #endregion

        #region Trade / Tick 数据读取

        /// <summary>
        /// 使用 DuckDB 极速批量加载指定日期区间的 Tick/Trade 逐笔成交数据
        /// </summary>
        public List<MarketTick> LoadTrades(string symbol, DateTime startUtc, DateTime endUtc)
        {
            var trades = new List<MarketTick>();
            startUtc = TimeHelper.EnsureUtc(startUtc);
            endUtc = TimeHelper.EnsureUtc(endUtc);

            long startMs = TimeHelper.ToUnixTimeMilliseconds(startUtc);
            long endMs = TimeHelper.ToUnixTimeMilliseconds(endUtc);

            var csvFiles = ResolveTradeCsvFiles(symbol, startUtc, endUtc);
            if (csvFiles.Count == 0)
            {
                return trades;
            }

            try
            {
                using var connection = new DuckDBConnection("DataSource=:memory:");
                connection.Open();

                string fileListSql = string.Join(", ", csvFiles.Select(f => $"'{f.Replace("\\", "/")}'"));
                string query = $@"
                    SELECT 
                        column00 AS trade_id,
                        column01 AS price,
                        column02 AS qty,
                        column03 AS quote_qty,
                        column04 AS trade_time,
                        column05 AS is_buyer_maker,
                        column06 AS is_best_match
                    FROM read_csv([{fileListSql}], header=false, auto_detect=true, union_by_name=false)
                    WHERE TRY_CAST(column04 AS BIGINT) IS NOT NULL
                      AND CAST(column04 AS BIGINT) >= {startMs}
                      AND CAST(column04 AS BIGINT) <= {endMs}
                    ORDER BY CAST(column04 AS BIGINT) ASC;";

                using var command = connection.CreateCommand();
                command.CommandText = query;

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    long timeMs = Convert.ToInt64(reader[4]);

                    var tick = new MarketTick
                    {
                        Symbol = symbol.ToUpper(),
                        TradeId = Convert.ToInt64(reader[0]),
                        Price = Convert.ToDecimal(reader[1]),
                        Quantity = Convert.ToDecimal(reader[2]),
                        QuoteQuantity = Convert.ToDecimal(reader[3]),
                        Time = TimeHelper.FromUnixTimeMilliseconds(timeMs),
                        IsBuyerMaker = Convert.ToBoolean(reader[5]),
                        IsBestMatch = reader.FieldCount > 6 ? Convert.ToBoolean(reader[6]) : true
                    };
                    trades.Add(tick);
                }
            }
            catch
            {
                trades = FallbackLoadTrades(symbol, csvFiles, startMs, endMs);
            }

            return trades;
        }

        #endregion

        #region 文件定位与解压支持

        private List<string> ResolveKlineCsvFiles(string symbol, string interval, DateTime startUtc, DateTime endUtc)
        {
            var csvFiles = new List<string>();
            DateTime current = startUtc.Date;
            DateTime end = endUtc.Date;
            string klineBaseDir = Config.GetKlineDataPath(symbol, interval);

            while (current <= end)
            {
                string dateStr = current.ToString("yyyy-MM-dd");
                string csvPath = Config.GetKlineFilePath(symbol, interval, current, ".csv");
                string zipPath = Config.GetKlineFilePath(symbol, interval, current, ".zip");

                // 1. 标准层级路径 (klines/30m/yyyy/MM/xxx.zip)
                if (File.Exists(csvPath))
                {
                    csvFiles.Add(csvPath);
                }
                else if (File.Exists(zipPath))
                {
                    string extracted = EnsureZipExtracted(zipPath, symbol, "klines", interval, current);
                    if (!string.IsNullOrEmpty(extracted) && File.Exists(extracted)) csvFiles.Add(extracted);
                }
                else
                {
                    // 2. 扁平路径或子目录扫描 (BTCUSDT/klines/30m/xxx-2026-01-01.zip)
                    string flatZip = Path.Combine(klineBaseDir, Config.GetKlineFileName(symbol, interval, current, ".zip"));
                    string flatCsv = Path.Combine(klineBaseDir, Config.GetKlineFileName(symbol, interval, current, ".csv"));

                    if (File.Exists(flatCsv))
                    {
                        csvFiles.Add(flatCsv);
                    }
                    else if (File.Exists(flatZip))
                    {
                        string extracted = EnsureZipExtracted(flatZip, symbol, "klines", interval, current);
                        if (!string.IsNullOrEmpty(extracted) && File.Exists(extracted)) csvFiles.Add(extracted);
                    }
                    else if (Directory.Exists(klineBaseDir))
                    {
                        // 动态模糊匹配包含该日期的文件
                        var matchedFiles = Directory.GetFiles(klineBaseDir, $"*{dateStr}*", SearchOption.AllDirectories);
                        foreach (var mf in matchedFiles)
                        {
                            if (mf.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                            {
                                csvFiles.Add(mf);
                                break;
                            }
                            else if (mf.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            {
                                string extracted = EnsureZipExtracted(mf, symbol, "klines", interval, current);
                                if (!string.IsNullOrEmpty(extracted) && File.Exists(extracted))
                                {
                                    csvFiles.Add(extracted);
                                    break;
                                }
                            }
                        }
                    }
                }

                current = current.AddDays(1);
            }

            return csvFiles.Distinct().ToList();
        }

        private List<string> ResolveTradeCsvFiles(string symbol, DateTime startUtc, DateTime endUtc)
        {
            var csvFiles = new List<string>();
            DateTime current = startUtc.Date;
            DateTime end = endUtc.Date;
            string tradeBaseDir = Config.GetTradeDataPath(symbol);

            while (current <= end)
            {
                string dateStr = current.ToString("yyyy-MM-dd");
                string csvPath = Config.GetTradeFilePath(symbol, current, ".csv");
                string zipPath = Config.GetTradeFilePath(symbol, current, ".zip");

                // 1. 标准层级路径 (trades/yyyy/MM/xxx-trades-yyyy-MM-dd.zip)
                if (File.Exists(csvPath))
                {
                    csvFiles.Add(csvPath);
                }
                else if (File.Exists(zipPath))
                {
                    string extracted = EnsureZipExtracted(zipPath, symbol, "trades", string.Empty, current);
                    if (!string.IsNullOrEmpty(extracted) && File.Exists(extracted)) csvFiles.Add(extracted);
                }
                else
                {
                    // 2. 扁平路径 (如 D:\data\binance_market_data\BTCUSDT\trades\BTCUSDT-trades-2026-01-01.zip)
                    string flatZip = Path.Combine(tradeBaseDir, Config.GetTradeFileName(symbol, current, ".zip"));
                    string flatCsv = Path.Combine(tradeBaseDir, Config.GetTradeFileName(symbol, current, ".csv"));

                    if (File.Exists(flatCsv))
                    {
                        csvFiles.Add(flatCsv);
                    }
                    else if (File.Exists(flatZip))
                    {
                        string extracted = EnsureZipExtracted(flatZip, symbol, "trades", string.Empty, current);
                        if (!string.IsNullOrEmpty(extracted) && File.Exists(extracted)) csvFiles.Add(extracted);
                    }
                    else if (Directory.Exists(tradeBaseDir))
                    {
                        // 3. 动态全目录递归搜索匹配对应日期的 trade/aggTrade 文件
                        var matchedFiles = Directory.GetFiles(tradeBaseDir, $"*{dateStr}*", SearchOption.AllDirectories);
                        foreach (var mf in matchedFiles)
                        {
                            if (mf.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                            {
                                csvFiles.Add(mf);
                                break;
                            }
                            else if (mf.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            {
                                string extracted = EnsureZipExtracted(mf, symbol, "trades", string.Empty, current);
                                if (!string.IsNullOrEmpty(extracted) && File.Exists(extracted))
                                {
                                    csvFiles.Add(extracted);
                                    break;
                                }
                            }
                        }
                    }
                }

                current = current.AddDays(1);
            }

            return csvFiles.Distinct().ToList();
        }

        private string EnsureZipExtracted(string zipFilePath, string symbol, string type, string interval, DateTime date)
        {
            try
            {
                string relativeSubDir = string.IsNullOrEmpty(interval)
                    ? Path.Combine(symbol.ToUpper(), type, date.ToString("yyyy"), date.ToString("MM"))
                    : Path.Combine(symbol.ToUpper(), type, interval, date.ToString("yyyy"), date.ToString("MM"));

                string targetDir = Path.Combine(_cacheDirectory, relativeSubDir);
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                string expectedCsvName = Path.GetFileNameWithoutExtension(zipFilePath) + ".csv";
                string targetCsvPath = Path.Combine(targetDir, expectedCsvName);

                if (File.Exists(targetCsvPath))
                {
                    return targetCsvPath;
                }

                using (var archive = ZipFile.OpenRead(zipFilePath))
                {
                    var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
                    if (entry != null)
                    {
                        entry.ExtractToFile(targetCsvPath, overwrite: true);
                        return targetCsvPath;
                    }
                }
            }
            catch
            {
                // 忽略解压失败
            }

            return string.Empty;
        }

        #endregion

        #region 备用安全流式读取器 (Zero-DuckDB 异常防御)

        private List<MarketKline> FallbackLoadKlines(string symbol, string interval, List<string> files, long startMs, long endMs)
        {
            var list = new List<MarketKline>();
            foreach (var file in files)
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
                using var reader = new StreamReader(stream);

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(',');
                    if (parts.Length < 11) continue;

                    if (!long.TryParse(parts[0], out long openTimeMs)) continue;
                    if (openTimeMs < startMs || openTimeMs > endMs) continue;

                    if (!long.TryParse(parts[6], out long closeTimeMs)) continue;
                    decimal.TryParse(parts[1], out decimal open);
                    decimal.TryParse(parts[2], out decimal high);
                    decimal.TryParse(parts[3], out decimal low);
                    decimal.TryParse(parts[4], out decimal close);
                    decimal.TryParse(parts[5], out decimal volume);
                    decimal.TryParse(parts[7], out decimal quoteVolume);
                    long.TryParse(parts[8], out long tradeCount);
                    decimal.TryParse(parts[9], out decimal takerBuyBase);
                    decimal.TryParse(parts[10], out decimal takerBuyQuote);

                    list.Add(new MarketKline
                    {
                        Symbol = symbol.ToUpper(),
                        Interval = interval,
                        OpenTime = TimeHelper.FromUnixTimeMilliseconds(openTimeMs),
                        CloseTime = TimeHelper.FromUnixTimeMilliseconds(closeTimeMs),
                        Open = open,
                        High = high,
                        Low = low,
                        Close = close,
                        Volume = volume,
                        QuoteVolume = quoteVolume,
                        TradesCount = tradeCount,
                        TakerBuyBaseVolume = takerBuyBase,
                        TakerBuyQuoteVolume = takerBuyQuote,
                        IsClosed = true
                    });
                }
            }
            return list;
        }

        private List<MarketTick> FallbackLoadTrades(string symbol, List<string> files, long startMs, long endMs)
        {
            var list = new List<MarketTick>();
            foreach (var file in files)
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
                using var reader = new StreamReader(stream);

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(',');
                    if (parts.Length < 6) continue;

                    if (!long.TryParse(parts[4], out long tradeTimeMs)) continue;
                    if (tradeTimeMs < startMs || tradeTimeMs > endMs) continue;

                    long.TryParse(parts[0], out long tradeId);
                    decimal.TryParse(parts[1], out decimal price);
                    decimal.TryParse(parts[2], out decimal qty);
                    decimal.TryParse(parts[3], out decimal quoteQty);
                    bool.TryParse(parts[5], out bool isBuyerMaker);
                    bool isBestMatch = parts.Length > 6 && bool.TryParse(parts[6], out bool bm) ? bm : true;

                    list.Add(new MarketTick
                    {
                        Symbol = symbol.ToUpper(),
                        TradeId = tradeId,
                        Price = price,
                        Quantity = qty,
                        QuoteQuantity = quoteQty,
                        Time = TimeHelper.FromUnixTimeMilliseconds(tradeTimeMs),
                        IsBuyerMaker = isBuyerMaker,
                        IsBestMatch = isBestMatch
                    });
                }
            }
            return list;
        }

        #endregion

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }
}
