using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingTerminal.Models;

namespace TradingTerminal.Services
{
    public class PositionManagementService : BackgroundService
    {
        private readonly ILogger<PositionManagementService> _logger;
        private readonly BinanceTradeWsService _tradeWsService;
        private readonly UserDataEventBus _userDataBus;

        // 🌟 1. 注入行情事件总线大喇叭，取代对 BinanceWebSocketService 的直接依赖
        private readonly MarketEventBus _marketEventBus;

        // 活跃仓位追踪器
        private readonly ConcurrentDictionary<string, PositionTracker> _activeTrackers = new();

        // 🌟 2. 本地专属的最新价格缓存字典
        private readonly ConcurrentDictionary<string, decimal> _latestPrices = new(StringComparer.OrdinalIgnoreCase);

        public PositionManagementService(
            ILogger<PositionManagementService> logger,
            BinanceTradeWsService tradeWsService,
            UserDataEventBus userDataBus,
            MarketEventBus marketEventBus) // 👈 构造函数注入行情总线
        {
            _logger = logger;
            _tradeWsService = tradeWsService;
            _userDataBus = userDataBus;
            _marketEventBus = marketEventBus;

            // 🌟 3. 订阅双轨总线：一边听仓位变化，一边听价格跳动
            _userDataBus.OnRawUserDataReceived += HandleAccountUpdate;
            _marketEventBus.OnKlineReceived += HandleKlinePriceUpdate;
        }

        // ==========================================
        // 🌟 监听行情总线：光速更新本地价格缓存
        // ==========================================
        private void HandleKlinePriceUpdate(KlineMessage msg)
        {
            // K 线在未收盘前，它的 Close 价格每秒都在跳动，这就是实时的最新成交价
            _latestPrices[msg.Symbol] = msg.Close;
        }

        // ==========================================
        // 🌟 监听账户总线：更新仓位状态
        // ==========================================
        private void HandleAccountUpdate(string jsonMessage)
        {
            try
            {
                using var eventDoc = JsonDocument.Parse(jsonMessage);
                if (eventDoc.RootElement.TryGetProperty("e", out var eventType) && eventType.GetString() == "ACCOUNT_UPDATE")
                {
                    var updateData = eventDoc.RootElement.GetProperty("a");
                    if (updateData.TryGetProperty("P", out var positionsArray))
                    {
                        foreach (var pos in positionsArray.EnumerateArray())
                        {
                            string symbol = pos.GetProperty("s").GetString();
                            decimal amount = decimal.Parse(pos.GetProperty("pa").GetString(), System.Globalization.CultureInfo.InvariantCulture);
                            decimal entryPrice = decimal.Parse(pos.GetProperty("ep").GetString(), System.Globalization.CultureInfo.InvariantCulture);

                            if (amount != 0)
                            {
                                bool isNewOrReversed = false;

                                if (!_activeTrackers.TryGetValue(symbol, out var tracker))
                                {
                                    isNewOrReversed = true;
                                    tracker = new PositionTracker { Symbol = symbol };
                                }
                                else if (Math.Sign(tracker.Quantity) != Math.Sign(amount))
                                {
                                    isNewOrReversed = true;
                                }

                                tracker.Quantity = amount;
                                tracker.EntryPrice = entryPrice;
                                tracker.Side = amount > 0 ? "BUY" : "SELL";

                                if (isNewOrReversed)
                                {
                                    tracker.OpenTime = DateTime.UtcNow;
                                    tracker.IsStopMovedToBE = false;
                                    _activeTrackers[symbol] = tracker;
                                    _logger.LogInformation($"🛡️ [仓位雷达] 捕获新仓位 {symbol}: 方向 {tracker.Side}, 均价 {tracker.EntryPrice}");
                                }
                                else
                                {
                                    _activeTrackers[symbol] = tracker;
                                }
                            }
                            else
                            {
                                if (_activeTrackers.TryRemove(symbol, out _))
                                {
                                    _logger.LogInformation($"🧹 [仓位雷达] {symbol} 仓位已平，解除追踪。");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"❌ 解析账户变动事件失败: {ex.Message}");
            }
        }

        // ==========================================
        // 🌟 巡检大循环：执行保本损策略
        // ==========================================
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🛡️ [仓位管理中心] 启动：2分钟浮盈保本策略已激活 (纯事件驱动架构)...");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 巡检频率：3秒一次，不消耗任何网络资源
                    await Task.Delay(10, stoppingToken);

                    var now = DateTime.UtcNow;

                    foreach (var tracker in _activeTrackers.Values.ToList())
                    {
                        if (tracker.IsStopMovedToBE) continue;

                        var duration = now - tracker.OpenTime;
                        if (duration.TotalMinutes < 2) continue;

                        // 🌟 直接从本地总线缓存中读取当前价格 (O(1) 内存操作，0 延迟)
                        decimal currentPrice = _latestPrices.TryGetValue(tracker.Symbol, out var price) ? price : 0m;

                        if (currentPrice <= 0) continue;

                        bool isProfitable = tracker.Side == "BUY"
                            ? currentPrice > tracker.EntryPrice
                            : currentPrice < tracker.EntryPrice;

                        if (isProfitable)
                        {
                            _logger.LogWarning($"🎯 [免费门票] {tracker.Symbol} 持仓满 2min 且已浮盈，强行移动止损至保本位附近...");

                            try
                            {
                                // 动作 A：撤掉原本带有巨大风险的宽幅止损单
                                await _tradeWsService.CancelAllOpenOrdersAsync(tracker.Symbol);

                                // 动作 B：计算带手续费补偿的保本价
                                decimal bePriceRaw = tracker.Side == "BUY"
                                    ? tracker.EntryPrice * 1.001m
                                    : tracker.EntryPrice * 0.999m;

                                decimal bePriceFormatted = _tradeWsService.FormatPrice(tracker.Symbol, bePriceRaw);
                                decimal qtyFormatted = _tradeWsService.FormatQuantity(tracker.Symbol, Math.Abs(tracker.Quantity));

                                await _tradeWsService.SetStopLossMarketAsync(
                                    tracker.Symbol,
                                    tracker.Side == "BUY" ? "LONG" : "SHORT",
                                    qtyFormatted,
                                    bePriceFormatted);

                                tracker.IsStopMovedToBE = true;
                                _logger.LogInformation($"✅ [免费门票] {tracker.Symbol} 补偿手续费级保本损 ({bePriceFormatted}) 设置成功！这笔交易已零风险！");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError($"❌ [保本失败] 设置 {tracker.Symbol} 保本损时出错: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ 仓位管理巡检异常: {ex.Message}");
                }
            }
        }

        public override void Dispose()
        {
            if (_userDataBus != null)
            {
                _userDataBus.OnRawUserDataReceived -= HandleAccountUpdate;
            }
            if (_marketEventBus != null)
            {
                // 🌟 释放时注销行情订阅，防止内存泄漏
                _marketEventBus.OnKlineReceived -= HandleKlinePriceUpdate;
            }
            base.Dispose();
        }
    }
}