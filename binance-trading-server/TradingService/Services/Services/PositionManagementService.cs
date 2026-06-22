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
    /// <summary>
    /// 仓位高级管理中心：专职负责监听仓位变化、移动保本损、阶梯止盈等扩展策略
    /// </summary>
    public class PositionManagementService : BackgroundService, IPositionManagementService
    {
        private readonly ILogger<PositionManagementService> _logger;
        private readonly BinanceTradeWsService _tradeWsService;
        private readonly UserDataEventBus _userDataBus;

        // 🌟 注入行情事件总线大喇叭，0 延迟获取跳动价格
        private readonly MarketEventBus _marketEventBus;

        // 活跃仓位追踪器 (🌟 优化：加上忽略大小写，防止符号比对失败)
        private readonly ConcurrentDictionary<string, PositionTracker> _activeTrackers = new(StringComparer.OrdinalIgnoreCase);

        // 本地专属的最新价格缓存字典
        private readonly ConcurrentDictionary<string, decimal> _latestPrices = new(StringComparer.OrdinalIgnoreCase);

        public PositionManagementService(
            ILogger<PositionManagementService> logger,
            BinanceTradeWsService tradeWsService,
            UserDataEventBus userDataBus,
            MarketEventBus marketEventBus)
        {
            _logger = logger;
            _tradeWsService = tradeWsService;
            _userDataBus = userDataBus;
            _marketEventBus = marketEventBus;

            // 订阅双轨总线：一边听仓位变化，一边听价格跳动
            _userDataBus.OnRawUserDataReceived += HandleAccountUpdate;
            _marketEventBus.OnKlineReceived += HandleKlinePriceUpdate;
        }

        // ==========================================
        // 🌟 外部调用接口：供其他 Service (如策略类) 查询仓位状态
        // ==========================================

        /// <summary>
        /// 检查指定币种当前是否有活动仓位（防止重复开仓）
        /// </summary>
        public bool HasActivePosition(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return false;
            return _activeTrackers.ContainsKey(symbol);
        }

        /// <summary>
        /// 检查当前全局是否有任何活动仓位（用于单线程或防多开逻辑）
        /// </summary>
        public bool HasAnyActivePosition()
        {
            return !_activeTrackers.IsEmpty;
        }

        /// <summary>
        /// 获取指定币种的当前仓位详情（方便策略获取开仓方向、均价等）
        /// </summary>
        public bool TryGetPosition(string symbol, out PositionTracker position)
        {
            position = null;
            if (string.IsNullOrEmpty(symbol)) return false;
            return _activeTrackers.TryGetValue(symbol, out position);
        }

        public void RegisterPositionStrategy(string symbol, string strategyName, decimal entryPrice, decimal takeProfitPrice)
        {
            if (_activeTrackers.TryGetValue(symbol, out var tracker))
            {
                tracker.StrategyName = strategyName;
                tracker.TakeProfitPrice = takeProfitPrice;
            }
            else
            {
                _activeTrackers[symbol] = new PositionTracker
                {
                    Symbol = symbol,
                    StrategyName = strategyName,
                    TakeProfitPrice = takeProfitPrice,
                    EntryPrice = entryPrice,
                    OpenTime = DateTime.UtcNow
                };
            }
        }

        // ==========================================
        // 🌟 监听行情总线：光速更新本地价格缓存
        // ==========================================
        private void HandleKlinePriceUpdate(IKline msg)
        {
            // K 线在未收盘前，它的 Close 价格每秒都在跳动，这就是实时的最新成交价
            _latestPrices[msg.Symbol] = msg.Close;
        }

        // ==========================================
        // 🌟 监听账户总线：精准更新仓位状态
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
            _logger.LogInformation("🛡️ [仓位管理中心] 启动：半盈(50% TP)保本策略已激活...");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(10, stoppingToken);
                    var now = DateTime.UtcNow;

                    foreach (var tracker in _activeTrackers.Values.ToList())
                    {
                        if (tracker.IsStopMovedToBE) continue;

                        // 1. 获取最新价
                        decimal currentPrice = _latestPrices.TryGetValue(tracker.Symbol, out var price) ? price : 0m;
                        if (currentPrice <= 0) continue;

                        var duration = now - tracker.OpenTime;

                        if (tracker.StrategyName == "MinVolumeReversalStrategyService")
                        {
                            // 1分钟成交量反转策略专属规则：盈利期间并且达到3分钟，移动止损位置到开仓价格
                            bool isInProfit = tracker.Side == "BUY"
                                ? currentPrice > tracker.EntryPrice
                                : currentPrice < tracker.EntryPrice;

                            bool isTimeReached = duration.TotalMinutes >= 3;

                            if (isInProfit && isTimeReached)
                            {
                                _logger.LogWarning($"🎯 [1m成交量反转保本触发] {tracker.Symbol} 持仓达到3分钟且已盈利(开仓价:{tracker.EntryPrice:F4}, 当前价:{currentPrice:F4})，移动止损至保本...");
                                await MoveStopToBEAsync(tracker);
                            }
                        }
                        else
                        {
                            // 默认规则：触碰半盈位 且 过去2分钟
                            if (tracker.TakeProfitPrice == 0)
                            {
                                continue;
                            }

                            decimal tpDistance = Math.Abs(tracker.TakeProfitPrice - tracker.EntryPrice);
                            decimal halfTpTargetPrice = tracker.Side == "BUY"
                                ? tracker.EntryPrice + (tpDistance * 0.5m)
                                : tracker.EntryPrice - (tpDistance * 0.5m);

                            bool isHalfTpReached = tracker.Side == "BUY"
                                ? currentPrice >= halfTpTargetPrice
                                : currentPrice <= halfTpTargetPrice;

                            bool isTimeReached = duration.TotalMinutes >= 2;

                            if (isHalfTpReached && isTimeReached)
                            {
                                _logger.LogWarning($"🎯 [半盈保护] {tracker.Symbol} 触碰半盈位 {halfTpTargetPrice:F4} (全盈目标:{tracker.TakeProfitPrice:F4})，执行保本损...");
                                await MoveStopToBEAsync(tracker);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ 巡检循环崩溃: {ex.Message}");
                }
            }
        }

        private async Task<bool> MoveStopToBEAsync(PositionTracker tracker)
        {
            try
            {
                // 动作 A：精准撤销旧止损
                await _tradeWsService.CancelStopLossOnlyAsync(tracker.Symbol);

                // 动作 B：挂载保本损
                decimal bePriceRaw = tracker.EntryPrice;
                if (tracker.StrategyName != "MinVolumeReversalStrategyService")
                {
                    bePriceRaw = tracker.Side == "BUY"
                        ? tracker.EntryPrice * 1.0005m  // 做多保本略高一点
                        : tracker.EntryPrice * 0.9995m; // 做空保本略低一点
                }

                decimal bePrice = _tradeWsService.FormatPrice(tracker.Symbol, bePriceRaw);
                decimal qty = _tradeWsService.FormatQuantity(tracker.Symbol, Math.Abs(tracker.Quantity));

                await _tradeWsService.SetStopLossMarketAsync(
                    tracker.Symbol,
                    tracker.Side == "BUY" ? "LONG" : "SHORT",
                    qty,
                    bePrice);

                tracker.IsStopMovedToBE = true;
                _logger.LogInformation($"✅ [保本成功] {tracker.Symbol} 已进入零风险模式。");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ [保本异常] {tracker.Symbol}: {ex.Message}");
                return false;
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
                _marketEventBus.OnKlineReceived -= HandleKlinePriceUpdate;
            }
            base.Dispose();
        }
    }
}