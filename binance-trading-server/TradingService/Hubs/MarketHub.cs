using Microsoft.AspNetCore.SignalR;
using System;
using System.Threading.Tasks;
using TradingTerminal.Services;

namespace TradingTerminal.Hubs
{
    public class MarketHub : Hub
    {
        private readonly BinanceWebSocketService _wsService;

        public MarketHub(BinanceWebSocketService wsService)
        {
            _wsService = wsService;
        }

        // 前端发来订阅指令
        public async Task Subscribe(string stream)
        {
            // 🌟 1. 精准路由：把当前浏览器连接加入以 stream 命名的专属房间
            await Groups.AddToGroupAsync(Context.ConnectionId, stream);
            
            // 🌟 2. 通知网关：前端有一个人需要这个流，请增加引用计数
            await _wsService.SubscribeFrontendAsync(Context.ConnectionId, stream);
        }

        // 前端发来退订指令
        public async Task Unsubscribe(string stream)
        {
            // 退出专属房间
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, stream);
            
            // 通知网关扣减引用计数
            await _wsService.UnsubscribeFrontendAsync(Context.ConnectionId, stream);
        }

        // 🌟 极为重要：前端直接关网页（断开连接）时的清理动作
        public override async Task OnDisconnectedAsync(Exception exception)
        {
            // 通知网关：这个用户跑了，把他之前订阅的所有计数量全部扣除！
            await _wsService.RemoveFrontendClientAsync(Context.ConnectionId);
            
            await base.OnDisconnectedAsync(exception);
        }
    }
}