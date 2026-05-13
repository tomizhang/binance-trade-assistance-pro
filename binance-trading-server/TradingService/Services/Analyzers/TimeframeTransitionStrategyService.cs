using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TradingTerminal.Hubs;
using TradingTerminal.Models;
using TradingTerminal.Utils;

namespace TradingTerminal.Services
{
    public class TimeframeTransitionStrategyService : StrategyBase
    {
        // --- 核心风控与过滤参数 ---
        private readonly int _consecutiveCountThreshold = 5; // 连跌/连涨 K 线数量阈值
        private readonly decimal _minVolatilityPercent = 0.01m; // M/W 波段的最小允许波动率 0.5%
        private readonly decimal _hugeMoveAtrMultiplier = 3.0m; // 连续 5 根总幅度需大于 ATR 的 3 倍才算暴跌/暴涨
        private readonly decimal _pinBarShadowRatio = 2.0m; // 影线至少是实体的 2 倍
        private readonly decimal _volumeSurgeMultiplier = 1.5m; // 放量要求 1.5 倍
        private readonly decimal _tickDominationRatio = 0.55m; // 爆仓单需要至少 55% 的主动买盘(做多)或卖盘(做空)
        private readonly decimal _fixedSlipPercent = 0.005m; // 止损在极值点外再加 0.5% 防扎针

        private readonly string[] _timeframes = new[] { "3m", "5m", "15m", "30m", "1h" };
        private readonly ConcurrentDictionary<string, List<KlineMessage>> _1mBuffer = new();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, List<KlineMessage>>> _tfBuffers = new();

        private readonly PositionManagementService _positionManager;
        private readonly ChartPublishService _chartPublishService;
        private readonly ConcurrentDictionary<string, DateTime> _lastTradeTime = new();
        private readonly ConcurrentDictionary<string, ObservationState> _observingStates = new();

        private class ObservationState
        {
            public string Symbol { get; set; }
            public string ObservationTimeframe { get; set; } // "1m" 或 "3m"
            public bool IsLookingForLong { get; set; }
            public long StartedAt { get; set; }
        }

        public TimeframeTransitionStrategyService(
            ILogger<TimeframeTransitionStrategyService> logger,
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
            this.IsOrderEnabled = true;
        }

        protected override void OnKlineReceived(KlineMessage msg)
        {
            string symbol = msg.Symbol;

            var buffer1m = _1mBuffer.GetOrAdd(symbol, _ => new List<KlineMessage>());

            // 1. 无论 K 线是否收盘，只要处在观察期内，我们都使用其实时跳动数据去寻找清扫点位（提前入场）
            if (buffer1m.Count >= 20)
            {
                CheckObservationState(symbol, msg, buffer1m);
            }

            if (!msg.IsClosed) return; // 历史缓冲区的合并必须等待 K 线正式收盘

            lock (buffer1m)
            {
                buffer1m.Add(msg);
                if (buffer1m.Count > 1500) buffer1m.RemoveAt(0);
            }

            foreach (var tf in _timeframes)
            {
                UpdateTimeframeBuffer(symbol, tf, msg);
            }

            if (buffer1m.Count < 20) return;

            // 2. 检查大周期是否产生了“连续单边情绪耗竭”并准备进入观察期
            CheckTimeframeTransition(symbol, msg.OpenTime);
        }

        private void CheckTimeframeTransition(string symbol, long current1mTime)
        {
            if (!_tfBuffers.TryGetValue(symbol, out var symbolData)) return;

            // 检查 15m 级别的连跌 -> 准备在 3m 找机会
            if (symbolData.TryGetValue("15m", out var buffer15m) && buffer15m.Count > 30)
            {
                // 如果当前时间正好是 15m 周期的尾巴 (即下一分钟就是整 15 分)
                if ((current1mTime + 60000) % (15 * 60 * 1000) == 0)
                {
                    EvaluateAccumulation(symbol, buffer15m, "15m", "3m");
                }
            }

            // 检查 3m 级别的连跌 -> 准备在 1m 找机会
            if (symbolData.TryGetValue("3m", out var buffer3m) && buffer3m.Count > 30)
            {
                if ((current1mTime + 60000) % (3 * 60 * 1000) == 0)
                {
                    EvaluateAccumulation(symbol, buffer3m, "3m", "1m");
                }
            }
        }

