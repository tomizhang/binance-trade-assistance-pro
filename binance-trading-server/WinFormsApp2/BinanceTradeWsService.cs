using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinFormsApp2
{
    /// <summary>
    /// 纯粹的 0 延迟 WebSocket 交易执行引擎 (专职负责私有 WS 专线长连接、心跳保活续命、签名、order.place 市价/限价单、algoOrder.place 止盈止损条件单)
    /// 服务连接: wss://ws-fapi.binance.com/ws-fapi/v1
    /// </summary>
    public class BinanceTradeWsService : IDisposable
    {
        private readonly string _apiKey;
        private readonly string _apiSecret;

        private ClientWebSocket? _tradeWs;
        private readonly HttpClient _httpClient = new HttpClient();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new ConcurrentDictionary<string, TaskCompletionSource<string>>();
        private readonly ConcurrentDictionary<string, (int QuantityPrecision, int PricePrecision)> _symbolPrecisions = new ConcurrentDictionary<string, (int, int)>();
        private CancellationTokenSource? _cts;
        private Task? _maintainTask;
        private Task? _heartbeatTask;

        public bool IsConnected => _tradeWs != null && _tradeWs.State == WebSocketState.Open;

        public event Action<string>? OnLog;

        public BinanceTradeWsService(string apiKey, string apiSecret)
        {
            _apiKey = apiKey ?? string.Empty;
            _apiSecret = apiSecret ?? string.Empty;
        }

        public async Task StartAsync()
        {
            if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
            {
                Log("⚠️ [WS交易网关] API Key 或 Secret 未配置，无法建立 WebSocket 交易专线。");
                return;
            }

            _cts = new CancellationTokenSource();
            Log("⏳ [WS交易网关] 正在初始化加载币安交易规则与建立 WebSocket 0 延迟交易专线...");
            await LoadExchangeInfoAsync().ConfigureAwait(false);

            _maintainTask = Task.Run(() => MaintainTradeStreamAsync(_cts.Token));
            _heartbeatTask = Task.Run(() => StartHeartbeatLoopAsync(_cts.Token));
        }

        private async Task MaintainTradeStreamAsync(CancellationToken stoppingToken)
        {
            var buffer = new byte[1024 * 16];

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _tradeWs?.Dispose();
                    _tradeWs = new ClientWebSocket();

                    // 1. 协议级 30 秒 Ping 帧保活 (RFC 6455 Protocol Level Ping/Pong)
                    _tradeWs.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

                    Uri wsUri = new Uri("wss://ws-fapi.binance.com/ws-fapi/v1");
                    await _tradeWs.ConnectAsync(wsUri, stoppingToken).ConfigureAwait(false);
                    Log("🟢 [WS交易专线] 已成功连接至币安 0 延迟 WebSocket 交易网关 (wss://ws-fapi.binance.com/ws-fapi/v1)！双层心跳保活已启动。");

                    while (_tradeWs.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                    {
                        using var ms = new MemoryStream();
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await _tradeWs.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken).ConfigureAwait(false);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            ms.Write(buffer, 0, result.Count);
                        }
                        while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close) break;

                        var jsonResponse = Encoding.UTF8.GetString(ms.ToArray());

                        using var doc = JsonDocument.Parse(jsonResponse);
                        if (doc.RootElement.TryGetProperty("id", out var idElement))
                        {
                            string? id = idElement.GetString();
                            if (id != null && _pendingRequests.TryRemove(id, out var tcs))
                            {
                                tcs.SetResult(jsonResponse);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        Log($"⚠️ [WS交易专线] 异常断开，1秒后重新建立连接: {ex.Message}");
                        await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <summary>
        /// 2. 应用级双向主动心跳保活循环 (每 30 秒向币安发送 {"id": "ping_...", "method": "ping"} 保活续命)
        /// </summary>
        private async Task StartHeartbeatLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);

                    if (IsConnected)
                    {
                        long rtt = await SendPingHeartbeatAsync().ConfigureAwait(false);
                        if (rtt >= 0)
                        {
                            // 保持通道活跃，防死锁与路由静默挂断
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"⚠️ [WS心跳检测异常] {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 主动向币安 WebSocket 节点发送应用级心跳包
        /// </summary>
        public async Task<long> SendPingHeartbeatAsync()
        {
            if (_tradeWs == null || _tradeWs.State != WebSocketState.Open) return -1;

            string requestId = $"ping_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            var payload = new
            {
                id = requestId,
                method = "ping"
            };

            string jsonPayload = JsonSerializer.Serialize(payload);
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests.TryAdd(requestId, tcs);

            var bytes = Encoding.UTF8.GetBytes(jsonPayload);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                await _tradeWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);

                var timeoutTask = Task.Delay(3000);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);

                if (completedTask == timeoutTask)
                {
                    _pendingRequests.TryRemove(requestId, out _);
                    Log("⚠️ [WS心跳无响应] 币安未在 3 秒内响应 Ping 心跳包，判定网络死锁，触发自动打断与重连...");
                    _tradeWs.Abort(); // 强制打断，促使 MaintainTradeStreamAsync 立即触发自动重连！
                    return -1;
                }

                sw.Stop();
                long rtt = sw.ElapsedMilliseconds;
                return rtt;
            }
            catch (Exception ex)
            {
                _pendingRequests.TryRemove(requestId, out _);
                Log($"⚠️ [WS心跳发送失败] {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// 核心 0 延迟 WebSocket 下单方法 (支持 order.place 与 algoOrder.place)
        /// </summary>
        public async Task<string> PlaceOrderWsAsync(
            string symbol,
            string side,
            string type,
            decimal quantity,
            decimal? price = null,
            decimal? stopPrice = null,
            bool reduceOnly = false)
        {
            if (_tradeWs == null || _tradeWs.State != WebSocketState.Open)
            {
                throw new Exception("WebSocket 交易专线未就绪，请检查 API 凭证或网络连接");
            }

            string requestId = Guid.NewGuid().ToString("N");
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            bool isAlgoOrder = type == "STOP_MARKET" ||
                               type == "TAKE_PROFIT_MARKET" ||
                               type == "STOP" ||
                               type == "TAKE_PROFIT" ||
                               type == "TRAILING_STOP_MARKET";

            var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                { "apiKey", _apiKey },
                { "symbol", symbol },
                { "side", side },
                { "type", type },
                { "quantity", quantity.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                { "timestamp", timestamp.ToString() }
            };

            if (price.HasValue)
            {
                parameters.Add("price", price.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (stopPrice.HasValue)
            {
                string stopPriceKey = isAlgoOrder ? "triggerPrice" : "stopPrice";
                parameters.Add(stopPriceKey, stopPrice.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (reduceOnly)
            {
                parameters.Add("reduceOnly", "true");
            }

            if (type == "LIMIT" || type == "STOP" || type == "TAKE_PROFIT")
            {
                parameters.Add("timeInForce", "GTC");
            }

            if (isAlgoOrder)
            {
                parameters.Add("algoType", "CONDITIONAL");
                parameters.Add("workingType", "CONTRACT_PRICE");
            }

            var queryStr = string.Join("&", parameters.Select(kvp => $"{kvp.Key}={kvp.Value}"));
            string signature = GenerateSignature(queryStr, _apiSecret);
            parameters.Add("signature", signature);

            string wsMethod = isAlgoOrder ? "algoOrder.place" : "order.place";

            var payload = new
            {
                id = requestId,
                method = wsMethod,
                @params = parameters
            };

            string jsonPayload = JsonSerializer.Serialize(payload);

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests.TryAdd(requestId, tcs);

            var bytes = Encoding.UTF8.GetBytes(jsonPayload);
            await _tradeWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);

            Log($"🚀 [WS 0延迟下单路由: {wsMethod}] [{symbol}] {side} {type} | 数量: {quantity} | 触发价: {stopPrice} | 只减仓: {reduceOnly}");

            // 5秒超时等待
            var timeoutTask = Task.Delay(5000);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);

            if (completedTask == timeoutTask)
            {
                _pendingRequests.TryRemove(requestId, out _);
                throw new TimeoutException($"WebSocket 下单响应超时 (单号 id: {requestId})");
            }

            string responseJson = await tcs.Task.ConfigureAwait(false);

            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("status", out var statusElement) && statusElement.GetInt32() != 200)
            {
                if (doc.RootElement.TryGetProperty("error", out var errorElement))
                {
                    int errorCode = errorElement.GetProperty("code").GetInt32();
                    string errorMsg = errorElement.GetProperty("msg").GetString() ?? "未知名义错误";
                    throw new Exception($"币安 WS 拒单 [{errorCode}]: {errorMsg}");
                }
                throw new Exception($"币安 WS 拒单: 未知错误状态码 {statusElement.GetInt32()}");
            }

            return responseJson;
        }

        /// <summary>
        /// 通过 WebSocket 专线 (algoOrder.place) 下达全自动止损条件单 (STOP_MARKET)
        /// </summary>
        public async Task<string> SetStopLossMarketWsAsync(string symbol, string positionSide, decimal quantity, decimal stopPrice)
        {
            string exitSide = (positionSide.Trim().ToUpperInvariant() == "LONG" || positionSide.Trim().ToUpperInvariant() == "BUY") ? "SELL" : "BUY";
            return await PlaceOrderWsAsync(symbol, exitSide, "STOP_MARKET", quantity, stopPrice: stopPrice, reduceOnly: true).ConfigureAwait(false);
        }

        /// <summary>
        /// 通过 WebSocket 专线 (algoOrder.place) 下达全自动止盈条件单 (TAKE_PROFIT_MARKET)
        /// </summary>
        public async Task<string> SetTakeProfitMarketWsAsync(string symbol, string positionSide, decimal quantity, decimal takeProfitPrice)
        {
            string exitSide = (positionSide.Trim().ToUpperInvariant() == "LONG" || positionSide.Trim().ToUpperInvariant() == "BUY") ? "SELL" : "BUY";
            return await PlaceOrderWsAsync(symbol, exitSide, "TAKE_PROFIT_MARKET", quantity, stopPrice: takeProfitPrice, reduceOnly: true).ConfigureAwait(false);
        }

        private async Task LoadExchangeInfoAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync("https://fapi.binance.com/fapi/v1/exchangeInfo").ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                foreach (var symbolElement in doc.RootElement.GetProperty("symbols").EnumerateArray())
                {
                    string? symbol = symbolElement.GetProperty("symbol").GetString();
                    if (!string.IsNullOrEmpty(symbol))
                    {
                        int qtyPrecision = symbolElement.GetProperty("quantityPrecision").GetInt32();
                        int pricePrecision = symbolElement.GetProperty("pricePrecision").GetInt32();

                        _symbolPrecisions[symbol] = (qtyPrecision, pricePrecision);
                    }
                }
                Log($"✅ [WS规则库] 成功缓存 {_symbolPrecisions.Count} 个合约交易对的精度规则！");
            }
            catch (Exception ex)
            {
                Log($"⚠️ [WS规则库] 获取 ExchangeInfo 失败: {ex.Message}");
            }
        }

        private string GenerateSignature(string message, string secret)
        {
            var keyBytes = Encoding.UTF8.GetBytes(secret);
            using var hmac = new HMACSHA256(keyBytes);
            return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))).Replace("-", "").ToLowerInvariant();
        }

        private void Log(string msg)
        {
            OnLog?.Invoke(msg);
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _tradeWs?.Dispose();
            _httpClient.Dispose();
            _cts?.Dispose();
        }
    }
}
