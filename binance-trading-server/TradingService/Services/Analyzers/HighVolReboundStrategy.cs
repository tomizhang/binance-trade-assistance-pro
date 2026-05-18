using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using TradingTerminal.Hubs;
using TradingTerminal.Models;

namespace TradingTerminal.Services
{
    public class HighVolStructureStrategyService : StrategyBase
    {
        private readonly PositionManagementService _positionManager;

        // === 参数设置 ===
        private readonly int _mtfMinutes = 10;       // 大周期：10分钟 (根据原逻辑)
        private readonly int _volMultiplier = 7;     // 巨量倍数
        private readonly int _pivotLookback = 5;     // 寻找高低点的范围
        private readonly int _shortTermPeriod = 5;   // 短期方向判断窗口

        // 风控参数
        private readonly decimal _stopLossPct = 0.01m;
        private readonly decimal _takeProfitPct = 0.01m;
        private readonly int _leverage = 5;
        private readonly decimal _marginUsdt = 1.5m;

        // === 内部状态 ===
        private readonly ConcurrentDictionary<string, List<IKline>> _1mBuffer = new();
        private readonly ConcurrentDictionary<string, List<IKline>> _mtfBuffer = new();
        private readonly ConcurrentDictionary<string, IKline> _currentMtf = new();
        private readonly ConcurrentDictionary<string, decimal> _lastStructureHigh = new();
        private readonly ConcurrentDictionary<string, decimal> _lastStructureLow = new();

        public HighVolStructureStrategyService(
            ILogger<HighVolStructureStrategyService> logger,
            IHubContext<MarketHub> hubContext,
            MarketEventBus eventBus,
            BinanceWebSocketService wsService,
            OrderChannel orderChannel,
            BinanceTradeWsService tradeWsService,
            PositionManagementService positionManager)
            : base(logger, hubContext, eventBus, wsService, orderChannel, tradeWsService)
        {
            _positionManager = positionManager;
            this.IsOrderEnabled = false; // 默认开启下单
            IsStrategyEnabled = false;
        }

        protected override async Task InitializeStrategyDataAsync(string symbol)
        {
            _logger.LogInformation($"[{GetType().Name}] 正在为 {symbol} 拉取历史数据...");

            try
            {
                // 拉取 1500 根 1m 数据，足以合成 150 根 10m 数据
                string json = await _wsService.GetHistoricalKlinesAsync(symbol, "1m", 1500);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var historyList = new List<IKline>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    historyList.Add(new KlineMessage
                    {
                        Symbol = symbol,
                        Interval = "1m",
                        OpenTime = item[0].GetInt64(),
                        Open = decimal.Parse(item[1].GetString()),
                        High = decimal.Parse(item[2].GetString()),
                        Low = decimal.Parse(item[3].GetString()),
                        Close = decimal.Parse(item[4].GetString()),
                        Volume = decimal.Parse(item[5].GetString()),
                        IsClosed = true
                    });
                }

                _1mBuffer[symbol] = historyList;

                // 历史数据合成为大周期 (MTF)
                var mtfList = new List<IKline>();
                IKline currentMtf = null;
                long bucketMs = _mtfMinutes * 60 * 1000L;

                foreach (var k1m in historyList)
                {
                    long bucketStart = k1m.OpenTime - (k1m.OpenTime % bucketMs);
                    bool isNewMtf = currentMtf == null || bucketStart > currentMtf.OpenTime;

                    if (isNewMtf)
                    {
                        if (currentMtf != null)
                        {
                            if (currentMtf is KlineMessage cmClosed) cmClosed.IsClosed = true;
                            mtfList.Add(currentMtf);
                        }

                        currentMtf = new KlineMessage
                        {
                            Symbol = symbol,
                            Interval = $"{_mtfMinutes}m",
                            OpenTime = bucketStart,
                            Open = k1m.Open,
                            High = k1m.High,
                            Low = k1m.Low,
                            Close = k1m.Close,
                            Volume = k1m.Volume,
                            IsClosed = false
                        };
                    }
                    else if (currentMtf != null && currentMtf is KlineMessage cm)
                    {
                        cm.High = Math.Max(cm.High, k1m.High);
                        cm.Low = Math.Min(cm.Low, k1m.Low);
                        cm.Close = k1m.Close;
                        cm.Volume += k1m.Volume;
                    }
                }

                _mtfBuffer[symbol] = mtfList;
                if (currentMtf != null) _currentMtf[symbol] = currentMtf;

                UpdateStructureLevels(symbol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"拉取 {symbol} 历史数据失败");
            }
        }

        protected override void OnKlineReceived(IKline msg)
        {
            string symbol = msg.Symbol;
            if (msg.Interval != "1m") return;

            var buffer1m = _1mBuffer.GetOrAdd(symbol, _ => new List<IKline>());

            // 每来一根 1m K 线，就更新合成的大周期
            UpdateMtfCandles(symbol, msg);

            if (buffer1m.Count >= Math.Max(150, _shortTermPeriod + 1))
            {
                CheckTradingLogic(symbol, msg, buffer1m);
            }

            if (!msg.IsClosed) return;

            lock (buffer1m)
            {
                buffer1m.Add(msg);
                if (buffer1m.Count > 1500) buffer1m.RemoveAt(0);
            }
        }

