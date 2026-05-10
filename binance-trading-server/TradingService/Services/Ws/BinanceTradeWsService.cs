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
    /// 纯粹的交易执行引擎 (专职负责私有 WS 连接、签名、下单、资金核算、精准撤单)
    /// </summary>
    public class BinanceTradeWsService : BackgroundService
    {
        private readonly ILogger<BinanceTradeWsService> _logger;
        private readonly string _apiKey;
        private readonly string _apiSecret;

        private ClientWebSocket _tradeWs = new ClientWebSocket();
        private readonly HttpClient _httpClient;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new();
        private readonly ConcurrentDictionary<string, (int QuantityPrecision, int PricePrecision)> _symbolPrecisions = new();

        public BinanceTradeWsService(IConfiguration config, ILogger<BinanceTradeWsService> logger)
        {
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
            await MaintainTradeStreamAsync(stoppingToken);
        }

        public async Task<List<string>> GetActivePositionSymbolsAsync()
        {
            var activeSymbols = new List<string>();
            try
            {
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
                    decimal amt = decimal.Parse(position.GetProperty("positionAmt").GetString(), System.Globalization.CultureInfo.InvariantCulture);
                    if (amt != 0)
                    {
                        activeSymbols.Add(position.GetProperty("symbol").GetString());
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ 获取远端持仓失败: {ex.Message}");
            }
            return activeSymbols;
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
                    await Task.Delay(1000, stoppingToken);
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
                    bool reduceOnly = false)
        {
            if (_tradeWs.State != WebSocketState.Open) throw new Exception("WebSocket 交易专线未就绪，请稍后再试");

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

            var timeoutTask = Task.Delay(100);
            var completedTask = await Task.WhenAny(tcs.Task);

            //if (completedTask == timeoutTask)
            //{
            //    _pendingRequests.TryRemove(requestId, out _);
            //    throw new TimeoutException("WS 下单超时，币安未在5秒内返回确认信息");
            //}

            string responseJson = await tcs.Task;

            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("status", out var statusElement) && statusElement.GetInt32() != 200)
            {
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
            var endpoints = new[] { "/fapi/v1/allOpenOrders", "/fapi/v1/algoOpenOrders" };

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
                        if (doc.RootElement.TryGetProperty("code", out var errCode) && errCode.GetInt32() != -2011)
                        {
                            _logger.LogError($"❌ [币安拒单] 撤单失败！端点 {endpoint} 返回: {json}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"💥 [网络异常] 无法连接到 {endpoint}，详细错误: {ex.Message}");
                }
            }
        }

        // ==========================================
        // 🌟 战场清理：精准狙击，只撤止损保留止盈
        // ==========================================
        public async Task CancelStopLossOnlyAsync(string symbol)
        {
            try
            {
                string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                string queryString = $"symbol={symbol.ToUpper()}&timestamp={timestamp}";
                string signature = GenerateSignature(queryString, _apiSecret);

                // 1. 查询该币种当前所有的挂单
                string url = $"https://fapi.binance.com/fapi/v1/openOrders?{queryString}&signature={signature}";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-MBX-APIKEY", _apiKey);

                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode) return;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                // 2. 遍历找出止损单进行精准撤销
                foreach (var order in doc.RootElement.EnumerateArray())
                {
                    string type = order.GetProperty("type").GetString();

                    // 币安的止损市价单通常是 STOP_MARKET 或 STOP
                    if (type == "STOP_MARKET" || type == "STOP")
                    {
                        long orderId = order.GetProperty("orderId").GetInt64();
                        await CancelSingleOrderAsync(symbol, orderId);
                        _logger.LogInformation($"🎯 [精准撤单] 成功撤销 {symbol} 的原止损单 (ID: {orderId})，止盈单已安全保留！");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ 提取或撤销原止损单失败: {ex.Message}");
            }
        }

        // 底层：根据订单 ID 撤销单一订单
        private async Task CancelSingleOrderAsync(string symbol, long orderId)
        {
            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            string queryString = $"symbol={symbol.ToUpper()}&orderId={orderId}&timestamp={timestamp}";
            string signature = GenerateSignature(queryString, _apiSecret);

            string url = $"https://fapi.binance.com/fapi/v1/order?{queryString}&signature={signature}";
            var request = new HttpRequestMessage(HttpMethod.Delete, url);
            request.Headers.Add("X-MBX-APIKEY", _apiKey);
            await _httpClient.SendAsync(request);
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

        public async Task<string> OpenMarketPositionAsync(string symbol, string side, decimal quantity) =>
            await PlaceOrderWsAsync(symbol, side, "MARKET", quantity);

        public async Task<string> OpenLimitPositionAsync(string symbol, string side, decimal quantity, decimal price) =>
            await PlaceOrderWsAsync(symbol, side, "LIMIT", quantity, price: price);

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
    }
}