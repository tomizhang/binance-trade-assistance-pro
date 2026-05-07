using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace TradingTerminal.Services
{
    /// <summary>
    /// 纯粹的交易执行引擎 (专职负责私有 WS 连接、签名、下单、以及监听仓位缓存)
    /// </summary>
    public class BinanceTradeWsService : BackgroundService
    {
        private readonly ILogger<BinanceTradeWsService> _logger;
        private readonly UserDataEventBus _userDataBus; // 🌟 注入事件总线
        private readonly string _apiKey;
        private readonly string _apiSecret;

        private ClientWebSocket _tradeWs = new ClientWebSocket();
        private readonly HttpClient _httpClient;

        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new();
        private readonly ConcurrentDictionary<string, (int QuantityPrecision, int PricePrecision)> _symbolPrecisions = new();

        // 本地仓位影子字典 (Key: Symbol, Value: 持仓数量)
        private readonly ConcurrentDictionary<string, decimal> _localPositions = new();

        public BinanceTradeWsService(
            IConfiguration config,
            ILogger<BinanceTradeWsService> logger,
            UserDataEventBus userDataBus) // 👈 构造函数注入
        {
            _logger = logger;
            _userDataBus = userDataBus;
            _apiKey = config["BinanceConfig:ApiKey"];
            _apiSecret = config["BinanceConfig:ApiSecret"];

            // 交易网关独立的 HTTP 客户端
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

            // 🌟 订阅大喇叭广播的原始用户数据
            // 注意：请确保你的 UserDataEventBus 中暴露了 OnRawUserDataReceived 事件
            _userDataBus.OnRawUserDataReceived += HandleUserDataMessage;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("⏳ [系统初始化] 正在阻塞加载交易规则，请稍候...");
            await LoadExchangeInfoAsync();
            _logger.LogInformation("✅ [系统初始化] 交易规则加载完毕，放行启动流程。");
            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🟢 [交易核心] 独立交易 WS 网关启动...");

            // 🌟 启动时同步拉取一次全量仓位，打底 (因为 WS 只有变动时才推送)
            await SyncInitialPositionsAsync();

            // 启动下单执行专线
            await MaintainTradeStreamAsync(stoppingToken);
        }

        // ==========================================
        // 🌟 仓位缓存与事件监听逻辑
        // ==========================================

        // 无延迟读取本地缓存
        public Task<bool> HasActivePositionAsync(string symbol)
        {
            if (_localPositions.TryGetValue(symbol.ToUpper(), out decimal amt))
            {
                return Task.FromResult(amt != 0);
            }
            return Task.FromResult(false);
        }

        public Task<List<string>> GetActivePositionSymbolsAsync()
        {
            var activeSymbols = _localPositions
                .Where(kvp => kvp.Value != 0)
                .Select(kvp => kvp.Key)
                .ToList();

            return Task.FromResult(activeSymbols);
        }

        // 接收 UserDataEventBus 传来的消息并解析仓位
        private void HandleUserDataMessage(string jsonMessage)
        {
            try
            {
                using var eventDoc = JsonDocument.Parse(jsonMessage);
                if (eventDoc.RootElement.TryGetProperty("e", out var eventType))
                {
                    string type = eventType.GetString();

                    // 我们只关心 ACCOUNT_UPDATE 事件 (仓位变化)
                    if (type == "ACCOUNT_UPDATE")
                    {
                        var updateData = eventDoc.RootElement.GetProperty("a");
                        if (updateData.TryGetProperty("P", out var positionsArray))
                        {
                            foreach (var pos in positionsArray.EnumerateArray())
                            {
                                string symbol = pos.GetProperty("s").GetString();
                                decimal amount = decimal.Parse(pos.GetProperty("pa").GetString(), System.Globalization.CultureInfo.InvariantCulture);

                                // 极其快速地更新本地内存
                                _localPositions[symbol] = amount;

                                if (amount != 0)
                                    _logger.LogInformation($"📡 [实盘仓位变更] {symbol} 当前持仓: {amount}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"❌ 解析账户变动事件失败: {ex.Message}");
            }
        }

        private async Task SyncInitialPositionsAsync()
        {
            try
            {
                _logger.LogInformation("📖 [仓位初始化] 正在拉取初始仓位数据...");
                string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                string queryString = $"timestamp={timestamp}";
                string signature = GenerateSignature(queryString, _apiSecret);
                string url = $"https://fapi.binance.com/fapi/v2/positionRisk?{queryString}&signature={signature}";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-MBX-APIKEY", _apiKey);

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                foreach (var position in doc.RootElement.EnumerateArray())
                {
                    string symbol = position.GetProperty("symbol").GetString();
                    decimal amt = decimal.Parse(position.GetProperty("positionAmt").GetString(), System.Globalization.CultureInfo.InvariantCulture);
                    _localPositions[symbol] = amt;
                }
                _logger.LogInformation($"✅ [仓位初始化] 成功建立本地仓位影子字典！");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ 初始仓位同步失败: {ex.Message}");
            }
        }

        // ==========================================
        // 下单核心执行专线
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

        public async Task<string> PlaceOrderWsAsync(
                    string symbol,
                    string side,
                    string type,
                    decimal quantity,
                    decimal? price = null,
                    decimal? stopPrice = null,
                    bool reduceOnly = false,
                    bool skipIfHasPosition = false)
        {
            if (_tradeWs.State != WebSocketState.Open) throw new Exception("WebSocket 交易专线未就绪，请稍后再试");

            // 🌟 风控拦截：零延迟阻断重复开单
            if (skipIfHasPosition)
            {
                bool hasPosition = await HasActivePositionAsync(symbol);
                if (hasPosition)
                {
                    _logger.LogWarning($"🛡️ [风控拦截] 发现 {symbol} 已存在活跃持仓，跳过本次 [{side} {type}] 开仓请求！");
                    return $"{{\"status\": 200, \"skipped\": true, \"msg\": \"Skipped {symbol} due to existing position\"}}";
                }
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
                parameters.Add("price", price.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (stopPrice.HasValue)
            {
                string stopPriceKey = isAlgoOrder ? "triggerPrice" : "stopPrice";
                parameters.Add(stopPriceKey, stopPrice.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (reduceOnly)
                parameters.Add("reduceOnly", "true");

            if (type == "LIMIT" || type == "STOP" || type == "TAKE_PROFIT")
                parameters.Add("timeInForce", "GTC");

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
            await _tradeWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

            _logger.LogInformation($"🚀 [WS下单路由: {wsMethod}] {symbol} {side} {type} 数量:{quantity} 价格:{price} 触发价:{stopPrice} 只减仓:{reduceOnly}");

            var timeoutTask = Task.Delay(5000);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _pendingRequests.TryRemove(requestId, out _);
                throw new TimeoutException("WS 下单超时，币安未在5秒内返回确认信息");
            }

            string responseJson = await tcs.Task;

            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("status", out var statusElement) && statusElement.GetInt32() != 200)
            {
                if (doc.RootElement.TryGetProperty("skipped", out var skippedElement) && skippedElement.GetBoolean() == true)
                {
                    return responseJson;
                }

                if (doc.RootElement.TryGetProperty("error", out var errorElement))
                {
                    int errorCode = errorElement.GetProperty("code").GetInt32();
                    string errorMsg = errorElement.GetProperty("msg").GetString();
                    throw new Exception($"币安拒单 [{errorCode}]: {errorMsg}");
                }
                throw new Exception($"币安拒单: 未知错误状态码 {statusElement.GetInt32()}");
            }

            return responseJson;
        }

        public async Task CancelAllOpenOrdersAsync(string symbol)
        {
            var endpoints = new[]
            {
                "/fapi/v1/allOpenOrders",
                "/fapi/v1/algoOpenOrders"
            };

            foreach (var endpoint in endpoints)
            {
                try
                {
                    string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                    string queryString = $"symbol={symbol.ToUpper()}&timestamp={timestamp}";
                    string signature = GenerateSignature(queryString, _apiSecret);

                    string url = $"https://fapi.binance.com{endpoint}?{queryString}&signature={signature}";

                    var request = new HttpRequestMessage(HttpMethod.Delete, url);
                    request.Headers.Add("X-MBX-APIKEY", _apiKey);

                    var response = await _httpClient.SendAsync(request);
                    string json = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("code", out var errCode))
                        {
                            int code = errCode.GetInt32();
                            if (code != -2011)
                            {
                                _logger.LogError($"❌ [币安拒单] 撤单失败！端点 {endpoint} 返回: {json}");
                            }
                            else
                            {
                                _logger.LogDebug($"ℹ️ [清理记录] {endpoint} 池子里没有需要撤的单子。");
                            }
                        }
                    }
                    else
                    {
                        _logger.LogInformation($"✅ [撤单成功] 端点 {endpoint} 成功清理！回执: {json}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"💥 [网络致命异常] 无法连接到 {endpoint}，请检查配置！详细错误: {ex.Message}");
                }
            }
        }

        private async Task LoadExchangeInfoAsync()
        {
            try
            {
                _logger.LogInformation("📖 [规则加载] 正在向币安拉取全网交易对精度规则...");
                var response = await _httpClient.GetAsync("https://fapi.binance.com/fapi/v1/exchangeInfo");
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                foreach (var symbolElement in doc.RootElement.GetProperty("symbols").EnumerateArray())
                {
                    string symbol = symbolElement.GetProperty("symbol").GetString();
                    int qtyPrecision = symbolElement.GetProperty("quantityPrecision").GetInt32();
                    int pricePrecision = symbolElement.GetProperty("pricePrecision").GetInt32();

                    _symbolPrecisions[symbol] = (qtyPrecision, pricePrecision);
                }
                _logger.LogInformation($"✅ [规则加载] 成功缓存 {_symbolPrecisions.Count} 个交易对的精度规则！");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ [规则加载] 获取 ExchangeInfo 失败: {ex.Message}");
            }
        }

        public decimal FormatQuantity(string symbol, decimal rawQty)
        {
            if (_symbolPrecisions.TryGetValue(symbol.ToUpper(), out var precision))
            {
                return Math.Round(rawQty, precision.QuantityPrecision, MidpointRounding.ToZero);
            }
            return Math.Round(rawQty, 0, MidpointRounding.ToZero);
        }

        public decimal FormatPrice(string symbol, decimal rawPrice)
        {
            if (_symbolPrecisions.TryGetValue(symbol.ToUpper(), out var precision))
            {
                return Math.Round(rawPrice, precision.PricePrecision, MidpointRounding.AwayFromZero);
            }
            return Math.Round(rawPrice, 2, MidpointRounding.AwayFromZero);
        }

        public async Task<decimal> ConvertUsdtToQuantityAsync(string symbol, decimal usdtMargin, decimal leverage)
        {
            string url = $"https://fapi.binance.com/fapi/v1/ticker/price?symbol={symbol.ToUpper()}";
            var response = await _httpClient.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            decimal currentPrice = decimal.Parse(doc.RootElement.GetProperty("price").GetString(), System.Globalization.CultureInfo.InvariantCulture);
            return (usdtMargin * leverage) / currentPrice;
        }

        public async Task<string> OpenMarketPositionAsync(string symbol, string side, decimal quantity, bool skipIfHasPosition = false) =>
            await PlaceOrderWsAsync(symbol, side, "MARKET", quantity, skipIfHasPosition: skipIfHasPosition);

        public async Task<string> OpenLimitPositionAsync(string symbol, string side, decimal quantity, decimal price, bool skipIfHasPosition = false) =>
            await PlaceOrderWsAsync(symbol, side, "LIMIT", quantity, price: price, skipIfHasPosition: skipIfHasPosition);

        public async Task<string> SetStopLossMarketAsync(string symbol, string positionSide, decimal quantity, decimal stopPrice) =>
            await PlaceOrderWsAsync(symbol, positionSide.ToUpper() == "LONG" ? "SELL" : "BUY", "STOP_MARKET", quantity, stopPrice: stopPrice, reduceOnly: true);

        public async Task<string> SetTakeProfitMarketAsync(string symbol, string positionSide, decimal quantity, decimal takeProfitPrice) =>
            await PlaceOrderWsAsync(symbol, positionSide.ToUpper() == "LONG" ? "SELL" : "BUY", "TAKE_PROFIT_MARKET", quantity, stopPrice: takeProfitPrice, reduceOnly: true);

        private string GenerateSignature(string message, string secret)
        {
            var keyBytes = Encoding.UTF8.GetBytes(secret);
            using var hmac = new HMACSHA256(keyBytes);
            return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))).Replace("-", "").ToLower();
        }

        public override void Dispose()
        {
            if (_userDataBus != null)
            {
                // 释放事件订阅，防止内存泄漏
                _userDataBus.OnRawUserDataReceived -= HandleUserDataMessage;
            }
            _tradeWs?.Dispose();
            _httpClient?.Dispose();
            base.Dispose();
        }
    }
}