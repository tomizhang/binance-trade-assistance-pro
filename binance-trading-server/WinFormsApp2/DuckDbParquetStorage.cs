using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DuckDB.NET.Data;

namespace WinFormsApp2
{
    /// <summary>
    /// DuckDB 与 Parquet 本地时间分区存储与极速 SQL 查询引擎 (DuckDB & Parquet Storage Engine)
    /// 严格遵循标准目录结构：
    ///   {RootDir}/{Symbol}/klines/{Interval}/{Year}/{Month}/{Symbol}-{Interval}-{Date}.parquet
    ///   {RootDir}/{Symbol}/trades/{Year}/{Month}/{Symbol}-trades-{Date}.parquet
    /// 支持动态字段自适应映射（兼容标准 Binance Vision Parquet 与自定义 Parquet），以及 ZIP 自动转 Parquet
    /// </summary>
    public static class DuckDbParquetStorage
    {
        #region 分区路径生成与命中检查

        public static string GetKlineParquetPath(string symbol, string interval, DateTime date)
        {
            return Config.GetKlineParquetPath(symbol, interval, date);
        }

        public static string GetKlineZipPath(string symbol, string interval, DateTime date)
        {
            return Config.GetKlineZipPath(symbol, interval, date);
        }

        public static string GetTickParquetPath(string symbol, DateTime date)
        {
            return Config.GetTradeParquetPath(symbol, date);
        }

        public static string GetTickZipPath(string symbol, DateTime date)
        {
            return Config.GetTradeZipPath(symbol, date);
        }

        public static bool HasKlineParquet(string symbol, string interval, DateTime date)
        {
            return File.Exists(GetKlineParquetPath(symbol, interval, date));
        }

        public static bool HasKlineZip(string symbol, string interval, DateTime date)
        {
            return File.Exists(GetKlineZipPath(symbol, interval, date));
        }

        public static bool HasTickParquet(string symbol, DateTime date)
        {
            return File.Exists(GetTickParquetPath(symbol, date));
        }

        public static bool HasTickZip(string symbol, DateTime date)
        {
            return File.Exists(GetTickZipPath(symbol, date));
        }

        #endregion

        #region DuckDB 写入与转存 Parquet

        /// <summary>
        /// 将内存中的 40 字节紧凑型 Tick 数组极速存入时间分区 Parquet 文件中
        /// 存放路径: {RootDir}/{Symbol}/trades/{Year}/{Month}/{Symbol}-trades-{Date}.parquet
        /// </summary>
        public static async Task SaveTicksToParquetAsync(string symbol, DateTime date, Tick[] ticks)
        {
            if (ticks == null || ticks.Length == 0) return;

            string parquetPath = GetTickParquetPath(symbol, date);

            await Task.Run(() =>
            {
                using var connection = new DuckDBConnection("Data Source=:memory:");
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE temp_ticks (
                        trade_time TIMESTAMP,
                        last_price DOUBLE,
                        volume DOUBLE
                    );";
                cmd.ExecuteNonQuery();

                // 使用 DuckDB Appender 批量极速写入
                using (var appender = connection.CreateAppender("temp_ticks"))
                {
                    for (int i = 0; i < ticks.Length; i++)
                    {
                        ref readonly var t = ref ticks[i];
                        var row = appender.CreateRow();
                        row.AppendValue(t.Time);
                        row.AppendValue((double)t.LastPrice);
                        row.AppendValue((double)t.Volume);
                        row.EndRow();
                    }
                }

                // 导出为 Snappy 压缩 Parquet 文件
                string escapedParquetPath = parquetPath.Replace("\\", "/");
                using var copyCmd = connection.CreateCommand();
                copyCmd.CommandText = $"COPY temp_ticks TO '{escapedParquetPath}' (FORMAT PARQUET, COMPRESSION SNAPPY);";
                copyCmd.ExecuteNonQuery();

                connection.Close();
            });
        }

