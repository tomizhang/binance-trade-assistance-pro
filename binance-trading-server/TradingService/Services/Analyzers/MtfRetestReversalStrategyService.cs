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
    [System.ComponentModel.DisplayName("5m高低点双棒确认反转策略")]
    public class MtfRetestReversalStrategyService : StrategyBase
    {
        private enum ObservationStatus
        {
            None,
            ObservingMinute1,      // In the first 1m bar
            WaitingForRetestMinute2 // First 1m bar closed with rebound, now waiting for touch in 2nd minute
        }

        private class ActiveObservation
        {
            public string Symbol { get; set; }
            public string Type { get; set; } // "LONG" or "SHORT"
            public decimal AnchorPrice { get; set; }
            public long CycleStartTime { get; set; } // OpenTime of the new 5m bar
            public ObservationStatus Status { get; set; } = ObservationStatus.None;
            public long SignalBarOpenTime { get; set; }
            public int SignalBarIdx { get; set; }
        }

        private readonly IPositionManagementService _positionManager;
        private readonly ChartPublishService _chartPublishService;

        private readonly ConcurrentDictionary<string, List<IKline>> _1mBuffer = new();
        private readonly ConcurrentDictionary<string, List<IKline>> _5mBuffer = new();
        private readonly ConcurrentDictionary<string, ActiveObservation> _activeObservations = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastTradeTime = new();

        private const decimal NearThreshold = 0.0015m; // 0.15%

        public MtfRetestReversalStrategyService(
            ILogger<MtfRetestReversalStrategyService> logger,
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
            this._timeframes = new[] { "1m", "5m" };
        }

        protected override async Task InitializeStrategyDataAsync(string symbol)
        {
            _logger.LogInformation($"[{GetType().Name}] 正在为 {symbol} 拉取历史数据...");

            var klines1m = await FetchHistoryAsync(symbol, "1m", 100);
            _1mBuffer[symbol] = klines1m;

            var klines5m = await FetchHistoryAsync(symbol, "5m", 150);
            _5mBuffer[symbol] = klines5m;
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

                // 核心：基于 1m 棒执行双棒确认分析与入场
                Analyze1mConfirm(symbol, msg);

                if (!msg.IsClosed) return;

                lock (buffer1m)
                {
                    buffer1m.Add(msg);
                    if (buffer1m.Count > 500) buffer1m.RemoveAt(0);
                }
            }
            else if (interval == "5m")
            {
                var buffer5m = _5mBuffer.GetOrAdd(symbol, _ => new List<IKline>());

                lock (buffer5m)
                {
                    var lastKline = buffer5m.LastOrDefault();
                    if (lastKline != null && lastKline.OpenTime == msg.OpenTime)
                    {
                        buffer5m[buffer5m.Count - 1] = msg;
                    }
                    else if (lastKline == null || msg.OpenTime > lastKline.OpenTime)
                    {
                        buffer5m.Add(new KlineMessage
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
                    if (buffer5m.Count > 200) buffer5m.RemoveAt(0);
                }

                if (msg.IsClosed)
                {
                    Check5mExtrema(symbol, buffer5m);
                }
            }
        }

        private void Check5mExtrema(string symbol, List<IKline> buffer5m)
        {
            if (buffer5m.Count < 20) return;

            List<IKline> klinesCopy;
            lock (buffer5m)
            {
                klinesCopy = buffer5m.ToList();
            }

            var highs = klinesCopy.Select(k => k.High).ToList();
            var lows = klinesCopy.Select(k => k.Low).ToList();

            // 使用 rightLen = 0，在 5m 柱收盘时立即可靠提取当前收盘柱的极值
            var (peaks, valleys) = PivotHelper.CalculatePeaks(highs, lows, leftLen: 5, rightLen: 0);

            int T = klinesCopy.Count - 1; // 刚刚收盘的这根 K 线索引

            if (valleys.Contains(T))
            {
                var obs = new ActiveObservation
                {
                    Symbol = symbol,
                    Type = "LONG",
                    AnchorPrice = klinesCopy[T].Low,
                    CycleStartTime = klinesCopy[T].OpenTime + 5 * 60 * 1000L, // 新周期的开盘时间
                    Status = ObservationStatus.ObservingMinute1,
                    SignalBarOpenTime = klinesCopy[T].OpenTime,
                    SignalBarIdx = T
                };
                _activeObservations[symbol] = obs;
                _logger.LogInformation($"🔍 [{symbol}] 5m 收盘为低点。前低价: {obs.AnchorPrice:F4}。进入新周期观察期，等待第 1 分钟反弹...");
            }
            else if (peaks.Contains(T))
            {
                var obs = new ActiveObservation
                {
                    Symbol = symbol,
                    Type = "SHORT",
                    AnchorPrice = klinesCopy[T].High,
                    CycleStartTime = klinesCopy[T].OpenTime + 5 * 60 * 1000L, // 新周期的开盘时间
                    Status = ObservationStatus.ObservingMinute1,
                    SignalBarOpenTime = klinesCopy[T].OpenTime,
                    SignalBarIdx = T
                };
                _activeObservations[symbol] = obs;
                _logger.LogInformation($"🔍 [{symbol}] 5m 收盘为高点。前高价: {obs.AnchorPrice:F4}。进入新周期观察期，等待第 1 分钟阴线反弹...");
            }
        }

        private void Analyze1mConfirm(string symbol, IKline msg)
        {
            if (!_activeObservations.TryGetValue(symbol, out var obs)) return;

            long diffMs = msg.OpenTime - obs.CycleStartTime;

            // 1. 如果新 5m 周期还没开始，直接返回
            if (diffMs < 0) return;

            // 2. 超出第 2 分钟，信号过期失效
            if (diffMs >= 120_000)
            {
                _activeObservations.TryRemove(symbol, out _);
                _logger.LogInformation($"⏳ [{symbol}] 第 2 分钟结束未触发价格回踩，双棒确认信号过期失效。");
                return;
            }

            // 3. 第 1 分钟 (diffMs == 0)，等第一根 1m 棒收盘时确认反弹
            if (diffMs == 0)
            {
                if (msg.IsClosed)
                {
                    if (obs.Type == "LONG")
                    {
                        // 多头反弹：第 1 分钟收阳线
                        if (msg.Close > msg.Open)
                        {
                            obs.Status = ObservationStatus.WaitingForRetestMinute2;
                            _logger.LogInformation($"✅ [{symbol}] 第 1 分钟阳线反弹确认！Close: {msg.Close:F4} > Open: {msg.Open:F4}。进入第 2 分钟等待回踩...");
                        }
                        else
                        {
                            _activeObservations.TryRemove(symbol, out _);
                            _logger.LogInformation($"❌ [{symbol}] 第 1 分钟未收阳线，反弹确认失败。信号注销。");
                        }
                    }
                    else if (obs.Type == "SHORT")
                    {
                        // 空头反弹：第 1 分钟收阴线
                        if (msg.Close < msg.Open)
                        {
                            obs.Status = ObservationStatus.WaitingForRetestMinute2;
                            _logger.LogInformation($"✅ [{symbol}] 第 1 分钟阴线反弹确认！Close: {msg.Close:F4} < Open: {msg.Open:F4}。进入第 2 分钟等待反弹回踩...");
                        }
                        else
                        {
                            _activeObservations.TryRemove(symbol, out _);
                            _logger.LogInformation($"❌ [{symbol}] 第 1 分钟未收阴线，反弹确认失败。信号注销。");
                        }
                    }
                }
                return;
            }

            // 4. 第 2 分钟 (diffMs == 60,000)，检查价格回踩入场
            if (diffMs == 60_000 && obs.Status == ObservationStatus.WaitingForRetestMinute2)
            {
                if (_positionManager.HasActivePosition(symbol)) return;
                if (_lastTradeTime.TryGetValue(symbol, out var lastTime) && (GetCurrentTime() - lastTime).TotalMinutes < 5) return;

                if (obs.Type == "LONG")
                {
                    // 下跌到前低点附近 (前低点 + 0.15% 范围内)
                    decimal upperLimit = obs.AnchorPrice * (1 + NearThreshold);
                    if (msg.Low <= upperLimit)
                    {
                        // 触发做多！
                        _activeObservations.TryRemove(symbol, out _);
                        ExecuteRetestTrade(symbol, isLong: true, msg.Close, obs);
                    }
                }
                else if (obs.Type == "SHORT")
                {
                    // 上涨到前高点附近 (前高点 - 0.15% 范围内)
                    decimal lowerLimit = obs.AnchorPrice * (1 - NearThreshold);
                    if (msg.High >= lowerLimit)
                    {
                        // 触发做空！
                        _activeObservations.TryRemove(symbol, out _);
                        ExecuteRetestTrade(symbol, isLong: false, msg.Close, obs);
                    }
                }
            }
        }

        private void ExecuteRetestTrade(string symbol, bool isLong, decimal entryPrice, ActiveObservation obs)
        {
            _lastTradeTime[symbol] = GetCurrentTime();

            decimal leverage = GetLeverage(20m);
            decimal targetRoeTp = 0.50m; // 止盈 20% ROE
            decimal riskRoeSl = 0.50m;  // 止损 15% ROE

            string direction = isLong ? "做多 (Valley二分回踩)" : "做空 (Peak二分冲高)";
            string reason = $"5m前K线极值{obs.Type}在第1分钟反弹后，于第2分钟回踩点{obs.AnchorPrice:F4}触及";

            _logger.LogWarning($"🎯 [{symbol}] {direction} 触发！价格: {entryPrice:F4}。{reason}，SL: 15% ROE, TP: 20% ROE");

            _ = Task.Run(async () =>
            {
                await PlaceOrderWithLeverageRiskAsync(
                    symbol, isLong, entryPrice, 1.5m, leverage, targetRoeTp, riskRoeSl, "MtfRetestReversal"
                );
            });

            // 获取触发该大周期的 K 线缓冲区进行图表快照渲染
            if (_5mBuffer.TryGetValue(symbol, out var buffer))
            {
                List<IKline> m5Klines;
                lock (buffer)
                {
                    m5Klines = buffer.ToList();
                }
                PublishRetestChart(symbol, m5Klines, obs.SignalBarIdx, obs.AnchorPrice, obs.Type, reason);
            }
        }

        private void PublishRetestChart(
            string symbol,
            List<IKline> m5Klines,
            int signalIdx,
            decimal anchorPrice,
            string type,
            string reason)
        {
            var chartItem = new ChartPublishItem
            {
                Symbol = symbol,
                StrategyName = $"MtfRetestReversal_5m",
                Klines = m5Klines.TakeLast(80).ToList()
            };

            int offset = m5Klines.Count - chartItem.Klines.Count;
            int signalChartIdx = signalIdx - offset;

            // 标记极值高/低点
            SKColor color = type == "LONG" ? SKColors.Green : SKColors.Red;
            if (signalChartIdx >= 0 && signalChartIdx < chartItem.Klines.Count)
            {
                chartItem.Points.Add((signalChartIdx, anchorPrice, color, 8f));
                chartItem.Texts.Add((type == "LONG" ? "Valley" : "Peak", signalChartIdx, anchorPrice, color));
            }

            // 绘制水平触发价线 (品红)
            int currentKIndex = chartItem.Klines.Count - 1;
            chartItem.Lines.Add((0, anchorPrice, currentKIndex + 3, anchorPrice, SKColors.Magenta, 2f));
            chartItem.Texts.Add(($"Anchor Line: {anchorPrice:F4}", 2, anchorPrice, SKColors.Magenta));

            // 描述文字
            string desc = $"{reason} | Price: {anchorPrice:F4}";
            chartItem.Texts.Add((desc, Math.Max(0, currentKIndex - 20), anchorPrice * 1.002m, SKColors.White));

            _ = _chartPublishService.PublishChartAsync(chartItem);
        }
    }
}
