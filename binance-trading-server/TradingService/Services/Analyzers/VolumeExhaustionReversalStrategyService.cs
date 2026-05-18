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
    public class VolumeExhaustionReversalStrategyService : StrategyBase
    {
        private readonly PositionManagementService _positionManager;
        private readonly ChartPublishService _chartPublishService;

        private readonly ConcurrentDictionary<string, List<IKline>> _1mBuffer = new();
        // 存储多周期聚合 K 线：3m, 5m, 1h
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, List<IKline>>> _tfBuffers = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastTradeTime = new();

        // 策略参数
        private readonly decimal _volumeSurgeMultiplier = 3.0m; // 1分钟爆量倍数
        private readonly int _trendCandleCount = 5; // 3m/5m 至少 5 根连跌
        private readonly decimal _supportZoneTolerance = 0.005m; // 1小时开盘价上下 0.5% 内视为附近
        private readonly decimal _fixedStopLossDistance = 0.005m; // 固定止损距离 0.5%
        private readonly decimal _targetPriceChange = 0.01m; // 止盈目标为标的物实际涨幅 1%
        private readonly double _rSquaredThreshold = 0.65; // 拟合优度阈值
        private readonly double _pinBarZoneThreshold = 0.5; // 如果 > 50% 的 K 线处于极端插针区，视为破坏

        public VolumeExhaustionReversalStrategyService(
            ILogger<VolumeExhaustionReversalStrategyService> logger,
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
            this.IsOrderEnabled = false;
            IsStrategyEnabled = false;
            // 订阅 1m 用于细颗粒度，并同时直接订阅 3m, 5m, 1h 以实现原生实时数据流
            this._timeframes = new[] { "1m", "3m", "5m", "1h" };
        }

        protected override async Task InitializeStrategyDataAsync(string symbol)
        {
            _logger.LogInformation($"[{GetType().Name}] 正在为 {symbol} 拉取历史数据...");

            var klines1m = await FetchHistoryAsync(symbol, "1m", 1500);
            _1mBuffer[symbol] = klines1m;

            var klines3m = await FetchHistoryAsync(symbol, "3m", 1500);
            var klines5m = await FetchHistoryAsync(symbol, "5m", 1500);
            // var klines1h = await FetchHistoryAsync(symbol, "1h", 1500);

            var tfData = _tfBuffers.GetOrAdd(symbol, _ => new ConcurrentDictionary<string, List<IKline>>());
            if (klines3m.Any()) tfData["3m"] = klines3m;
            if (klines5m.Any()) tfData["5m"] = klines5m;
            // if (klines1h.Any()) tfData["1h"] = klines1h;
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
                        OpenTime = item[0].GetInt64(),
                        Open = decimal.Parse(item[1].GetString()),
                        High = decimal.Parse(item[2].GetString()),
                        Low = decimal.Parse(item[3].GetString()),
                        Close = decimal.Parse(item[4].GetString()),
                        Volume = decimal.Parse(item[5].GetString()),
                        TradeCount = item[8].GetInt32(),
                        TakerBuyBaseVolume = decimal.Parse(item[9].GetString()),
                        IsClosed = true
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

                // 无论是收盘还是未收盘，都用实时 1m K 线去检查条件
                if (buffer1m.Count >= 50)
                {
                    CheckReversalConditions(symbol, msg, buffer1m);
                }

                if (!msg.IsClosed) return; // 历史缓冲区合并必须等待正式收盘

                lock (buffer1m)
                {
                    buffer1m.Add(msg);
                    if (buffer1m.Count > 1000) buffer1m.RemoveAt(0);
                }
            }
            else
            {
                // 实时维护 1m 以外的时间周期 K 线 (3m, 5m, 1h)
                var symbolData = _tfBuffers.GetOrAdd(symbol, _ => new ConcurrentDictionary<string, List<IKline>>());
                var buffer = symbolData.GetOrAdd(interval, _ => new List<IKline>());

                lock (buffer)
                {
                    var lastKline = buffer.LastOrDefault();
                    if (lastKline != null && lastKline.OpenTime == msg.OpenTime)
                    {
                        // 覆盖当前正在跳动的周期 K 线
                        buffer[buffer.Count - 1] = msg;
                    }
                    else if (lastKline == null || msg.OpenTime > lastKline.OpenTime)
                    {
                        // 进入新周期，添加新的 K 线
                        buffer.Add(new KlineMessage
                        {
                            Symbol = msg.Symbol,
                            Interval = msg.Interval,
                            IsClosed = msg.IsClosed,
                            Open = msg.Open,
                            High = msg.High,
                            Low = msg.Low,
                            Close = msg.Close,
                            Volume = msg.Volume,
                            OpenTime = msg.OpenTime,
                            TradeCount = msg.TradeCount,
                            TakerBuyBaseVolume = msg.TakerBuyBaseVolume
                        });
                    }

                    if (buffer.Count > 200) buffer.RemoveAt(0);
                }
            }
        }

        private void CheckReversalConditions(string symbol, IKline current1m, List<IKline> buffer1m)
        {
            if (_positionManager.HasAnyActivePosition()) return;
            if (_lastTradeTime.TryGetValue(symbol, out var lastTime) && (DateTime.Now - lastTime).TotalMinutes < 5) return;

            if (!_tfBuffers.TryGetValue(symbol, out var tfData)) return;
            if (!tfData.TryGetValue("3m", out var buffer3m) || buffer3m.Count < _trendCandleCount + 1) return;
            if (!tfData.TryGetValue("5m", out var buffer5m) || buffer5m.Count < _trendCandleCount + 1) return;
            // if (!tfData.TryGetValue("1h", out var buffer1h) || buffer1h.Count < 2) return;

            // 1. 检查 1 分钟大成交量
            decimal avgVol = buffer1m.TakeLast(51).Take(50).Average(k => k.Volume);
            if (current1m.Volume <= avgVol * _volumeSurgeMultiplier) return;

            // 2. 检查 3 分钟和 5 分钟连续趋势结构
            // bool isDownward3m = IsDownwardStructure(buffer3m, _trendCandleCount);
            bool isDownward5m = IsDownwardStructure(buffer5m, _trendCandleCount);

            // bool isUpward3m = IsUpwardStructure(buffer3m, _trendCandleCount);
            bool isUpward5m = IsUpwardStructure(buffer5m, _trendCandleCount);

            bool isLongSignal = /*isDownward3m &&*/ isDownward5m;
            bool isShortSignal = /*isUpward3m &&*/ isUpward5m;

            // 如果没有命中多空任何一种结构，则返回
            if (!isLongSignal && !isShortSignal) return;

            // 3. 检查是否在 1 小时开盘价附近
            // 因为现在 1h 是原生订阅的，Last() 就是实时的 1h K 线
            // var current1h = buffer1h.Last();

            // decimal openPrice1h = current1h.Open;
            // decimal deviation = Math.Abs(current1m.Close - openPrice1h) / openPrice1h;

            // if (deviation > _supportZoneTolerance) return; // 距离 1h 开仓价太远，不是有效的支撑位

            // --- 满足所有条件，触发开仓 ---
            _lastTradeTime[symbol] = DateTime.Now;

            decimal stopLoss = isLongSignal
                ? current1m.Low * (1 - _fixedStopLossDistance)
                : current1m.High * (1 + _fixedStopLossDistance);

            decimal takeProfit = isLongSignal
                ? current1m.Close * (1 + _targetPriceChange)
                : current1m.Close * (1 - _targetPriceChange);

            string direction = isLongSignal ? "做多" : "做空";
            string structureStr = isLongSignal ? "连跌" : "连涨";
            string triggerReason = $"1m爆量({current1m.Volume / avgVol:F1}x) + 3/5m{structureStr}";

            _logger.LogWarning($"🎯 [{symbol}] {triggerReason}！触发{direction}反转。SL: {stopLoss:F4}, TP: {takeProfit:F4}");

            // 执行下单（计算 ROE：本金涨幅 % * 杠杆）
            decimal leverage = 5.0m;
            decimal requiredRoeTp = _targetPriceChange * leverage; // 1% 涨幅对应 5% ROE
            decimal requiredRoeSl = _fixedStopLossDistance * leverage; // 0.5% 跌幅对应 2.5% ROE

            _ = Task.Run(async () =>
            {
                await PlaceOrderWithLeverageRiskAsync(
                    symbol, isLongSignal, current1m.Close, 1.5m, leverage, requiredRoeTp, requiredRoeSl, "VolumeExhaustionReversal"
                );
            });

            // 绘图推送
            // PublishChart(symbol, buffer1m, current1m, openPrice1h, stopLoss, takeProfit, triggerReason);
        }

        private bool IsDownwardStructure(List<IKline> buffer, int count)
        {
            var contextBuffer = buffer.ToList();
            var recentData = contextBuffer.TakeLast(count + 1).ToList();
            if (recentData.Count < count) return false;

            // 采用 HA 均线处理以过滤一部分毛刺
            var haData = CumulativeHeikinAshiHelper.Calculate(recentData);
            var evalHa = haData.TakeLast(count).ToList();

            // 1. 使用 HA 的 High 价进行回归拟合
            double[] y = evalHa.Select(k => (double)k.High).ToArray();
            double[] x = Enumerable.Range(0, count).Select(i => (double)i).ToArray();

            CalculateLinearRegression(x, y, out double slope, out double intercept, out double rSquared);

            // 2. 判定整体趋势向下且 R² 达标
            double avgPrice = y.Average();
            double normalizedSlope = slope / avgPrice;
            if (normalizedSlope >= -0.0001) return false; // 斜率不够向下，非明显跌势
            if (rSquared < _rSquaredThreshold) return false; // 趋势不够平滑，R²不达标

            // 3. 构建插针平行线并统计区间
            double maxPositiveResidual = 0;
            for (int i = 0; i < count; i++)
            {
                double lineValue = slope * i + intercept;
                double residual = y[i] - lineValue;
                if (residual > maxPositiveResidual) maxPositiveResidual = residual;
            }

            // 定义插针高危区间的底部边界 (取最大偏离度的一半作为界限)
            // 回归线到最高插针点这半壁江山中，取最极端的上半区
            double dangerZoneLowerBound = maxPositiveResidual * 0.5;
            int klinesInDangerZone = 0;

            for (int i = 0; i < count; i++)
            {
                double lineValue = slope * i + intercept;
                double residual = y[i] - lineValue;

                // 如果这根 K 线的 High 落入了插针极端区
                if (residual > dangerZoneLowerBound)
                {
                    klinesInDangerZone++;
                }
            }

            // 4. 判断是否破坏结构
            if ((double)klinesInDangerZone / count > _pinBarZoneThreshold)
            {
                return false; // 超过 50% 的 K 线在高危区，说明不是单一插针，是阻力位横盘，形态破坏
            }

            return true;
        }

        private bool IsUpwardStructure(List<IKline> buffer, int count)
        {
            var contextBuffer = buffer.ToList();
            var recentData = contextBuffer.TakeLast(count + 1).ToList();
            if (recentData.Count < count) return false;

            // 采用 HA 均线处理以过滤一部分毛刺
            var haData = CumulativeHeikinAshiHelper.Calculate(recentData);
            var evalHa = haData.TakeLast(count).ToList();

            // 1. 使用 HA 的 Low 价进行回归拟合
            double[] y = evalHa.Select(k => (double)k.Low).ToArray();
            double[] x = Enumerable.Range(0, count).Select(i => (double)i).ToArray();

            CalculateLinearRegression(x, y, out double slope, out double intercept, out double rSquared);

            // 2. 判定整体趋势向上且 R² 达标
            double avgPrice = y.Average();
            double normalizedSlope = slope / avgPrice;
            if (normalizedSlope <= 0.0001) return false; // 斜率不够向上，非明显涨势
            if (rSquared < _rSquaredThreshold) return false; // 趋势不够平滑，R²不达标

            // 3. 构建向下插针平行线并统计区间
            double minNegativeResidual = 0;
            for (int i = 0; i < count; i++)
            {
                double lineValue = slope * i + intercept;
                double residual = y[i] - lineValue;
                // 向上趋势中的插针是向下探底，因此残差是负数
                if (residual < minNegativeResidual) minNegativeResidual = residual;
            }

            // 定义向下插针高危区间的上部边界 (取最大负偏离度的一半作为界限)
            double dangerZoneUpperBound = minNegativeResidual * 0.5;
            int klinesInDangerZone = 0;

            for (int i = 0; i < count; i++)
            {
                double lineValue = slope * i + intercept;
                double residual = y[i] - lineValue;

                // 如果这根 K 线的 Low 落入了向下的极端插针区 (residual 负得很厉害)
                if (residual < dangerZoneUpperBound)
                {
                    klinesInDangerZone++;
                }
            }

            // 4. 判断是否破坏结构
            if ((double)klinesInDangerZone / count > _pinBarZoneThreshold)
            {
                return false; // 超过 50% 的 K 线在高危区，说明不是单一向下插针，是底部横盘，形态破坏
            }

            return true;
        }

        private void PublishChart(string symbol, List<IKline> buffer1m, IKline current1m, decimal open1h, decimal stopLoss, decimal takeProfit, string reason)
        {
            var chartItem = new ChartPublishItem
            {
                Symbol = symbol,
                StrategyName = "VolumeExhaustionReversal",
                Klines = buffer1m.TakeLast(150).ToList()
            };

            int currentKIndex = chartItem.Klines.Count - 1;

            // 标记触发点
            chartItem.Points.Add((currentKIndex, current1m.Close, SkiaSharp.SKColors.Yellow, 8f));
            chartItem.Texts.Add((reason, Math.Max(0, currentKIndex - 30), current1m.High * 1.002m, SkiaSharp.SKColors.Yellow));

            // 1 小时开盘价基准线 (紫色)
            chartItem.Lines.Add((0, open1h, currentKIndex + 20, open1h, SkiaSharp.SKColors.Purple, 2f));
            chartItem.Texts.Add(($"1H Open: {open1h:F4}", 10, open1h, SkiaSharp.SKColors.Purple));

            // 止损线 (红色)
            chartItem.Lines.Add((currentKIndex, stopLoss, currentKIndex + 20, stopLoss, SkiaSharp.SKColors.Red, 2f));
            chartItem.Texts.Add(($"SL: {stopLoss:F4}", currentKIndex + 2, stopLoss, SkiaSharp.SKColors.Red));

            // 止盈线 (绿色)
            chartItem.Lines.Add((currentKIndex, takeProfit, currentKIndex + 20, takeProfit, SkiaSharp.SKColors.Green, 2f));
            chartItem.Texts.Add(($"TP: {takeProfit:F4}", currentKIndex + 2, takeProfit, SkiaSharp.SKColors.Green));

            _ = _chartPublishService.PublishChartAsync(chartItem);
        }

        private void CalculateLinearRegression(double[] x, double[] y, out double slope, out double intercept, out double rSquared)
        {
            if (x.Length != y.Length || x.Length < 2)
            {
                throw new ArgumentException("数组长度必须一致且大于1");
            }

            int n = x.Length;
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;

            for (int i = 0; i < n; i++)
            {
                sumX += x[i];
                sumY += y[i];
                sumXY += x[i] * y[i];
                sumX2 += x[i] * x[i];
            }

            double meanX = sumX / n;
            double meanY = sumY / n;

            // 计算斜率 (Slope) 和截距 (Intercept)
            slope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
            intercept = meanY - slope * meanX;

            // 计算 R 平方 (R-Squared)
            double ssTot = 0, ssRes = 0;
            for (int i = 0; i < n; i++)
            {
                double predictedY = slope * x[i] + intercept;
                ssTot += (y[i] - meanY) * (y[i] - meanY);
                ssRes += (y[i] - predictedY) * (y[i] - predictedY);
            }

            rSquared = ssTot == 0 ? 0 : 1 - (ssRes / ssTot);
        }
    }
}
