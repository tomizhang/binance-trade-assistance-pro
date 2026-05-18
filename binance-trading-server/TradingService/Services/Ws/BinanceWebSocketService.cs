using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingTerminal.Hubs;
using TradingTerminal.Models;

namespace TradingTerminal.Services
{
    /// <summary>
    /// 纯粹的行情数据中枢 (专职负责公共数据、自定义聚合 K 线、延迟测速)
    /// </summary>
    public class BinanceWebSocketService : BackgroundService
    {
        private readonly IHubContext<MarketHub> _hubContext;
        private readonly ILogger<BinanceWebSocketService> _logger;
        private readonly MarketEventBus _eventBus;
        private readonly int DefaultTake = 100;
        private readonly CustomKlineAggregator _aggregator = new();
        private ClientWebSocket _publicWs = new ClientWebSocket();

        private readonly HttpClient _httpClient;

        private readonly ConcurrentDictionary<string, int> _masterStreamCounts = new();
        private readonly ConcurrentDictionary<string, int> _binanceStreamCounts = new();
        private readonly ConcurrentDictionary<string, HashSet<string>> _clientSubs = new();

        public BinanceWebSocketService(IHubContext<MarketHub> hubContext, ILogger<BinanceWebSocketService> logger, MarketEventBus eventBus)
        {
            _hubContext = hubContext;
            _logger = logger;
            _eventBus = eventBus;

#if DEBUG
            WebProxy proxy = new WebProxy("socks5://127.0.0.1:10808");
#endif
            SocketsHttpHandler handler = new SocketsHttpHandler
            {
#if DEBUG
                Proxy = proxy,
#endif
                UseProxy = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            _httpClient = new HttpClient(handler);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 [行情中枢] 独立公共行情流网关启动...");
            _ = Task.Run(() => MeasureBackendToBinanceLatency(stoppingToken), stoppingToken);
            await MaintainPublicStreamAsync(stoppingToken);
        }

        public async Task<string> GetHistoricalKlinesAsync(string symbol, string interval, int limit = 1000, long? endTime = null)
        {
            var (baseInterval, neededLimit) = _aggregator.GetBaseHistoryRequestParams(interval, limit);

            string url = $"https://fapi.binance.com/fapi/v1/klines?symbol={symbol}&interval={baseInterval}&limit={neededLimit}";
            if (endTime.HasValue) url += $"&endTime={endTime.Value}";

            try
            {
                var response = await _httpClient.GetAsync(url);
                var rawContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    throw new Exception($"币安 HTTP 接口请求失败，状态码: {response.StatusCode}");

                _logger.LogInformation($"✅ [行情预热] {symbol} {interval} 历史K线基底拉取成功。");
                return _aggregator.AggregateHistoricalJson(rawContent, interval);
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ [行情预热] 获取历史K线异常: {ex.Message}");
                throw;
            }
        }

        private async Task MaintainPublicStreamAsync(CancellationToken stoppingToken)
        {
            var buffer = new byte[1024 * 128];

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _publicWs?.Dispose();
                    _publicWs = new ClientWebSocket();
#if DEBUG
                    _publicWs.Options.Proxy = new WebProxy("socks5://127.0.0.1:10808");
#endif
                    _publicWs.Options.KeepAliveInterval = TimeSpan.FromMinutes(2);

                    await _publicWs.ConnectAsync(new Uri("wss://fstream.binance.com/market/stream?streams=!miniTicker@arr/!markPrice@arr@1s"), stoppingToken);
                    _logger.LogInformation("✅ [行情中枢] 公共数据大盘已连接");

                    var activeStreams = _binanceStreamCounts.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
                    if (activeStreams.Any())
                    {
                        await SendWsCommandAsync(activeStreams, "SUBSCRIBE");
                    }

                    while (_publicWs.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                    {
                        var result = await _publicWs.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken);
                        if (result.MessageType == WebSocketMessageType.Close) break;

                        var rawJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        ProcessAndRouteMarketData(rawJson, stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"⚠️ [行情中枢] 连线断开，2秒后重连: {ex.Message}");
                    await Task.Delay(2000, stoppingToken);
                }
            }
        }

