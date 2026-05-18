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

namespace TradingTerminal.Services
{
    /// <summary>
    /// V形态/倒V形态 反转狙击策略
    /// 猎杀散户在关键支撑/阻力位的突破追单行为
    /// </summary>
    public class VReversalStrategyService : StrategyBase
    {
        private class VObservationState
        {
            public bool IsShorting { get; set; }        // true = V形态做空 (测试前高); false = 倒V形态做多 (测试前低)
            public decimal PivotPrice { get; set; }     // 瞄准的前高/前低点
            public int CandlesWatched { get; set; }     // 观察了多少根 1m K线
            public decimal BaseAvgVolume { get; set; }  // 进入观察期前的 1m 均量
        }

        private readonly PositionManagementService _positionManager;
        private readonly ChartPublishService _chartPublishService;

        private readonly ConcurrentDictionary<string, List<IKline>> _1mBuffer = new();
        // 存储多周期聚合 K 线：3m, 5m
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, List<IKline>>> _tfBuffers = new();

        // 存储 3m 和 5m 级别合并的关键位点位 (支撑/压力)
        private readonly ConcurrentDictionary<string, (List<decimal> Peaks, List<decimal> Valleys)> _pivotLevels = new();

        // 观察名单与防重发控制
        private readonly ConcurrentDictionary<string, VObservationState> _observations = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastTradeTime = new();

        public VReversalStrategyService(
            ILogger<VReversalStrategyService> logger,
            IHubContext<MarketHub> hubContext,
            MarketEventBus eventBus,
            BinanceWebSocketService wsService,
            OrderChannel orderChannel,
            BinanceTradeWsService tradeWsService,
            PositionManagementService positionManager,
            ChartPublishService chartPublishService)
            : base(logger, hubContext, eventBus, wsService, orderChannel, tradeWsService)
        {
            _positionManager = positionManager;
            _chartPublishService = chartPublishService;

            this.IsOrderEnabled = true; // 实盘下单开关
            this.IsStrategyEnabled = true;

            // 订阅所需的周期
            this._timeframes = new[] { "1m", "3m", "5m" };
        }

        protected override async Task InitializeStrategyDataAsync(string symbol)
        {
            _logger.LogInformation($"[{GetType().Name}] 正在为 {symbol} 拉取历史数据...");

            var klines1m = await FetchHistoryAsync(symbol, "1m", 150);
            _1mBuffer[symbol] = klines1m;

            var klines3m = await FetchHistoryAsync(symbol, "3m", 150);
            var klines5m = await FetchHistoryAsync(symbol, "5m", 150);

            var tfData = _tfBuffers.GetOrAdd(symbol, _ => new ConcurrentDictionary<string, List<IKline>>());
            if (klines3m.Any()) tfData["3m"] = klines3m;
            if (klines5m.Any()) tfData["5m"] = klines5m;

            RecalculateMultiTimeframePivots(symbol);
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

                if (buffer1m.Count >= 60)
                {
                    CheckReversalConditions(symbol, msg, buffer1m);
                }

                if (!msg.IsClosed) return;

                lock (buffer1m)
                {
                    buffer1m.Add(msg);
                    if (buffer1m.Count > 200) buffer1m.RemoveAt(0);
                }
            }
            else
            {
                // 实时维护 3m 和 5m 结构
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

                // 大周期收盘时，利用 PivotHelper 重新提取高低点
                if (msg.IsClosed)
                {
                    RecalculateMultiTimeframePivots(symbol);
                }
            }
        }

