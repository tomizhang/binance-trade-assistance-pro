using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingTerminal.Hubs;
using TradingTerminal.Models;
using TradingTerminal.Utils;
using SkiaSharp;

namespace TradingTerminal.Services
{
    [System.ComponentModel.DisplayName("多周期高低点触及反转策略")]
    public class CustomStrategyService : StrategyBase
    {
        private class PendingTrigger
        {
            public string Symbol { get; set; }
            public string Type { get; set; } // "LONG" (on valley) or "SHORT" (on peak)
            public decimal TriggerPrice { get; set; }
            public long SignalBarOpenTime { get; set; }
            public long ExpiryTime { get; set; } // Unix timestamp in milliseconds
            public string Timeframe { get; set; } // "5m" or "15m"
            public int SignalBarIdx { get; set; } // Index in the buffer when triggered
        }

        private readonly IPositionManagementService _positionManager;
        private readonly ChartPublishService _chartPublishService;

        private readonly ConcurrentDictionary<string, List<IKline>> _1mBuffer = new();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, List<IKline>>> _tfBuffers = new();
        private readonly ConcurrentDictionary<string, List<PendingTrigger>> _activeTriggers = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastTradeTime = new();

        public CustomStrategyService(
            ILogger<CustomStrategyService> logger,
            IHubContext<MarketHub> hubContext,
            MarketEventBus eventBus,
            BinanceWebSocketService wsService,
            OrderChannel orderChannel,
            BinanceTradeWsService tradeWsService,
            IPositionManagementService positionManager,
            ChartPublishService chartPublishService)
            : base(logger, hubContext, eventBus, wsService, orderChannel, tradeWsService)
        {
            _positionManager = positionManager;
            _chartPublishService = chartPublishService;

            this.IsOrderEnabled = false;
            this.IsStrategyEnabled = false;
            this._timeframes = new[] { "1m", "3m", "5m" };
        }

        protected override async Task InitializeStrategyDataAsync(string symbol)
        {
            _logger.LogInformation($"[{GetType().Name}] 正在为 {symbol} 拉取历史数据...");

            var klines1m = await FetchHistoryAsync(symbol, "1m", 1000);
            _1mBuffer[symbol] = klines1m;

            var klines3m = await FetchHistoryAsync(symbol, "3m", 1500);
            var klines5m = await FetchHistoryAsync(symbol, "5m", 1500);

            var tfData = _tfBuffers.GetOrAdd(symbol, _ => new ConcurrentDictionary<string, List<IKline>>());
            if (klines3m.Any()) tfData["3m"] = klines3m;
            if (klines5m.Any()) tfData["5m"] = klines5m;
        }

