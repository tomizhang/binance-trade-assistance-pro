using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Text.Json;
using TradingTerminal.Hubs;
using TradingTerminal.Utils;

namespace TradingTerminal.Services
{
    public class HeikinAshiService : StrategyBase
    {
        private readonly HeikinAshiEngine _engine;
        private readonly ConcurrentDictionary<string, List<KlineMessage>> _klineBuffer = new();
        private const int BUFFER_SIZE = 50;

        public HeikinAshiService(
            ILogger<HeikinAshiService> logger,
            HeikinAshiEngine engine,
            IHubContext<MarketHub> hubContext,
            MarketEventBus eventBus,
            BinanceWebSocketService wsService,
            OrderChannel orderChannel,
            BinanceTradeWsService tradeWsService)
            : base(logger, hubContext, eventBus, wsService, orderChannel, tradeWsService)
        {
            _engine = engine;
        }

        protected override void OnKlineReceived(KlineMessage msg)
        {
            // 1. 调用引擎执行反转与风控判断
            LiveHaResult liveHa = _engine.ProcessLiveKlineAndCheckReversal(
                msg.Symbol, msg.Interval, msg.Open, msg.Close, msg.High, msg.Low, msg.Volume, msg.OpenTime, msg.IsClosed);

            if (msg.IsClosed)
            {
                string key = $"{msg.Symbol}_{msg.Interval}";
                var buffer = _klineBuffer.GetOrAdd(key, _ => new List<KlineMessage>());
                buffer.Add(msg);
                if (buffer.Count > BUFFER_SIZE) buffer.RemoveAt(0);

                if (liveHa.IsReversed)
                {
                    ProcessReversalSignal(msg, buffer, liveHa);
                }
            }
        }

        private void ProcessReversalSignal(KlineMessage msg, List<KlineMessage> buffer, LiveHaResult liveHa)
        {
            if (buffer.Count < 10) return;

            // 2. 均线结构共振判定
            var smoothedHighs = SmoothData(buffer.Select(k => k.High).ToList(), 3);
            var smoothedLows = SmoothData(buffer.Select(k => k.Low).ToList(), 3);
            var (peaks, valleys) = PivotHelper.CalculatePeaks(smoothedHighs, smoothedLows, 3, 1);

            int currentIndex = buffer.Count - 1;
            bool confirmPivot = false;
            decimal pivotPrice = 0m;

            if (liveHa.IsBullish)
            {
                if (valleys.Contains(currentIndex - 1)) { confirmPivot = true; pivotPrice = buffer[currentIndex - 1].Low; }
                else if (valleys.Contains(currentIndex - 2)) { confirmPivot = true; pivotPrice = buffer[currentIndex - 2].Low; }
            }
            else
            {
                if (peaks.Contains(currentIndex - 1)) { confirmPivot = true; pivotPrice = buffer[currentIndex - 1].High; }
                else if (peaks.Contains(currentIndex - 2)) { confirmPivot = true; pivotPrice = buffer[currentIndex - 2].High; }
            }

            if (confirmPivot && pivotPrice > 0)
            {
                _logger.LogWarning($"🔥 [HA策略] {msg.Symbol} 结构共振确认，极值点: {pivotPrice}");
                _ = Task.Run(() => ExecuteFinalStrategy(msg.Symbol, liveHa.IsBullish, msg.Close, pivotPrice));
            }
        }

        private async Task ExecuteFinalStrategy(string symbol, bool isBullish, decimal currentPrice, decimal pivotPrice)
        {
            // 3. 计算止损与止盈参数
            decimal rawRoeSl = (Math.Abs(currentPrice - pivotPrice) / currentPrice) * 5m; // 假设5倍杠杆
            decimal riskRoeSl = Math.Clamp(rawRoeSl, 0.015m, 0.05m);
            decimal targetRoeTp = riskRoeSl * 1.5m;

            decimal stopLossPrice = isBullish ? currentPrice * (1m - (riskRoeSl / 5m)) : currentPrice * (1m + (riskRoeSl / 5m));
            decimal takeProfitPrice = isBullish ? currentPrice * (1m + (targetRoeTp / 5m)) : currentPrice * (1m - (targetRoeTp / 5m));

            // 调用基类公共下单方法
            //await PlaceOrderWithProtectionAsync(symbol, isBullish, currentPrice, stopLossPrice, takeProfitPrice, 1.5m, 5m, "HA_MA_Structure");
        }

        protected override async Task InitializeStrategyDataAsync(List<string> symbols)
        {
            foreach (var sym in symbols)
            {
                try
                {
                    string json = await _wsService.GetHistoricalKlinesAsync(sym, "2m", 1000);
                    using var doc = JsonDocument.Parse(json);
                    var klines = new List<dynamic>();
                    var bufferList = new List<KlineMessage>();

                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        var k = new KlineMessage
                        {
                            OpenTime = item[0].GetInt64(),
                            Open = decimal.Parse(item[1].GetString()),
                            High = decimal.Parse(item[2].GetString()),
                            Low = decimal.Parse(item[3].GetString()),
                            Close = decimal.Parse(item[4].GetString()),
                            Volume = decimal.Parse(item[5].GetString())
                        };
                        klines.Add(new { k.OpenTime, k.Open, k.High, k.Low, k.Close, k.Volume });
                        bufferList.Add(k);
                    }

                    _engine.InitializeFromHistory(sym, "2m", klines);
                    _klineBuffer[$"{sym}_2m"] = bufferList.Skip(Math.Max(0, bufferList.Count - BUFFER_SIZE)).ToList();
                }
                catch (Exception ex) { _logger.LogError($"❌ {sym} 初始化失败: {ex.Message}"); }
            }
        }
    }
}