        private void CheckReversalConditions(string symbol, IKline current1m, List<IKline> buffer1m)
        {
            if (_positionManager.HasAnyActivePosition()) return;
            if (_lastTradeTime.TryGetValue(symbol, out var lastTime) && (DateTime.Now - lastTime).TotalMinutes < 5) return;

            // ==========================================
            // 🛡️ 阶段 1：观察期，判断加速与反转
            // ==========================================
            if (_observations.TryGetValue(symbol, out var obs))
            {
                if (current1m.IsClosed) obs.CandlesWatched++;

                if (obs.CandlesWatched > 5)
                {
                    _observations.TryRemove(symbol, out _);
                    _logger.LogInformation($"⏳ [{symbol}] V反转观察期超时，未见加速假突破行为，放弃入场。");
                    return;
                }

                // 判断短时间加速：当前一分钟量能大于基础均量的 1.5 倍
                bool isAccelerating = current1m.Volume > obs.BaseAvgVolume * 1.5m;

                if (obs.IsShorting) // V形态，在阻力位准备做空
                {
                    // 止损或失效：如果实体强势突破并站稳前高点上方 0.3%，直接取消观察
                    if (current1m.Close > obs.PivotPrice * 1.003m)
                    {
                        _observations.TryRemove(symbol, out _);
                        return;
                    }

                    // 狙击点：量能放大，最高价摸到了前高，但收盘价被死死压制在阻力下方
                    if (isAccelerating && current1m.High >= obs.PivotPrice * 0.998m && current1m.Close < obs.PivotPrice)
                    {
                        ExecuteTrade(symbol, obs, current1m);
                    }
                }
                else // 倒V形态，在支撑位准备做多
                {
                    if (current1m.Close < obs.PivotPrice * 0.997m)
                    {
                        _observations.TryRemove(symbol, out _);
                        return;
                    }

                    // 狙击点：量能放大，最低价摸到了前低，但收盘价被买盘托起
                    if (isAccelerating && current1m.Low <= obs.PivotPrice * 1.002m && current1m.Close > obs.PivotPrice)
                    {
                        ExecuteTrade(symbol, obs, current1m);
                    }
                }
                return;
            }

            // ==========================================
            // 🚀 阶段 2：寻找 V / 倒V 形态并切入观察
            // ==========================================
            if (!_pivotLevels.TryGetValue(symbol, out var levels)) return;

            decimal avgVol = buffer1m.TakeLast(30).Average(k => k.Volume);

            // 🌟 严格使用 PivotHelper 获取近期的微观起涨点/起跌点
            var highs1m = buffer1m.Select(k => (decimal)k.High).ToList();
            var lows1m = buffer1m.Select(k => (decimal)k.Low).ToList();
            var (peaks1m, valleys1m) = PivotHelper.CalculatePeaks(highs1m, lows1m, 5, 2);

            // 寻找 V 形态阻力 (做空观察)
            foreach (var peak in levels.Peaks)
            {
                // 🌟 使用 High 价格判断是否碰到了前高
                if (current1m.High >= peak * 0.998m && current1m.High <= peak * 1.005m)
                {
                    if (valleys1m.Any())
                    {
                        decimal recentLow = lows1m[valleys1m.Last()]; // 真正的微观谷底
                        if ((peak - recentLow) / recentLow > 0.02m) // 振幅必须大于 2%
                        {
                            _observations[symbol] = new VObservationState { IsShorting = true, PivotPrice = peak, CandlesWatched = 0, BaseAvgVolume = avgVol };
                            _logger.LogWarning($"👀 [{symbol}] 触碰前高 {peak:F4}, V形态底部 {recentLow:F4} (起涨振幅>2%)。切入空头观察...");
                            return;
                        }
                    }
                }
            }

            // 寻找倒 V 形态支撑 (做多观察)
            foreach (var valley in levels.Valleys)
            {
                // 🌟 使用 Low 价格判断是否碰到了前低
                if (current1m.Low <= valley * 1.002m && current1m.Low >= valley * 0.995m)
                {
                    if (peaks1m.Any())
                    {
                        decimal recentHigh = highs1m[peaks1m.Last()]; // 真正的微观山峰
                        if ((recentHigh - valley) / valley > 0.02m) // 振幅必须大于 2%
                        {
                            _observations[symbol] = new VObservationState { IsShorting = false, PivotPrice = valley, CandlesWatched = 0, BaseAvgVolume = avgVol };
                            _logger.LogWarning($"👀 [{symbol}] 触碰前低 {valley:F4}, 倒V形态顶部 {recentHigh:F4} (起跌振幅>2%)。切入多头观察...");
                            return;
                        }
                    }
                }
            }
        }

