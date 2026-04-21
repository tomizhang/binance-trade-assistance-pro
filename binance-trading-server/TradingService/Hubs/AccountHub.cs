// Hubs/AccountHub.cs
using Microsoft.AspNetCore.SignalR;

public class AccountHub : Hub 
{
    // 这里可以留空，我们主要在后台 Worker 中调用 HubContext 推送
}