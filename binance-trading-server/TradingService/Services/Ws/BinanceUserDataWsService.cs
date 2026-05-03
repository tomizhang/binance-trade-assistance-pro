using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingTerminal.Hubs; // 确保这里指向你正确的 Hub 命名空间

namespace TradingTerminal.Services
{
    /// <summary>
    /// 账户私有数据中枢：专职负责 ListenKey 维护、账户变动推送、盈亏风控上报
    /// </summary>
    public class BinanceUserDataWsService : BackgroundService
    {
        private readonly ILogger<BinanceUserDataWsService> _logger;
        private readonly IHubContext<MarketHub> _hubContext;
        private readonly UserDataEventBus _userDataBus; // 🌟 换成事件总线大喇叭
        private readonly string _apiKey;
        private readonly HttpClient _httpClient;

        private string _currentListenKey;
        private Timer _keepAliveTimer;

        public BinanceUserDataWsService(
            ILogger<BinanceUserDataWsService> logger,
            IConfiguration config,
            IHubContext<MarketHub> hubContext,
            UserDataEventBus userDataBus) // 👈 注入进来
        {
            _logger = logger;
            _hubContext = hubContext;
            _userDataBus = userDataBus;
            _apiKey = config["BinanceConfig:ApiKey"];

            // 独立配置的高性能 HttpClient
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
            _httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://fapi.binance.com") };
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🛡️ [私有数据流] 守护进程启动，准备对接币安账户系统...");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 1. 获取门票 (ListenKey)
                    _currentListenKey = await CreateListenKeyAsync();
                    _logger.LogInformation($"🔑 [私有数据流] 成功获取 ListenKey，准备连接 WS...");

                    // 2. 启动续命定时器 (每 30 分钟续期一次)
                    _keepAliveTimer?.Dispose();
                    _keepAliveTimer = new Timer(async _ => await KeepAliveListenKeyAsync(), null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

                    // 3. 建立 WebSocket 连接
                    using var ws = new ClientWebSocket();
#if DEBUG
                    ws.Options.Proxy = new WebProxy("socks5://127.0.0.1:10808");
#endif
                    ws.Options.KeepAliveInterval = TimeSpan.FromMinutes(2);

                    await ws.ConnectAsync(new Uri($"wss://fstream.binance.com/private/ws/{_currentListenKey}"), stoppingToken);
                    _logger.LogInformation("🟢 [私有数据流] WS 连接成功！开始监听账户变化...");

                    var buffer = new byte[1024 * 16]; // 16KB 缓冲区

                    while (ws.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken);
                        if (result.MessageType == WebSocketMessageType.Close) break;

                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);

                        // 🌟 1. 广播原始数据
                        _userDataBus.PublishRawUserData(message);

                        // 🌟 2. 解析并广播订单实体事件
                        ProcessUserDataMessage(message);

                        // 将原始数据原样透传给前端 (让 Vue 更新余额、持仓状态等)
                        await _hubContext.Clients.All.SendAsync("ReceiveAccountUpdate", message, stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"⚠️ [私有数据流] 连接断开，5秒后尝试重建: {ex.Message}");
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }

        // ==========================================
        // 🌟 盈亏与风控解析引擎
        // ==========================================
        private void ProcessUserDataMessage(string jsonMessage)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonMessage);
                var root = doc.RootElement;

                if (root.TryGetProperty("e", out var eventTypeElement))
                {
                    string eventType = eventTypeElement.GetString();

                    if (eventType == "ORDER_TRADE_UPDATE")
                    {
                        var orderData = root.GetProperty("o");

                        // 组装标准化事件载体
                        var tradeEvent = new OrderTradeUpdateEvent
                        {
                            Symbol = orderData.GetProperty("s").GetString(),
                            OrderStatus = orderData.GetProperty("X").GetString(),
                            OrderType = orderData.GetProperty("o").GetString(),
                            ClientOrderId = orderData.GetProperty("c").GetString(),
                            RealizedPnl = decimal.Parse(orderData.GetProperty("rp").GetString(), System.Globalization.CultureInfo.InvariantCulture),
                            Commission = decimal.Parse(orderData.GetProperty("n").GetString(), System.Globalization.CultureInfo.InvariantCulture)
                        };

                        // 🌟 大喇叭广播：订单状态变了！风控、统计模块你们自己拿去用吧！
                        _userDataBus.PublishOrderTradeUpdate(tradeEvent);
                    }
                    else if (eventType == "MARGIN_CALL")
                    {
                        _logger.LogCritical("🚨 [账户警告] 收到追加保证金通知！");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ 解析私有数据失败: {ex.Message}");
            }
        }

        // ==========================================
        // 🎫 ListenKey 生命周期管理
        // ==========================================
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

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("⏳ [私有数据流] ListenKey 续命成功");
                }
                else
                {
                    _logger.LogWarning($"⚠️ [私有数据流] ListenKey 续命失败，将在下一次外层循环重新申请。");
                }
            }
            catch { /* 忽略网络波动异常，等待下一次续命 */ }
        }

        public override void Dispose()
        {
            _keepAliveTimer?.Dispose();
            base.Dispose();
        }
    }
}