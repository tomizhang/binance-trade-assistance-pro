// Services/BinanceWsApiService.cs
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TradingService.Services
{
    public class BinanceWsApiService : BackgroundService
    {
        private readonly ILogger<BinanceWsApiService> _logger;
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private ClientWebSocket _ws;

        // 🌟 核心：用于桥接异步 WebSocket 响应和同步调用的“神仙字典”
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new();

        public BinanceWsApiService(IConfiguration config, ILogger<BinanceWsApiService> logger)
        {
            _logger = logger;
            _apiKey = config["BinanceConfig:ApiKey"];
            _apiSecret = config["BinanceConfig:ApiSecret"];
            _ws = new ClientWebSocket();
        }

        // ==========================================
        // 1. 后台守护进程：维持连接并疯狂接收响应
        // ==========================================
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 连接到币安的 U本位 WebSocket API 端点
            await ConnectWsAsync(stoppingToken);

            var buffer = new byte[1024 * 16]; // 分配 16KB 接收缓冲区

            while (!stoppingToken.IsCancellationRequested)
            {
                if (_ws.State != WebSocketState.Open)
                {
                    _logger.LogWarning("⚠️ WS API 断开，尝试重连...");
                    await Task.Delay(3000, stoppingToken);
                    await ConnectWsAsync(stoppingToken);
                    continue;
                }

                try
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken);
                    if (result.MessageType == WebSocketMessageType.Close) continue;

                    var jsonResponse = Encoding.UTF8.GetString(buffer, 0, result.Count);

                    // 🌟 核心逻辑：从返回的 JSON 中提取 ID，并通知对应的 Task 醒来
                    using var doc = JsonDocument.Parse(jsonResponse);
                    if (doc.RootElement.TryGetProperty("id", out var idElement))
                    {
                        string id = idElement.GetString();
                        if (id != null && _pendingRequests.TryRemove(id, out var tcs))
                        {
                            // 唤醒处于 await 状态的下单请求，把结果塞进去
                            tcs.SetResult(jsonResponse);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "读取 WS API 时发生异常");
                }
            }
        }

        private async Task ConnectWsAsync(CancellationToken token)
        {
            try
            {
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(new Uri("wss://ws-fapi.binance.com/ws-fapi/v1"), token);
                _logger.LogInformation("🟢 币安 WebSocket 交易 API 专线已连接！");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🔴 WS API 连接失败");
            }
        }

        // ==========================================
        // 2. 对外暴露的下单方法 (供 Controller 调用)
        // ==========================================
        public async Task<string> PlaceOrderWsAsync(string symbol, string side, string type, decimal quantity, decimal? price = null)
        {
            if (_ws.State != WebSocketState.Open) throw new Exception("WebSocket 交易专线未连接");

            // 1. 生成唯一请求 ID
            string requestId = Guid.NewGuid().ToString("N");
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 2. 构造签名前的 Query 字符串 (必须严格按字母顺序排序，或与发送顺序绝对一致)
            var queryBuilder = new StringBuilder();
            queryBuilder.Append($"apiKey={_apiKey}&");
            if (type == "LIMIT" && price.HasValue) queryBuilder.Append($"price={price.Value}&");
            queryBuilder.Append($"quantity={quantity}&");
            queryBuilder.Append($"side={side}&");
            queryBuilder.Append($"symbol={symbol}&");
            if (type == "LIMIT") queryBuilder.Append("timeInForce=GTC&");
            queryBuilder.Append($"timestamp={timestamp}&");
            queryBuilder.Append($"type={type}");

            string queryString = queryBuilder.ToString();
            string signature = GenerateSignature(queryString, _apiSecret);

            // 3. 构造最终的 JSON-RPC 发送包
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

            // 过滤掉 null 的属性
            string jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

            // 4. 创建一个挂起的任务 (TCS)
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests.TryAdd(requestId, tcs);

            // 5. 发送指令到 WebSocket
            var bytes = Encoding.UTF8.GetBytes(jsonPayload);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

            _logger.LogInformation("🚀 [WS专线] 已发送订单: {Id}", requestId);

            // 6. 等待币安的异步回调 (设置 5 秒超时保护)
            var timeoutTask = Task.Delay(5000);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _pendingRequests.TryRemove(requestId, out _);
                throw new TimeoutException("WS 下单超时，币安未在5秒内返回确认信息");
            }

            return await tcs.Task; // 返回包含成功状态和交易明细的 JSON
        }

        private string GenerateSignature(string message, string secret)
        {
            var keyBytes = Encoding.UTF8.GetBytes(secret);
            using var hmac = new HMACSHA256(keyBytes);
            return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))).Replace("-", "").ToLower();
        }
    }
}