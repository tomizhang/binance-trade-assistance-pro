using Binance.Net.Enums;
using Common.Cursor;
using Common.Helper;
using Common.Interfaces;
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
    /// 支持直接读取 CSV / Zip 压缩包并直接返回 DuckDB 原生列式游标 (无时间过滤)
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

        #region 🌟 队列管道流式数据游标 (后台生产预取与背压控制，避免一次性加载大内存与界面卡顿)

        /// <summary>
        /// 队列管道流式获取 K 线数据游标 (后台逐文件生产预取，内存恒定，零界面卡顿)
        /// </summary>
        public IRawDataCursor QueryStreamingKlineCursor(string symbol, string interval, DateTime startUtc, DateTime endUtc, int queueCapacity = 2000)
        {
            var csvFiles = ResolveKlineCsvFiles(symbol, interval, startUtc, endUtc);
            if (csvFiles.Count == 0)
            {
                return new RawMarketDataCursor(new List<MarketKline>());
            }

            return StreamingQueueRawDataCursor.CreateKlineStreamingCursor(async (writer, ct) =>
            {
                foreach (var file in csvFiles)
                {
                    if (ct.IsCancellationRequested) break;

                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
                    using var reader = new StreamReader(stream);

                    string? line;
                    while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var parts = line.Split(',');
                        if (parts.Length < 6) continue;

                        if (!long.TryParse(parts[0], out long openTimeMs)) continue;
                        long closeTimeMs = parts.Length > 6 && long.TryParse(parts[6], out long ctm) ? ctm : openTimeMs;

                        decimal.TryParse(parts[1], out decimal open);
                        decimal.TryParse(parts[2], out decimal high);
                        decimal.TryParse(parts[3], out decimal low);
                        decimal.TryParse(parts[4], out decimal close);
                        decimal.TryParse(parts[5], out decimal volume);
                        decimal quoteVolume = parts.Length > 7 && decimal.TryParse(parts[7], out decimal qv) ? qv : 0m;
                        long tradeCount = parts.Length > 8 && long.TryParse(parts[8], out long tc) ? tc : 0L;
                        decimal takerBuyBase = parts.Length > 9 && decimal.TryParse(parts[9], out decimal tbb) ? tbb : 0m;
                        decimal takerBuyQuote = parts.Length > 10 && decimal.TryParse(parts[10], out decimal tbq) ? tbq : 0m;

                        var row = new RawKlineRow(
                            openTimeMs, open, high, low, close, volume, closeTimeMs, quoteVolume, tradeCount, takerBuyBase, takerBuyQuote);

                        await writer.WriteAsync(row, ct).ConfigureAwait(false);
                    }
                }
            }, queueCapacity: queueCapacity, totalEstimatedCount: csvFiles.Count * 48);
        }

        /// <summary>
        /// 队列管道流式获取 Tick/Trade 数据游标 (支持数百万笔海量逐笔成交流式推送，零 GC 压力)
        /// </summary>
        public IRawDataCursor QueryStreamingTradeCursor(string symbol, DateTime startUtc, DateTime endUtc, int queueCapacity = 10000)
        {
            var csvFiles = ResolveTradeCsvFiles(symbol, startUtc, endUtc);
            if (csvFiles.Count == 0)
            {
                return new RawMarketDataCursor(new List<MarketTick>());
            }

            return StreamingQueueRawDataCursor.CreateTradeStreamingCursor(async (writer, ct) =>
            {
                foreach (var file in csvFiles)
                {
                    if (ct.IsCancellationRequested) break;

                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
                    using var reader = new StreamReader(stream);

                    string? line;
                    while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var parts = line.Split(',');
                        if (parts.Length < 6) continue;

                        if (!long.TryParse(parts[4], out long tradeTimeMs)) continue;

                        long.TryParse(parts[0], out long tradeId);
                        decimal.TryParse(parts[1], out decimal price);
                        decimal.TryParse(parts[2], out decimal qty);
                        decimal.TryParse(parts[3], out decimal quoteQty);
                        bool.TryParse(parts[5], out bool isBuyerMaker);
                        bool isBestMatch = parts.Length > 6 && bool.TryParse(parts[6], out bool bm) ? bm : true;

                        var row = new RawTradeRow(tradeId, price, qty, quoteQty, tradeTimeMs, isBuyerMaker, isBestMatch);
                        await writer.WriteAsync(row, ct).ConfigureAwait(false);
                    }
                }
            }, queueCapacity: queueCapacity, totalEstimatedCount: csvFiles.Count * 10000);
        }

        #endregion

        #region 🌟 直接返回 DuckDB 原生高性能列式游标 (无时间过滤)

        /// <summary>
        /// 直接执行 DuckDB 查询并返回原生列式游标 (不进行时间过滤，直接返回全部解析出的 K 线数据)
        /// </summary>
        public IRawDataCursor QueryRawKlineCursor(string symbol, string interval, DateTime startUtc, DateTime endUtc)
        {
            var csvFiles = ResolveKlineCsvFiles(symbol, interval, startUtc, endUtc);
            if (csvFiles.Count == 0)
            {
                return new RawMarketDataCursor(new List<MarketKline>());
            }

            try
            {
                using var connection = new DuckDBConnection("DataSource=:memory:");
                connection.Open();

                string fileListSql = string.Join(", ", csvFiles.Select(f => $"'{f.Replace("\\", "/")}'"));
                // 🌟 不进行实时间过滤，直接读取全部数据并按时间戳升序排序
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
                    ORDER BY CAST(column00 AS BIGINT) ASC;";

                using var command = connection.CreateCommand();
                command.CommandText = query;

                using var reader = command.ExecuteReader();
                return DuckDbRawDataCursor.CreateFromKlineReader((DuckDBDataReader)reader);
            }
            catch
            {
                // 回退：无时间过滤读取
                var list = FallbackLoadKlines(symbol, interval, csvFiles);
                return new RawMarketDataCursor(list);
            }
        }

        /// <summary>
        /// 直接执行 DuckDB 查询并返回原生列式 Trade 游标 (无时间过滤)
        /// </summary>
        public IRawDataCursor QueryRawTradeCursor(string symbol, DateTime startUtc, DateTime endUtc)
        {
            var csvFiles = ResolveTradeCsvFiles(symbol, startUtc, endUtc);
            if (csvFiles.Count == 0)
            {
                return new RawMarketDataCursor(new List<MarketTick>());
            }

            try
            {
                using var connection = new DuckDBConnection("DataSource=:memory:");
                connection.Open();

                string fileListSql = string.Join(", ", csvFiles.Select(f => $"'{f.Replace("\\", "/")}'"));
                // 🌟 不进行实时间过滤，直接读取全部 Trade 数据
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
                    ORDER BY CAST(column04 AS BIGINT) ASC;";

                using var command = connection.CreateCommand();
                command.CommandText = query;

                using var reader = command.ExecuteReader();
                return DuckDbRawDataCursor.CreateFromTradeReader((DuckDBDataReader)reader);
            }
            catch
            {
                var list = FallbackLoadTrades(symbol, csvFiles);
                return new RawMarketDataCursor(list);
            }
        }

        #endregion

        #region K 线与 Trade 数据列表直接读取 (无时间过滤)

        /// <summary>
        /// 使用 DuckDB 极速批量加载指定日期区间的 K 线数据 (无时间过滤)
        /// </summary>
        public List<MarketKline> LoadKlines(string symbol, string interval, DateTime startUtc, DateTime endUtc)
        {
            var klines = new List<MarketKline>();
            var csvFiles = ResolveKlineCsvFiles(symbol, interval, startUtc, endUtc);
            if (csvFiles.Count == 0)
            {
                return klines;
            }

            try
            {
                using var connection = new DuckDBConnection("DataSource=:memory:");
                connection.Open();

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
                        QuoteVolume = reader.FieldCount > 7 ? Convert.ToDecimal(reader[7]) : 0m,
                        TradesCount = reader.FieldCount > 8 ? Convert.ToInt64(reader[8]) : 0L,
                        TakerBuyBaseVolume = reader.FieldCount > 9 ? Convert.ToDecimal(reader[9]) : 0m,
                        TakerBuyQuoteVolume = reader.FieldCount > 10 ? Convert.ToDecimal(reader[10]) : 0m,
                        IsClosed = true
                    };
                    klines.Add(kline);
                }
            }
            catch
            {
                klines = FallbackLoadKlines(symbol, interval, csvFiles);
            }

            return klines;
        }

        /// <summary>
        /// 使用 DuckDB 极速批量加载指定日期区间的 Tick/Trade 逐笔成交数据 (无时间过滤)
        /// </summary>
        public List<MarketTick> LoadTrades(string symbol, DateTime startUtc, DateTime endUtc)
        {
            var trades = new List<MarketTick>();
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
                trades = FallbackLoadTrades(symbol, csvFiles);
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

        #region 备用安全流式读取器 (无时间过滤)

        private List<MarketKline> FallbackLoadKlines(string symbol, string interval, List<string> files)
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
                    if (parts.Length < 6) continue;

                    if (!long.TryParse(parts[0], out long openTimeMs)) continue;
                    long closeTimeMs = parts.Length > 6 && long.TryParse(parts[6], out long ctm) ? ctm : openTimeMs;

                    decimal.TryParse(parts[1], out decimal open);
                    decimal.TryParse(parts[2], out decimal high);
                    decimal.TryParse(parts[3], out decimal low);
                    decimal.TryParse(parts[4], out decimal close);
                    decimal.TryParse(parts[5], out decimal volume);
                    decimal quoteVolume = parts.Length > 7 && decimal.TryParse(parts[7], out decimal qv) ? qv : 0m;
                    long tradeCount = parts.Length > 8 && long.TryParse(parts[8], out long tc) ? tc : 0L;
                    decimal takerBuyBase = parts.Length > 9 && decimal.TryParse(parts[9], out decimal tbb) ? tbb : 0m;
                    decimal takerBuyQuote = parts.Length > 10 && decimal.TryParse(parts[10], out decimal tbq) ? tbq : 0m;

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

        private List<MarketTick> FallbackLoadTrades(string symbol, List<string> files)
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