        private void EvaluateAccumulation(string symbol, List<KlineMessage> buffer, string sourceTf, string targetTf)
        {
            var recent = buffer.TakeLast(_consecutiveCountThreshold).ToList();
            if (recent.Count < _consecutiveCountThreshold) return;

            bool isAllBearish = recent.All(k => k.Close < k.Open);
            bool isAllBullish = recent.All(k => k.Close > k.Open);

            if (!isAllBearish && !isAllBullish) return; // 没有连续单边情绪

            // 计算动态 ATR 用于判断暴跌幅度和动态止盈
            decimal avgAtr = CalculateATR(buffer.TakeLast(20).ToList());
            if (avgAtr <= 0) return;

            decimal totalMove = Math.Abs(recent.First().Open - recent.Last().Close);

            // 过滤：总移动幅度必须大于 X 倍的 ATR，否则只是无波澜的阴跌
            if (totalMove < avgAtr * _hugeMoveAtrMultiplier) return;

            // 过滤：大周期震荡形态检查 (M/W 形态) - 使用 HA 均线平滑
            if (!HasNormalFluctuations(buffer, avgAtr)) return;

            // 如果通过所有过滤，进入观察期寻找小级别流动性清扫
            _observingStates[symbol] = new ObservationState
            {
                Symbol = symbol,
                ObservationTimeframe = targetTf,
                IsLookingForLong = isAllBearish, // 连跌做多，连涨做空
                StartedAt = recent.Last().OpenTime
            };

            _logger.LogInformation($"🔍 [{symbol}] {sourceTf} 发生剧烈单边行情 (连续 {_consecutiveCountThreshold} 根)，进入 {targetTf} 观察期寻找反转机会。");
        }

