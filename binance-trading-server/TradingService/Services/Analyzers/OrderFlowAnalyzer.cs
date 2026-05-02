using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingTerminal.Hubs;
using TradingTerminal.Models;

namespace TradingTerminal.Services
{
    // 🌟 继承 BackgroundService，让它随 ASP.NET 启动而自动存活
    public class OrderFlowAnalyzer : BackgroundService
    {
        private readonly ILogger<OrderFlowAnalyzer> _logger;
        private readonly MarketEventBus _eventBus;
        private readonly IHubContext<MarketHub> _marketHub;

        private readonly ConcurrentDictionary<string, SymbolMinuteState> _states = new();

        private const decimal VOLUME_SURGE_MULTIPLIER = 2.5m;
        private const decimal OI_SURGE_PERCENT = 0.5m;

        // 🌟 依赖注入事件总线和 Hub
        public OrderFlowAnalyzer(
            ILogger<OrderFlowAnalyzer> logger,
            MarketEventBus eventBus,
            IHubContext<MarketHub> marketHub)
        {
            _logger = logger;
            _eventBus = eventBus;
            _marketHub = marketHub;

            // 🌟 核心：在构造时自动订阅总线事件！
            _eventBus.OnOpenInterestReceived += HandleOpenInterest;
            //_eventBus.OnKlineReceived += HandleKline;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🧠 [量价策略引擎] 已启动，正在监听事件总线...");
            return Task.CompletedTask; // 事件驱动模型，不需要 while 循环死等
        }

        // --- 事件处理函数 ---

        private void HandleOpenInterest(string symbol, decimal oiValue)
        {
            var state = _states.GetOrAdd(symbol, _ => new SymbolMinuteState());
            if (state.StartOi == 0) state.StartOi = oiValue;
            state.CurrentOi = oiValue;
        }

        //private void HandleKline(string symbol, bool isClosed, decimal open, decimal close, decimal volume)
        //{
        //    if (!isClosed) return;

        //    var state = _states.GetOrAdd(symbol, _ => new SymbolMinuteState());

        //    if (state.PreviousMinuteVolume > 0 && state.StartOi > 0)
        //    {
        //        decimal oiChangePercent = ((state.CurrentOi - state.StartOi) / state.StartOi) * 100m;
        //        decimal priceChangePercent = ((close - open) / open) * 100m;
        //        decimal volumeMultiplier = volume / state.PreviousMinuteVolume;

        //        if (volumeMultiplier >= VOLUME_SURGE_MULTIPLIER && Math.Abs(oiChangePercent) >= OI_SURGE_PERCENT)
        //        {
        //            MarketSignalType signalType = MarketSignalType.None;

        //            if (priceChangePercent > 0 && oiChangePercent > 0) signalType = MarketSignalType.StrongLong;
        //            else if (priceChangePercent < 0 && oiChangePercent > 0) signalType = MarketSignalType.StrongShort;
        //            else if (priceChangePercent > 0 && oiChangePercent < 0) signalType = MarketSignalType.ShortCovering;
        //            else if (priceChangePercent < 0 && oiChangePercent < 0) signalType = MarketSignalType.LongLiquidation;

        //            if (signalType != MarketSignalType.None)
        //            {
        //                _logger.LogWarning("🚨 [量价猎手] {Symbol} 触发 {Signal}! 价格: {P}%, OI: {O}%, 量: {V}x",
        //                    symbol, signalType, Math.Round(priceChangePercent, 2), Math.Round(oiChangePercent, 2), Math.Round(volumeMultiplier, 1));

        //                // 推送给前端
        //                _marketHub.Clients.All.SendAsync("ReceiveStrategyAlert", new
        //                {
        //                    symbol = symbol,
        //                    signalType = signalType.ToString(),
        //                    priceChange = priceChangePercent,
        //                    oiChange = oiChangePercent,
        //                    volMultiplier = volumeMultiplier,
        //                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        //                });
        //            }
        //        }
        //    }

        //    state.StartOi = state.CurrentOi;
        //    state.PreviousMinuteVolume = volume;
        //}
    }
}