        private void ExecuteTrade(string symbol, VObservationState obs, IKline triggerKline)
        {
            _observations.TryRemove(symbol, out _);
            _lastTradeTime[symbol] = DateTime.Now;

            bool isLongSignal = !obs.IsShorting;
            string tradeType = isLongSignal ? "做多 (倒V支撑假突破)" : "做空 (V形态阻力假突破)";

            // 2.3.3 严格止损：前高点/低点外延 0.5%
            decimal stopLoss = isLongSignal ? obs.PivotPrice * 0.995m : obs.PivotPrice * 1.005m;
            decimal takeProfit = isLongSignal ? triggerKline.Close * 1.015m : triggerKline.Close * 0.985m; // 目标涨跌 1.5%

            _logger.LogWarning($"🎯 [{symbol}] {tradeType} 狙击成功！加速衰竭确认。SL: {stopLoss:F4}, TP: {takeProfit:F4}");

            decimal leverage = 10.0m;
            decimal requiredRoeTp = Math.Abs(takeProfit - triggerKline.Close) / triggerKline.Close * leverage;
            decimal requiredRoeSl = Math.Abs(triggerKline.Close - stopLoss) / triggerKline.Close * leverage;

            _ = Task.Run(async () =>
            {
                await PlaceOrderWithLeverageRiskAsync(
                    symbol, isLongSignal, triggerKline.Close, 1.50m, leverage, requiredRoeTp, requiredRoeSl, "V_Reversal_Hunter"
                );
            });

            // 绘图推送给前端
            // PublishChart(symbol, _1mBuffer[symbol], triggerKline, obs.PivotPrice, stopLoss, takeProfit, tradeType);
        }

        // ==========================================
        // 🌟 多周期支撑压力合并提取
        // ==========================================
        private void RecalculateMultiTimeframePivots(string symbol)
        {
            if (!_tfBuffers.TryGetValue(symbol, out var tfData)) return;

            var allHighs = new List<decimal>();
            var allLows = new List<decimal>();

            foreach (var tf in new[] { "3m", "5m" })
            {
                if (tfData.TryGetValue(tf, out var buffer) && buffer.Count > 10)
                {
                    lock (buffer)
                    {
                        // 🌟 严格传入 High / Low 集合交由 PivotHelper 计算宏观高低点
                        var highs = buffer.Select(k => (decimal)k.High).ToList();
                        var lows = buffer.Select(k => (decimal)k.Low).ToList();

                        var (pIdx, vIdx) = PivotHelper.CalculatePeaks(highs, lows, 3, 3);
                        allHighs.AddRange(pIdx.Select(i => highs[i]));
                        allLows.AddRange(vIdx.Select(i => lows[i]));
                    }
                }
            }

            _pivotLevels[symbol] = (
                allHighs.Distinct().OrderByDescending(x => x).ToList(),
                allLows.Distinct().OrderBy(x => x).ToList()
            );
        }

        private void PublishChart(string symbol, List<IKline> buffer1m, IKline current1m, decimal pivotPrice, decimal stopLoss, decimal takeProfit, string reason)
        {
            var chartItem = new ChartPublishItem
            {
                Symbol = symbol,
                StrategyName = "V_Reversal",
                Klines = buffer1m.TakeLast(150).ToList()
            };

            int currentKIndex = chartItem.Klines.Count - 1;

            chartItem.Points.Add((currentKIndex, current1m.Close, SkiaSharp.SKColors.Yellow, 8f));
            chartItem.Texts.Add((reason, Math.Max(0, currentKIndex - 30), current1m.High * 1.002m, SkiaSharp.SKColors.Yellow));

            chartItem.Lines.Add((0, pivotPrice, currentKIndex + 20, pivotPrice, SkiaSharp.SKColors.Purple, 2f));
            chartItem.Texts.Add(($"Pivot: {pivotPrice:F4}", 10, pivotPrice, SkiaSharp.SKColors.Purple));

            chartItem.Lines.Add((currentKIndex, stopLoss, currentKIndex + 20, stopLoss, SkiaSharp.SKColors.Red, 2f));
            chartItem.Texts.Add(($"SL: {stopLoss:F4}", currentKIndex + 2, stopLoss, SkiaSharp.SKColors.Red));

            chartItem.Lines.Add((currentKIndex, takeProfit, currentKIndex + 20, takeProfit, SkiaSharp.SKColors.Green, 2f));
            chartItem.Texts.Add(($"TP: {takeProfit:F4}", currentKIndex + 2, takeProfit, SkiaSharp.SKColors.Green));

            _ = _chartPublishService.PublishChartAsync(chartItem);
        }
    }
}