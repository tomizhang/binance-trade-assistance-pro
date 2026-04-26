using Microsoft.AspNetCore.SignalR;
using System.Net.WebSockets;
using System.Text;
using TradingTerminal.Hubs;

namespace TradingTerminal.Services
{
    public class BinanceWebSocketService : BackgroundService
    {
        private readonly IHubContext<MarketHub> _hubContext;
        private readonly ILogger<BinanceWebSocketService> _logger;
        private ClientWebSocket _publicWs = new ClientWebSocket();

        public BinanceWebSocketService(IHubContext<MarketHub> hubContext, ILogger<BinanceWebSocketService> logger)
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
                    _publicWs = new ClientWebSocket();
                    // 🌟 仅连接公共组合流 (Ticker 和 标记价格)
                    //await _publicWs.ConnectAsync(new Uri("wss://fstream.binance.com/stream?streams=!miniTicker@arr/!markPrice@arr@1s"), stoppingToken);
                    await _publicWs.ConnectAsync(new Uri("wss://fstream.binance.com/market/stream?streams=!miniTicker@arr/!markPrice@arr@1s"), stoppingToken);
                    _logger.LogInformation("✅ [C# 数据中枢] 已连接币安公共流");

                    var buffer = new byte[1024 * 128];
                    while (_publicWs.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                    {
                        var result = await _publicWs.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken);
                        if (result.MessageType == WebSocketMessageType.Close) break;

                        var rawJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        // 🌟 原封不动转发给前端
                        await _hubContext.Clients.All.SendAsync("ReceiveMarketData", rawJson, stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"公共流断开重连中: {ex.Message}");
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }

        // 供前端动态订阅 K 线
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
    }
}