using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Linq;
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
    public class PositionDetails
    {
        public string Symbol { get; set; }
        public decimal Quantity { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal UnrealizedPnL { get; set; }
        public decimal Leverage { get; set; } = 1m;
    }

    /// <summary>
    /// 账户私有数据中枢：初始化获取全量仓位/盈亏，WS被动热更新，外加 5 秒 HTTP 静默看门狗兜底
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

        // 🌟 新增：5 秒静默看门狗
        private Timer _idleSyncTimer;
        private int _isSyncing = 0; // 防并发锁

        public decimal CachedUsdtBalanceWithPnL { get; private set; } = 0m;
        public decimal CachedPureWalletBalance { get; private set; } = 0m;
        public decimal CachedAvailableBalance { get; private set; } = 0m;

        public ConcurrentDictionary<string, PositionDetails> ActivePositions { get; } = new(StringComparer.OrdinalIgnoreCase);

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
                Proxy = new System.Net.WebProxy("socks5://127.0.0.1:10808"),
#endif
                UseProxy = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            _httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://fapi.binance.com") };
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🛡️ [资产同步] 正在启动私有数据流引擎...");

            // 1. 启动时获取一次高调的底账 (打印日志)
            await SyncAccountAndPositionsAsync(isSilent: false);

            // 🌟 2. 启动 5 秒静默看门狗定时器
            _idleSyncTimer = new Timer(OnIdleSyncTimerTriggered, null, 5000, Timeout.Infinite);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _currentListenKey = await CreateListenKeyAsync();
                    _keepAliveTimer?.Dispose();
                    _keepAliveTimer = new Timer(async _ => await KeepAliveListenKeyAsync(), null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

                    using var streamWs = new ClientWebSocket();
#if DEBUG
                    streamWs.Options.Proxy = new System.Net.WebProxy("socks5://127.0.0.1:10808");
#endif
                    await streamWs.ConnectAsync(new Uri($"wss://fstream.binance.com/private/ws/{_currentListenKey}"), stoppingToken);
                    _logger.LogInformation("🟢 [监听专线] 账户数据流连接成功，开启被动热更新与看门狗！");

                    var buffer = new byte[1024 * 16];

                    while (streamWs.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                    {
                        using var ms = new System.IO.MemoryStream();
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await streamWs.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            ms.Write(buffer, 0, result.Count);
                        }
                        while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close) break;

                        var message = Encoding.UTF8.GetString(ms.ToArray());
                        _userDataBus.PublishRawUserData(message);

                        ProcessUserDataMessage(message);

                        await _hubContext.Clients.All.SendAsync("ReceiveAccountUpdate", message, stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"⚠️ [监听专线] 断开，5秒后重连: {ex.Message}");
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }

        public void UpdateLeverage(string symbol, decimal leverage)
        {
            if (leverage <= 0) return;
            ActivePositions.AddOrUpdate(symbol,
                new PositionDetails { Symbol = symbol, Leverage = leverage },
                (k, old) => { old.Leverage = leverage; return old; });

            RecalculateAvailableBalance();
        }

        // ==========================================
        // 🌟 5 秒看门狗触发方法
        // ==========================================
        private void OnIdleSyncTimerTriggered(object state)
        {
            // 使用 Interlocked 防止网络卡顿时并发执行
            if (Interlocked.CompareExchange(ref _isSyncing, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 强制走一遍 HTTP 获取，isSilent=true 不刷屏
                        await SyncAccountAndPositionsAsync(isSilent: true);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _isSyncing, 0);
                        // HTTP 执行完后，重新挂载下一个 5 秒定时
                        _idleSyncTimer?.Change(1000, Timeout.Infinite);
                    }
                });
            }
        }

        // ==========================================
        // 🌟 HTTP 账户全量同步 (增加静默参数)
        // ==========================================
        private async Task SyncAccountAndPositionsAsync(bool isSilent)
        {
            try
            {
                string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                string queryString = $"timestamp={timestamp}";
                string signature = GenerateSignature(queryString, _apiSecret);

                var request = new HttpRequestMessage(HttpMethod.Get, $"/fapi/v2/account?{queryString}&signature={signature}");
                request.Headers.Add("X-MBX-APIKEY", _apiKey);

                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    if (!isSilent) _logger.LogWarning($"⚠️ HTTP 账户同步失败，状态码: {response.StatusCode}");
                    return;
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                foreach (var assetData in root.GetProperty("assets").EnumerateArray())
                {
                    if (assetData.GetProperty("asset").GetString() == "USDT")
                    {
                        CachedPureWalletBalance = decimal.Parse(assetData.GetProperty("walletBalance").GetString(), System.Globalization.CultureInfo.InvariantCulture);
                        CachedUsdtBalanceWithPnL = decimal.Parse(assetData.GetProperty("crossWalletBalance").GetString(), System.Globalization.CultureInfo.InvariantCulture);
                        CachedAvailableBalance = decimal.Parse(assetData.GetProperty("availableBalance").GetString(), System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    }
                }

                foreach (var pos in root.GetProperty("positions").EnumerateArray())
                {
                    string sym = pos.GetProperty("symbol").GetString();
                    decimal amt = decimal.Parse(pos.GetProperty("positionAmt").GetString(), System.Globalization.CultureInfo.InvariantCulture);
                    decimal lev = decimal.Parse(pos.GetProperty("leverage").GetString(), System.Globalization.CultureInfo.InvariantCulture);
                    decimal ep = decimal.Parse(pos.GetProperty("entryPrice").GetString(), System.Globalization.CultureInfo.InvariantCulture);
                    decimal up = decimal.Parse(pos.GetProperty("unrealizedProfit").GetString(), System.Globalization.CultureInfo.InvariantCulture);

                    ActivePositions[sym] = new PositionDetails
                    {
                        Symbol = sym,
                        Quantity = amt,
                        EntryPrice = ep,
                        UnrealizedPnL = up,
                        Leverage = lev > 0 ? lev : 1m
                    };
                }

                if (!isSilent)
                {
                    _logger.LogInformation($"🚀 [账户同步] 成功！纯净余额: {CachedPureWalletBalance:F2}, 可用余额: {CachedAvailableBalance:F2}, 监控 {ActivePositions.Count} 个仓位。");
                }
            }
            catch (Exception ex)
            {
                if (!isSilent) _logger.LogError($"❌ HTTP 账户同步异常: {ex.Message}");
            }
        }

        private void ProcessUserDataMessage(string jsonMessage)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonMessage);
                var root = doc.RootElement;

                if (!root.TryGetProperty("e", out var eventTypeElement)) return;
                string eventType = eventTypeElement.GetString();

                if (eventType == "ORDER_TRADE_UPDATE")
                {
                    var orderData = root.GetProperty("o");
                    var tradeEvent = new OrderTradeUpdateEvent
                    {
                        Symbol = orderData.GetProperty("s").GetString(),
                        OrderStatus = orderData.GetProperty("X").GetString(),
                        OrderType = orderData.GetProperty("o").GetString(),
                        RealizedPnl = decimal.Parse(orderData.GetProperty("rp").GetString(), System.Globalization.CultureInfo.InvariantCulture)
                    };
                    _userDataBus.PublishOrderTradeUpdate(tradeEvent);
                }
                else if (eventType == "ACCOUNT_UPDATE")
                {
                    // 🌟 收到账户更新推送，重置 5 秒看门狗！
                    // 意思是：既然有活的数据来，这 5 秒内就不需要再发 HTTP 请求了
                    _idleSyncTimer?.Change(5000, Timeout.Infinite);

                    var updateData = root.GetProperty("a");

                    if (updateData.TryGetProperty("B", out var balancesArray))
                    {
                        foreach (var bal in balancesArray.EnumerateArray())
                        {
                            if (bal.GetProperty("a").GetString() == "USDT")
                            {
                                if (bal.TryGetProperty("wb", out var wbElement))
                                    CachedPureWalletBalance = decimal.Parse(wbElement.GetString(), System.Globalization.CultureInfo.InvariantCulture);

                                if (bal.TryGetProperty("cw", out var cwElement))
                                    CachedUsdtBalanceWithPnL = decimal.Parse(cwElement.GetString(), System.Globalization.CultureInfo.InvariantCulture);
                            }
                        }
                    }

                    if (updateData.TryGetProperty("P", out var positionsArray))
                    {
                        foreach (var pos in positionsArray.EnumerateArray())
                        {
                            string sym = pos.GetProperty("s").GetString();
                            decimal amt = decimal.Parse(pos.GetProperty("pa").GetString(), System.Globalization.CultureInfo.InvariantCulture);
                            decimal ep = decimal.Parse(pos.GetProperty("ep").GetString(), System.Globalization.CultureInfo.InvariantCulture);
                            decimal up = decimal.Parse(pos.GetProperty("up").GetString(), System.Globalization.CultureInfo.InvariantCulture);

                            ActivePositions.AddOrUpdate(sym,
                                new PositionDetails { Symbol = sym, Quantity = amt, EntryPrice = ep, UnrealizedPnL = up, Leverage = 20m },
                                (k, old) => {
                                    old.Quantity = amt;
                                    old.EntryPrice = ep;
                                    old.UnrealizedPnL = up;
                                    return old;
                                });
                        }
                    }

                    RecalculateAvailableBalance();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ 解析资产数据失败: {ex.Message}");
            }
        }

        private void RecalculateAvailableBalance()
        {
            decimal totalUsedMargin = 0m;
            foreach (var p in ActivePositions.Values)
            {
                if (p.Quantity != 0)
                {
                    totalUsedMargin += (Math.Abs(p.Quantity) * p.EntryPrice) / p.Leverage;
                }
            }
            decimal newAvailable = CachedUsdtBalanceWithPnL - totalUsedMargin;
            CachedAvailableBalance = newAvailable > 0 ? newAvailable * 0.995m : 0m;
        }

        private string GenerateSignature(string message, string secret)
        {
            var keyBytes = Encoding.UTF8.GetBytes(secret);
            using var hmac = new HMACSHA256(keyBytes);
            return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))).Replace("-", "").ToLower();
        }

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
            _idleSyncTimer?.Dispose();
            _keepAliveTimer?.Dispose();
            base.Dispose();
        }
    }
}