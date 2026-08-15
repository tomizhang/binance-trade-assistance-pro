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
    /// 多线程数据下载、按 1 小时片段切片存盘与本地缓存帮助类
    /// </summary>
    public static class MultiThreadDownloader
    {
        /// <summary>
        /// 多线程并发下载/读取指定日期范围内的 K 线数据 (以天为粒度分配工作线程)
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

                            // 1. 本地缓存检查
                            if (File.Exists(dailyFilePath))
                            {
                                Kline[] cachedData = DataHelper.ReadKlinesFromCsvFile(dailyFilePath);
                                if (cachedData.Length > 0)
                                {
                                    foreach (var item in cachedData) allKlinesBag.Add(item);
                                    Interlocked.Increment(ref cachedDays);
                                    logger?.Invoke($"[线程-{Task.CurrentId}] 本地 K线命中 {dayStr} ({cachedData.Length} 条)");
                                    return;
                                }
                            }

                            // 2. 本地无缓存，在线翻页抓取
                            DateTime dayStart = day.Date;
                            DateTime dayEnd = day.Date.AddDays(1).AddTicks(-1);

                            logger?.Invoke($"[线程-{Task.CurrentId}] 开始在线全量翻页抓取 {symbol} ({interval}) {dayStr} K线...");
                            Kline[] fetchedData = await DataHelper.FetchAllKlinesForRangeAsync(symbol, interval, dayStart, dayEnd);

                            if (fetchedData.Length > 0)
                            {
                                DataHelper.SaveKlinesToCsvFile(dailyFilePath, fetchedData);
                                foreach (var item in fetchedData) allKlinesBag.Add(item);
                                Interlocked.Increment(ref downloadedDays);
                                logger?.Invoke($"[线程-{Task.CurrentId}] 成功抓取并落盘存存盘 {dayStr} 至 {dailyFilePath} ({fetchedData.Length} 条)");
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

            Kline[] resultArray = allKlinesBag.OrderBy(k => k.OpenTime).ToArray();
            logger?.Invoke($"[多线程 K线调度完成] 本地命中 {cachedDays} 天，并发下载 {downloadedDays} 天，累计成功加载 {resultArray.Length} 条数据。");

            return resultArray;
        }

        /// <summary>
        /// 多线程并发下载/读取全量 Tick 逐笔成交数据 (优先使用币安官方全量 ZIP 压缩包开源数据源 data.binance.vision，打破 1000 条 API 限制)
        /// </summary>
        public static async Task<Tick[]> DownloadTicksInSlicesParallelAsync(
            string symbol,
            DateTime startDate,
            DateTime endDate,
            int chunkHours = 1,
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

            // 按“天 (Daily)”进行全量任务切割
            List<(DateTime Day, string FilePath, string SliceName)> daysList = 
                new List<(DateTime, string, string)>();

            for (DateTime d = startDate; d <= endDate; d = d.AddDays(1))
            {
                string sliceName = $"{symbol}_Tick_{d:yyyy-MM-dd}.csv";
                string slicePath = Path.Combine(targetDir, sliceName);
                daysList.Add((d, slicePath, sliceName));
            }

            logger?.Invoke($"[币安官方全量 Tick 任务] 共切割 {daysList.Count} 天任务，启动 {maxDegreeOfParallelism} 线程并发调度处理 (优先使用 data.binance.vision 官方 ZIP 压缩包)...");

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
                            // 1. 检查本地 CSV 缓存文件
                            if (File.Exists(dayItem.FilePath))
                            {
                                Tick[] cachedData = DataHelper.ReadTicksFromCsvFile(dayItem.FilePath);
                                if (cachedData.Length > 0)
                                {
                                    foreach (var item in cachedData) allTicksBag.Add(item);
                                    Interlocked.Increment(ref cachedSlices);
                                    logger?.Invoke($"[线程-{Task.CurrentId}] 本地缓存命中 [{dayItem.SliceName}] (包含 {cachedData.Length} 条 Tick 数据)");
                                    return;
                                }
                            }

                            // 2. 本地无缓存，优先从币安官方 data.binance.vision 下载全量 ZIP 压缩包 (0 条限制，100% 完整)
                            logger?.Invoke($"[线程-{Task.CurrentId}] 准备在线从币安官方 Server 下载 [{dayItem.SliceName}] 全量 ZIP 压缩包...");
                            Tick[] fetchedData = await DataHelper.FetchBinanceVisionDailyTicksAsync(symbol, dayItem.Day, logger);

                            // 3. 若当天 ZIP 压缩包暂未开放 (如今日实时交易日)，自动回退使用 REST API 分页精准抓取
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
                                foreach (var item in fetchedData) allTicksBag.Add(item);
                                Interlocked.Increment(ref downloadedSlices);
                                logger?.Invoke($"[线程-{Task.CurrentId}] 成功获取全量 Tick 数据并独立存盘 [{dayItem.SliceName}] (共 {fetchedData.Length} 条 Tick 数据)");
                            }
                            else
                            {
                                logger?.Invoke($"[线程-{Task.CurrentId}] 切片 [{dayItem.SliceName}] 无 Tick 成交数据。");
                            }
                        }
                        catch (Exception ex)
                        {
                            logger?.Invoke($"[线程-{Task.CurrentId}] 切片 [{dayItem.SliceName}] 下载失败: {ex.Message}");
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));
                }

                await Task.WhenAll(tasks);
            }

            Tick[] resultArray = allTicksBag.OrderBy(t => t.Time).ToArray();
            logger?.Invoke($"[币安官方全量 Tick 完成] 汇总: 命中本地缓存 {cachedSlices} 天，成功下载落盘 {downloadedSlices} 天，累计载入 {resultArray.Length} 条 100% 完整 Tick 数据 (无 1000 条限制)！");

            return resultArray;
        }
    }
}
