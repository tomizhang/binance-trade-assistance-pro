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
    [System.ComponentModel.DisplayName("V型及倒V型形态拟合策略")]
    public class VStructureRegressionStrategyService : StrategyBase
    {
        public class ShadowFreeHeikinAshiKline : IKline
        {
            public string Symbol { get; set; }
            public string Interval { get; set; }
            public long OpenTime { get; set; }
            public decimal Open { get; set; }
            public decimal High { get; set; }
            public decimal Low { get; set; }
            public decimal Close { get; set; }
            public decimal Volume { get; set; }
            public int TradeCount { get; set; }
            public decimal TakerBuyBaseVolume { get; set; }
            public bool IsClosed { get; set; }
        }

        private readonly IPositionManagementService _positionManager;
        private readonly ChartPublishService _chartPublishService;

        private readonly ConcurrentDictionary<string, List<IKline>> _15mBuffer = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastTradeTime = new();

        public VStructureRegressionStrategyService(
            ILogger<VStructureRegressionStrategyService> logger,
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
#else
            this.IsOrderEnabled = false;
            this.IsStrategyEnabled = false; // 本地调试默认开启计算
#endif
            this._timeframes = new[] { "1m", "15m" };
        }

        protected override async Task InitializeStrategyDataAsync(string symbol)
        {
            _logger.LogInformation($"[{GetType().Name}] 正在为 {symbol} 拉取历史数据...");
            var klines15m = await FetchHistoryAsync(symbol, "15m", 150);
            _15mBuffer[symbol] = klines15m;
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

            if (interval == "15m")
            {
                var buffer15m = _15mBuffer.GetOrAdd(symbol, _ => new List<IKline>());

                lock (buffer15m)
                {
                    var lastKline = buffer15m.LastOrDefault();
                    if (lastKline != null && lastKline.OpenTime == msg.OpenTime)
                    {
                        buffer15m[buffer15m.Count - 1] = msg;
                    }
                    else if (lastKline == null || msg.OpenTime > lastKline.OpenTime)
                    {
                        buffer15m.Add(new KlineMessage
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
                    if (buffer15m.Count > 300) buffer15m.RemoveAt(0);
                }

                if (msg.IsClosed)
                {
                    CheckVStructureConditions(symbol, buffer15m);
                }
            }
        }

        private void CheckVStructureConditions(string symbol, List<IKline> raw15mBuffer)
        {
            if (_positionManager.HasActivePosition(symbol)) return;
            if (_lastTradeTime.TryGetValue(symbol, out var lastTime) && (GetCurrentTime() - lastTime).TotalMinutes < 15) return;

            List<IKline> klinesCopy;
            lock (raw15mBuffer)
            {
                klinesCopy = raw15mBuffer.ToList();
            }

            if (klinesCopy.Count < 30) return;

            // 1. 基于15分钟平均open，high，low，close处理k线 (使用3周期移动平均平滑滤波)
            var rawOpens = klinesCopy.Select(k => k.Open).ToList();
            var rawHighs = klinesCopy.Select(k => k.High).ToList();
            var rawLows = klinesCopy.Select(k => k.Low).ToList();
            var rawCloses = klinesCopy.Select(k => k.Close).ToList();

            var smoothOpens = SmoothData(rawOpens, 3);
            var smoothHighs = SmoothData(rawHighs, 3);
            var smoothLows = SmoothData(rawLows, 3);
            var smoothCloses = SmoothData(rawCloses, 3);

            var smoothedKlines = new List<IKline>();
            for (int i = 0; i < klinesCopy.Count; i++)
            {
                smoothedKlines.Add(new KlineMessage
                {
                    Symbol = klinesCopy[i].Symbol,
                    Interval = klinesCopy[i].Interval,
                    OpenTime = klinesCopy[i].OpenTime,
                    Open = smoothOpens[i],
                    High = smoothHighs[i],
                    Low = smoothLows[i],
                    Close = smoothCloses[i],
                    Volume = klinesCopy[i].Volume,
                    TradeCount = klinesCopy[i].TradeCount,
                    TakerBuyBaseVolume = klinesCopy[i].TakerBuyBaseVolume,
                    IsClosed = klinesCopy[i].IsClosed
                });
            }

            // 2. 进行平均k线处理后去掉影线后 (Heikin-Ashi + shadow-free filtering)
            var haKlines = ConvertToShadowFreeHeikinAshi(smoothedKlines);

            // 3. 计算极值点 (高低点)
            var highs = haKlines.Select(k => k.High).ToList();
            var lows = haKlines.Select(k => k.Low).ToList();
            var (peaks, valleys) = PivotHelper.CalculatePeaks(highs, lows, leftLen: 5, rightLen: 3);

            int T = haKlines.Count - 1;

            // 4. 谷点寻找 (下跌 V 形态 -> 做空)
            int lastValleyIdx = valleys.LastOrDefault(-1);
            if (lastValleyIdx != -1 && (T - lastValleyIdx) >= 4 && (T - lastValleyIdx) <= 15 && lastValleyIdx >= 5)
            {
                // 寻找谷点前的最近峰点 (V的左起点)
                int lastPeakIdx = peaks.LastOrDefault(p => p < lastValleyIdx, -1);
                if (lastPeakIdx != -1 && (lastValleyIdx - lastPeakIdx) >= 5 && (lastValleyIdx - lastPeakIdx) <= 15)
                {
                    // 拟合左腿段: lastPeakIdx -> lastValleyIdx
                    double[] xLeft = Enumerable.Range(0, lastValleyIdx - lastPeakIdx + 1).Select(i => (double)i).ToArray();
                    double[] yLeft = haKlines.Skip(lastPeakIdx).Take(lastValleyIdx - lastPeakIdx + 1).Select(k => (double)k.Close).ToArray();
                    CalculateLinearRegression(xLeft, yLeft, out double slopeLeft, out double interceptLeft, out double r2Left);

                    // 拟合右腿段: lastValleyIdx -> T
                    double[] xRight = Enumerable.Range(0, T - lastValleyIdx + 1).Select(i => (double)i).ToArray();
                    double[] yRight = haKlines.Skip(lastValleyIdx).Take(T - lastValleyIdx + 1).Select(k => (double)k.Close).ToArray();
                    CalculateLinearRegression(xRight, yRight, out double slopeRight, out double interceptRight, out double r2Right);

                    if (slopeLeft < 0 && slopeRight > 0 && r2Left >= 0.5 && r2Right >= 0.5)
                    {
                        // 以 V 底部 (Valley) 为平面坐标系的原点，计算入角与出角角度
                        int dxIn = lastPeakIdx - lastValleyIdx; // 负值 (入角X轴在第二象限)
                        decimal dyInPercent = (haKlines[lastPeakIdx].Close - haKlines[lastValleyIdx].Close) / haKlines[lastValleyIdx].Close * 100m; // 正值 (比原点高)
                        double entryAngle = Math.Atan2((double)dyInPercent, (double)dxIn) * 180.0 / Math.PI;

                        int dxOut = T - lastValleyIdx; // 正值 (出角X轴在第一象限)
                        decimal dyOutPercent = (haKlines[T].Close - haKlines[lastValleyIdx].Close) / haKlines[lastValleyIdx].Close * 100m; // 正值
                        double exitAngle = Math.Atan2((double)dyOutPercent, (double)dxOut) * 180.0 / Math.PI;

                        // 下跌 V 形态，触发做空
                        TriggerTrade(symbol, false, klinesCopy, haKlines, peaks, valleys, lastPeakIdx, lastValleyIdx, T, slopeLeft, interceptLeft, slopeRight, interceptRight, entryAngle, exitAngle, "V-Shape (SHORT)");
                        return;
                    }
                }
            }

            // 5. 峰点寻找 (上涨倒 V 形态 -> 做多)
            int lastPeakIdxInv = peaks.LastOrDefault(-1);
            if (lastPeakIdxInv != -1 && (T - lastPeakIdxInv) >= 4 && (T - lastPeakIdxInv) <= 15 && lastPeakIdxInv >= 5)
            {
                // 寻找峰点前的最近谷点 (倒V的左起点)
                int lastValleyIdxInv = valleys.LastOrDefault(v => v < lastPeakIdxInv, -1);
                if (lastValleyIdxInv != -1 && (lastPeakIdxInv - lastValleyIdxInv) >= 5 && (lastPeakIdxInv - lastValleyIdxInv) <= 15)
                {
                    // 拟合左腿段: lastValleyIdxInv -> lastPeakIdxInv
                    double[] xLeft = Enumerable.Range(0, lastPeakIdxInv - lastValleyIdxInv + 1).Select(i => (double)i).ToArray();
                    double[] yLeft = haKlines.Skip(lastValleyIdxInv).Take(lastPeakIdxInv - lastValleyIdxInv + 1).Select(k => (double)k.Close).ToArray();
                    CalculateLinearRegression(xLeft, yLeft, out double slopeLeft, out double interceptLeft, out double r2Left);

                    // 拟合右腿段: lastPeakIdxInv -> T
                    double[] xRight = Enumerable.Range(0, T - lastPeakIdxInv + 1).Select(i => (double)i).ToArray();
                    double[] yRight = haKlines.Skip(lastPeakIdxInv).Take(T - lastPeakIdxInv + 1).Select(k => (double)k.Close).ToArray();
                    CalculateLinearRegression(xRight, yRight, out double slopeRight, out double interceptRight, out double r2Right);

                    if (slopeLeft > 0 && slopeRight < 0 && r2Left >= 0.5 && r2Right >= 0.5)
                    {
                        // 以 倒V 顶部 (Peak) 为平面坐标系的原点，计算入角与出角角度
                        int dxIn = lastValleyIdxInv - lastPeakIdxInv; // 负值 (入角X轴在第三象限)
                        decimal dyInPercent = (haKlines[lastValleyIdxInv].Close - haKlines[lastPeakIdxInv].Close) / haKlines[lastPeakIdxInv].Close * 100m; // 负值 (比原点低)
                        double entryAngle = Math.Atan2((double)dyInPercent, (double)dxIn) * 180.0 / Math.PI;

                        int dxOut = T - lastPeakIdxInv; // 正值 (出角X轴在第四象限)
                        decimal dyOutPercent = (haKlines[T].Close - haKlines[lastPeakIdxInv].Close) / haKlines[lastPeakIdxInv].Close * 100m; // 负值
                        double exitAngle = Math.Atan2((double)dyOutPercent, (double)dxOut) * 180.0 / Math.PI;

                        // 上涨倒 V 形态，触发做多
                        TriggerTrade(symbol, true, klinesCopy, haKlines, peaks, valleys, lastValleyIdxInv, lastPeakIdxInv, T, slopeLeft, interceptLeft, slopeRight, interceptRight, entryAngle, exitAngle, "Inverted V-Shape (LONG)");
                        return;
                    }
                }
            }
        }

        private void TriggerTrade(
            string symbol,
            bool isLong,
            List<IKline> raw15mKlines,
            List<ShadowFreeHeikinAshiKline> haKlines,
            List<int> peakIndices,
            List<int> valleyIndices,
            int leftLegStart,
            int pivotIndex,
            int rightLegEnd,
            double slopeLeft,
            double interceptLeft,
            double slopeRight,
            double interceptRight,
            double entryAngle,
            double exitAngle,
            string patternName)
        {
            _lastTradeTime[symbol] = GetCurrentTime();

            decimal currentPrice = raw15mKlines.Last().Close;
            decimal leverage = GetLeverage(20m);
            decimal targetRoeTp = 0.011m * leverage; // 2.0% TP
            decimal riskRoeSl = 0.010m * leverage;  // 1.5% SL

            string logDetail = $"{patternName} | 入场:{currentPrice:F4}, 入角:{entryAngle:F1}°, 出角:{exitAngle:F1}°";
            _logger.LogWarning($"🎯 [{symbol}] 触发 {logDetail}！");

            _ = Task.Run(async () =>
            {
                await PlaceOrderWithLeverageRiskAsync(
                    symbol, isLong, currentPrice, 1.5m, leverage, targetRoeTp, riskRoeSl, "VStructureRegression"
                );
            });

            // 渲染策略拟合可视化图表
            PublishVChart(symbol, raw15mKlines, haKlines, peakIndices, valleyIndices, leftLegStart, pivotIndex, rightLegEnd, slopeLeft, interceptLeft, slopeRight, interceptRight, entryAngle, exitAngle, patternName);
        }

        private List<ShadowFreeHeikinAshiKline> ConvertToShadowFreeHeikinAshi(List<IKline> rawKlines)
        {
            var result = new List<ShadowFreeHeikinAshiKline>();
            if (rawKlines == null || rawKlines.Count == 0) return result;

            decimal prevOpen = (rawKlines[0].Open + rawKlines[0].Close) / 2;
            decimal prevClose = (rawKlines[0].Open + rawKlines[0].High + rawKlines[0].Low + rawKlines[0].Close) / 4;

            result.Add(new ShadowFreeHeikinAshiKline
            {
                Symbol = rawKlines[0].Symbol,
                Interval = rawKlines[0].Interval,
                OpenTime = rawKlines[0].OpenTime,
                Open = prevOpen,
                Close = prevClose,
                High = Math.Max(prevOpen, prevClose),
                Low = Math.Min(prevOpen, prevClose),
                Volume = rawKlines[0].Volume,
                TradeCount = rawKlines[0].TradeCount,
                TakerBuyBaseVolume = rawKlines[0].TakerBuyBaseVolume,
                IsClosed = rawKlines[0].IsClosed
            });

            for (int i = 1; i < rawKlines.Count; i++)
            {
                var raw = rawKlines[i];
                decimal close = (raw.Open + raw.High + raw.Low + raw.Close) / 4;
                decimal open = (prevOpen + prevClose) / 2;
                decimal high = Math.Max(open, close);
                decimal low = Math.Min(open, close);

                result.Add(new ShadowFreeHeikinAshiKline
                {
                    Symbol = raw.Symbol,
                    Interval = raw.Interval,
                    OpenTime = raw.OpenTime,
                    Open = open,
                    Close = close,
                    High = high,
                    Low = low,
                    Volume = raw.Volume,
                    TradeCount = raw.TradeCount,
                    TakerBuyBaseVolume = raw.TakerBuyBaseVolume,
                    IsClosed = raw.IsClosed
                });

                prevOpen = open;
                prevClose = close;
            }

            return result;
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

            slope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
            intercept = meanY - slope * meanX;

            double ssTot = 0, ssRes = 0;
            for (int i = 0; i < n; i++)
            {
                double predictedY = slope * x[i] + intercept;
                ssTot += (y[i] - meanY) * (y[i] - meanY);
                ssRes += (y[i] - predictedY) * (y[i] - predictedY);
            }

            rSquared = ssTot == 0 ? 0 : 1 - (ssRes / ssTot);
        }

        private void PublishVChart(
            string symbol,
            List<IKline> raw15mKlines,
            List<ShadowFreeHeikinAshiKline> haKlines,
            List<int> peakIndices,
            List<int> valleyIndices,
            int leftLegStart,
            int pivotIndex,
            int rightLegEnd,
            double slopeLeft,
            double interceptLeft,
            double slopeRight,
            double interceptRight,
            double entryAngle,
            double exitAngle,
            string patternName)
        {
            var chartItem = new ChartPublishItem
            {
                Symbol = symbol,
                StrategyName = "VStructureRegression",
                Klines = raw15mKlines.TakeLast(80).ToList()
            };

            int offset = raw15mKlines.Count - chartItem.Klines.Count;

            // 1. 画极值点标记
            foreach (var pIdx in peakIndices)
            {
                int mappedIdx = pIdx - offset;
                if (mappedIdx >= 0 && mappedIdx < chartItem.Klines.Count)
                {
                    chartItem.Points.Add((mappedIdx, raw15mKlines[pIdx].High, SkiaSharp.SKColors.Red, 6f));
                    chartItem.Texts.Add(("Peak", mappedIdx, raw15mKlines[pIdx].High * 1.001m, SkiaSharp.SKColors.Red));
                }
            }

            foreach (var vIdx in valleyIndices)
            {
                int mappedIdx = vIdx - offset;
                if (mappedIdx >= 0 && mappedIdx < chartItem.Klines.Count)
                {
                    chartItem.Points.Add((mappedIdx, raw15mKlines[vIdx].Low, SkiaSharp.SKColors.Green, 6f));
                    chartItem.Texts.Add(("Valley", mappedIdx, raw15mKlines[vIdx].Low * 0.999m, SkiaSharp.SKColors.Green));
                }
            }

            // 2. 画线性回归拟合段 (以品红色大圆点作为形态的原点/分水岭)
            int leftStartChartIdx = leftLegStart - offset;
            int pivotChartIdx = pivotIndex - offset;
            if (leftStartChartIdx >= 0 && pivotChartIdx < chartItem.Klines.Count)
            {
                decimal yStart = (decimal)(interceptLeft);
                decimal yEnd = (decimal)(slopeLeft * (pivotIndex - leftLegStart) + interceptLeft);
                chartItem.Lines.Add((leftStartChartIdx, yStart, pivotChartIdx, yEnd, SkiaSharp.SKColors.Yellow, 3f));
            }

            int rightEndChartIdx = rightLegEnd - offset;
            if (pivotChartIdx >= 0 && rightEndChartIdx < chartItem.Klines.Count)
            {
                decimal yStart = (decimal)(interceptRight);
                decimal yEnd = (decimal)(slopeRight * (rightLegEnd - pivotIndex) + interceptRight);
                chartItem.Lines.Add((pivotChartIdx, yStart, rightEndChartIdx, yEnd, SkiaSharp.SKColors.Cyan, 3f));
            }

            // 3. 画中心原点
            if (pivotChartIdx >= 0 && pivotChartIdx < chartItem.Klines.Count)
            {
                decimal pivotPrice = raw15mKlines[pivotIndex].Close;
                chartItem.Points.Add((pivotChartIdx, pivotPrice, SkiaSharp.SKColors.Magenta, 10f));
                chartItem.Texts.Add(($"Origin ({patternName})", pivotChartIdx, pivotPrice, SkiaSharp.SKColors.Magenta));
            }

            // 4. 画描述文字与拟合指标
            string desc = $"{patternName} Triggered | Entry: {entryAngle:F1}° | Exit: {exitAngle:F1}°";
            int textIdx = Math.Max(0, rightEndChartIdx - 25);
            decimal textPrice = raw15mKlines[rightLegEnd].High * 1.002m;
            chartItem.Texts.Add((desc, textIdx, textPrice, SkiaSharp.SKColors.White));

            _ = _chartPublishService.PublishChartAsync(chartItem);
        }
    }
}
