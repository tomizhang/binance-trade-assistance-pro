using Binance.Net.Enums;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WinFormsApp2
{
    /// <summary>
    /// 多线程数据下载、DuckDB & Parquet 本地时间分区转存与极速 SQL 读取帮助类
    /// </summary>
    public static class MultiThreadDownloader
    {
        /// <summary>
        /// 多线程并发下载/读取指定日期范围内的 K 线数据 (以天为粒度分配工作线程，优先使用 DuckDB Parquet 时间分区)
        /// </summary>
        public static async Task<Kline[]> DownloadKlinesParallelAsync(
            string symbol,
            KlineInterval interval,
            DateTime startDate,
            DateTime endDate,
            int maxDegreeOfParallelism = 4,
            Action<string>? logger = null)
        {
            string targetDir = Config.GetDataPath(symbol, interval);
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            startDate = startDate.Date;
            endDate = endDate.Date;

            if (endDate < startDate)
            {
                var temp = startDate;
                startDate = endDate;
                endDate = temp;
            }

            List<DateTime> daysList = new List<DateTime>();
            for (DateTime d = startDate; d <= endDate; d = d.AddDays(1))
            {
                daysList.Add(d);
            }

            logger?.Invoke($"[多线程 K线任务] 启动 {maxDegreeOfParallelism} 线程并发调度，共需处理 {daysList.Count} 天数据...");

            ConcurrentBag<Kline> allKlinesBag = new ConcurrentBag<Kline>();
            int cachedDays = 0;
            int downloadedDays = 0;

            using (SemaphoreSlim semaphore = new SemaphoreSlim(maxDegreeOfParallelism))
            {
                List<Task> tasks = new List<Task>();

                foreach (var day in daysList)
                {
                    await semaphore.WaitAsync();

                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            string dayStr = day.ToString("yyyy-MM-dd");
                            string dailyFilePath = Path.Combine(targetDir, $"{symbol}_{interval}_{dayStr}.csv");
                            string intervalStr = interval.ToString();

                            // 1. 优先检查 DuckDB Parquet 时间分区存盘 (0.1ms 极速 SQL 读取)
                            if (DuckDbParquetStorage.HasKlineParquet(symbol, intervalStr, day))
                            {
                                Kline[]? parquetKlines = await DuckDbParquetStorage.ReadKlinesFromParquetAsync(symbol, intervalStr, day);
                                if (parquetKlines != null && parquetKlines.Length > 0)
                                {
                                    foreach (var item in parquetKlines) allKlinesBag.Add(item);
                                    Interlocked.Increment(ref cachedDays);
                                    logger?.Invoke($"[线程-{Task.CurrentId}] DuckDB Parquet K线分区命中 [{dayStr}] ({parquetKlines.Length} 帧)");
                                    return;
                                }
                            }

                            // 2. 本地 CSV 缓存检查，命中后自动升级转存为 Parquet
                            if (File.Exists(dailyFilePath))
                            {
                                Kline[] cachedData = DataHelper.ReadKlinesFromCsvFile(dailyFilePath);
                                if (cachedData.Length > 0)
                                {
                                    foreach (var item in cachedData) allKlinesBag.Add(item);
                                    Interlocked.Increment(ref cachedDays);
                                    logger?.Invoke($"[线程-{Task.CurrentId}] 本地 CSV K线命中 [{dayStr}] ({cachedData.Length} 帧，自动转存 Parquet)...");
                                    _ = DuckDbParquetStorage.SaveKlinesToParquetAsync(symbol, intervalStr, day, cachedData);
                                    return;
                                }
                            }

                            // 3. 本地无缓存，在线全量翻页抓取
                            DateTime dayStart = day.Date;
                            DateTime dayEnd = day.Date.AddDays(1).AddTicks(-1);

                            logger?.Invoke($"[线程-{Task.CurrentId}] 开始在线全量翻页抓取 {symbol} ({interval}) {dayStr} K线...");
                            Kline[] fetchedData = await DataHelper.FetchAllKlinesForRangeAsync(symbol, interval, dayStart, dayEnd);

                            if (fetchedData.Length > 0)
                            {
                                DataHelper.SaveKlinesToCsvFile(dailyFilePath, fetchedData);
                                await DuckDbParquetStorage.SaveKlinesToParquetAsync(symbol, intervalStr, day, fetchedData);

                                foreach (var item in fetchedData) allKlinesBag.Add(item);
                                Interlocked.Increment(ref downloadedDays);
                                logger?.Invoke($"[线程-{Task.CurrentId}] 成功抓取全量 K线并保存为 Parquet 格式 [{dayStr}] ({fetchedData.Length} 帧)");
                            }
                            else
                            {
                                logger?.Invoke($"[线程-{Task.CurrentId}] {dayStr} 无 K线数据。");
                            }
                        }
                        catch (Exception ex)
                        {
                            logger?.Invoke($"[线程-{Task.CurrentId}] {day:yyyy-MM-dd} K线下载失败: {ex.Message}");
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));
                }

                await Task.WhenAll(tasks);
            }

            Kline[] resultArray = allKlinesBag.ToArray();
            Array.Sort(resultArray, (a, b) => a.OpenTime.CompareTo(b.OpenTime));
            logger?.Invoke($"[多线程 K线调度完成] 本地/DuckDB 命中 {cachedDays} 天，并发下载 {downloadedDays} 天，累计成功加载 {resultArray.Length} 条数据 (严格时间升序重排)。");

            return resultArray;
        }

        /// <summary>
        /// 多线程并发下载/读取全量 Tick 逐笔成交数据 (优先使用 DuckDB Parquet 时间分区与币安官方全量 ZIP 压缩包)
        /// </summary>
        public static async Task<Tick[]> DownloadTicksInSlicesParallelAsync(
            string symbol,
            DateTime startDate,
            DateTime endDate,
            int maxDegreeOfParallelism = 4,
            Action<string>? logger = null)
        {
            string targetDir = Config.GetTradeDataPath(symbol);
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            startDate = startDate.Date;
            endDate = endDate.Date;

            if (endDate < startDate)
            {
                var temp = startDate;
                startDate = endDate;
                endDate = temp;
            }

            List<(DateTime Day, string FilePath, string SliceName)> daysList = 
                new List<(DateTime, string, string)>();

            for (DateTime d = startDate; d <= endDate; d = d.AddDays(1))
            {
                string sliceName = $"{symbol}_Tick_{d:yyyy-MM-dd}.csv";
                string slicePath = Path.Combine(targetDir, sliceName);
                daysList.Add((d, slicePath, sliceName));
            }

            logger?.Invoke($"[币安官方全量 Tick 任务] 共切割 {daysList.Count} 天任务，启动 {maxDegreeOfParallelism} 线程并发调度 (优先 DuckDB Parquet 极速读取)...");

            ConcurrentBag<Tick> allTicksBag = new ConcurrentBag<Tick>();
            int cachedSlices = 0;
            int downloadedSlices = 0;

            using (SemaphoreSlim semaphore = new SemaphoreSlim(maxDegreeOfParallelism))
            {
                List<Task> tasks = new List<Task>();

                foreach (var dayItem in daysList)
                {
                    await semaphore.WaitAsync();

                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            // 1. 优先检查 DuckDB Parquet 时间分区存盘 (0.1ms 极速 SQL 读取，高密度 Snappy 压缩)
                            if (DuckDbParquetStorage.HasTickParquet(symbol, dayItem.Day))
                            {
                                Tick[]? parquetTicks = await DuckDbParquetStorage.ReadTicksFromParquetAsync(symbol, dayItem.Day);
                                if (parquetTicks != null && parquetTicks.Length > 0)
                                {
                                    foreach (var item in parquetTicks) allTicksBag.Add(item);
                                    Interlocked.Increment(ref cachedSlices);
                                    logger?.Invoke($"[线程-{Task.CurrentId}] DuckDB Parquet Tick 分区命中 [{dayItem.Day:yyyy-MM-dd}] ({parquetTicks.Length} 条 Tick)");
                                    return;
                                }
                            }

                            // 2. 次选检查本地 CSV 缓存文件，若存在则读取并自动转存为 Parquet 时间分区格式
                            if (File.Exists(dayItem.FilePath))
                            {
                                Tick[] cachedData = DataHelper.ReadTicksFromCsvFile(dayItem.FilePath);
                                if (cachedData.Length > 0)
                                {
                                    foreach (var item in cachedData) allTicksBag.Add(item);
                                    Interlocked.Increment(ref cachedSlices);
                                    logger?.Invoke($"[线程-{Task.CurrentId}] 本地 CSV 缓存命中 [{dayItem.SliceName}] ({cachedData.Length} 条 Tick，自动转存 Parquet)...");
                                    _ = DuckDbParquetStorage.SaveTicksToParquetAsync(symbol, dayItem.Day, cachedData);
                                    return;
                                }
                            }

                            // 3. 本地无缓存，优先从币安官方 data.binance.vision 下载全量 ZIP 压缩包 (0 条限制，100% 完整)
                            logger?.Invoke($"[线程-{Task.CurrentId}] 准备在线从币安官方 Server 下载 [{dayItem.SliceName}] 全量 ZIP 压缩包...");
                            Tick[] fetchedData = await DataHelper.FetchBinanceVisionDailyTicksAsync(symbol, dayItem.Day, logger);

                            // 4. 若当天 ZIP 压缩包暂未开放 (如今日实时交易日)，自动回退使用 REST API 分页抓取
                            if (fetchedData.Length == 0)
                            {
                                logger?.Invoke($"[线程-{Task.CurrentId}] 官方 ZIP 暂未准备完毕，自动回退使用 REST API 翻页抓取当天 [{dayItem.SliceName}] Tick 数据...");
                                DateTime dayStart = dayItem.Day.Date;
                                DateTime dayEnd = dayStart.AddDays(1).AddTicks(-1);
                                fetchedData = await DataHelper.FetchTradeTicksForTimeWindowAsync(symbol, dayStart, dayEnd);
                            }

                            if (fetchedData.Length > 0)
                            {
                                DataHelper.SaveTicksToCsvFile(dayItem.FilePath, fetchedData);
                                await DuckDbParquetStorage.SaveTicksToParquetAsync(symbol, dayItem.Day, fetchedData);

                                foreach (var item in fetchedData) allTicksBag.Add(item);
                                Interlocked.Increment(ref downloadedSlices);
                                logger?.Invoke($"[线程-{Task.CurrentId}] 成功获取全量 Tick 并保存为 DuckDB Parquet 格式 [{dayItem.Day:yyyy-MM-dd}] ({fetchedData.Length} 条 Tick)");
                            }
                            else
                            {
                                logger?.Invoke($"[线程-{Task.CurrentId}] 切片 [{dayItem.SliceName}] 无 Tick 成交数据。");
                            }
                        }
                        catch (Exception ex)
                        {
                            logger?.Invoke($"[线程-{Task.CurrentId}] 切片 [{dayItem.SliceName}] 处理失败: {ex.Message}");
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));
                }

                await Task.WhenAll(tasks);
            }

            Tick[] resultArray = allTicksBag.ToArray();
            Array.Sort(resultArray, (a, b) => a.Time.CompareTo(b.Time));
            logger?.Invoke($"[多线程 Tick 调度完成] DuckDB/本地 命中 {cachedSlices} 天，并发抓取 {downloadedSlices} 天，累计成功加载 {resultArray.Length} 条 Tick 数据 (严格时间升序重排)。");

            return resultArray;
        }
    }
}