        /// <summary>
        /// 将 K 线数组存入时间分区 Parquet 文件中
        /// 存放路径: {RootDir}/{Symbol}/klines/{Interval}/{Year}/{Month}/{Symbol}-{Interval}-{Date}.parquet
        /// </summary>
        public static async Task SaveKlinesToParquetAsync(string symbol, string interval, DateTime date, Kline[] klines)
        {
            if (klines == null || klines.Length == 0) return;

            string parquetPath = GetKlineParquetPath(symbol, interval, date);

            await Task.Run(() =>
            {
                using var connection = new DuckDBConnection("Data Source=:memory:");
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE temp_klines (
                        open_time TIMESTAMP,
                        open_price DOUBLE,
                        high_price DOUBLE,
                        low_price DOUBLE,
                        close_price DOUBLE,
                        volume DOUBLE,
                        close_time TIMESTAMP
                    );";
                cmd.ExecuteNonQuery();

                using (var appender = connection.CreateAppender("temp_klines"))
                {
                    for (int i = 0; i < klines.Length; i++)
                    {
                        var k = klines[i];
                        var row = appender.CreateRow();
                        row.AppendValue(k.OpenTime);
                        row.AppendValue((double)k.OpenPrice);
                        row.AppendValue((double)k.HighPrice);
                        row.AppendValue((double)k.LowPrice);
                        row.AppendValue((double)k.ClosePrice);
                        row.AppendValue((double)k.Volume);
                        row.AppendValue(k.CloseTime);
                        row.EndRow();
                    }
                }

                string escapedParquetPath = parquetPath.Replace("\\", "/");
                using var copyCmd = connection.CreateCommand();
                copyCmd.CommandText = $"COPY temp_klines TO '{escapedParquetPath}' (FORMAT PARQUET, COMPRESSION SNAPPY);";
                copyCmd.ExecuteNonQuery();

                connection.Close();
            });
        }

        #endregion

        #region DuckDB 极速 SQL 查询 Parquet (支持字段自适应解析)

        /// <summary>
        /// 从时间分区 Parquet 文件中使用 DuckDB SQL 极速读取指定日期的 40 字节紧凑型 Tick 数据
        /// 具备多列名自适应映射 (支持 trade_time/time/timestamp, last_price/price, volume/qty)
        /// </summary>
        public static async Task<Tick[]?> ReadTicksFromParquetAsync(string symbol, DateTime date)
        {
            string parquetPath = GetTickParquetPath(symbol, date);
            if (!File.Exists(parquetPath)) return null;

            return await Task.Run(() =>
            {
                List<Tick> list = new List<Tick>(100000);
                using var connection = new DuckDBConnection("Data Source=:memory:");
                connection.Open();

                string escapedParquetPath = parquetPath.Replace("\\", "/");
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT * FROM read_parquet('{escapedParquetPath}');";

                using var reader = cmd.ExecuteReader();
                int colTime = -1, colPrice = -1, colVol = -1;

                // 动态构建字段索引映射
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string colName = reader.GetName(i).ToLowerInvariant().Replace("_", "");
                    if (colName == "time" || colName == "tradetime" || colName == "timestamp" || colName == "transacttime")
                        colTime = i;
                    else if (colName == "price" || colName == "lastprice")
                        colPrice = i;
                    else if (colName == "volume" || colName == "qty" || colName == "quantity" || colName == "amount" || colName == "vol")
                        colVol = i;
                }

                // 缺省回退
                if (colTime < 0) colTime = 0;
                if (colPrice < 0 && reader.FieldCount > 1) colPrice = 1;
                if (colVol < 0 && reader.FieldCount > 2) colVol = 2;

                while (reader.Read())
                {
                    DateTime time = ReadDateTimeValue(reader, colTime);
                    decimal price = ReadDecimalValue(reader, colPrice);
                    decimal vol = colVol >= 0 ? ReadDecimalValue(reader, colVol) : 0m;

                    list.Add(new Tick(time, price, vol));
                }

                connection.Close();
                return list.OrderBy(t => t.Time).ToArray();
            });
        }

