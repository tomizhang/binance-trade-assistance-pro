using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingTerminal.Hubs;
using TradingTerminal.Models;

namespace TradingTerminal.Services
{
    [System.ComponentModel.DisplayName("1分钟成交量反转策略")]
    public class MinVolumeReversalStrategyService : StrategyBase
    {
        private readonly IPositionManagementService _positionManager;

        private readonly ConcurrentDictionary<string, List<IKline>> _1mBuffer = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastTradeTime = new();

        // 策略参数
        private readonly int _volumePeriod = 20; // 计算最近量的均值的K线周期
        private readonly decimal _volumeMultiplier = 3.0m; // 1分钟爆量倍数
        private readonly int _slopePeriod = 5; // 计算最近K线斜率的周期
        private readonly decimal _stopLossDistance = 0.015m; // 止损距离 1.5%
        private readonly decimal _takeProfitDistance = 0.015m; // 止盈距离 3.0%

        public MinVolumeReversalStrategyService(
            ILogger<MinVolumeReversalStrategyService> logger,
            IHubContext<MarketHub> hubContext,
            MarketEventBus eventBus,
            BinanceWebSocketService wsService,
            OrderChannel orderChannel,
            BinanceTradeWsService tradeWsService,
            IPositionManagementService positionManager)
            : base(logger, hubContext, eventBus, wsService, orderChannel, tradeWsService)
        {
            _positionManager = positionManager;
            this.IsOrderEnabled = false;
            IsStrategyEnabled = false;
            this._timeframes = new[] { "1m" };
        }

        protected override async Task InitializeStrategyDataAsync(string symbol)
        {
            _logger.LogInformation($"[{GetType().Name}] 正在为 {symbol} 拉取历史数据...");

            var klines1m = await FetchHistoryAsync(symbol, "1m", 500);
            _1mBuffer[symbol] = klines1m;
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

                if (buffer1m.Count >= Math.Max(_volumePeriod, _slopePeriod))
                {
                    CheckReversalConditions(symbol, msg, buffer1m);
                }

                if (!msg.IsClosed) return;

                lock (buffer1m)
                {
                    buffer1m.Add(msg);
                    if (buffer1m.Count > 1000) buffer1m.RemoveAt(0);
                }
            }
        }

        private void CheckReversalConditions(string symbol, IKline current1m, List<IKline> buffer1m)
        {
            if (_positionManager.HasAnyActivePosition()) return;
            if (_lastTradeTime.TryGetValue(symbol, out var lastTime) && (GetCurrentTime() - lastTime).TotalMinutes < 5) return;

            // 1. 计算 1 分钟大成交量 (前 20 根的平均成交量)
            decimal avgVol = buffer1m.TakeLast(_volumePeriod).Average(k => k.Volume);
            if (current1m.Volume <= avgVol * _volumeMultiplier) return;

            // 2. 判定方向：最近 5 根 K 线的斜率 (包含当前 K 线)
            var recentKlines = buffer1m.TakeLast(_slopePeriod - 1).ToList();
            recentKlines.Add(current1m);

            double[] y = recentKlines.Select(k => (double)k.Close).ToArray();
            double[] x = Enumerable.Range(0, y.Length).Select(i => (double)i).ToArray();

            CalculateLinearRegression(x, y, out double slope, out double intercept, out double rSquared);

            if (Math.Abs(slope) < 0.000001) return; // 斜率极小，视作无趋势，不进行交易

            bool isLongSignal = slope < 0; // 斜率向下则开多
            string direction = isLongSignal ? "做多" : "做空";
            string triggerReason = $"1m爆量({current1m.Volume / avgVol:F1}x) + 最近{_slopePeriod}根K线斜率向下";
            if (!isLongSignal)
            {
                triggerReason = $"1m爆量({current1m.Volume / avgVol:F1}x) + 最近{_slopePeriod}根K线斜率向上";
            }

            _lastTradeTime[symbol] = GetCurrentTime();

            _logger.LogWarning($"🎯 [{symbol}] {triggerReason}！触发{direction}反转。");

            // 执行下单
            decimal leverage = GetLeverage(100.0m);
            decimal requiredRoeTp = _takeProfitDistance * leverage;
            decimal requiredRoeSl = _stopLossDistance * leverage;

            _ = Task.Run(async () =>
            {
                await PlaceOrderWithLeverageRiskAsync(
                    symbol, isLongSignal, current1m.Close, 1.5m, leverage, requiredRoeTp, requiredRoeSl, "MinVolumeReversalStrategyService"
                );
            });
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
    }
}