        private async void ProcessAndRouteMarketData(string jsonMessage, CancellationToken stoppingToken)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonMessage);
                if (doc.RootElement.TryGetProperty("stream", out var streamElement))
                {
                    string streamName = streamElement.GetString();

                    if (streamName.Contains("miniTicker") || streamName.Contains("markPrice"))
                    {
                        await _hubContext.Clients.All.SendAsync("ReceiveMarketData", jsonMessage, stoppingToken);
                    }
                    else if (streamName.Contains("@kline_"))
                    {
                        KlineMessage msg = null;
                        if (doc.RootElement.TryGetProperty("data", out var dataNode))
                        {
                            var kNode = dataNode.GetProperty("k");
                            msg = new KlineMessage
                            {
                                Symbol = kNode.GetProperty("s").GetString().ToUpper(),
                                Interval = kNode.GetProperty("i").GetString(),
                                IsClosed = kNode.GetProperty("x").GetBoolean(),
                                Open = decimal.Parse(kNode.GetProperty("o").GetString()),
                                Close = decimal.Parse(kNode.GetProperty("c").GetString()),
                                High = decimal.Parse(kNode.GetProperty("h").GetString()),
                                Low = decimal.Parse(kNode.GetProperty("l").GetString()),
                                Volume = decimal.Parse(kNode.GetProperty("v").GetString()),
                                OpenTime = kNode.GetProperty("t").GetInt64(),

                                // 🌟 核心修复 1：提取原始推送中的成交笔数 (n) 和 主动买入基础量 (V)
                                TradeCount = kNode.TryGetProperty("n", out var nElement) ? nElement.GetInt32() : 0,
                                TakerBuyBaseVolume = kNode.TryGetProperty("V", out var vElement) ? decimal.Parse(vElement.GetString()) : 0m
                            };
                        }

                        if (streamName.EndsWith("@kline_1m") && msg != null)
                        {
                            if (_masterStreamCounts.TryGetValue(streamName, out int c1) && c1 > 0)
                                await _hubContext.Clients.Group(streamName).SendAsync("ReceiveMarketData", jsonMessage, stoppingToken);

                            _eventBus.PublishKline(msg);

                            string symbolLower = msg.Symbol.ToLower();
                            var activeCustomIntervals = CustomKlineAggregator.SupportedCustomIntervals.Keys
                                .Where(interval => _masterStreamCounts.TryGetValue($"{symbolLower}@kline_{interval}", out int c) && c > 0)
                                .ToList();

                            if (activeCustomIntervals.Any())
                            {
                                var syntheticKlines = _aggregator.Process1mKline(msg, activeCustomIntervals);
                                foreach (var sk in syntheticKlines)
                                {
                                    string syntheticJson = CreateSyntheticKlineJson(sk);
                                    string targetGroup = $"{symbolLower}@kline_{sk.Interval}";

                                    await _hubContext.Clients.Group(targetGroup).SendAsync("ReceiveMarketData", syntheticJson, stoppingToken);
                                    _eventBus.PublishKline(sk);
                                }
                            }
                        }
                        else
                        {
                            await _hubContext.Clients.Group(streamName).SendAsync("ReceiveMarketData", jsonMessage, stoppingToken);
                            if (msg != null) _eventBus.PublishKline(msg);
                        }
                    }
                }
            }
            catch { }
        }

        private string CreateSyntheticKlineJson(IKline msg)
        {
            var payload = new
            {
                stream = $"{msg.Symbol.ToLower()}@kline_{msg.Interval}",
                data = new
                {
                    e = "kline",
                    E = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    s = msg.Symbol,
                    k = new
                    {
                        t = msg.OpenTime,
                        o = msg.Open.ToString("0.########"),
                        c = msg.Close.ToString("0.########"),
                        h = msg.High.ToString("0.########"),
                        l = msg.Low.ToString("0.########"),
                        v = msg.Volume.ToString("0.########"),

                        // 🌟 核心修复 2：将成交笔数和主动买入量组装回发送给前端的 JSON 中
                        n = msg.TradeCount,
                        V = msg.TakerBuyBaseVolume.ToString("0.########"),

                        i = msg.Interval,
                        x = msg.IsClosed
                    }
                }
            };
            return JsonSerializer.Serialize(payload);
        }

        private string GetBinanceStreamName(string stream)
        {
            if (stream.Contains("@kline_"))
            {
                var parts = stream.Split("@kline_");
                if (_aggregator.IsCustomInterval(parts[1])) return $"{parts[0]}@kline_1m";
            }
            return stream;
        }

        private async Task ChangeStreamSubscriptionAsync(string stream, int delta)
        {
            _masterStreamCounts.AddOrUpdate(stream, addValueFactory: key => delta > 0 ? delta : 0, updateValueFactory: (key, old) => Math.Max(0, old + delta));

            string binanceStream = GetBinanceStreamName(stream);
            var newBinanceCount = _binanceStreamCounts.AddOrUpdate(binanceStream, addValueFactory: key => delta > 0 ? delta : 0, updateValueFactory: (key, old) => Math.Max(0, old + delta));

            if (_publicWs == null || _publicWs.State != WebSocketState.Open) return;

            if (delta > 0 && newBinanceCount == 1) await SendWsCommandAsync(new[] { binanceStream }, "SUBSCRIBE");
            else if (delta < 0 && newBinanceCount == 0) await SendWsCommandAsync(new[] { binanceStream }, "UNSUBSCRIBE");
        }

        private async Task SendWsCommandAsync(IEnumerable<string> streams, string method)
        {
            var payload = new { method, @params = streams, id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            var json = JsonSerializer.Serialize(payload);
            await _publicWs.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task SubscribeFrontendAsync(string connectionId, string stream)
        {
            var subs = _clientSubs.GetOrAdd(connectionId, _ => new HashSet<string>());
            bool added; lock (subs) { added = subs.Add(stream); }
            if (added) await ChangeStreamSubscriptionAsync(stream, 1);
        }

        public async Task UnsubscribeFrontendAsync(string connectionId, string stream)
        {
            if (_clientSubs.TryGetValue(connectionId, out var subs))
            {
                bool removed; lock (subs) { removed = subs.Remove(stream); }
                if (removed) await ChangeStreamSubscriptionAsync(stream, -1);
            }
        }

        public async Task RemoveFrontendClientAsync(string connectionId)
        {
            if (_clientSubs.TryRemove(connectionId, out var subs))
            {
                foreach (var stream in subs) await ChangeStreamSubscriptionAsync(stream, -1);
            }
        }

        public async Task SubscribeBackendAsync(IEnumerable<string> streams)
        {
            foreach (var s in streams) await ChangeStreamSubscriptionAsync(s, 1);
        }

        public async Task UnsubscribeBackendAsync(IEnumerable<string> streams)
        {
            foreach (var s in streams) await ChangeStreamSubscriptionAsync(s, -1);
        }

        private async Task MeasureBackendToBinanceLatency(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    await _httpClient.GetAsync("https://fapi.binance.com/fapi/v1/ping", stoppingToken);
                    sw.Stop();
                    await _hubContext.Clients.All.SendAsync("ReceiveBackendLatency", sw.ElapsedMilliseconds, stoppingToken);
                }
                catch { }
                await Task.Delay(2000, stoppingToken);
            }
        }

        public async Task<List<string>> RefreshTopSymbolsAsync(CancellationToken stoppingToken)
        {
            try
            {
                var response = await _httpClient.GetAsync("https://fapi.binance.com/fapi/v1/ticker/24hr", stoppingToken);
                if (!response.IsSuccessStatusCode) return null;

                var json = await response.Content.ReadAsStringAsync(stoppingToken);
                using var doc = JsonDocument.Parse(json);

                var validTickers = doc.RootElement.EnumerateArray().Where(x => x.GetProperty("symbol").GetString().EndsWith("USDT")).ToList();
                var topVolume = validTickers.OrderByDescending(x => decimal.Parse(x.GetProperty("quoteVolume").GetString())).Take(DefaultTake).Select(x => x.GetProperty("symbol").GetString().ToUpper());
                var topGainers = validTickers.OrderByDescending(x => decimal.Parse(x.GetProperty("priceChangePercent").GetString())).Take(DefaultTake).Select(x => x.GetProperty("symbol").GetString().ToUpper());

                var list = new HashSet<string>(topVolume.Concat(topGainers)).ToList();
                return list;
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ 刷新热门币种名单失败: {ex.Message}");
            }
            return null;
        }

        public override void Dispose()
        {
            _publicWs?.Dispose();
            _httpClient?.Dispose();
            base.Dispose();
        }
    }
}