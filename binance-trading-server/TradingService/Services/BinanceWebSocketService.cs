using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingTerminal.Hubs; // 请确保这里的命名空间与你的项目匹配

namespace TradingTerminal.Services
{
    public class BinanceWebSocketService : BackgroundService
    {
        private readonly IHubContext<MarketHub> _hubContext;
        private readonly ILogger<BinanceWebSocketService> _logger;
        private readonly MarketEventBus _eventBus;
        private readonly string _apiKey;
        private readonly string _apiSecret;

        // 🌟 双轨制 WebSocket 实例
        private ClientWebSocket _publicWs = new ClientWebSocket();
        private ClientWebSocket _tradeWs = new ClientWebSocket();

        private readonly HttpClient _httpClient;
        // 用于 WS API 下单的异步回调字典
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new();

        // ==========================================
        // 🌟 核心升级：引用计数与精准路由引擎
        // ==========================================
        // 记录所有前端+后端对某个流的需求总数
        private readonly ConcurrentDictionary<string, int> _masterStreamCounts = new();
        // 记录每个前端网页 (ConnectionId) 对应订阅了哪些流
        private readonly ConcurrentDictionary<string, HashSet<string>> _clientSubs = new();

        public BinanceWebSocketService(IHubContext<MarketHub> hubContext, IConfiguration config, ILogger<BinanceWebSocketService> logger, MarketEventBus eventBus)
        {
            _hubContext = hubContext;
            _logger = logger;
            _apiKey = config["BinanceConfig:ApiKey"];
            _apiSecret = config["BinanceConfig:ApiSecret"];

            // 1. 创建代理对象 (推荐 SOCKS5)
#if DEBUG
            WebProxy proxy = new WebProxy("socks5://127.0.0.1:10808");
#endif
            // 2. 配置高性能的底层的 SocketsHttpHandler
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
            // 🌟 启动独立线程：专门测量 C# 到币安的真实延迟
            _ = Task.Run(() => MeasureBackendToBinanceLatency(stoppingToken), stoppingToken);

            var publicStreamTask = MaintainPublicStreamAsync(stoppingToken);
            var tradeStreamTask = MaintainTradeStreamAsync(stoppingToken);

            await Task.WhenAll(publicStreamTask, tradeStreamTask);
        }

        // ==========================================
        // 轨 1：公共行情流 (带智能分流与拦截)
        // ==========================================
        private async Task MaintainPublicStreamAsync(CancellationToken stoppingToken)
        {
            var buffer = new byte[1024 * 128]; // 128KB 缓冲区

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

                    // 依然保留默认的全局基础数据订阅
                    await _publicWs.ConnectAsync(new Uri("wss://fstream.binance.com/market/stream?streams=!miniTicker@arr/!markPrice@arr@1s"), stoppingToken);
                    _logger.LogInformation("✅ [公共行情轨] 已连接");

                    // 🌟 断网重连防御：恢复活跃计数 > 0 的流
                    var activeStreams = _masterStreamCounts.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
                    if (activeStreams.Any())
                    {
                        await SendWsCommandAsync(activeStreams, "SUBSCRIBE");
                        _logger.LogInformation($"🔄 自动恢复了 {activeStreams.Count} 个因断网丢失的订阅流");
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

        // 🌟 智能数据分流与拦截核心
        private async void ProcessAndRouteMarketData(string jsonMessage, CancellationToken stoppingToken)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonMessage);
                if (doc.RootElement.TryGetProperty("stream", out var streamElement))
                {
                    string streamName = streamElement.GetString();

                    // 🎯 分流器：如果是大盘基础数据，全局广播；如果是 K 线，精确路由！
                    if (streamName.Contains("miniTicker") || streamName.Contains("markPrice"))
                    {
                        await _hubContext.Clients.All.SendAsync("ReceiveMarketData", jsonMessage, stoppingToken);
                    }
                    else
                    {
                        // 路由到指定组：前端只有调用过 Subscribe 并加入 Group 的人才能收到
                        await _hubContext.Clients.Group(streamName).SendAsync("ReceiveMarketData", jsonMessage, stoppingToken);
                    }

                    // 统一解析 K 线并扔给后端总线，供 HA 等策略引擎使用
                    if (streamName.EndsWith("@kline_1m") || streamName.Contains("@kline_"))
                    {
                        if (doc.RootElement.TryGetProperty("data", out var dataNode))
                        {
                            var kNode = dataNode.GetProperty("k");
                            var msg = new KlineMessage
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
                            _eventBus.PublishKline(msg);
                        }
                    }
                }
            }
            catch { /* 忽略非标 JSON */ }
        }

        // ==========================================
        // 🌟 引用计数与动态订阅管理
        // ==========================================
        private async Task ChangeStreamSubscriptionAsync(string stream, int delta)
        {
            // 更新计数器
            var newCount = _masterStreamCounts.AddOrUpdate(
                stream,
                addValueFactory: key => delta > 0 ? delta : 0,
                updateValueFactory: (key, old) => Math.Max(0, old + delta)
            );

            if (_publicWs == null || _publicWs.State != WebSocketState.Open) return;

            // 0 变 1 发起真实订阅，X 变 0 发起真实退订
            if (delta > 0 && newCount == 1)
            {
                await SendWsCommandAsync(new[] { stream }, "SUBSCRIBE");
            }
            else if (delta < 0 && newCount == 0)
            {
                await SendWsCommandAsync(new[] { stream }, "UNSUBSCRIBE");
            }
        }

        private async Task SendWsCommandAsync(IEnumerable<string> streams, string method)
        {
            var payload = new { method, @params = streams, id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            var json = JsonSerializer.Serialize(payload);
            await _publicWs.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
            _logger.LogInformation($"{(method == "SUBSCRIBE" ? "📡" : "🗑️")} [统一网关] {method}: {string.Join(", ", streams)}");
        }

        // ------------------------------------------
        // 供前端 Hub 调用的精准路由 API
        // ------------------------------------------
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

        // ------------------------------------------
        // 供后端服务 (HA, 策略引擎) 调用的纯享 API
        // ------------------------------------------
        public async Task SubscribeBackendAsync(IEnumerable<string> streams)
        {
            foreach (var s in streams) await ChangeStreamSubscriptionAsync(s, 1);
        }

        public async Task UnsubscribeBackendAsync(IEnumerable<string> streams)
        {
            foreach (var s in streams) await ChangeStreamSubscriptionAsync(s, -1);
        }

        // ==========================================
        // 为了兼容你旧代码中直接传入 List 的批量订阅方法
        // ==========================================
        public async Task SubscribeStreamsAsync(IEnumerable<string> streams)
        {
            await SubscribeBackendAsync(streams);
        }

        public async Task UnsubscribeStreamsAsync(IEnumerable<string> streams)
        {
            await UnsubscribeBackendAsync(streams);
        }

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