        /// <summary>
        /// 从时间分区 Parquet 文件中使用 DuckDB SQL 极速读取指定日期的 K 线数据
        /// 具备多列名自适应映射 (支持 open_time/opentime, open/open_price, high, low, close, volume, close_time)
        /// </summary>
        public static async Task<Kline[]?> ReadKlinesFromParquetAsync(string symbol, string interval, DateTime date)
        {
            string parquetPath = GetKlineParquetPath(symbol, interval, date);
            if (!File.Exists(parquetPath)) return null;

            return await Task.Run(() =>
            {
                List<Kline> list = new List<Kline>(1500);
                using var connection = new DuckDBConnection("Data Source=:memory:");
                connection.Open();

                string escapedParquetPath = parquetPath.Replace("\\", "/");
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT * FROM read_parquet('{escapedParquetPath}');";

                using var reader = cmd.ExecuteReader();
                int colOpenTime = -1, colOpen = -1, colHigh = -1, colLow = -1, colClose = -1, colVol = -1, colCloseTime = -1;
                int colQuoteVol = -1, colCount = -1, colTakerBase = -1, colTakerQuote = -1;

                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string colName = reader.GetName(i).ToLowerInvariant().Replace("_", "");
                    if (colName == "opentime" || (colName.Contains("open") && colName.Contains("time")))
                        colOpenTime = i;
                    else if (colName == "open" || colName == "openprice")
                        colOpen = i;
                    else if (colName == "high" || colName == "highprice")
                        colHigh = i;
                    else if (colName == "low" || colName == "lowprice")
                        colLow = i;
                    else if (colName == "close" || colName == "closeprice")
                        colClose = i;
                    else if (colName == "volume" || colName == "vol" || colName == "basevolume")
                        colVol = i;
                    else if (colName == "closetime" || (colName.Contains("close") && colName.Contains("time")))
                        colCloseTime = i;
                    else if (colName == "quotevolume" || colName == "quoteassetvolume")
                        colQuoteVol = i;
                    else if (colName == "count" || colName == "trades" || colName == "tradecount")
                        colCount = i;
                    else if (colName == "takerbuybasevolume" || colName == "takerbuyvolume" || colName == "takerbuybaseassetvolume")
                        colTakerBase = i;
                    else if (colName == "takerbuyquotevolume" || colName == "takerbuyquoteassetvolume")
                        colTakerQuote = i;
                }

                // 默认序号回退
                if (colOpenTime < 0) colOpenTime = 0;
                if (colOpen < 0 && reader.FieldCount > 1) colOpen = 1;
                if (colHigh < 0 && reader.FieldCount > 2) colHigh = 2;
                if (colLow < 0 && reader.FieldCount > 3) colLow = 3;
                if (colClose < 0 && reader.FieldCount > 4) colClose = 4;
                if (colVol < 0 && reader.FieldCount > 5) colVol = 5;
                if (colCloseTime < 0 && reader.FieldCount > 6) colCloseTime = 6;

                while (reader.Read())
                {
                    DateTime openTime = ReadDateTimeValue(reader, colOpenTime);
                    decimal openPrice = ReadDecimalValue(reader, colOpen);
                    decimal highPrice = ReadDecimalValue(reader, colHigh);
                    decimal lowPrice = ReadDecimalValue(reader, colLow);
                    decimal closePrice = ReadDecimalValue(reader, colClose);
                    decimal vol = colVol >= 0 ? ReadDecimalValue(reader, colVol) : 0m;
                    DateTime closeTime = colCloseTime >= 0 ? ReadDateTimeValue(reader, colCloseTime) : openTime.AddMinutes(1);

                    decimal quoteVol = colQuoteVol >= 0 ? ReadDecimalValue(reader, colQuoteVol) : 0m;
                    int tradeCount = colCount >= 0 ? ReadIntValue(reader, colCount) : 0;
                    decimal takerBase = colTakerBase >= 0 ? ReadDecimalValue(reader, colTakerBase) : 0m;
                    decimal takerQuote = colTakerQuote >= 0 ? ReadDecimalValue(reader, colTakerQuote) : 0m;

                    list.Add(new Kline
                    {
                        OpenTime = openTime,
                        OpenPrice = openPrice,
                        HighPrice = highPrice,
                        LowPrice = lowPrice,
                        ClosePrice = closePrice,
                        Volume = vol,
                        CloseTime = closeTime,
                        QuoteVolume = quoteVol,
                        TradeCount = tradeCount,
                        TakerBuyBaseVolume = takerBase,
                        TakerBuyQuoteVolume = takerQuote
                    });
                }

                connection.Close();
                return list.OrderBy(k => k.OpenTime).ToArray();
            });
        }

        #endregion

        #region ZIP 压缩包自动解压与 Parquet 转存转换

        /// <summary>
        /// 从本地同目录下的 K 线 ZIP 文件提取 CSV 数据并自动转换为 Parquet 文件入库
        /// </summary>
        public static async Task<Kline[]?> ExtractAndConvertKlineZipAsync(string symbol, string interval, DateTime date)
        {
            string zipPath = GetKlineZipPath(symbol, interval, date);
            if (!File.Exists(zipPath)) return null;

            try
            {
                using var fileStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
                var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
                if (entry == null) return null;

                using var entryStream = entry.Open();
                using var reader = new StreamReader(entryStream, Encoding.UTF8);

                List<Kline> klineList = new List<Kline>();
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("open_time") || line.StartsWith("OpenTime")) continue;

                    if (DataHelper.TryParseKlineFromCsv(line, out Kline kline))
                    {
                        klineList.Add(kline);
                    }
                }

                if (klineList.Count > 0)
                {
                    var sorted = klineList.OrderBy(k => k.OpenTime).ToArray();
                    // 异步存入 Parquet 缓存
                    await SaveKlinesToParquetAsync(symbol, interval, date, sorted).ConfigureAwait(false);
                    return sorted;
                }
            }
            catch
            {
                // 忽略解压异常
            }

            return null;
        }

        /// <summary>
        /// 从本地同目录下的 Trades ZIP 文件提取 CSV 数据并自动转换为 Parquet 文件入库
        /// </summary>
        public static async Task<Tick[]?> ExtractAndConvertTickZipAsync(string symbol, DateTime date)
        {
            string zipPath = GetTickZipPath(symbol, date);
            if (!File.Exists(zipPath)) return null;

            try
            {
                using var fileStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
                var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
                if (entry == null) return null;

                using var entryStream = entry.Open();
                using var reader = new StreamReader(entryStream, Encoding.UTF8);

                List<Tick> tickList = new List<Tick>();
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("id") || line.StartsWith("agg_trade_id") || line.StartsWith("trade")) continue;

                    string[] parts = line.Split(',');
                    if (parts.Length < 5) continue;

                    try
                    {
                        decimal price = decimal.Parse(parts[1], CultureInfo.InvariantCulture);
                        decimal volume = decimal.Parse(parts[2], CultureInfo.InvariantCulture);
                        int timeIdx = parts.Length > 5 && long.TryParse(parts[5], out _) ? 5 : 4;

                        if (long.TryParse(parts[timeIdx], CultureInfo.InvariantCulture, out long timeMs))
                        {
                            DateTime time = DateTimeOffset.FromUnixTimeMilliseconds(timeMs).LocalDateTime;
                            tickList.Add(new Tick(time, price, volume));
                        }
                    }
                    catch { }
                }

                if (tickList.Count > 0)
                {
                    var sorted = tickList.OrderBy(t => t.Time).ToArray();
                    // 异步存入 Parquet 缓存
                    await SaveTicksToParquetAsync(symbol, date, sorted).ConfigureAwait(false);
                    return sorted;
                }
            }
            catch
            {
                // 忽略解压异常
            }

            return null;
        }

        #endregion

        #region 类型安全提取辅助函数

        private static DateTime ReadDateTimeValue(DuckDBDataReader reader, int colIndex)
        {
            if (colIndex < 0 || reader.IsDBNull(colIndex)) return DateTime.MinValue;

            try
            {
                var val = reader.GetValue(colIndex);
                if (val is DateTime dt) return dt;

                if (val is long l)
                {
                    if (l > 1000000000000L) // 毫秒
                        return DateTimeOffset.FromUnixTimeMilliseconds(l).LocalDateTime;
                    if (l > 1000000000L)    // 秒
                        return DateTimeOffset.FromUnixTimeSeconds(l).LocalDateTime;
                    if (l > 1000000000000000L) // 微秒
                        return DateTimeOffset.FromUnixTimeMilliseconds(l / 1000).LocalDateTime;
                }

                if (val is double d)
                {
                    long dl = (long)d;
                    if (dl > 1000000000000L)
                        return DateTimeOffset.FromUnixTimeMilliseconds(dl).LocalDateTime;
                    if (dl > 1000000000L)
                        return DateTimeOffset.FromUnixTimeSeconds(dl).LocalDateTime;
                }

                string str = val.ToString() ?? string.Empty;
                if (DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDt))
                {
                    return parsedDt;
                }
            }
            catch { }

            return DateTime.MinValue;
        }

        private static decimal ReadDecimalValue(DuckDBDataReader reader, int colIndex)
        {
            if (colIndex < 0 || reader.IsDBNull(colIndex)) return 0m;

            try
            {
                var val = reader.GetValue(colIndex);
                if (val is decimal dec) return dec;
                if (val is double d) return (decimal)d;
                if (val is float f) return (decimal)f;
                if (val is long l) return l;
                if (val is int i) return i;

                string str = val.ToString() ?? "0";
                if (decimal.TryParse(str, CultureInfo.InvariantCulture, out var parsedDec))
                {
                    return parsedDec;
                }
            }
            catch { }

            return 0m;
        }

        private static int ReadIntValue(DuckDBDataReader reader, int colIndex)
        {
            if (colIndex < 0 || reader.IsDBNull(colIndex)) return 0;

            try
            {
                var val = reader.GetValue(colIndex);
                if (val is int i) return i;
                if (val is long l) return (int)l;
                if (val is double d) return (int)d;
                if (val is decimal dec) return (int)dec;

                string str = val.ToString() ?? "0";
                if (int.TryParse(str, CultureInfo.InvariantCulture, out var parsedInt))
                {
                    return parsedInt;
                }
            }
            catch { }

            return 0;
        }

        #endregion
    }
}
