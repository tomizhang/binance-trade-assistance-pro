using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingTerminal.Hubs;

namespace TradingTerminal.Services
{
    public class BinanceWebSocketService : BackgroundService
    {
        private readonly IHubContext<MarketHub> _hubContext;
        private readonly ILogger<BinanceWebSocketService> _logger;
        private readonly MarketEventBus _eventBus;
        private readonly string _apiKey;
        private readonly string _apiSecret;

        // 🌟 自定义 K 线聚合器
        private readonly CustomKlineAggregator _aggregator = new();

        private ClientWebSocket _publicWs = new ClientWebSocket();
        private ClientWebSocket _tradeWs = new ClientWebSocket();

        // 🌟 内部统一的高性能 HTTP 客户端 (自带代理和连接池，复用于所有 REST API)
        private readonly HttpClient _httpClient;

        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new();

        // 账本A：记录前端真实要什么 (例如要 2m, 4m)
        private readonly ConcurrentDictionary<string, int> _masterStreamCounts = new();

        // 账本B：记录我们实际向币安要什么 (把 2m 降级翻译成 1m 的计费总数)
        private readonly ConcurrentDictionary<string, int> _binanceStreamCounts = new();

        private readonly ConcurrentDictionary<string, HashSet<string>> _clientSubs = new();

        public BinanceWebSocketService(IHubContext<MarketHub> hubContext, IConfiguration config, ILogger<BinanceWebSocketService> logger, MarketEventBus eventBus)
        {
            _hubContext = hubContext;
            _logger = logger;
            _apiKey = config["BinanceConfig:ApiKey"];
            _apiSecret = config["BinanceConfig:ApiSecret"];

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
            _eventBus = eventBus;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 [统一引擎] 币安双轨 WebSocket 服务启动...");
            _ = Task.Run(() => MeasureBackendToBinanceLatency(stoppingToken), stoppingToken);
            var publicStreamTask = MaintainPublicStreamAsync(stoppingToken);
            var tradeStreamTask = MaintainTradeStreamAsync(stoppingToken);
            await Task.WhenAll(publicStreamTask, tradeStreamTask);
        }

        // ==========================================
        // 🌟 统一数据网关接口：获取历史 K 线 (包含自定义周期处理)
        // ==========================================
        public async Task<string> GetHistoricalKlinesAsync(string symbol, string interval, int limit = 1000, long? endTime = null)
        {
            // 1. 偷梁换柱：获取底层的真实周期和安全请求数量
            var (baseInterval, neededLimit) = _aggregator.GetBaseHistoryRequestParams(interval, limit);

            string url = $"https://fapi.binance.com/fapi/v1/klines?symbol={symbol}&interval={baseInterval}&limit={neededLimit}";
            if (endTime.HasValue)
            {
                url += $"&endTime={endTime.Value}";
            }

            try
            {
                // 2. 复用高性能 _httpClient 请求币安
                var response = await _httpClient.GetAsync(url);
                var rawContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"❌ [统一网关] 币安历史K线接口报错: {rawContent}");
                    throw new Exception($"币安接口请求失败，状态码: {response.StatusCode}");
                }

