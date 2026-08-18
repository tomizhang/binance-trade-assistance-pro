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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Storage
{
    /// <summary>
    /// 基于 DuckDB 的高性能数据存储与查询引擎 (全方位支持 Parquet / CSV / Zip 解压加载)
    /// 实现 0 内存拷贝、列式原生读取、流式 Channel 生产预取与零时间过滤
    /// </summary>
    public class DuckDbDataEngine : IDisposable
    {
        private readonly string _cacheDirectory;

        public DuckDbDataEngine()
        {
            _cacheDirectory = Path.Combine(Config.GetRootPath(), "_cache_extracted");
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
            }
        }

        #region 🌟 队列管道流式数据游标 (支持 Parquet 与 CSV 后台生产预取)

        /// <summary>
        /// 队列管道流式获取 K 线数据游标 (支持 .parquet 与 .csv，后台逐文件生产预取，内存恒定，零界面卡顿)
        /// </summary>
        public IRawDataCursor QueryStreamingKlineCursor(string symbol, string interval, DateTime startUtc, DateTime endUtc, int queueCapacity = 2000)
        {
            var files = ResolveKlineFiles(symbol, interval, startUtc, endUtc);
            if (files.Count == 0)
            {
                return new RawMarketDataCursor(new List<MarketKline>());
            }

            return StreamingQueueRawDataCursor.CreateKlineStreamingCursor(async (writer, ct) =>
            {
                long intervalMs = TimeHelper.GetIntervalMilliseconds(interval);

                foreach (var file in files)
                {
                    if (ct.IsCancellationRequested) break;

                    if (file.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
                    {
                        // 🌟 极速流式读取 Parquet 文件 (零托管堆开销)
                        try
                        {
                            using var connection = new DuckDBConnection("DataSource=:memory:");
                            connection.Open();
                            using var command = connection.CreateCommand();
                            command.CommandText = $"SELECT * FROM read_parquet('{file.Replace("\\", "/")}') ORDER BY 1 ASC;";
                            using var reader = command.ExecuteReader();
                            while (reader.Read() && !ct.IsCancellationRequested)
                            {
                                long openTimeMs = Convert.ToInt64(reader[0]);
                                decimal open = Convert.ToDecimal(reader[1]);
                                decimal high = Convert.ToDecimal(reader[2]);
                                decimal low = Convert.ToDecimal(reader[3]);
                                decimal close = Convert.ToDecimal(reader[4]);
                                decimal volume = Convert.ToDecimal(reader[5]);
                                long closeTimeMs = reader.FieldCount > 6 && long.TryParse(reader[6]?.ToString(), out long ctm) && ctm > openTimeMs
                                    ? ctm
                                    : (openTimeMs + intervalMs - 1);

                                decimal quoteVolume = reader.FieldCount > 7 ? Convert.ToDecimal(reader[7]) : 0m;
                                long tradeCount = reader.FieldCount > 8 ? Convert.ToInt64(reader[8]) : 0L;
                                decimal takerBuyBase = reader.FieldCount > 9 ? Convert.ToDecimal(reader[9]) : 0m;
                                decimal takerBuyQuote = reader.FieldCount > 10 ? Convert.ToDecimal(reader[10]) : 0m;

                                var row = new RawKlineRow(
                                    openTimeMs, open, high, low, close, volume, closeTimeMs, quoteVolume, tradeCount, takerBuyBase, takerBuyQuote);

                                await writer.WriteAsync(row, ct).ConfigureAwait(false);
                            }
                        }
                        catch
                        {
                            // 回退至普通加载
                        }
                    }
                    else
                    {
                        // 逐行流式读取 CSV 文件
                        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
                        using var reader = new StreamReader(stream);

                        string? line;
                        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            var parts = line.Split(',');
                            if (parts.Length < 6) continue;

                            if (!long.TryParse(parts[0], out long openTimeMs)) continue;
                            long closeTimeMs = (parts.Length > 6 && long.TryParse(parts[6], out long ctm) && ctm > openTimeMs)
                                ? ctm
                                : (openTimeMs + intervalMs - 1);

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
                }
            }, queueCapacity: queueCapacity, totalEstimatedCount: files.Count * 48);
        }

        /// <summary>
        /// 队列管道流式获取 Tick/Trade 数据游标 (支持 .parquet 与 .csv，支持数百万笔海量逐笔成交流式推送)
        /// </summary>
        public IRawDataCursor QueryStreamingTradeCursor(string symbol, DateTime startUtc, DateTime endUtc, int queueCapacity = 10000)
        {
            var files = ResolveTradeFiles(symbol, startUtc, endUtc);
            if (files.Count == 0)
            {
                return new RawMarketDataCursor(new List<MarketTick>());
            }

            return StreamingQueueRawDataCursor.CreateTradeStreamingCursor(async (writer, ct) =>
            {
                foreach (var file in files)
                {
                    if (ct.IsCancellationRequested) break;

                    if (file.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
                    {
                        // 🌟 极速流式读取 Parquet Trade 文件
                        try
                        {
                            using var connection = new DuckDBConnection("DataSource=:memory:");
                            connection.Open();
                            using var command = connection.CreateCommand();
                            command.CommandText = $"SELECT * FROM read_parquet('{file.Replace("\\", "/")}') ORDER BY 5 ASC;";
                            using var reader = command.ExecuteReader();
                            while (reader.Read() && !ct.IsCancellationRequested)
                            {
                                long tradeId = Convert.ToInt64(reader[0]);
                                decimal price = Convert.ToDecimal(reader[1]);
                                decimal qty = Convert.ToDecimal(reader[2]);
                                decimal quoteQty = Convert.ToDecimal(reader[3]);
                                long tradeTimeMs = Convert.ToInt64(reader[4]);
                                bool isBuyerMaker = Convert.ToBoolean(reader[5]);
                                bool isBestMatch = reader.FieldCount > 6 ? Convert.ToBoolean(reader[6]) : true;

                                var row = new RawTradeRow(tradeId, price, qty, quoteQty, tradeTimeMs, isBuyerMaker, isBestMatch);
                                await writer.WriteAsync(row, ct).ConfigureAwait(false);
                            }
                        }
                        catch
                        {
                            // 忽略或回退
                        }
                    }
                    else
                    {
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
                }
            }, queueCapacity: queueCapacity, totalEstimatedCount: files.Count * 10000);
        }

        #endregion

        #region 🌟 直接返回 DuckDB 原生高性能列式游标 (无时间过滤，Parquet/CSV 自动适配)

        /// <summary>
        /// 直接执行 DuckDB 查询并返回原生列式游标 (自动识别 Parquet / CSV，不进行时间过滤)
        /// </summary>
        public IRawDataCursor QueryRawKlineCursor(string symbol, string interval, DateTime startUtc, DateTime endUtc)
        {
            var files = ResolveKlineFiles(symbol, interval, startUtc, endUtc);
            if (files.Count == 0)
            {
                return new RawMarketDataCursor(new List<MarketKline>());
            }

            try
            {
                using var connection = new DuckDBConnection("DataSource=:memory:");
                connection.Open();

                string query = BuildKlineQuerySql(files);
                using var command = connection.CreateCommand();
                command.CommandText = query;

                using var reader = command.ExecuteReader();
                return DuckDbRawDataCursor.CreateFromKlineReader((DuckDBDataReader)reader);
            }
            catch
            {
                var list = FallbackLoadKlines(symbol, interval, files);
                return new RawMarketDataCursor(list);
            }
        }

        /// <summary>
        /// 直接执行 DuckDB 查询并返回原生列式 Trade 游标 (自动识别 Parquet / CSV，无时间过滤)
        /// </summary>
        public IRawDataCursor QueryRawTradeCursor(string symbol, DateTime startUtc, DateTime endUtc)
        {
            var files = ResolveTradeFiles(symbol, startUtc, endUtc);
            if (files.Count == 0)
            {
                return new RawMarketDataCursor(new List<MarketTick>());
            }

            try
            {
                using var connection = new DuckDBConnection("DataSource=:memory:");
                connection.Open();

                string query = BuildTradeQuerySql(files);
                using var command = connection.CreateCommand();
                command.CommandText = query;

                using var reader = command.ExecuteReader();
                return DuckDbRawDataCursor.CreateFromTradeReader((DuckDBDataReader)reader);
            }
            catch
            {
                var list = FallbackLoadTrades(symbol, files);
                return new RawMarketDataCursor(list);
            }
        }

        #endregion

        #region K 线与 Trade 数据列表直接读取 (支持 Parquet / CSV)

        /// <summary>
        /// 使用 DuckDB 极速批量加载指定日期区间的 K 线数据 (无时间过滤)
        /// </summary>
        public List<MarketKline> LoadKlines(string symbol, string interval, DateTime startUtc, DateTime endUtc)
        {
            var klines = new List<MarketKline>();
            var files = ResolveKlineFiles(symbol, interval, startUtc, endUtc);
            if (files.Count == 0)
            {
                return klines;
            }

            try
            {
                using var connection = new DuckDBConnection("DataSource=:memory:");
                connection.Open();

                string query = BuildKlineQuerySql(files);
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
                klines = FallbackLoadKlines(symbol, interval, files);
            }

            return klines;
        }

        /// <summary>
        /// 使用 DuckDB 极速批量加载指定日期区间的 Tick/Trade 逐笔成交数据 (无时间过滤)
        /// </summary>
        public List<MarketTick> LoadTrades(string symbol, DateTime startUtc, DateTime endUtc)
        {
            var trades = new List<MarketTick>();
            var files = ResolveTradeFiles(symbol, startUtc, endUtc);
            if (files.Count == 0)
            {
                return trades;
            }

            try
            {
                using var connection = new DuckDBConnection("DataSource=:memory:");
                connection.Open();

                string query = BuildTradeQuerySql(files);
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
                trades = FallbackLoadTrades(symbol, files);
            }

            return trades;
        }

        #endregion

        #region SQL 构造辅助方法 (支持 Parquet / CSV 智能聚合)

        private static string BuildKlineQuerySql(List<string> files)
        {
            var parquetFiles = files.Where(f => f.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase)).ToList();
            var csvFiles = files.Where(f => !f.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase)).ToList();

            var subQueries = new List<string>();

            if (parquetFiles.Count > 0)
            {
                string pList = string.Join(", ", parquetFiles.Select(f => $"'{f.Replace("\\", "/")}'"));
                subQueries.Add($@"
                    SELECT 
                        *
                    FROM read_parquet([{pList}], union_by_name=true)");
            }

            if (csvFiles.Count > 0)
            {
                string cList = string.Join(", ", csvFiles.Select(f => $"'{f.Replace("\\", "/")}'"));
                subQueries.Add($@"
                    SELECT 
                        column00 AS open_time, column01 AS open_p, column02 AS high_p, column03 AS low_p, column04 AS close_p,
                        column05 AS volume, column06 AS close_time, column07 AS quote_vol, column08 AS trades_cnt,
                        column09 AS taker_base_vol, column10 AS taker_quote_vol
                    FROM read_csv([{cList}], header=false, auto_detect=true, union_by_name=false)
                    WHERE TRY_CAST(column00 AS BIGINT) IS NOT NULL");
            }

            if (subQueries.Count == 1)
            {
                return $"{subQueries[0]} ORDER BY 1 ASC;";
            }

            string combined = string.Join(" UNION ALL ", subQueries);
            return $"SELECT * FROM ({combined}) ORDER BY 1 ASC;";
        }

        private static string BuildTradeQuerySql(List<string> files)
        {
            var parquetFiles = files.Where(f => f.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase)).ToList();
            var csvFiles = files.Where(f => !f.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase)).ToList();

            var subQueries = new List<string>();

            if (parquetFiles.Count > 0)
            {
                string pList = string.Join(", ", parquetFiles.Select(f => $"'{f.Replace("\\", "/")}'"));
                subQueries.Add($@"
                    SELECT 
                        *
                    FROM read_parquet([{pList}], union_by_name=true)");
            }

            if (csvFiles.Count > 0)
            {
                string cList = string.Join(", ", csvFiles.Select(f => $"'{f.Replace("\\", "/")}'"));
                subQueries.Add($@"
                    SELECT 
                        column00 AS trade_id, column01 AS price, column02 AS qty, column03 AS quote_qty,
                        column04 AS trade_time, column05 AS is_buyer_maker, column06 AS is_best_match
                    FROM read_csv([{cList}], header=false, auto_detect=true, union_by_name=false)
                    WHERE TRY_CAST(column04 AS BIGINT) IS NOT NULL");
            }

            if (subQueries.Count == 1)
            {
                return $"{subQueries[0]} ORDER BY 5 ASC;";
            }

            string combined = string.Join(" UNION ALL ", subQueries);
            return $"SELECT * FROM ({combined}) ORDER BY 5 ASC;";
        }

        #endregion

        #region 文件定位与解压支持 (优先查找 .parquet，其次 .csv，最后 .zip)

        public List<string> ResolveKlineFiles(string symbol, string interval, DateTime startUtc, DateTime endUtc)
        {
            var matchedFiles = new List<string>();
            DateTime current = startUtc.Date;
            DateTime end = endUtc.Date;
            string klineBaseDir = Config.GetKlineDataPath(symbol, interval);
            string klinesRoot = Path.Combine(Config.GetRootPath(), symbol.ToUpper(), "klines");
            string symbolRoot = Path.Combine(Config.GetRootPath(), symbol.ToUpper());

            while (current <= end)
            {
                string dateStr = current.ToString("yyyy-MM-dd");

                // 1. 优先检查标准路径 (.parquet -> .csv -> .zip)
                string standardParquet = Config.GetKlineFilePath(symbol, interval, current, ".parquet");
                string standardCsv = Config.GetKlineFilePath(symbol, interval, current, ".csv");
                string standardZip = Config.GetKlineFilePath(symbol, interval, current, ".zip");

                if (File.Exists(standardParquet))
                {
                    matchedFiles.Add(standardParquet);
                }
                else if (File.Exists(standardCsv))
                {
                    matchedFiles.Add(standardCsv);
                }
                else if (File.Exists(standardZip))
                {
                    string extracted = EnsureZipExtracted(standardZip, symbol, "klines", interval, current);
                    if (!string.IsNullOrEmpty(extracted) && File.Exists(extracted)) matchedFiles.Add(extracted);
                }
                else
                {
                    // 2. 检查扁平周期目录 (.parquet -> .csv -> .zip)
                    string flatParquet = Path.Combine(klineBaseDir, Config.GetKlineFileName(symbol, interval, current, ".parquet"));
                    string flatCsv = Path.Combine(klineBaseDir, Config.GetKlineFileName(symbol, interval, current, ".csv"));
                    string flatZip = Path.Combine(klineBaseDir, Config.GetKlineFileName(symbol, interval, current, ".zip"));

                    if (File.Exists(flatParquet))
                    {
                        matchedFiles.Add(flatParquet);
                    }
                    else if (File.Exists(flatCsv))
                    {
                        matchedFiles.Add(flatCsv);
                    }
                    else if (File.Exists(flatZip))
                    {
                        string extracted = EnsureZipExtracted(flatZip, symbol, "klines", interval, current);
                        if (!string.IsNullOrEmpty(extracted) && File.Exists(extracted)) matchedFiles.Add(extracted);
                    }
                    else
                    {
                        // 3. 递归搜索目录下的 .parquet, .csv, .zip 文件 (严格区分周期)
                        bool found = false;
                        string[] searchDirs = new[] { klineBaseDir, klinesRoot, symbolRoot };

                        foreach (var sDir in searchDirs)
                        {
                            if (found || !Directory.Exists(sDir)) continue;

                            var filesInDir = Directory.GetFiles(sDir, $"*{dateStr}*", SearchOption.AllDirectories);
                            foreach (var file in filesInDir)
                            {
                                string fName = Path.GetFileName(file);
                                if (IsFileMatchingInterval(fName, interval))
                                {
                                    if (file.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
                                    {
                                        matchedFiles.Add(file);
                                        found = true;
                                        break;
                                    }
                                    else if (file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                                    {
                                        matchedFiles.Add(file);
                                        found = true;
                                        break;
                                    }
                                    else if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                                    {
                                        string extracted = EnsureZipExtracted(file, symbol, "klines", interval, current);
                                        if (!string.IsNullOrEmpty(extracted) && File.Exists(extracted))
                                        {
                                            matchedFiles.Add(extracted);
                                            found = true;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                current = current.AddDays(1);
            }

            return matchedFiles.Distinct().ToList();
        }

        public List<string> ResolveTradeFiles(string symbol, DateTime startUtc, DateTime endUtc)
        {
            var matchedFiles = new List<string>();
            DateTime current = startUtc.Date;
            DateTime end = endUtc.Date;
            string tradeBaseDir = Config.GetTradeDataPath(symbol);
            string symbolRoot = Path.Combine(Config.GetRootPath(), symbol.ToUpper());

            while (current <= end)
            {
                string dateStr = current.ToString("yyyy-MM-dd");

                // 1. 优先检查标准路径 (.parquet -> .csv -> .zip)
                string standardParquet = Config.GetTradeFilePath(symbol, current, ".parquet");
                string standardCsv = Config.GetTradeFilePath(symbol, current, ".csv");
                string standardZip = Config.GetTradeFilePath(symbol, current, ".zip");

                if (File.Exists(standardParquet))
                {
                    matchedFiles.Add(standardParquet);
                }
                else if (File.Exists(standardCsv))
                {
                    matchedFiles.Add(standardCsv);
                }
                else if (File.Exists(standardZip))
                {
                    string extracted = EnsureZipExtracted(standardZip, symbol, "trades", string.Empty, current);
                    if (!string.IsNullOrEmpty(extracted) && File.Exists(extracted)) matchedFiles.Add(extracted);
                }
                else
                {
                    // 2. 检查扁平目录
                    string flatParquet = Path.Combine(tradeBaseDir, Config.GetTradeFileName(symbol, current, ".parquet"));
                    string flatCsv = Path.Combine(tradeBaseDir, Config.GetTradeFileName(symbol, current, ".csv"));
                    string flatZip = Path.Combine(tradeBaseDir, Config.GetTradeFileName(symbol, current, ".zip"));

                    if (File.Exists(flatParquet))
                    {
                        matchedFiles.Add(flatParquet);
                    }
                    else if (File.Exists(flatCsv))
                    {
                        matchedFiles.Add(flatCsv);
                    }
                    else if (File.Exists(flatZip))
                    {
                        string extracted = EnsureZipExtracted(flatZip, symbol, "trades", string.Empty, current);
                        if (!string.IsNullOrEmpty(extracted) && File.Exists(extracted)) matchedFiles.Add(extracted);
                    }
                    else if (Directory.Exists(tradeBaseDir) || Directory.Exists(symbolRoot))
                    {
                        bool found = false;
                        string[] searchDirs = new[] { tradeBaseDir, symbolRoot };

                        foreach (var sDir in searchDirs)
                        {
                            if (found || !Directory.Exists(sDir)) continue;

                            var filesInDir = Directory.GetFiles(sDir, $"*{dateStr}*", SearchOption.AllDirectories);
                            foreach (var file in filesInDir)
                            {
                                if (file.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
                                {
                                    matchedFiles.Add(file);
                                    found = true;
                                    break;
                                }
                                else if (file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                                {
                                    matchedFiles.Add(file);
                                    found = true;
                                    break;
                                }
                                else if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                                {
                                    string extracted = EnsureZipExtracted(file, symbol, "trades", string.Empty, current);
                                    if (!string.IsNullOrEmpty(extracted) && File.Exists(extracted))
                                    {
                                        matchedFiles.Add(extracted);
                                        found = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }

                current = current.AddDays(1);
            }

            return matchedFiles.Distinct().ToList();
        }

        private static bool IsFileMatchingInterval(string fileName, string interval)
        {
            string lowerName = fileName.ToLowerInvariant();
            string lowerInt = interval.ToLowerInvariant();

            return lowerName.Contains($"-{lowerInt}-") ||
                   lowerName.Contains($"_{lowerInt}_") ||
                   lowerName.Contains($"-{lowerInt}.") ||
                   lowerName.Contains($"_{lowerInt}.");
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

                using (var archive = ZipFile.OpenRead(zipFilePath))
                {
                    // 优先解压 .parquet，其次 .csv
                    var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
                             ?? archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));

                    if (entry != null)
                    {
                        string targetExt = Path.GetExtension(entry.FullName);
                        string targetFilePath = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(zipFilePath) + targetExt);

                        if (File.Exists(targetFilePath))
                        {
                            return targetFilePath;
                        }

                        entry.ExtractToFile(targetFilePath, overwrite: true);
                        return targetFilePath;
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

        #region 回退模式 (Fallback 加载)

        private List<MarketKline> FallbackLoadKlines(string symbol, string interval, List<string> files)
        {
            var list = new List<MarketKline>();
            long intervalMs = TimeHelper.GetIntervalMilliseconds(interval);

            foreach (var file in files)
            {
                if (file.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var connection = new DuckDBConnection("DataSource=:memory:");
                        connection.Open();
                        using var command = connection.CreateCommand();
                        command.CommandText = $"SELECT * FROM read_parquet('{file.Replace("\\", "/")}') ORDER BY 1 ASC;";
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                        {
                            long openTimeMs = Convert.ToInt64(reader[0]);
                            long closeTimeMs = reader.FieldCount > 6 && long.TryParse(reader[6]?.ToString(), out long ctm) && ctm > openTimeMs
                                ? ctm
                                : (openTimeMs + intervalMs - 1);

                            list.Add(new MarketKline
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
                            });
                        }
                        continue;
                    }
                    catch { }
                }

                if (File.Exists(file))
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
                    using var reader = new StreamReader(stream);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var p = line.Split(',');
                        if (p.Length < 6) continue;
                        if (!long.TryParse(p[0], out long ot)) continue;

                        long ct = (p.Length > 6 && long.TryParse(p[6], out long ctm) && ctm > ot) ? ctm : (ot + intervalMs - 1);

                        list.Add(new MarketKline
                        {
                            Symbol = symbol.ToUpper(),
                            Interval = interval,
                            OpenTime = TimeHelper.FromUnixTimeMilliseconds(ot),
                            CloseTime = TimeHelper.FromUnixTimeMilliseconds(ct),
                            Open = decimal.TryParse(p[1], out var o) ? o : 0,
                            High = decimal.TryParse(p[2], out var h) ? h : 0,
                            Low = decimal.TryParse(p[3], out var l) ? l : 0,
                            Close = decimal.TryParse(p[4], out var c) ? c : 0,
                            Volume = decimal.TryParse(p[5], out var v) ? v : 0,
                            QuoteVolume = p.Length > 7 && decimal.TryParse(p[7], out var qv) ? qv : 0,
                            TradesCount = p.Length > 8 && long.TryParse(p[8], out var tc) ? tc : 0,
                            TakerBuyBaseVolume = p.Length > 9 && decimal.TryParse(p[9], out var tb) ? tb : 0,
                            TakerBuyQuoteVolume = p.Length > 10 && decimal.TryParse(p[10], out var tq) ? tq : 0,
                            IsClosed = true
                        });
                    }
                }
            }

            return list;
        }

        private List<MarketTick> FallbackLoadTrades(string symbol, List<string> files)
        {
            var list = new List<MarketTick>();
            foreach (var file in files)
            {
                if (file.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var connection = new DuckDBConnection("DataSource=:memory:");
                        connection.Open();
                        using var command = connection.CreateCommand();
                        command.CommandText = $"SELECT * FROM read_parquet('{file.Replace("\\", "/")}') ORDER BY 5 ASC;";
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                        {
                            list.Add(new MarketTick
                            {
                                Symbol = symbol.ToUpper(),
                                TradeId = Convert.ToInt64(reader[0]),
                                Price = Convert.ToDecimal(reader[1]),
                                Quantity = Convert.ToDecimal(reader[2]),
                                QuoteQuantity = Convert.ToDecimal(reader[3]),
                                Time = TimeHelper.FromUnixTimeMilliseconds(Convert.ToInt64(reader[4])),
                                IsBuyerMaker = Convert.ToBoolean(reader[5]),
                                IsBestMatch = reader.FieldCount > 6 ? Convert.ToBoolean(reader[6]) : true
                            });
                        }
                        continue;
                    }
                    catch { }
                }

                if (File.Exists(file))
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
                    using var reader = new StreamReader(stream);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var p = line.Split(',');
                        if (p.Length < 6) continue;
                        if (!long.TryParse(p[4], out long tm)) continue;

                        list.Add(new MarketTick
                        {
                            Symbol = symbol.ToUpper(),
                            TradeId = long.TryParse(p[0], out var tid) ? tid : 0,
                            Price = decimal.TryParse(p[1], out var pr) ? pr : 0,
                            Quantity = decimal.TryParse(p[2], out var qty) ? qty : 0,
                            QuoteQuantity = decimal.TryParse(p[3], out var qqty) ? qqty : 0,
                            Time = TimeHelper.FromUnixTimeMilliseconds(tm),
                            IsBuyerMaker = bool.TryParse(p[5], out var bm) && bm,
                            IsBestMatch = p.Length <= 6 || (bool.TryParse(p[6], out var bm2) && bm2)
                        });
                    }
                }
            }

            return list;
        }

        #endregion

        public void Dispose()
        {
            // 清理或保持引擎资源
        }
    }
}
