using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TradingTerminal.Models;

namespace TradingTerminal.Services.Backtest.Services
{
    public class BinanceDataDownloadService
    {
        private readonly ILogger<BinanceDataDownloadService> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _cacheDirectory;

        public BinanceDataDownloadService(ILogger<BinanceDataDownloadService> logger)
        {
            _logger = logger;
            _cacheDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BacktestData");

            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
            }

#if DEBUG
            WebProxy proxy = new WebProxy("socks5://127.0.0.1:10808");
#endif
            SocketsHttpHandler handler = new SocketsHttpHandler
            {
#if DEBUG
                Proxy = proxy,
                UseProxy = true,
#endif
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            _httpClient = new HttpClient(handler);
        }

        public async Task<List<IKline>> GetKlinesAsync(string symbol, string interval, DateTime startTime, DateTime endTime)
        {
            symbol = symbol.ToUpper();
            string cacheFileName = $"{symbol}_{interval}_{startTime:yyyyMMddHHmmss}_{endTime:yyyyMMddHHmmss}.json";
            string cacheFilePath = Path.Combine(_cacheDirectory, cacheFileName);

            if (File.Exists(cacheFilePath))
            {
                _logger.LogInformation($"📦 [数据缓存] 命中缓存 {cacheFileName}，正在加载...");
                try
                {
                    string cachedJson = await File.ReadAllTextAsync(cacheFilePath);
                    var cachedList = JsonSerializer.Deserialize<List<KlineMessage>>(cachedJson);
                    if (cachedList != null && cachedList.Any())
                    {
                        return cachedList.Cast<IKline>().ToList();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"⚠️ [数据缓存] 读取缓存文件失败: {ex.Message}，将重新从币安拉取。");
                }
            }

            _logger.LogInformation($"🌐 [数据下载] 开始从币安下载 {symbol} {interval} K线数据 ({startTime:yyyy-MM-dd HH:mm:ss} 至 {endTime:yyyy-MM-dd HH:mm:ss})");

            var allKlines = new List<IKline>();
            long currentStartMs = new DateTimeOffset(startTime).ToUnixTimeMilliseconds();
            long endMs = new DateTimeOffset(endTime).ToUnixTimeMilliseconds();
            
            while (currentStartMs < endMs)
            {
                string url = $"https://fapi.binance.com/fapi/v1/klines?symbol={symbol}&interval={interval}&startTime={currentStartMs}&endTime={endMs}&limit=1000";
                
                try
                {
                    var response = await _httpClient.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                    {
                        string errMsg = await response.Content.ReadAsStringAsync();
                        throw new Exception($"API错误: {response.StatusCode}, {errMsg}");
                    }

                    string json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    
                    var fetchedKlines = new List<KlineMessage>();
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        fetchedKlines.Add(new KlineMessage
                        {
                            Symbol = symbol,
                            Interval = interval,
                            IsClosed = true,
                            OpenTime = item[0].GetInt64(),
                            Open = decimal.Parse(item[1].GetString(), System.Globalization.CultureInfo.InvariantCulture),
                            High = decimal.Parse(item[2].GetString(), System.Globalization.CultureInfo.InvariantCulture),
                            Low = decimal.Parse(item[3].GetString(), System.Globalization.CultureInfo.InvariantCulture),
                            Close = decimal.Parse(item[4].GetString(), System.Globalization.CultureInfo.InvariantCulture),
                            Volume = decimal.Parse(item[5].GetString(), System.Globalization.CultureInfo.InvariantCulture),
                            TradeCount = item[8].GetInt32(),
                            TakerBuyBaseVolume = decimal.Parse(item[9].GetString(), System.Globalization.CultureInfo.InvariantCulture)
                        });
                    }

                    if (!fetchedKlines.Any())
                    {
                        break;
                    }

                    allKlines.AddRange(fetchedKlines);
                    
                    long lastOpenTime = fetchedKlines.Last().OpenTime;
                    
                    // 避免死循环：如果时间戳没有前进，手动加1毫秒
                    if (lastOpenTime <= currentStartMs)
                    {
                        currentStartMs += 60000; // 前进 1 分钟
                    }
                    else
                    {
                        currentStartMs = lastOpenTime + 1;
                    }

                    _logger.LogInformation($"⚡ [数据下载] 已加载 {allKlines.Count} 根 K线，时间推进至 {DateTimeOffset.FromUnixTimeMilliseconds(currentStartMs).DateTime:yyyy-MM-dd HH:mm:ss}");

                    // 避免币安频限，加一小段延迟
                    await Task.Delay(100);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ [数据下载] 获取 {symbol} 失败: {ex.Message}");
                    throw;
                }
            }

            // 保存到缓存
            try
            {
                string jsonToWrite = JsonSerializer.Serialize(allKlines.Cast<KlineMessage>().ToList(), new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(cacheFilePath, jsonToWrite);
                _logger.LogInformation($"💾 [数据缓存] 成功写入本地缓存: {cacheFileName}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"⚠️ [数据缓存] 缓存写入失败: {ex.Message}");
            }

            return allKlines;
        }
    }
}
