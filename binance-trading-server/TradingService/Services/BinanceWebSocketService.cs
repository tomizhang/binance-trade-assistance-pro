using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
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
        private readonly string _apiKey;
        private readonly string _apiSecret;

        // 🌟 双轨制 WebSocket 实例
        private ClientWebSocket _publicWs = new ClientWebSocket();
        private ClientWebSocket _tradeWs = new ClientWebSocket();

        // 用于 WS API 下单的异步回调字典
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new();

        public BinanceWebSocketService(IHubContext<MarketHub> hubContext, IConfiguration config, ILogger<BinanceWebSocketService> logger)
        {
            _hubContext = hubContext;
            _logger = logger;
            _apiKey = config["BinanceConfig:ApiKey"];
            _apiSecret = config["BinanceConfig:ApiSecret"];
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 [统一引擎] 币安双轨 WebSocket 服务启动...");

            // 并行启动两个不阻塞的循环任务
            var publicStreamTask = MaintainPublicStreamAsync(stoppingToken);
            var tradeStreamTask = MaintainTradeStreamAsync(stoppingToken);

            await Task.WhenAll(publicStreamTask, tradeStreamTask);
        }

        // ==========================================
        // 轨 1：公共行情流 (接收 K线、Ticker 并转发给 Vue)
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

                    // 🌟 核心修复：自动处理 Ping/Pong 心跳，防止被币安强踢
                    _publicWs.Options.KeepAliveInterval = TimeSpan.FromMinutes(2);

                    await _publicWs.ConnectAsync(new Uri("wss://fstream.binance.com/market/stream?streams=!miniTicker@arr/!markPrice@arr@1s"), stoppingToken);
                    _logger.LogInformation("✅ [公共行情轨] 已连接");

                    while (_publicWs.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                    {
                        var result = await _publicWs.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken);
                        if (result.MessageType == WebSocketMessageType.Close) break;

                        var rawJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        // 原封不动通过 SignalR 转发给前端
                        await _hubContext.Clients.All.SendAsync("ReceiveMarketData", rawJson, stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"⚠️ [公共行情轨] 断开，5秒后重连: {ex.Message}");
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }

        // ==========================================
        // 轨 2：交易 API 专线 (专门用来极速下单)
        // ==========================================
        private async Task MaintainTradeStreamAsync(CancellationToken stoppingToken)
        {
            var buffer = new byte[1024 * 16]; // 16KB 缓冲区

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _tradeWs?.Dispose();
                    _tradeWs = new ClientWebSocket();

                    // 🌟 核心修复：自动处理 Ping/Pong 心跳
                    _tradeWs.Options.KeepAliveInterval = TimeSpan.FromMinutes(2);

                    await _tradeWs.ConnectAsync(new Uri("wss://ws-fapi.binance.com/ws-fapi/v1"), stoppingToken);
                    _logger.LogInformation("🟢 [交易 API 轨] 专线已连接");

                    while (_tradeWs.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                    {
                        var result = await _tradeWs.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken);
                        if (result.MessageType == WebSocketMessageType.Close) break;

                        var jsonResponse = Encoding.UTF8.GetString(buffer, 0, result.Count);

                        // 从返回的 JSON 中提取 ID，并通知对应的挂起请求醒来
                        using var doc = JsonDocument.Parse(jsonResponse);
                        if (doc.RootElement.TryGetProperty("id", out var idElement))
                        {
                            string id = idElement.GetString();
                            if (id != null && _pendingRequests.TryRemove(id, out var tcs))
                            {
                                tcs.SetResult(jsonResponse); // 唤醒下单 Task
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

        // ==========================================
        // 对外暴露接口 1：动态订阅/退订行情 (前端 SignalR 触发)
        // ==========================================
        public async Task SubscribeStreamAsync(string streamName)
        {
            if (_publicWs.State == WebSocketState.Open)
            {
                var req = $"{{\"method\":\"SUBSCRIBE\",\"params\":[\"{streamName}\"],\"id\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}";
                await _publicWs.SendAsync(Encoding.UTF8.GetBytes(req), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }

        public async Task UnsubscribeStreamAsync(string streamName)
        {
            if (_publicWs.State == WebSocketState.Open)
            {
                var req = $"{{\"method\":\"UNSUBSCRIBE\",\"params\":[\"{streamName}\"],\"id\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}";
                await _publicWs.SendAsync(Encoding.UTF8.GetBytes(req), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }

        // ==========================================
        // 对外暴露接口 2：WS 极速下单 (Controller 触发)
        // ==========================================
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
    }
}