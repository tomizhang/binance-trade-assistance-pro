using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WinFormsApp2
{
    /// <summary>
    /// K线与Tick数据读取、完整下载、转换与本地缓存辅助类
    /// </summary>
    public static class DataHelper
    {
        #region 模型转换 (Model Conversion)

        /// <summary>
        /// 将 Binance.Net 的 IBinanceKline 转换为 struct Kline
        /// </summary>
        public static Kline ToKline(IBinanceKline kline)
        {
            return new Kline
            {
                OpenTime = kline.OpenTime,
                OpenPrice = kline.OpenPrice,
                HighPrice = kline.HighPrice,
                LowPrice = kline.LowPrice,
                ClosePrice = kline.ClosePrice,
                Volume = kline.Volume,
                CloseTime = kline.CloseTime,
                QuoteVolume = kline.QuoteVolume,
                TradeCount = kline.TradeCount,
                TakerBuyBaseVolume = kline.TakerBuyBaseVolume,
                TakerBuyQuoteVolume = kline.TakerBuyQuoteVolume
            };
        }

        /// <summary>
        /// 将 Binance.Net 的 24H 行情对象转换为 struct Tick
        /// </summary>
        public static Tick ToTick(IBinance24HPrice price, SymbolType? symbolType = null)
        {
            return new Tick
            {
                Symbol = price.Symbol,
                Time = DateTime.Now,
                LastPrice = price.LastPrice,
                OpenPrice = price.OpenPrice,
                HighPrice = price.HighPrice,
                LowPrice = price.LowPrice,
                Volume = price.Volume,
                QuoteVolume = price.QuoteVolume,
                SymbolType = symbolType
            };
        }

        /// <summary>
        /// 将 Binance.Net 的逐笔成交对象 (Recent Trade) 转换为 struct Tick
        /// </summary>
        public static Tick ToTick(IBinanceRecentTrade trade, string symbol, SymbolType? symbolType = null)
        {
            return new Tick
            {
                Symbol = symbol,
                Time = trade.TradeTime,
                LastPrice = trade.Price,
                OpenPrice = trade.Price,
                HighPrice = trade.Price,
                LowPrice = trade.Price,
                Volume = trade.BaseQuantity,
                QuoteVolume = trade.QuoteQuantity,
                SymbolType = symbolType
            };
        }

        /// <summary>
        /// 将 Binance.Net 的归集成交对象 (Aggregated Trade) 转换为 struct Tick
        /// </summary>
        public static Tick ToTick(IBinanceAggregatedTrade trade, string symbol, SymbolType? symbolType = null)
        {
            return new Tick
            {
                Symbol = symbol,
                Time = trade.TradeTime,
                LastPrice = trade.Price,
                OpenPrice = trade.Price,
                HighPrice = trade.Price,
                LowPrice = trade.Price,
                Volume = trade.Quantity,
                QuoteVolume = trade.Price * trade.Quantity,
                SymbolType = symbolType
            };
        }

        #endregion

        #region CSV 格式解析与写入 (CSV Parse & Write)

        /// <summary>
        /// 从 CSV 单行解析 Kline 结构体
        /// 格式支持: open_time, open, high, low, close, volume, close_time, quote_volume, count, taker_buy_base, taker_buy_quote
        /// 时间字段支持毫秒时间戳或 DateTime 字符串
        /// </summary>
        public static bool TryParseKlineFromCsv(string csvLine, out Kline kline)
        {
            kline = default;
            if (string.IsNullOrWhiteSpace(csvLine)) return false;

            string[] parts = csvLine.Split(',');
            if (parts.Length < 6) return false;

            try
            {
                kline = new Kline
                {
                    OpenTime = ParseTime(parts[0]),
                    OpenPrice = decimal.Parse(parts[1], CultureInfo.InvariantCulture),
                    HighPrice = decimal.Parse(parts[2], CultureInfo.InvariantCulture),
                    LowPrice = decimal.Parse(parts[3], CultureInfo.InvariantCulture),
                    ClosePrice = decimal.Parse(parts[4], CultureInfo.InvariantCulture),
                    Volume = decimal.Parse(parts[5], CultureInfo.InvariantCulture),
                    CloseTime = parts.Length > 6 ? ParseTime(parts[6]) : DateTime.MinValue,
                    QuoteVolume = parts.Length > 7 ? decimal.Parse(parts[7], CultureInfo.InvariantCulture) : 0m,
                    TradeCount = parts.Length > 8 ? int.Parse(parts[8], CultureInfo.InvariantCulture) : 0,
                    TakerBuyBaseVolume = parts.Length > 9 ? decimal.Parse(parts[9], CultureInfo.InvariantCulture) : 0m,
                    TakerBuyQuoteVolume = parts.Length > 10 ? decimal.Parse(parts[10], CultureInfo.InvariantCulture) : 0m
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 从 CSV 单行解析 Tick 结构体
        /// 支持格式: symbol, time, lastPrice, openPrice, highPrice, lowPrice, volume, quoteVolume
        /// </summary>
        public static bool TryParseTickFromCsv(string csvLine, out Tick tick)
        {
            tick = default;
            if (string.IsNullOrWhiteSpace(csvLine)) return false;

            string[] parts = csvLine.Split(',');
            if (parts.Length < 2) return false;

            try
            {
                // 判断第2列是时间还是价格
                if (DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedTime) ||
                    long.TryParse(parts[1], out _))
                {
                    tick = new Tick
                    {
                        Symbol = parts[0].Trim(),
                        Time = ParseTime(parts[1]),
                        LastPrice = decimal.Parse(parts[2], CultureInfo.InvariantCulture),
                        OpenPrice = parts.Length > 3 ? decimal.Parse(parts[3], CultureInfo.InvariantCulture) : decimal.Parse(parts[2], CultureInfo.InvariantCulture),
                        HighPrice = parts.Length > 4 ? decimal.Parse(parts[4], CultureInfo.InvariantCulture) : decimal.Parse(parts[2], CultureInfo.InvariantCulture),
                        LowPrice = parts.Length > 5 ? decimal.Parse(parts[5], CultureInfo.InvariantCulture) : decimal.Parse(parts[2], CultureInfo.InvariantCulture),
                        Volume = parts.Length > 6 ? decimal.Parse(parts[6], CultureInfo.InvariantCulture) : 0m,
                        QuoteVolume = parts.Length > 7 ? decimal.Parse(parts[7], CultureInfo.InvariantCulture) : 0m
                    };
                }
                else
                {
                    tick = new Tick
                    {
                        Symbol = parts[0].Trim(),
                        Time = DateTime.Now,
                        LastPrice = decimal.Parse(parts[1], CultureInfo.InvariantCulture),
                        OpenPrice = parts.Length > 2 ? decimal.Parse(parts[2], CultureInfo.InvariantCulture) : 0m,
                        HighPrice = parts.Length > 3 ? decimal.Parse(parts[3], CultureInfo.InvariantCulture) : 0m,
                        LowPrice = parts.Length > 4 ? decimal.Parse(parts[4], CultureInfo.InvariantCulture) : 0m,
                        Volume = parts.Length > 5 ? decimal.Parse(parts[5], CultureInfo.InvariantCulture) : 0m,
                        QuoteVolume = parts.Length > 6 ? decimal.Parse(parts[6], CultureInfo.InvariantCulture) : 0m
                    };
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 将 Kline 集合导出并保存为 CSV 文件
        /// </summary>
        public static void SaveKlinesToCsvFile(string filePath, IEnumerable<Kline> klines)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var writer = new StreamWriter(filePath, false, System.Text.Encoding.UTF8);
            foreach (var k in klines)
            {
                string line = string.Format(CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6:yyyy-MM-dd HH:mm:ss},{7},{8},{9},{10}",
                    k.OpenTime, k.OpenPrice, k.HighPrice, k.LowPrice, k.ClosePrice, k.Volume,
                    k.CloseTime, k.QuoteVolume, k.TradeCount, k.TakerBuyBaseVolume, k.TakerBuyQuoteVolume);
                writer.WriteLine(line);
            }
        }

        /// <summary>
        /// 将 Tick 逐笔成交数据导出并保存为 CSV 文件
        /// </summary>
        public static void SaveTicksToCsvFile(string filePath, IEnumerable<Tick> ticks)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var writer = new StreamWriter(filePath, false, System.Text.Encoding.UTF8);
            foreach (var t in ticks)
            {
                string line = string.Format(CultureInfo.InvariantCulture,
                    "{0},{1:yyyy-MM-dd HH:mm:ss.fff},{2},{3},{4},{5},{6},{7}",
                    t.Symbol, t.Time, t.LastPrice, t.OpenPrice, t.HighPrice, t.LowPrice, t.Volume, t.QuoteVolume);
                writer.WriteLine(line);
            }
        }

        private static DateTime ParseTime(string timeStr)
        {
            timeStr = timeStr.Trim();
            if (long.TryParse(timeStr, out long timestamp))
            {
                if (timestamp > 1000000000000L)
                    return DateTimeOffset.FromUnixTimeMilliseconds(timestamp).LocalDateTime;
                else if (timestamp > 1000000000L)
                    return DateTimeOffset.FromUnixTimeSeconds(timestamp).LocalDateTime;
            }
            return DateTime.Parse(timeStr, CultureInfo.InvariantCulture);
        }

        #endregion

        #region 本地文件读取 (File Data Reading)

        /// <summary>
        /// 读取单个 CSV 文件并转换为 Kline 数组
        /// </summary>
        public static Kline[] ReadKlinesFromCsvFile(string filePath)
        {
            if (!File.Exists(filePath)) return Array.Empty<Kline>();

            List<Kline> list = new List<Kline>();
            foreach (var line in File.ReadLines(filePath))
            {
                if (TryParseKlineFromCsv(line, out Kline kline))
                {
                    list.Add(kline);
                }
            }
            return list.ToArray();
        }

        /// <summary>
        /// 读取单个 CSV 文件并转换为 Tick 数组
        /// </summary>
        public static Tick[] ReadTicksFromCsvFile(string filePath)
        {
            if (!File.Exists(filePath)) return Array.Empty<Tick>();

            List<Tick> list = new List<Tick>();
            foreach (var line in File.ReadLines(filePath))
            {
                if (TryParseTickFromCsv(line, out Tick tick))
                {
                    list.Add(tick);
                }
            }
            return list.ToArray();
        }

        /// <summary>
        /// 根据 Config 配置路径，按币种和时间周期读取对应目录下的所有 K线 CSV 数据
        /// </summary>
        public static Kline[] LoadKlinesForCoin(string coin, KlineInterval interval)
        {
            string dirPath = Config.GetDataPath(coin, interval);
            if (!Directory.Exists(dirPath)) return Array.Empty<Kline>();

            List<Kline> allKlines = new List<Kline>();
            foreach (var file in Directory.GetFiles(dirPath, "*.csv"))
            {
                allKlines.AddRange(ReadKlinesFromCsvFile(file));
            }
            return allKlines.OrderBy(k => k.OpenTime).ToArray();
        }

        /// <summary>
        /// 根据 Config 配置路径，按币种读取交易/Tick 数据目录下的所有 CSV 数据
        /// </summary>
        public static Tick[] LoadTradeTicksForCoin(string coin)
        {
            string dirPath = Config.GetTradeDataPath(coin);
            if (!Directory.Exists(dirPath)) return Array.Empty<Tick>();

            List<Tick> allTicks = new List<Tick>();
            foreach (var file in Directory.GetFiles(dirPath, "*.csv"))
            {
                allTicks.AddRange(ReadTicksFromCsvFile(file));
            }
            return allTicks.OrderBy(t => t.Time).ToArray();
        }

        #endregion

        #region 币安在线 API 读取 (Binance REST API Reading)

        /// <summary>
        /// 通过 Binance API 在线获取 K 线数据并转换为 Kline 数组
        /// </summary>
        public static async Task<Kline[]> FetchKlinesFromApiAsync(
            string symbol, 
            KlineInterval interval, 
            DateTime? startTime = null, 
            DateTime? endTime = null, 
            int limit = 1000)
        {
            using var client = new BinanceRestClient();
            var result = await client.SpotApi.ExchangeData.GetKlinesAsync(symbol, interval, startTime, endTime, limit);
            if (!result.Success || result.Data == null)
            {
                throw new Exception($"获取 K 线数据失败: {result.Error?.Message}");
            }

            return result.Data.Select(ToKline).ToArray();
        }

        /// <summary>
        /// 通过 Binance API 在线获取近期完整成交 Tick 列表 (Recent Trades)
        /// </summary>
        public static async Task<Tick[]> FetchRecentTradeTicksFromApiAsync(string symbol, int limit = 1000)
        {
            using var client = new BinanceRestClient();
            var result = await client.SpotApi.ExchangeData.GetRecentTradesAsync(symbol, limit: limit);
            if (!result.Success || result.Data == null)
            {
                throw new Exception($"获取 Tick 近期成交数据失败: {result.Error?.Message}");
            }

            return result.Data.Select(trade => ToTick(trade, symbol)).ToArray();
        }

        /// <summary>
        /// 通过 Binance API 在线获取指定时间范围的归集成交 Tick 列表 (Aggregated Trades)
        /// </summary>
        public static async Task<Tick[]> FetchAggregatedTradeTicksFromApiAsync(
            string symbol, 
            DateTime? startTime = null, 
            DateTime? endTime = null, 
            int limit = 1000)
        {
            using var client = new BinanceRestClient();
            var result = await client.SpotApi.ExchangeData.GetAggregatedTradeHistoryAsync(symbol, startTime: startTime, endTime: endTime, limit: limit);
            if (!result.Success || result.Data == null)
            {
                throw new Exception($"获取 Tick 历史成交数据失败: {result.Error?.Message}");
            }

            return result.Data.Select(trade => ToTick(trade, symbol)).ToArray();
        }

        /// <summary>
        /// 翻页抓取指定时间范围内的全量 K 线数据 (自动处理 1000 条限制)
        /// </summary>
        public static async Task<Kline[]> FetchAllKlinesForRangeAsync(
            string symbol,
            KlineInterval interval,
            DateTime startTime,
            DateTime endTime,
            int maxChunks = 50)
        {
            using var client = new BinanceRestClient();
            List<Kline> allKlines = new List<Kline>();
            DateTime currentStart = startTime;

            for (int chunkCount = 0; chunkCount < maxChunks; chunkCount++)
            {
                var result = await client.SpotApi.ExchangeData.GetKlinesAsync(symbol, interval, currentStart, endTime, limit: 1000);
                if (!result.Success || result.Data == null || !result.Data.Any())
                {
                    break;
                }

                var list = result.Data.ToList();
                foreach (var item in list)
                {
                    if (item.OpenTime >= startTime && item.OpenTime <= endTime)
                    {
                        allKlines.Add(ToKline(item));
                    }
                }

                DateTime maxTime = list.Max(k => k.OpenTime);
                if (maxTime <= currentStart || maxTime >= endTime)
                {
                    break;
                }

                currentStart = maxTime.AddSeconds(1);

                if (list.Count < 1000)
                {
                    break;
                }
            }

            return allKlines.OrderBy(k => k.OpenTime).ToArray();
        }

        /// <summary>
        /// 精准抓取指定时间窗口 (如 1 小时切片) 内的 100% 完整 Tick 逐笔/归集成交数据。
        /// 采用无冲突状态机：第 1 次使用 startTime/endTime 定位起点，后续仅使用 fromId = maxId + 1 持续滚动翻页，直到超过窗口结束时间。
        /// </summary>
        public static async Task<Tick[]> FetchTradeTicksForTimeWindowAsync(
            string symbol,
            DateTime windowStart,
            DateTime windowEnd,
            int maxPages = 500)
        {
            using var client = new BinanceRestClient();
            List<Tick> windowTicks = new List<Tick>();

            // 阶段 1：使用 startTime / endTime 获取当前时间窗口的第一批 1000 条成交
            var initialResult = await client.SpotApi.ExchangeData.GetAggregatedTradeHistoryAsync(
                symbol,
                startTime: windowStart,
                endTime: windowEnd,
                limit: 1000);

            if (!initialResult.Success || initialResult.Data == null || !initialResult.Data.Any())
            {
                return Array.Empty<Tick>();
            }

            var initialList = initialResult.Data.ToList();
            foreach (var trade in initialList)
            {
                if (trade.TradeTime >= windowStart && trade.TradeTime <= windowEnd)
                {
                    windowTicks.Add(ToTick(trade, symbol));
                }
            }

            // 若第一批不满 1000 条，说明该时间窗口内的数据已全部获取完毕
            if (initialList.Count < 1000)
            {
                return windowTicks.OrderBy(t => t.Time).ToArray();
            }

            long currentFromId = initialList.Max(t => t.Id) + 1;

            // 阶段 2：仅使用 fromId = maxId + 1 连续向后无缝滚动翻页
            for (int page = 0; page < maxPages; page++)
            {
                var pageResult = await client.SpotApi.ExchangeData.GetAggregatedTradeHistoryAsync(
                    symbol,
                    fromId: currentFromId,
                    limit: 1000);

                if (!pageResult.Success || pageResult.Data == null || !pageResult.Data.Any())
                {
                    break;
                }

                var pageList = pageResult.Data.ToList();
                bool reachedWindowEnd = false;

                foreach (var trade in pageList)
                {
                    if (trade.TradeTime > windowEnd)
                    {
                        reachedWindowEnd = true;
                        break;
                    }
                    if (trade.TradeTime >= windowStart)
                    {
                        windowTicks.Add(ToTick(trade, symbol));
                    }
                }

                if (reachedWindowEnd)
                {
                    break;
                }

                long maxIdInPage = pageList.Max(t => t.Id);
                if (maxIdInPage < currentFromId)
                {
                    break;
                }

                currentFromId = maxIdInPage + 1;

                if (pageList.Count < 1000)
                {
                    break;
                }
            }

            return windowTicks.OrderBy(t => t.Time).ToArray();
        }

        #endregion

        #region 本地缓存优先与自动下载 (Local Cache First & Auto Download)

        /// <summary>
        /// 优先从 Config 指定的本地缓存目录读取最新 Tick 列表；若无文件，则从在线 API 抓取完整 Recent Trades 并存盘
        /// </summary>
        public static async Task<Tick[]> GetOrFetchTradeTicksAsync(string symbol, int limit = 1000, Action<string>? logger = null)
        {
            string targetDir = Config.GetTradeDataPath(symbol);

            // 1. 检查本地目录是否存在 CSV 缓存文件
            if (Directory.Exists(targetDir) && Directory.GetFiles(targetDir, "*.csv").Length > 0)
            {
                logger?.Invoke($"[缓存命中] 从本地目录 [{targetDir}] 读取 Tick 数据...");
                Tick[] localData = LoadTradeTicksForCoin(symbol);
                if (localData.Length > 0)
                {
                    logger?.Invoke($"[本地缓存] 成功加载 {localData.Length} 条 Tick 成交数据。");
                    return localData;
                }
            }

            // 2. 本地无缓存文件，在线下载完整 Recent Trades 列表并保存缓存
            logger?.Invoke($"[无本地缓存] 开始在线下载 {symbol} 最新 {limit} 条完整 Tick 成交数据并写入 [{targetDir}]...");
            Tick[] apiTicks = await FetchRecentTradeTicksFromApiAsync(symbol, limit: limit);

            if (apiTicks.Length > 0)
            {
                string fileName = $"{symbol}_Trade_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                string savePath = Path.Combine(targetDir, fileName);
                SaveTicksToCsvFile(savePath, apiTicks);
                logger?.Invoke($"[自动缓存成功] 已存入本地 Tick 存盘文件: {savePath} (共 {apiTicks.Length} 条成交数据)");
            }

            return apiTicks;
        }

        /// <summary>
        /// 按天为最小单位，在 [startDate, endDate] 日期范围内获取完整的 Tick 归集成交数据。
        /// 对范围内的每一天，优先检查并读取本地按天命名的 CSV 缓存文件；若不存在则自动通过 API 下载当天的 Tick 数据并保存缓存。
        /// </summary>
        public static async Task<Tick[]> GetOrFetchTradeTicksForDateRangeAsync(
            string symbol,
            DateTime startDate,
            DateTime endDate,
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

            List<Tick> resultList = new List<Tick>();
            int cachedDays = 0;
            int downloadedDays = 0;

            for (DateTime d = startDate; d <= endDate; d = d.AddDays(1))
            {
                string dayStr = d.ToString("yyyy-MM-dd");
                string dailyFilePath = Path.Combine(targetDir, $"{symbol}_Trade_{dayStr}.csv");

                if (File.Exists(dailyFilePath))
                {
                    Tick[] dayTicks = ReadTicksFromCsvFile(dailyFilePath);
                    if (dayTicks.Length > 0)
                    {
                        resultList.AddRange(dayTicks);
                        cachedDays++;
                        continue;
                    }
                }

                // 本地无该日缓存，在线下载全天 Tick 数据
                DateTime dayStart = d.Date;
                DateTime dayEnd = d.Date.AddDays(1).AddTicks(-1);

                logger?.Invoke($"[Tick 天级下载] 下载 {symbol} 在 {dayStr} 的完整 Tick 成交数据...");

                try
                {
                    Tick[] dayTicks = await FetchAggregatedTradeTicksFromApiAsync(symbol, dayStart, dayEnd, limit: 1000);
                    if (dayTicks.Length > 0)
                    {
                        SaveTicksToCsvFile(dailyFilePath, dayTicks);
                        resultList.AddRange(dayTicks);
                        downloadedDays++;
                        logger?.Invoke($"[Tick 落盘成功] 已成功缓存 {dayStr} 数据至 {dailyFilePath} ({dayTicks.Length} 条)");
                    }
                    else
                    {
                        logger?.Invoke($"[无 Tick 数据] {dayStr} API 未返回 Tick 数据。");
                    }
                }
                catch (Exception ex)
                {
                    logger?.Invoke($"[Tick 下载失败] {dayStr} 数据获取异常: {ex.Message}");
                }
            }

            logger?.Invoke($"[Tick 按天检索完成] {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}: 本地命中 {cachedDays} 天，在线下载 {downloadedDays} 天，累计读取 {resultList.Count} 条 Tick 数据。");

            return resultList.OrderBy(t => t.Time).ToArray();
        }

        /// <summary>
        /// 自定义获取指定时间范围的数据（最小时间粒度：天）。
        /// 针对 [startDate, endDate] 中的每一天，优先检索并读取本地按天存盘的 CSV 缓存（格式: Symbol_Interval_2026-08-14.csv）；
        /// 若本地无该日缓存，则自动通过 API 下载当天的 K 线数据并写入本地缓存目录。
        /// </summary>
        public static async Task<Kline[]> GetOrFetchKlinesForDateRangeAsync(
            string symbol,
            KlineInterval interval,
            DateTime startDate,
            DateTime endDate,
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

            List<Kline> resultList = new List<Kline>();
            int cachedDays = 0;
            int downloadedDays = 0;

            for (DateTime d = startDate; d <= endDate; d = d.AddDays(1))
            {
                string dayStr = d.ToString("yyyy-MM-dd");
                string dailyFilePath = Path.Combine(targetDir, $"{symbol}_{interval}_{dayStr}.csv");

                if (File.Exists(dailyFilePath))
                {
                    Kline[] dayKlines = ReadKlinesFromCsvFile(dailyFilePath);
                    if (dayKlines.Length > 0)
                    {
                        resultList.AddRange(dayKlines);
                        cachedDays++;
                        continue;
                    }
                }

                // 本地无该日缓存，在线下载全天数据 (00:00:00 至 23:59:59.999)
                DateTime dayStart = d.Date;
                DateTime dayEnd = d.Date.AddDays(1).AddTicks(-1);

                logger?.Invoke($"[天级下载] 下载 {symbol} ({interval}) 在 {dayStr} 的数据...");

                try
                {
                    Kline[] dayKlines = await FetchKlinesFromApiAsync(symbol, interval, dayStart, dayEnd, limit: 1440);
                    if (dayKlines.Length > 0)
                    {
                        SaveKlinesToCsvFile(dailyFilePath, dayKlines);
                        resultList.AddRange(dayKlines);
                        downloadedDays++;
                        logger?.Invoke($"[天级落盘] 成功缓存 {dayStr} 数据至 {dailyFilePath} ({dayKlines.Length} 条)");
                    }
                    else
                    {
                        logger?.Invoke($"[无数据] {dayStr} API 未返回数据。");
                    }
                }
                catch (Exception ex)
                {
                    logger?.Invoke($"[下载失败] {dayStr} 数据获取异常: {ex.Message}");
                }
            }

            logger?.Invoke($"[按天检索完成] {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}: 本地命中 {cachedDays} 天，在线下载 {downloadedDays} 天，累计读取 {resultList.Count} 条数据。");

            return resultList.OrderBy(k => k.OpenTime).ToArray();
        }

        #endregion
    }
}
