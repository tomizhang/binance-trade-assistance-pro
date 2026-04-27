using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// 根据你的报错路径，命名空间应为 TradingService.BackgroundWorkers
namespace TradingService.BackgroundWorkers
{
    public class BinanceUserDataWorker : BackgroundService
    {
        private readonly ILogger<BinanceUserDataWorker> _logger;
        private readonly IServiceProvider _serviceProvider;

        // 🌟 核心排错点：必须是 IHubContext，绝对不能是 IHubClients
        private readonly IHubContext<AccountHub> _hubContext;

        public BinanceUserDataWorker(
            ILogger<BinanceUserDataWorker> logger,
            IServiceProvider serviceProvider,
            IHubContext<AccountHub> hubContext) // 注入的类型也必须严格一致
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _hubContext = hubContext;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 🌟 1. 加一句启动日志，证明进来了
            _logger.LogInformation("🔥 守护进程已触发，准备执行 ExecuteAsync...");

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var accountService = scope.ServiceProvider.GetRequiredService<BinanceAccountService>();

                // 🌟 2. 尝试获取门票（这里极容易因为 API 错误挂掉）
                _logger.LogInformation("正在向币安申请 ListenKey...");
                string listenKey = await accountService.CreateListenKeyAsync();
                _logger.LogInformation("🔑 已成功获取 ListenKey: {Key}", listenKey);

                var timer = new Timer(async _ => {
                    await accountService.KeepAliveListenKeyAsync();
                    _logger.LogInformation("⏳ ListenKey 已成功续命");
                }, null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri($"wss://fstream.binance.com/private/ws/{listenKey}"), stoppingToken);
                _logger.LogInformation("🟢 币安私有 WebSocket 连接成功！开始监听数据...");

                var buffer = new byte[1024 * 8];
                while (!stoppingToken.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);

                    // 可以把收到的原始数据打印出来看看
                    // _logger.LogInformation("收到账户数据: {Data}", message);

                    await _hubContext.Clients.All.SendAsync("ReceiveAccountUpdate", message, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                // 🌟 3. 如果死了，必须留下尸检报告
                _logger.LogError(ex, "💀 守护进程在运行中发生致命错误，已退出！错误信息: {Message}", ex.Message);
            }
        }
    }
}