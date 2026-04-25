using Microsoft.AspNetCore.SignalR;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TradingTerminal.Hubs;

namespace TradingTerminal.Services
{
    public class BinanceDataForwarderService : BackgroundService
    {
        private readonly IHubContext<MarketHub> _hubContext;
        private readonly ILogger<BinanceDataForwarderService> _logger;

        public BinanceDataForwarderService(IHubContext<MarketHub> hubContext, ILogger<BinanceDataForwarderService> logger)
        {
            _hubContext = hubContext;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var ws = new ClientWebSocket();
                    // 连接币安组合流
                    await ws.ConnectAsync(new Uri("wss://fstream.binance.com/stream?streams=!miniTicker@arr/!markPrice@arr@1s"), stoppingToken);
                    _logger.LogInformation("✅ 已连接到币安 WebSocket");

                    var buffer = new byte[1024 * 64]; // 64KB 缓冲区

                    while (ws.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken);
                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            var rawJson = Encoding.UTF8.GetString(buffer, 0, result.Count);

                            // 🌟 核心：将收到的原始 JSON 直接转发给所有连接了 SignalR 的前端客户端
                            // 你也可以在这里用 System.Text.Json 进行反序列化，清洗成 DTO 后再转发
                            await _hubContext.Clients.All.SendAsync("ReceiveMarketData", rawJson, cancellationToken: stoppingToken);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"WebSocket 异常断开，5秒后重连: {ex.Message}");
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }
    }
}