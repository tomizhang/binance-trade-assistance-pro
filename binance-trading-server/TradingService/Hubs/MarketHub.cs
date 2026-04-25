using Microsoft.AspNetCore.SignalR;
using TradingTerminal.Services;

namespace TradingTerminal.Hubs
{
    public class MarketHub : Hub
    {
        private readonly BinanceWebSocketService _wsService;
        public MarketHub(BinanceWebSocketService wsService) { _wsService = wsService; }

        public async Task Subscribe(string streamName) => await _wsService.SubscribeStreamAsync(streamName);
        public async Task Unsubscribe(string streamName) => await _wsService.UnsubscribeStreamAsync(streamName);
    }
}