        private void CheckObservationState(string symbol, KlineMessage msg1m, List<KlineMessage> buffer1m)
        {
            if (!_observingStates.TryGetValue(symbol, out var obs)) return;

            // 观察期如果超过 15 根目标 K 线没找到机会，就放弃
            int timeoutMinutes = obs.ObservationTimeframe == "3m" ? 45 : 15;
            if ((msg1m.OpenTime - obs.StartedAt) / 60000 > timeoutMinutes)
            {
                _observingStates.TryRemove(symbol, out _);
                return;
            }

            if (_positionManager.HasAnyActivePosition())
            {
                _observingStates.TryRemove(symbol, out _);
                return;
            }

            if (_lastTradeTime.TryGetValue(symbol, out var lastTime) && (DateTime.Now - lastTime).TotalMinutes < 5) return;

            // 取决于要在什么周期找机会，我们取出对应的 K 线去判断
            KlineMessage evalKline = msg1m;
            List<KlineMessage> contextBuffer = buffer1m;

            if (obs.ObservationTimeframe == "3m")
            {
                if (!_tfBuffers.TryGetValue(symbol, out var tfDataState) || !tfDataState.TryGetValue("3m", out var buffer3m) || buffer3m.Count < 10) return;
                
                var last3m = buffer3m.Last();
                long bucket3m = msg1m.OpenTime - (msg1m.OpenTime % (3 * 60 * 1000));
                
                if (last3m.OpenTime == bucket3m)
                {
                    // 动态合并当前未收盘的实时 1m K 线，以此得出“当前跳动中的 3m K线”
                    evalKline = new KlineMessage
                    {
                        OpenTime = last3m.OpenTime,
                        Open = last3m.Open,
                        High = Math.Max(last3m.High, msg1m.High),
                        Low = Math.Min(last3m.Low, msg1m.Low),
                        Close = msg1m.Close,
                        Volume = last3m.Volume + msg1m.Volume,
                        TakerBuyBaseVolume = last3m.TakerBuyBaseVolume + msg1m.TakerBuyBaseVolume
                    };
                }
                else
                {
                    // 此时跳动的 1m 是一根全新的 3m 起点
                    evalKline = msg1m;
                }
                
                contextBuffer = buffer3m;
            }

            // 判断流动性清扫/爆仓特征
            decimal body = Math.Abs(evalKline.Close - evalKline.Open);
            decimal upperShadow = evalKline.High - Math.Max(evalKline.Open, evalKline.Close);
            decimal lowerShadow = Math.Min(evalKline.Open, evalKline.Close) - evalKline.Low;

            decimal avgVol = contextBuffer.TakeLast(11).Take(10).Average(k => k.Volume);
            bool isVolumeSurge = evalKline.Volume > avgVol * _volumeSurgeMultiplier;

            // 买卖主动比例：Tick Ratio 映射 (主动买入量 / 总量)
            decimal buyRatio = evalKline.Volume > 0 ? evalKline.TakerBuyBaseVolume / evalKline.Volume : 0m;
            decimal sellRatio = 1m - buyRatio;

            // 🌟 核心修复：基于 3m 和 5m 周期的综合 ATR 计算波动率，决定目标 ROE
            decimal smallAtr = 0m;
            if (_tfBuffers.TryGetValue(symbol, out var tfDataForVol))
            {
                var buffer3m = tfDataForVol.TryGetValue("3m", out var b3) ? b3 : new List<KlineMessage>();
                var buffer5m = tfDataForVol.TryGetValue("5m", out var b5) ? b5 : new List<KlineMessage>();
                
                decimal atr3m = CalculateATR(buffer3m.TakeLast(20).ToList());
                decimal atr5m = CalculateATR(buffer5m.TakeLast(20).ToList());
                
                if (atr3m > 0 && atr5m > 0) smallAtr = (atr3m + atr5m) / 2m;
                else if (atr3m > 0) smallAtr = atr3m;
                else if (atr5m > 0) smallAtr = atr5m;
            }
            
            if (smallAtr == 0) smallAtr = CalculateATR(contextBuffer.TakeLast(20).ToList()); // 兜底

            decimal smallVolatility = smallAtr / msg1m.Close;

            // 根据小周期波动率动态设定目标 ROE (要求：大波动 50%平仓，小波动 15%平仓)
            decimal targetRoeTp = smallVolatility >= 0.003m ? 0.50m : 0.15m;
            decimal riskRoeSl = smallVolatility >= 0.003m ? 0.30m : 0.10m; // 调整对应的止损比例维持盈亏比

            decimal leverage = 5.0m;
            decimal priceChangeTp = targetRoeTp / leverage;
            decimal priceChangeSl = riskRoeSl / leverage;

            bool triggered = false;
            decimal stopLoss = 0m;
            decimal tpPrice = 0m;
            string reason = "";

            if (obs.IsLookingForLong)
            {
                // 寻找插针做多：长下影线 + 爆量 + 买盘主动
                if (lowerShadow > body * _pinBarShadowRatio && isVolumeSurge && buyRatio > _tickDominationRatio)
                {
                    triggered = true;
                    stopLoss = msg1m.Close * (1 - priceChangeSl);
                    tpPrice = msg1m.Close * (1 + priceChangeTp);
                    reason = $"清扫做多: 小周期波幅={smallVolatility:P2}, 买盘={buyRatio:P}, 设防={riskRoeSl:P0}/{targetRoeTp:P0}";
                }
            }
            else
            {
                // 寻找插针做空：长上影线 + 爆量 + 卖盘主动
                if (upperShadow > body * _pinBarShadowRatio && isVolumeSurge && sellRatio > _tickDominationRatio)
                {
                    triggered = true;
                    stopLoss = msg1m.Close * (1 + priceChangeSl);
                    tpPrice = msg1m.Close * (1 - priceChangeTp);
                    reason = $"清扫做空: 小周期波幅={smallVolatility:P2}, 卖盘={sellRatio:P}, 设防={riskRoeSl:P0}/{targetRoeTp:P0}";
                }
            }

            if (triggered)
            {
                _observingStates.TryRemove(symbol, out _);
                _lastTradeTime[symbol] = DateTime.Now;

                _logger.LogWarning($"🔥 [{symbol}] {reason}！触发小周期入场。SL: {stopLoss:F4}, TP: {tpPrice:F4}");

                _ = Task.Run(async () =>
                {
                    // 传入 targetRoeTp 和 riskRoeSl (这是小数比例，如 0.5 代表 50%)
                    await PlaceOrderWithLeverageRiskAsync(
                        symbol, obs.IsLookingForLong, msg1m.Close, 1.5m, leverage, targetRoeTp, riskRoeSl, "LiquiditySweep"
                    );
                });

                // 画图发复盘记录，传入百分比用于显示
                PublishChart(symbol, buffer1m, msg1m, obs.IsLookingForLong, stopLoss, targetRoeTp * 100m, reason);
            }
        }

