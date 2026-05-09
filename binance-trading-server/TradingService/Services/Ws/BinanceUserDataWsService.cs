using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingTerminal.Hubs;

namespace TradingTerminal.Services
{
    /// <summary>
    /// 账户私有数据中枢：实现零延迟资产同步，支持提取不含盈亏的纯净余额
    /// </summary>
    public class BinanceUserDataWsService : BackgroundService
    {
        private readonly ILogger<BinanceUserDataWsService> _logger;
        private readonly IHubContext<MarketHub> _hubContext;
        private readonly UserDataEventBus _userDataBus;
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private readonly HttpClient _httpClient;

        private string _currentListenKey;
        private Timer _keepAliveTimer;

        // ==========================================
        // 🌟 零延迟内存资产缓存
        // ==========================================

        /// <summary>
        /// 1. 包含盈亏的余额 (对应 WS 中的 cw)
        /// </summary>
        public decimal CachedUsdtBalanceWithPnL { get; private set; } = 0m;

        /// <summary>
        /// 2. 🌟 纯净钱包余额 (对应 WS 中的 wb)：除去所有未实现盈亏。
        /// 这是你要求的“除去盈亏”的底账。
        /// </summary>
        public decimal CachedPureWalletBalance { get; private set; } = 0m;

        public BinanceUserDataWsService(
            ILogger<BinanceUserDataWsService> logger,
            IConfiguration config,
            IHubContext<MarketHub> hubContext,
            UserDataEventBus userDataBus)
        {
            _logger = logger;
            _hubContext = hubContext;
            _userDataBus = userDataBus;
            _apiKey = config["BinanceConfig:ApiKey"];
            _apiSecret = config["BinanceConfig:ApiSecret"];

            SocketsHttpHandler handler = new SocketsHttpHandler
            {
#if DEBUG
                Proxy = new WebProxy("socks5://127.0.0.1:10808"),
#endif
                UseProxy = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            _httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://fapi.binance.com") };
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🛡️ [资产同步] 启动零延迟监控...");

            // 冷启动先同步一次绝对值
            await SyncInitialBalanceAsync();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _currentListenKey = await CreateListenKeyAsync();
                    _keepAliveTimer?.Dispose();
                    _keepAliveTimer = new Timer(async _ => await KeepAliveListenKeyAsync(), null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

                    using var ws = new ClientWebSocket();
#if DEBUG
                    ws.Options.Proxy = new WebProxy("socks5://127.0.0.1:10808");
#endif
                    await ws.ConnectAsync(new Uri($"wss://fstream.binance.com/private/ws/{_currentListenKey}"), stoppingToken);

                    var buffer = new byte[1024 * 16];

                    while (ws.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken);
                        if (result.MessageType == WebSocketMessageType.Close) break;

                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        _userDataBus.PublishRawUserData(message);

                        // 🌟 热更新逻辑：解析数据流并提取 wb
                        ProcessUserDataMessage(message);

                        await _hubContext.Clients.All.SendAsync("ReceiveAccountUpdate", message, stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"⚠️ [资产同步] WS 断开: {ex.Message}");
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }

        private void ProcessUserDataMessage(string jsonMessage)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonMessage);
                var root = doc.RootElement;

                if (root.TryGetProperty("e", out var eventTypeElement))
                {
                    string eventType = eventTypeElement.GetString();

                    if (eventType == "ACCOUNT_UPDATE")
                    {
                        var updateData = root.GetProperty("a");
                        if (updateData.TryGetProperty("B", out var balancesArray))
                        {
                            foreach (var bal in balancesArray.EnumerateArray())
                            {
                                if (bal.GetProperty("a").GetString() == "USDT")
                                {
                                    // 🌟 核心提取
                                    // wb (Wallet Balance): 除去盈亏的纯钱包金额
                                    if (bal.TryGetProperty("wb", out var wbElement))
                                    {
                                        CachedPureWalletBalance = decimal.Parse(wbElement.GetString(), System.Globalization.CultureInfo.InvariantCulture);
                                    }

                                    // cw (Cross Wallet Balance): 包含盈亏的余额
                                    if (bal.TryGetProperty("cw", out var cwElement))
                                    {
                                        CachedUsdtBalanceWithPnL = decimal.Parse(cwElement.GetString(), System.Globalization.CultureInfo.InvariantCulture);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ 解析资产数据失败: {ex.Message}");
            }
        }

        // 辅助方法：冷启动校准
        private async Task SyncInitialBalanceAsync()
        {
            try
            {
                string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                string queryString = $"timestamp={timestamp}";
                string signature = GenerateSignature(queryString, _apiSecret);

                var request = new HttpRequestMessage(HttpMethod.Get, $"/fapi/v2/balance?{queryString}&signature={signature}");
                request.Headers.Add("X-MBX-APIKEY", _apiKey);

                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode) return;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                foreach (var assetData in doc.RootElement.EnumerateArray())
                {
                    if (assetData.GetProperty("asset").GetString() == "USDT")
                    {
                        // 初始校准：balance 是不含盈亏的钱包余额
                        CachedPureWalletBalance = decimal.Parse(assetData.GetProperty("balance").GetString(), System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ 初始资产同步失败: {ex.Message}");
            }
        }

        private string GenerateSignature(string message, string secret)
        {
            var keyBytes = Encoding.UTF8.GetBytes(secret);
            using var hmac = new HMACSHA256(keyBytes);
            return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))).Replace("-", "").ToLower();
        }

        // ListenKey 生命周期管理逻辑保持不变...
        private async Task<string> CreateListenKeyAsync()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/fapi/v1/listenKey");
            request.Headers.Add("X-MBX-APIKEY", _apiKey);
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("listenKey").GetString();
        }

        private async Task KeepAliveListenKeyAsync()
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Put, "/fapi/v1/listenKey");
                request.Headers.Add("X-MBX-APIKEY", _apiKey);
                await _httpClient.SendAsync(request);
            }
            catch { }
        }

        public override void Dispose()
        {
            _keepAliveTimer?.Dispose();
            base.Dispose();
        }
    }
}