        private void UpdateMtfCandles(string symbol, IKline k1m)
        {
            var mtfList = _mtfBuffer.GetOrAdd(symbol, _ => new List<IKline>());
            _currentMtf.TryGetValue(symbol, out var currentMtf);

            long bucketMs = _mtfMinutes * 60 * 1000L;
            long bucketStart = k1m.OpenTime - (k1m.OpenTime % bucketMs);
            bool isNewMtf = currentMtf == null || bucketStart > currentMtf.OpenTime;

            if (isNewMtf)
            {
                if (currentMtf != null)
                {
                    if (currentMtf is KlineMessage cmClosed) cmClosed.IsClosed = true;
                    lock (mtfList)
                    {
                        mtfList.Add(currentMtf);
                        if (mtfList.Count > 200) mtfList.RemoveAt(0);
                    }
                    // 新大周期收盘后，更新高低点结构位
                    UpdateStructureLevels(symbol);
                }

                currentMtf = new KlineMessage
                {
                    Symbol = symbol,
                    Interval = $"{_mtfMinutes}m",
                    OpenTime = bucketStart,
                    Open = k1m.Open,
                    High = k1m.High,
                    Low = k1m.Low,
                    Close = k1m.Close,
                    Volume = k1m.Volume,
                    IsClosed = false
                };
                _currentMtf[symbol] = currentMtf;
            }
            else if (currentMtf != null && currentMtf is KlineMessage cm)
            {
                cm.High = Math.Max(cm.High, k1m.High);
                cm.Low = Math.Min(cm.Low, k1m.Low);
                cm.Close = k1m.Close;
                cm.Volume += k1m.Volume;
            }
        }

        private void UpdateStructureLevels(string symbol)
        {
            if (!_mtfBuffer.TryGetValue(symbol, out var mtfList)) return;

            List<IKline> recentMtf;
            lock (mtfList)
            {
                recentMtf = mtfList.TakeLast(50).ToList();
            }

            int count = recentMtf.Count;
            if (count < 5) return;

            decimal lastHigh = 0;
            decimal lastLow = 0;

            // 寻找最近的阻力位 (High Pivot - Based on Close)
            for (int i = count - 2; i >= 2; i--)
            {
                var mid = recentMtf[i];
                if (mid.Close > recentMtf[i - 1].Close && mid.Close > recentMtf[i - 2].Close &&
                    mid.Close > recentMtf[i + 1].Close)
                {
                    lastHigh = mid.Close;
                    break;
                }
            }

            // 寻找最近的支撑位 (Low Pivot - Based on Close)
            for (int i = count - 2; i >= 2; i--)
            {
                var mid = recentMtf[i];
                if (mid.Close < recentMtf[i - 1].Close && mid.Close < recentMtf[i - 2].Close &&
                    mid.Close < recentMtf[i + 1].Close)
                {
                    lastLow = mid.Close;
                    break;
                }
            }

            if (lastHigh > 0) _lastStructureHigh[symbol] = lastHigh;
            if (lastLow > 0) _lastStructureLow[symbol] = lastLow;
        }

        private void CheckTradingLogic(string symbol, IKline kline, List<IKline> buffer1m)
        {
            if (_positionManager.HasAnyActivePosition()) return;

            List<IKline> evalBuffer;
            lock (buffer1m)
            {
                evalBuffer = buffer1m.TakeLast(150 + _shortTermPeriod + 1).ToList();
            }

            if (evalBuffer.Count < 150) return;

            decimal avgVol = evalBuffer.TakeLast(150).Average(k => k.Volume);
            if (avgVol == 0) return;

            bool isHighVol = kline.Volume > avgVol * _volMultiplier;
            if (!isHighVol) return;

            decimal priceAgo = evalBuffer[evalBuffer.Count - 1 - _shortTermPeriod].Close;
            bool isShortTermUp = kline.Close > priceAgo;
            bool isShortTermDown = kline.Close < priceAgo;

            _lastStructureLow.TryGetValue(symbol, out decimal structLow);
            _lastStructureHigh.TryGetValue(symbol, out decimal structHigh);

            if (structLow == 0 || structHigh == 0) return;

            // === 趋势判定 (基于 Close 结构位) ===
            bool isTrendDown = kline.Close < structLow;
            bool isTrendUp = kline.Close > structHigh;

            // 场景 A: 结构看多 + 短期回调 + 巨量 -> 做多 (顺大势买入回调)
            bool isLongSignal = isTrendUp && isShortTermDown;
            
            // 场景 B: 结构看空 + 短期反弹 + 巨量 -> 做空 (顺大势卖出反弹)
            bool isShortSignal = isTrendDown && isShortTermUp;

            if (isLongSignal || isShortSignal)
            {
                _logger.LogInformation($"[{symbol}] 触发 HighVolStructure! 1m巨量({kline.Volume/avgVol:F2}倍) 结构区间[{structLow}, {structHigh}] 现价:{kline.Close} 方向:{(isLongSignal ? "多" : "空")}");
                
                _ = Task.Run(async () =>
                {
                    await PlaceOrderWithLeverageRiskAsync(
                        symbol,
                        isLongSignal,
                        kline.Close,
                        _marginUsdt,
                        _leverage,
                        _takeProfitPct * _leverage,
                        _stopLossPct * _leverage,
                        "HighVolStructure"
                    );
                });
            }
        }
    }
}