        private void PublishChart(string symbol, List<KlineMessage> buffer1m, KlineMessage entryKline, bool isLong, decimal slPrice, decimal tpPercent, string reason)
        {
            var chartItem = new ChartPublishItem
            {
                Symbol = symbol,
                StrategyName = "LiquiditySweep",
                Klines = buffer1m.TakeLast(500).ToList() // 显示过去 500 分钟
            };

            int entryIdx = chartItem.Klines.Count - 1;
            decimal tpPrice = isLong ? entryKline.Close * (1 + tpPercent / 100m) : entryKline.Close * (1 - tpPercent / 100m);

            chartItem.Points.Add((entryIdx, entryKline.Close, isLong ? SkiaSharp.SKColors.Green : SkiaSharp.SKColors.Red, 10f));
            chartItem.Texts.Add((reason, Math.Max(0, entryIdx - 40), entryKline.High * 1.002m, SkiaSharp.SKColors.Yellow));

            chartItem.Lines.Add((entryIdx, slPrice, entryIdx + 15, slPrice, SkiaSharp.SKColors.Red, 2f));
            chartItem.Texts.Add(($"SL: {slPrice:F4}", entryIdx + 2, slPrice, SkiaSharp.SKColors.Red));

            chartItem.Lines.Add((entryIdx, tpPrice, entryIdx + 15, tpPrice, SkiaSharp.SKColors.Green, 2f));
            chartItem.Texts.Add(($"TP({tpPercent:F2}%): {tpPrice:F4}", entryIdx + 2, tpPrice, SkiaSharp.SKColors.Green));

            _ = _chartPublishService.PublishChartAsync(chartItem);
        }

        // ==========================================
        // 辅助算法
        // ==========================================
        private bool HasNormalFluctuations(List<KlineMessage> buffer, decimal minVolatilityRequired)
        {
            if (buffer.Count < 30) return false;

            // 1. 获取过去 30 根数据
            var recentData = buffer.TakeLast(30).ToList();

            // 2. 使用 HA 平均线平滑处理噪音
            var haData = CumulativeHeikinAshiHelper.Calculate(recentData);
            var highs = haData.Select(h => h.High).ToList();
            var lows = haData.Select(h => h.Low).ToList();

            // 3. 计算极值点
            var (peaks, valleys) = PivotHelper.CalculatePeaks(highs, lows, 2, 2);

            // 4. 至少需要 3 个转折点才能构成 M 或 W
            if (peaks.Count + valleys.Count < 3) return false;

            // 5. 检查波峰波谷之间的波动幅度
            if (peaks.Any() && valleys.Any())
            {
                decimal maxPeak = highs[peaks.Max()];
                decimal minValley = lows[valleys.Max()];
                decimal volatility = (maxPeak - minValley) / minValley;
                return volatility >= minVolatilityRequired; // 必须大于最小要求波动率
            }
            return false;
        }

