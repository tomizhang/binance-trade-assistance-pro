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
    /// 🌟 V形态/倒V形态 流动性回扫猎杀策略 (Liquidity Sweep Hunter) - 宏观大周期版
    /// 核心逻辑：基于 15m 和 1d 的大级别阻力/支撑，精准狙击做市商 +0.5%/-0.5% 的扫损假突破
    /// </summary>
    [System.ComponentModel.DisplayName("VReversal 反转策略 (1m入场+15m判定+1d周期)")]
    public class VReversalStrategyService : StrategyBase
    {
        private class VObservationState
        {
            public bool IsShorting { get; set; }        // true = 猎杀前高流动性(做空); false = 猎杀前低流动性(做多)
            public decimal PivotPrice { get; set; }     // 瞄准的前高/前低点 (来自30m/1h)
            public decimal SweepPrice { get; set; }     // 🌟 做市商回扫目标价 (前高+0.5% 或 前低-0.5%)

            public int CandlesWatched { get; set; }     // 已观察的时间 (盆底耗时)
            public double EntryAngle { get; set; }      // 入角 (到达关键点前的冲刺角度)
            public int MaxObservationCandles { get; set; } // 动态计算的最大过渡时间
        }

        private readonly IPositionManagementService _positionManager;
        private readonly ChartPublishService _chartPublishService;

        private readonly ConcurrentDictionary<string, List<IKline>> _1mBuffer = new();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, List<IKline>>> _tfBuffers = new();
        private readonly ConcurrentDictionary<string, (List<decimal> Peaks, List<decimal> Valleys)> _pivotLevels = new();

        private readonly ConcurrentDictionary<string, VObservationState> _observations = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastTradeTime = new();

        public VReversalStrategyService(
            ILogger<VReversalStrategyService> logger,
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

#if !DEBUG
            this.IsOrderEnabled = true; // 开启实盘执行
            this.IsStrategyEnabled = true;
#endif
            // 🌟 订阅 1m 用于微观狙击，订阅 15m 和 1d 用于宏观阻力/支撑
            this._timeframes = new[] { "1m", "15m", "1d" };
        }

        // ==========================================
        // 🌟 初始化与历史数据预热
        // ==========================================
        protected override async Task InitializeStrategyDataAsync(string symbol)
        {
            _logger.LogInformation($"[{GetType().Name}] 正在为 {symbol} 拉取宏观历史数据底库...");

            // 1m 扩大到 500 根，覆盖长达 8 小时的微观变动
            var klines1m = await FetchHistoryAsync(symbol, "1m", 500);
            _1mBuffer[symbol] = klines1m;

            var klines15m = await FetchHistoryAsync(symbol, "15m", 150);
            var klines1d = await FetchHistoryAsync(symbol, "1d", 150);

            var tfData = _tfBuffers.GetOrAdd(symbol, _ => new ConcurrentDictionary<string, List<IKline>>());
            if (klines15m.Any()) tfData["15m"] = klines15m;
            if (klines1d.Any()) tfData["1d"] = klines1d;

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
                    if (lastKline != null && lastKline.OpenTime == msg.OpenTime) buffer[buffer.Count - 1] = msg;
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

                if (msg.IsClosed) RecalculateMultiTimeframePivots(symbol);
            }
        }

        // ==========================================
        // 🌟 核心：回扫猎杀状态机
        // ==========================================
        private void CheckReversalConditions(string symbol, IKline currentTick, List<IKline> buffer1m)
        {
            if (_positionManager.HasActivePosition(symbol)) return;
            if (_lastTradeTime.TryGetValue(symbol, out var lastTime) && (GetCurrentTime() - lastTime).TotalMinutes < 5) return;

            // ----------------------------------------------------
            // 🛡️ 阶段 1：观察期，等待做市商触碰 +0.5%/-0.5% 的红线
            // ----------------------------------------------------
            if (_observations.TryGetValue(symbol, out var obs))
            {
                if (currentTick.IsClosed) obs.CandlesWatched++;

                if (obs.CandlesWatched > obs.MaxObservationCandles)
                {
                    _observations.TryRemove(symbol, out _);
                    _logger.LogInformation($"⏳ [{symbol}] 超过入角决定的过渡时间 ({obs.MaxObservationCandles}分钟)，未发生宏观流动性回扫，放弃入场。");
                    return;
                }

                if (obs.IsShorting) // 🌟 猎杀前高上方 +0.5% 的流动性
                {
                    if (currentTick.Close >= obs.SweepPrice)
                    {
                        ExecuteTrade(symbol, obs, currentTick);
                        return;
                    }
                    if (currentTick.Close < obs.PivotPrice * 0.995m) _observations.TryRemove(symbol, out _);
                }
                else // 🌟 猎杀前低下方 -0.5% 的流动性
                {
                    if (currentTick.Close <= obs.SweepPrice)
                    {
                        ExecuteTrade(symbol, obs, currentTick);
                        return;
                    }
                    if (currentTick.Close > obs.PivotPrice * 1.005m) _observations.TryRemove(symbol, out _);
                }
                return;
            }

            // ----------------------------------------------------
            // 🚀 阶段 2：扫描点位接触，计算入角，分配盆底时间，挂载虚拟扫损单
            // ----------------------------------------------------
            if (!_pivotLevels.TryGetValue(symbol, out var levels)) return;

            var highs1m = buffer1m.Select(k => (decimal)k.High).ToList();
            var lows1m = buffer1m.Select(k => (decimal)k.Low).ToList();
            var (peaks1m, valleys1m) = PivotHelper.CalculatePeaks(highs1m, lows1m, 5, 2);

            // 寻找宏观前高阻力接触
            foreach (var peak in levels.Peaks)
            {
                if (currentTick.Close >= peak * 0.998m && currentTick.Close <= peak * 1.002m)
                {
                    if (valleys1m.Any())
                    {
                        int recentLowIdx = valleys1m.Last();
                        decimal recentLow = lows1m[recentLowIdx];

                        if ((peak - recentLow) / recentLow > 0.02m)
                        {
                            int xDistance = Math.Max(1, buffer1m.Count - recentLowIdx);
                            double entryAngle = SlopeHelper.CalculateNormalizedAngle(recentLow, currentTick.Close, xDistance);
                            int maxObs = CalculateDynamicTransitionTime(Math.Abs(entryAngle));

                            decimal sweepPrice = peak * 1.005m;

                            _observations[symbol] = new VObservationState
                            {
                                IsShorting = true,
                                PivotPrice = peak,
                                SweepPrice = sweepPrice,
                                CandlesWatched = 0,
                                EntryAngle = entryAngle,
                                MaxObservationCandles = maxObs
                            };

                            _logger.LogWarning($"👀 [{symbol}] 逼近 15m/1d 宏观前高 {peak:F4}。入角:{entryAngle:F1}°。部署流动性红线: {sweepPrice:F4} (+0.5%)");
                            return;
                        }
                    }
                }
            }

            // 寻找宏观前低支撑接触
            foreach (var valley in levels.Valleys)
            {
                if (currentTick.Close <= valley * 1.002m && currentTick.Close >= valley * 0.998m)
                {
                    if (peaks1m.Any())
                    {
                        int recentHighIdx = peaks1m.Last();
                        decimal recentHigh = highs1m[recentHighIdx];

                        if ((recentHigh - valley) / valley > 0.02m)
                        {
                            int xDistance = Math.Max(1, buffer1m.Count - recentHighIdx);
                            double entryAngle = SlopeHelper.CalculateNormalizedAngle(recentHigh, currentTick.Close, xDistance);
                            int maxObs = CalculateDynamicTransitionTime(Math.Abs(entryAngle));

                            decimal sweepPrice = valley * 0.995m;

                            _observations[symbol] = new VObservationState
                            {
                                IsShorting = false,
                                PivotPrice = valley,
                                SweepPrice = sweepPrice,
                                CandlesWatched = 0,
                                EntryAngle = entryAngle,
                                MaxObservationCandles = maxObs
                            };

                            _logger.LogWarning($"👀 [{symbol}] 逼近 15m/1d 宏观前低 {valley:F4}。入角:{entryAngle:F1}°。部署流动性红线: {sweepPrice:F4} (-0.5%)");
                            return;
                        }
                    }
                }
            }
        }

        private int CalculateDynamicTransitionTime(double absAngle)
        {
            if (absAngle >= 85) return 2;
            if (absAngle >= 65) return 4;
            if (absAngle >= 35) return 6;
            if (absAngle >= 15) return 10;
            return 15;
        }

        // ==========================================
        // 🌟 交易执行
        // ==========================================
        private void ExecuteTrade(string symbol, VObservationState obs, IKline triggerTick)
        {
            _observations.TryRemove(symbol, out _);
            _lastTradeTime[symbol] = GetCurrentTime();

            bool isLongSignal = !obs.IsShorting;
            string tradeType = isLongSignal ? "做多 (吞噬大周期假跌破)" : "做空 (吞噬大周期假突破)";

            // 🌟 核心优化：止盈止损逻辑调换
            // 止损放宽到 1.5%，止盈缩小到 0.5% 
            decimal stopLoss = isLongSignal ? obs.SweepPrice * 0.993m : obs.SweepPrice * 1.007m;
            decimal takeProfit = isLongSignal ? obs.SweepPrice * 1.005m : obs.SweepPrice * 0.995m;

            _logger.LogWarning($"🎯 [{symbol}] {tradeType}！大级别做市商回扫完成。入场价: {triggerTick.Close:F4}。SL: {stopLoss:F4}(-1.5%), TP: {takeProfit:F4}(+0.5%)");

            decimal leverage = GetLeverage(5.0m);
            decimal requiredRoeTp = Math.Abs(takeProfit - triggerTick.Close) / triggerTick.Close * leverage;
            decimal requiredRoeSl = Math.Abs(triggerTick.Close - stopLoss) / triggerTick.Close * leverage;

            _ = Task.Run(async () =>
            {
                await PlaceOrderWithLeverageRiskAsync(
                    symbol, isLongSignal, triggerTick.Close, 1.5m, leverage, requiredRoeTp, requiredRoeSl, "Macro_Sweep_Hunter_Inverted", OrderAction.OpenLimit
                );
            });
        }

        // ==========================================
        // 🌟 15m/1d 宏观支撑压力合并提取 
        // ==========================================
        private void RecalculateMultiTimeframePivots(string symbol)
        {
            if (!_tfBuffers.TryGetValue(symbol, out var tfData)) return;

            var allHighs = new List<decimal>();
            var allLows = new List<decimal>();

            foreach (var tf in new[] { "15m", "1d" })
            {
                if (tfData.TryGetValue(tf, out var buffer) && buffer.Count > 10)
                {
                    lock (buffer)
                    {
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
    }
}