        private async Task<List<IKline>> FetchHistoryAsync(string symbol, string interval, int limit)
        {
            try
            {
                string json = await _wsService.GetHistoricalKlinesAsync(symbol, interval, limit);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var historyList = new List<IKline>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    historyList.Add(new KlineMessage
                    {
                        Symbol = symbol,
                        Interval = interval,
                        IsClosed = true,
                        OpenTime = item[0].GetInt64(),
                        Open = decimal.Parse(item[1].GetString()),
                        High = decimal.Parse(item[2].GetString()),
                        Low = decimal.Parse(item[3].GetString()),
                        Close = decimal.Parse(item[4].GetString()),
                        Volume = decimal.Parse(item[5].GetString()),
                        TradeCount = item[8].GetInt32(),
                        TakerBuyBaseVolume = decimal.Parse(item[9].GetString())
                    });
                }
                return historyList;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"拉取 {symbol} {interval} 历史数据失败");
                return new List<IKline>();
            }
        }

        protected override void OnKlineReceived(IKline msg)
        {
            string symbol = msg.Symbol;
            string interval = msg.Interval;

            if (interval == "1m")
            {
                var buffer1m = _1mBuffer.GetOrAdd(symbol, _ => new List<IKline>());

                // 检查触及做单
                CheckPriceTouch(symbol, msg);

                if (!msg.IsClosed) return;

                lock (buffer1m)
                {
                    buffer1m.Add(msg);
                    if (buffer1m.Count > 500) buffer1m.RemoveAt(0);
                }
            }
            else
            {
                var symbolData = _tfBuffers.GetOrAdd(symbol, _ => new ConcurrentDictionary<string, List<IKline>>());
                var buffer = symbolData.GetOrAdd(interval, _ => new List<IKline>());

                lock (buffer)
                {
                    var lastKline = buffer.LastOrDefault();
                    if (lastKline != null && lastKline.OpenTime == msg.OpenTime)
                    {
                        buffer[buffer.Count - 1] = msg;
                    }
                    else if (lastKline == null || msg.OpenTime > lastKline.OpenTime)
                    {
                        buffer.Add(new KlineMessage
                        {
                            Symbol = msg.Symbol,
                            Interval = msg.Interval,
                            IsClosed = msg.IsClosed,
                            OpenTime = msg.OpenTime,
                            Open = msg.Open,
                            High = msg.High,
                            Low = msg.Low,
                            Close = msg.Close,
                            Volume = msg.Volume,
                            TradeCount = msg.TradeCount,
                            TakerBuyBaseVolume = msg.TakerBuyBaseVolume
                        });
                    }
                    if (buffer.Count > 200) buffer.RemoveAt(0);
                }

                if (msg.IsClosed)
                {
                    CheckMtfOverlap(symbol);
                }
            }
        }

        private void CheckMtfOverlap(string symbol)
        {
            var symbolData = _tfBuffers.GetOrAdd(symbol, _ => new ConcurrentDictionary<string, List<IKline>>());
            if (!symbolData.TryGetValue("3m", out var buffer3m) || !symbolData.TryGetValue("5m", out var buffer5m)) return;

            List<IKline> klines3m;
            List<IKline> klines5m;

            lock (buffer3m) { klines3m = buffer3m.ToList(); }
            lock (buffer5m) { klines5m = buffer5m.ToList(); }

            if (klines3m.Count < 20 || klines5m.Count < 20) return;

            var highs5m = klines5m.Select(k => k.High).ToList();
            var lows5m = klines5m.Select(k => k.Low).ToList();
            var (peaks5m, valleys5m) = PivotHelper.CalculatePeaks(highs5m, lows5m, leftLen: 5, rightLen: 1);

            var highs3m = klines3m.Select(k => k.High).ToList();
            var lows3m = klines3m.Select(k => k.Low).ToList();
            var (peaks3m, valleys3m) = PivotHelper.CalculatePeaks(highs3m, lows3m, leftLen: 5, rightLen: 1);

            int T5 = klines5m.Count - 1;
            int signalIdx5 = T5 - 1;

            if (signalIdx5 < 0) return;

            long signalOpenTime5 = klines5m[signalIdx5].OpenTime;
            long signalEndTime5 = signalOpenTime5 + 5 * 60 * 1000L;
            long expiryTime = signalOpenTime5 + 8 * 60 * 1000L; // 5m 信号在下个 5m 周期内前 3m 有效

            var symbolTriggers = _activeTriggers.GetOrAdd(symbol, _ => new List<PendingTrigger>());

            // 1. 检查做空 (高点重叠)
            if (peaks5m.Contains(signalIdx5))
            {
                var overlapping3mPeak = peaks3m.FirstOrDefault(pIdx => klines3m[pIdx].OpenTime >= signalOpenTime5 && klines3m[pIdx].OpenTime < signalEndTime5, -1);
                if (overlapping3mPeak != -1)
                {
                    decimal triggerPrice = klines5m[signalIdx5].High;
                    lock (symbolTriggers)
                    {
                        if (!symbolTriggers.Any(t => t.Type == "SHORT" && t.SignalBarOpenTime == signalOpenTime5))
                        {
                            symbolTriggers.RemoveAll(t => t.Type == "SHORT");
                            symbolTriggers.Add(new PendingTrigger
                            {
                                Symbol = symbol,
                                Type = "SHORT",
                                TriggerPrice = triggerPrice,
                                SignalBarOpenTime = signalOpenTime5,
                                ExpiryTime = expiryTime,
                                Timeframe = "5m",
                                SignalBarIdx = signalIdx5
                            });
                            _logger.LogWarning($"🎯 [高点重叠部署] {symbol} | 5m高点价格: {triggerPrice:F4}。对应 3m 高点开盘时间: {DateTimeOffset.FromUnixTimeMilliseconds(klines3m[overlapping3mPeak].OpenTime).ToLocalTime():HH:mm:ss}");
                        }
                    }
                }
            }

            // 2. 检查做多 (低点重叠)
            if (valleys5m.Contains(signalIdx5))
            {
                var overlapping3mValley = valleys3m.FirstOrDefault(vIdx => klines3m[vIdx].OpenTime >= signalOpenTime5 && klines3m[vIdx].OpenTime < signalEndTime5, -1);
                if (overlapping3mValley != -1)
                {
                    decimal triggerPrice = klines5m[signalIdx5].Low;
                    lock (symbolTriggers)
                    {
                        if (!symbolTriggers.Any(t => t.Type == "LONG" && t.SignalBarOpenTime == signalOpenTime5))
                        {
                            symbolTriggers.RemoveAll(t => t.Type == "LONG");
                            symbolTriggers.Add(new PendingTrigger
                            {
                                Symbol = symbol,
                                Type = "LONG",
                                TriggerPrice = triggerPrice,
                                SignalBarOpenTime = signalOpenTime5,
                                ExpiryTime = expiryTime,
                                Timeframe = "5m",
                                SignalBarIdx = signalIdx5
                            });
                            _logger.LogWarning($"🎯 [低点重叠部署] {symbol} | 5m低点价格: {triggerPrice:F4}。对应 3m 低点开盘时间: {DateTimeOffset.FromUnixTimeMilliseconds(klines3m[overlapping3mValley].OpenTime).ToLocalTime():HH:mm:ss}");
                        }
                    }
                }
            }
        }

        private void CheckPriceTouch(string symbol, IKline current1m)
        {
            if (!_activeTriggers.TryGetValue(symbol, out var triggers) || !triggers.Any()) return;

            long currentTimeMs = current1m.OpenTime;

            lock (triggers)
            {
                // 1. 清理过期的触发器
                var expired = triggers.Where(t => currentTimeMs >= t.ExpiryTime).ToList();
                foreach (var exp in expired)
                {
                    _logger.LogInformation($"⏳ [{symbol}] {exp.Timeframe} {exp.Type} 触发器已过期失效 (价格: {exp.TriggerPrice:F4})");
                    triggers.Remove(exp);
                }

                if (!triggers.Any()) return;
                if (_positionManager.HasActivePosition(symbol)) return;
                if (_lastTradeTime.TryGetValue(symbol, out var lastTime) && (GetCurrentTime() - lastTime).TotalMinutes < 5) return;

                // 2. 检查价格触及
                PendingTrigger triggeredItem = null;
                bool isLong = false;

                foreach (var trigger in triggers)
                {
                    if (trigger.Type == "SHORT" && current1m.High >= trigger.TriggerPrice)
                    {
                        triggeredItem = trigger;
                        isLong = false;
                        break;
                    }
                    else if (trigger.Type == "LONG" && current1m.Low <= trigger.TriggerPrice)
                    {
                        triggeredItem = trigger;
                        isLong = true;
                        break;
                    }
                }

                if (triggeredItem != null)
                {
                    triggers.Remove(triggeredItem);
                    ExecuteTouchTrade(symbol, isLong, current1m.Close, triggeredItem);
                }
            }
        }

        private void ExecuteTouchTrade(string symbol, bool isLong, decimal entryPrice, PendingTrigger trigger)
        {
            _lastTradeTime[symbol] = GetCurrentTime();

            decimal leverage = GetLeverage(10m);
            decimal targetRoeTp = 0.50m; // 止盈 20% ROE
            decimal riskRoeSl = 0.55m;  // 止损 15% ROE

            string direction = isLong ? "做多 (低点重叠触及)" : "做空 (高点重叠触及)";
            string reason = $"{trigger.Timeframe}前K线与3m重叠{trigger.Type}价格{trigger.TriggerPrice:F4}触及";

            _logger.LogWarning($"🎯 [{symbol}] {direction} 触发！价格: {entryPrice:F4}。{reason}，SL: 15% ROE, TP: 20% ROE");

            _ = Task.Run(async () =>
            {
                await PlaceOrderWithLeverageRiskAsync(
                    symbol, isLong, entryPrice, 1.5m, leverage, targetRoeTp, riskRoeSl, "MtfTouchReversal"
                );
            });

            // 获取触发该大周期的 K 线缓冲区进行图表快照渲染
            if (_tfBuffers.TryGetValue(symbol, out var symbolData) && symbolData.TryGetValue("5m", out var buffer))
            {
                List<IKline> mtfKlines;
                lock (buffer)
                {
                    mtfKlines = buffer.ToList();
                }
                PublishTouchChart(symbol, "5m", mtfKlines, trigger.SignalBarIdx, trigger.TriggerPrice, trigger.Type, reason);
            }
        }

        private void PublishTouchChart(
            string symbol,
            string timeframe,
            List<IKline> mtfKlines,
            int signalIdx,
            decimal triggerPrice,
            string type,
            string reason)
        {
            var chartItem = new ChartPublishItem
            {
                Symbol = symbol,
                StrategyName = $"MtfTouchReversal_{timeframe}",
                Klines = mtfKlines.TakeLast(80).ToList()
            };

            int offset = mtfKlines.Count - chartItem.Klines.Count;
            int signalChartIdx = signalIdx - offset;

            // 标记触发高/低点
            SKColor color = type == "LONG" ? SKColors.Green : SKColors.Red;
            if (signalChartIdx >= 0 && signalChartIdx < chartItem.Klines.Count)
            {
                chartItem.Points.Add((signalChartIdx, triggerPrice, color, 8f));
                chartItem.Texts.Add((type == "LONG" ? "Valley" : "Peak", signalChartIdx, triggerPrice, color));
            }

            // 绘制水平触发价线 (品红)
            int currentKIndex = chartItem.Klines.Count - 1;
            chartItem.Lines.Add((0, triggerPrice, currentKIndex + 5, triggerPrice, SKColors.Magenta, 2f));
            chartItem.Texts.Add(($"Trigger Line: {triggerPrice:F4}", 2, triggerPrice, SKColors.Magenta));

            // 描述文字
            string desc = $"{reason} | Touch Price: {triggerPrice:F4}";
            chartItem.Texts.Add((desc, Math.Max(0, currentKIndex - 20), triggerPrice * 1.002m, SKColors.White));

            _ = _chartPublishService.PublishChartAsync(chartItem);
        }
    }
}
