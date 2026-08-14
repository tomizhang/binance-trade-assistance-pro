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
        /// 多线程并发下载/读取片段化 (按 1 小时切片存盘) 的全量 Tick 逐笔/归集成交数据 (解决文件太大与1000条不完整问题)
        /// </summary>
        /// <param name="symbol">交易对名称</param>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="chunkHours">每个片段的小时数 (默认 1 小时一个文件片段)</param>
        /// <param name="maxDegreeOfParallelism">并发线程数量 (默认 4 线程)</param>
        /// <param name="logger">日志输出委托</param>
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
            endDate = endDate.Date.AddDays(1).AddTicks(-1);

            if (endDate < startDate)
            {
                var temp = startDate;
                startDate = endDate;
                endDate = temp;
            }

            // 按 1 小时切片分割任务
            List<(DateTime SliceStart, DateTime SliceEnd, string FilePath, string SliceName)> slicesList = 
                new List<(DateTime, DateTime, string, string)>();

            DateTime current = startDate;
            while (current < endDate)
            {
                DateTime sliceStart = current;
                DateTime sliceEnd = current.AddHours(chunkHours).AddTicks(-1);
                if (sliceEnd > endDate) sliceEnd = endDate;

                string sliceName = $"{symbol}_Trade_{sliceStart:yyyy-MM-dd_HH}.csv";
                string slicePath = Path.Combine(targetDir, sliceName);

                slicesList.Add((sliceStart, sliceEnd, slicePath, sliceName));
                current = current.AddHours(chunkHours);
            }

            logger?.Invoke($"[片段化多线程 Tick 任务] 切割为 {slicesList.Count} 个 [{chunkHours}小时] 片段，启动 {maxDegreeOfParallelism} 线程并发调度处理...");

            ConcurrentBag<Tick> allTicksBag = new ConcurrentBag<Tick>();
            int cachedSlices = 0;
            int downloadedSlices = 0;

            using (SemaphoreSlim semaphore = new SemaphoreSlim(maxDegreeOfParallelism))
            {
                List<Task> tasks = new List<Task>();

                foreach (var slice in slicesList)
                {
                    await semaphore.WaitAsync();

                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            // 1. 检查本地 1-小时切片 CSV 缓存文件
                            if (File.Exists(slice.FilePath))
                            {
                                Tick[] cachedData = DataHelper.ReadTicksFromCsvFile(slice.FilePath);
                                if (cachedData.Length > 0)
                                {
                                    foreach (var item in cachedData) allTicksBag.Add(item);
                                    Interlocked.Increment(ref cachedSlices);
                                    logger?.Invoke($"[线程-{Task.CurrentId}] 本地切片命中 [{slice.SliceName}] (包含 {cachedData.Length} 条 Tick)");
                                    return;
                                }
                            }

                            // 2. 本地无切片文件，启动无冲突翻页状态机精准抓取该 1-小时切片内的 100% 完整 Tick 数据
                            logger?.Invoke($"[线程-{Task.CurrentId}] 开始精准抓取切片 [{slice.SliceName}] 全量数据 (时间窗口: {slice.SliceStart:HH:mm} ~ {slice.SliceEnd:HH:mm})...");
                            Tick[] fetchedData = await DataHelper.FetchTradeTicksForTimeWindowAsync(symbol, slice.SliceStart, slice.SliceEnd);

                            if (fetchedData.Length > 0)
                            {
                                DataHelper.SaveTicksToCsvFile(slice.FilePath, fetchedData);
                                foreach (var item in fetchedData) allTicksBag.Add(item);
                                Interlocked.Increment(ref downloadedSlices);
                                logger?.Invoke($"[线程-{Task.CurrentId}] 成功抓取全量切片并独立存盘 [{slice.SliceName}] (共 {fetchedData.Length} 条 Tick)");
                            }
                            else
                            {
                                logger?.Invoke($"[线程-{Task.CurrentId}] 切片 [{slice.SliceName}] 无 Tick 成交数据。");
                            }
                        }
                        catch (Exception ex)
                        {
                            logger?.Invoke($"[线程-{Task.CurrentId}] 切片 [{slice.SliceName}] 下载失败: {ex.Message}");
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
            logger?.Invoke($"[片段化多线程 Tick 完成] 汇总: 命中本地切片 {cachedSlices} 个，并发抓取落盘切片 {downloadedSlices} 个，累计读取 {resultArray.Length} 条 100% 完整 Tick 数据。");

            return resultArray;
        }
    }
}