        private decimal CalculateATR(List<KlineMessage> klines)
        {
            if (klines.Count < 2) return 0m;
            decimal sumTr = 0m;
            for (int i = 1; i < klines.Count; i++)
            {
                decimal tr1 = klines[i].High - klines[i].Low;
                decimal tr2 = Math.Abs(klines[i].High - klines[i - 1].Close);
                decimal tr3 = Math.Abs(klines[i].Low - klines[i - 1].Close);
                decimal trueRange = Math.Max(tr1, Math.Max(tr2, tr3));
                sumTr += trueRange;
            }
            return sumTr / (klines.Count - 1);
        }

        private void UpdateTimeframeBuffer(string symbol, string tf, KlineMessage msg1m)
        {
            var symbolData = _tfBuffers.GetOrAdd(symbol, _ => new ConcurrentDictionary<string, List<KlineMessage>>());
            var buffer = symbolData.GetOrAdd(tf, _ => new List<KlineMessage>());

            long intervalMs = GetIntervalMs(tf);
            long bucketTime = msg1m.OpenTime - (msg1m.OpenTime % intervalMs);

            lock (buffer)
            {
                var lastKline = buffer.LastOrDefault();
                if (lastKline != null && lastKline.OpenTime == bucketTime)
                {
                    lastKline.High = Math.Max(lastKline.High, msg1m.High);
                    lastKline.Low = Math.Min(lastKline.Low, msg1m.Low);
                    lastKline.Close = msg1m.Close;
                    lastKline.Volume += msg1m.Volume;
                    lastKline.TakerBuyBaseVolume += msg1m.TakerBuyBaseVolume; // 累加主动买盘
                }
                else
                {
                    buffer.Add(new KlineMessage
                    {
                        Symbol = msg1m.Symbol,
                        OpenTime = bucketTime,
                        High = msg1m.High,
                        Low = msg1m.Low,
                        Open = msg1m.Open,
                        Close = msg1m.Close,
                        Volume = msg1m.Volume,
                        TakerBuyBaseVolume = msg1m.TakerBuyBaseVolume,
                        IsClosed = true
                    });
                    if (buffer.Count > 100) buffer.RemoveAt(0); // 大周期仅需 100 根用于 M/W 计算
                }
            }
        }

        private long GetIntervalMs(string tf)
        {
            return tf switch
            {
                "3m" => 3 * 60 * 1000,
                "5m" => 5 * 60 * 1000,
                "15m" => 15 * 60 * 1000,
                "30m" => 30 * 60 * 1000,
                "1h" => 60 * 60 * 1000,
                _ => 60 * 1000
            };
        }

        protected override async Task InitializeStrategyDataAsync(string symbol)
        {
            foreach (var tf in _timeframes)
            {
                try
                {
                    string json = await _wsService.GetHistoricalKlinesAsync(symbol, tf, 50);
                    using var doc = JsonDocument.Parse(json);
                    var historyList = new List<KlineMessage>();

                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        historyList.Add(new KlineMessage
                        {
                            Symbol = symbol,
                            OpenTime = item[0].GetInt64(),
                            Open = decimal.Parse(item[1].GetString()),
                            High = decimal.Parse(item[2].GetString()),
                            Low = decimal.Parse(item[3].GetString()),
                            Close = decimal.Parse(item[4].GetString()),
                            Volume = decimal.Parse(item[5].GetString()),
                            TakerBuyBaseVolume = decimal.Parse(item[9].GetString()), // 币安API中 Taker buy base asset volume 是第10个元素 (索引9)
                            IsClosed = true
                        });
                    }

                    var symbolData = _tfBuffers.GetOrAdd(symbol, _ => new ConcurrentDictionary<string, List<KlineMessage>>());
                    symbolData[tf] = historyList;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ {symbol} {tf} 初始化失败: {ex.Message}");
                }
            }
        }
    }
}
