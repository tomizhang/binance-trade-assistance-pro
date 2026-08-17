using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DuckDB.NET.Data;

namespace WinFormsApp2
{
    /// <summary>
    /// DuckDB 与 Parquet 本地时间分区存储引擎 (DuckDB & Parquet Storage Engine)
    /// 实现下载数据转换为按时间分区的 Parquet 压缩文件存放，并利用 DuckDB 极速 SQL 进行秒级查询
    /// </summary>
    public static class DuckDbParquetStorage
    {
        private static string BaseDataDir => Config.GetParquetRootPath();

        #region 分区路径生成与命中检查

        public static string GetTickParquetPath(string symbol, DateTime date)
        {
            string dir = Path.Combine(BaseDataDir, "ticks", symbol.ToUpper(), date.ToString("yyyy"), date.ToString("MM"));
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return Path.Combine(dir, $"{symbol.ToUpper()}-ticks-{date:yyyy-MM-dd}.parquet");
        }

        public static string GetKlineParquetPath(string symbol, string interval, DateTime date)
        {
            string dir = Path.Combine(BaseDataDir, "klines", symbol.ToUpper(), interval, date.ToString("yyyy"), date.ToString("MM"));
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return Path.Combine(dir, $"{symbol.ToUpper()}-{interval}-{date:yyyy-MM-dd}.parquet");
        }

        public static bool HasTickParquet(string symbol, DateTime date)
        {
            return File.Exists(GetTickParquetPath(symbol, date));
        }

        public static bool HasKlineParquet(string symbol, string interval, DateTime date)
        {
            return File.Exists(GetKlineParquetPath(symbol, interval, date));
        }

        #endregion

        #region DuckDB 写入与转存 Parquet

        /// <summary>
        /// 将内存中的 40 字节紧凑型 Tick 数组极速存入时间分区 Parquet 文件中
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

        #region DuckDB 极速 SQL 查询 Parquet

        /// <summary>
        /// 从时间分区 Parquet 文件中使用 DuckDB SQL 极速读取指定日期的 40 字节紧凑型 Tick 数据
        /// </summary>
        public static async Task<Tick[]?> ReadTicksFromParquetAsync(string symbol, DateTime date)
        {
            string parquetPath = GetTickParquetPath(symbol, date);
            if (!File.Exists(parquetPath)) return null;

            return await Task.Run(() =>
            {
                List<Tick> list = new List<Tick>();
                using var connection = new DuckDBConnection("Data Source=:memory:");
                connection.Open();

                string escapedParquetPath = parquetPath.Replace("\\", "/");
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT trade_time, last_price, volume FROM read_parquet('{escapedParquetPath}') ORDER BY trade_time ASC;";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    DateTime time = reader.GetDateTime(0);
                    double lastPrice = reader.GetDouble(1);
                    double vol = reader.GetDouble(2);

                    list.Add(new Tick(time, (decimal)lastPrice, (decimal)vol));
                }

                connection.Close();
                return list.ToArray();
            });
        }

        /// <summary>
        /// 从时间分区 Parquet 文件中使用 DuckDB SQL 极速读取指定日期的 K 线数据
        /// </summary>
        public static async Task<Kline[]?> ReadKlinesFromParquetAsync(string symbol, string interval, DateTime date)
        {
            string parquetPath = GetKlineParquetPath(symbol, interval, date);
            if (!File.Exists(parquetPath)) return null;

            return await Task.Run(() =>
            {
                List<Kline> list = new List<Kline>();
                using var connection = new DuckDBConnection("Data Source=:memory:");
                connection.Open();

                string escapedParquetPath = parquetPath.Replace("\\", "/");
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT open_time, open_price, high_price, low_price, close_price, volume, close_time FROM read_parquet('{escapedParquetPath}') ORDER BY open_time ASC;";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    DateTime openTime = reader.GetDateTime(0);
                    double openPrice = reader.GetDouble(1);
                    double highPrice = reader.GetDouble(2);
                    double lowPrice = reader.GetDouble(3);
                    double closePrice = reader.GetDouble(4);
                    double vol = reader.GetDouble(5);
                    DateTime closeTime = reader.GetDateTime(6);

                    list.Add(new Kline
                    {
                        OpenTime = openTime,
                        OpenPrice = (decimal)openPrice,
                        HighPrice = (decimal)highPrice,
                        LowPrice = (decimal)lowPrice,
                        ClosePrice = (decimal)closePrice,
                        Volume = (decimal)vol,
                        CloseTime = closeTime
                    });
                }

                connection.Close();
                return list.ToArray();
            });
        }

        #endregion
    }
}