                // 3. 瞒天过海：利用聚合器加工 2m/4m 等自定义周期，并伪装成币安官方 JSON 返回
                return _aggregator.AggregateHistoricalJson(rawContent, interval);
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ [统一网关] 获取历史K线异常: {ex.Message}");
                throw;
            }
        }


        // ==========================================
        // 轨 1：公共行情流 (带智能分流与拦截)
        // ==========================================
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
                    _logger.LogInformation("✅ [公共行情轨] 已连接");

                    // 断网重连：根据向币安的翻译账本进行恢复
                    var activeStreams = _binanceStreamCounts.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
                    if (activeStreams.Any())
                    {
                        await SendWsCommandAsync(activeStreams, "SUBSCRIBE");
                        _logger.LogInformation($"🔄 自动恢复了 {activeStreams.Count} 个底层依赖订阅流");
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
                    _logger.LogWarning($"⚠️ [公共行情轨] 断开，5秒后重连: {ex.Message}");
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }

        // 智能数据分流与聚合器拦截核心
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
                                OpenTime = kNode.GetProperty("t").GetInt64()
                            };
                        }

                        // 如果这是 1m 基础流，喂给加工厂！
                        if (streamName.EndsWith("@kline_1m") && msg != null)
                        {
                            if (_masterStreamCounts.TryGetValue(streamName, out int c1) && c1 > 0)
                            {
                                await _hubContext.Clients.Group(streamName).SendAsync("ReceiveMarketData", jsonMessage, stoppingToken);
                            }
                            _eventBus.PublishKline(msg);

                            // 寻找当前激活的自定义加工任务
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
            catch { /* 忽略非标 JSON */ }
        }

        private string CreateSyntheticKlineJson(KlineMessage msg)
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
                        i = msg.Interval,
                        x = msg.IsClosed
                    }
                }
            };
            return JsonSerializer.Serialize(payload);
        }

        // ==========================================
        // 订阅管理机制 (含降级翻译)
        // ==========================================
        private string GetBinanceStreamName(string stream)
        {
            if (stream.Contains("@kline_"))
            {
                var parts = stream.Split("@kline_");
                if (_aggregator.IsCustomInterval(parts[1]))
                {
                    return $"{parts[0]}@kline_1m"; // 降级到 1m
                }
            }
            return stream;
        }

        private async Task ChangeStreamSubscriptionAsync(string stream, int delta)
        {
            _masterStreamCounts.AddOrUpdate(
                stream,
                addValueFactory: key => delta > 0 ? delta : 0,
                updateValueFactory: (key, old) => Math.Max(0, old + delta)
            );

            string binanceStream = GetBinanceStreamName(stream);
            var newBinanceCount = _binanceStreamCounts.AddOrUpdate(
                binanceStream,
                addValueFactory: key => delta > 0 ? delta : 0,
                updateValueFactory: (key, old) => Math.Max(0, old + delta)
            );

            if (_publicWs == null || _publicWs.State != WebSocketState.Open) return;

            if (delta > 0 && newBinanceCount == 1)
            {
                await SendWsCommandAsync(new[] { binanceStream }, "SUBSCRIBE");
            }
            else if (delta < 0 && newBinanceCount == 0)
            {
                await SendWsCommandAsync(new[] { binanceStream }, "UNSUBSCRIBE");
            }
        }

        private async Task SendWsCommandAsync(IEnumerable<string> streams, string method)
        {
            var payload = new { method, @params = streams, id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            var json = JsonSerializer.Serialize(payload);
            await _publicWs.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
            _logger.LogInformation($"{(method == "SUBSCRIBE" ? "📡" : "🗑️")} [统一网关] {method}: {string.Join(", ", streams)}");
        }

        public async Task SubscribeFrontendAsync(string connectionId, string stream)
        {
            var subs = _clientSubs.GetOrAdd(connectionId, _ => new HashSet<string>());
            bool added;
            lock (subs) { added = subs.Add(stream); }
            if (added) await ChangeStreamSubscriptionAsync(stream, 1);
        }

        public async Task UnsubscribeFrontendAsync(string connectionId, string stream)
        {
            if (_clientSubs.TryGetValue(connectionId, out var subs))
            {
                bool removed;
                lock (subs) { removed = subs.Remove(stream); }
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

        public async Task SubscribeStreamsAsync(IEnumerable<string> streams) => await SubscribeBackendAsync(streams);
        public async Task UnsubscribeStreamsAsync(IEnumerable<string> streams) => await UnsubscribeBackendAsync(streams);

        // ==========================================
        // 轨 2：交易 API 专线 (专门用来极速下单 - 保持不变)
        // ==========================================
        private async Task MaintainTradeStreamAsync(CancellationToken stoppingToken)
        {
            var buffer = new byte[1024 * 16];

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _tradeWs?.Dispose();
                    _tradeWs = new ClientWebSocket();
#if DEBUG
                    _tradeWs.Options.Proxy = new WebProxy("socks5://127.0.0.1:10808");
#endif
                    _tradeWs.Options.KeepAliveInterval = TimeSpan.FromMinutes(2);

                    await _tradeWs.ConnectAsync(new Uri("wss://ws-fapi.binance.com/ws-fapi/v1"), stoppingToken);
                    _logger.LogInformation("🟢 [交易 API 轨] 专线已连接");

                    while (_tradeWs.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                    {
                        var result = await _tradeWs.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken);
                        if (result.MessageType == WebSocketMessageType.Close) break;

                        var jsonResponse = Encoding.UTF8.GetString(buffer, 0, result.Count);

                        using var doc = JsonDocument.Parse(jsonResponse);
                        if (doc.RootElement.TryGetProperty("id", out var idElement))
                        {
                            string id = idElement.GetString();
                            if (id != null && _pendingRequests.TryRemove(id, out var tcs))
                            {
                                tcs.SetResult(jsonResponse);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"⚠️ [交易 API 轨] 断开，5秒后重连: {ex.Message}");
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }

        public async Task<string> PlaceOrderWsAsync(string symbol, string side, string type, decimal quantity, decimal? price = null)
        {
            if (_tradeWs.State != WebSocketState.Open) throw new Exception("WebSocket 交易专线未就绪，请稍后再试");

            string requestId = Guid.NewGuid().ToString("N");
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var queryBuilder = new StringBuilder();
            queryBuilder.Append($"apiKey={_apiKey}&");
            if (type == "LIMIT" && price.HasValue) queryBuilder.Append($"price={price.Value}&");
            queryBuilder.Append($"quantity={quantity}&");
            queryBuilder.Append($"side={side}&");
            queryBuilder.Append($"symbol={symbol}&");
            if (type == "LIMIT") queryBuilder.Append("timeInForce=GTC&");
            queryBuilder.Append($"timestamp={timestamp}&");
            queryBuilder.Append($"type={type}");

            string signature = GenerateSignature(queryBuilder.ToString(), _apiSecret);

            var payload = new
            {
                id = requestId,
                method = "order.place",
                @params = new
                {
                    apiKey = _apiKey,
                    price = type == "LIMIT" ? price?.ToString() : null,
                    quantity = quantity.ToString(),
                    side = side,
                    symbol = symbol,
                    timeInForce = type == "LIMIT" ? "GTC" : null,
                    timestamp = timestamp,
                    type = type,
                    signature = signature
                }
            };

            string jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests.TryAdd(requestId, tcs);

            var bytes = Encoding.UTF8.GetBytes(jsonPayload);
            await _tradeWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            _logger.LogInformation($"🚀 [WS专线] 已发送订单: {symbol} {side} {quantity}");

            var timeoutTask = Task.Delay(5000);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _pendingRequests.TryRemove(requestId, out _);
                throw new TimeoutException("WS 下单超时，币安未在5秒内返回确认信息");
            }

            return await tcs.Task;
        }

        private string GenerateSignature(string message, string secret)
        {
            var keyBytes = Encoding.UTF8.GetBytes(secret);
            using var hmac = new HMACSHA256(keyBytes);
            return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))).Replace("-", "").ToLower();
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

                var validTickers = doc.RootElement.EnumerateArray()
                    .Where(x => x.GetProperty("symbol").GetString().EndsWith("USDT"))
                    .ToList();

                var topVolume = validTickers
                    .OrderByDescending(x => decimal.Parse(x.GetProperty("quoteVolume").GetString()))
                    .Take(10)
                    .Select(x => x.GetProperty("symbol").GetString().ToUpper());

                var topGainers = validTickers
                    .OrderByDescending(x => decimal.Parse(x.GetProperty("priceChangePercent").GetString()))
                    .Take(10)
                    .Select(x => x.GetProperty("symbol").GetString().ToUpper());

                try
                {
                    var list = new HashSet<string>(topVolume.Concat(topGainers)).ToList();
                    _logger.LogInformation($"🔥 [雷达更新] 最新锁定的资金战场 (共 {list.Count} 个): {string.Join(", ", list)}");
                    return list;
                }
                finally { }
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ 刷新热门币种名单失败: {ex.Message}");
            }
            return null;
        }
    }
}