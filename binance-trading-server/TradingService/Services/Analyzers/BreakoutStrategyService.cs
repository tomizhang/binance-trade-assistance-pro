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
    /// <summary>
    /// 实时量价突破策略：基于 Tick 碰撞 + 动态维护的 1h 级历史极值点
    /// </summary>
    public class BreakoutStrategyService : StrategyBase
    {
        // 1分钟 K线缓冲区 (仅用于近期均量计算)
        private readonly ConcurrentDictionary<string, List<KlineMessage>> _historyBuffer = new();
        private const int LOOKBACK_WINDOW = 60;

        // 🌟 新增：1小时 K线缓冲区 (存储最新的 500 根 1h K线)
        private readonly ConcurrentDictionary<string, List<KlineMessage>> _1hHistoryBuffer = new();

        // 存储 1 小时级别的关键位点位 (支撑/压力)
        private readonly ConcurrentDictionary<string, (List<decimal> Peaks, List<decimal> Valleys)> _pivotLevels = new();

        // 注入仓位管理中心，防重复开仓
        private readonly PositionManagementService _positionManager;

        public BreakoutStrategyService(
            ILogger<BreakoutStrategyService> logger,
            IHubContext<MarketHub> hubContext,
            MarketEventBus eventBus,
            BinanceWebSocketService wsService,
            OrderChannel orderChannel,
            BinanceTradeWsService tradeWsService,
            PositionManagementService positionManager)
            : base(logger, hubContext, eventBus, wsService, orderChannel, tradeWsService)
        {
            _positionManager = positionManager;
            this.IsOrderEnabled = true; // 开启下单控制
        }

        protected override void OnKlineReceived(KlineMessage msg)
        {
            string key = msg.Symbol;
            var buffer = _historyBuffer.GetOrAdd(key, _ => new List<KlineMessage>());

            if (msg.IsClosed)
            {
                // 1. 维护 1 分钟 K 线缓冲区
                lock (buffer)
                {
                    buffer.Add(msg);
                    if (buffer.Count > LOOKBACK_WINDOW) buffer.RemoveAt(0);
                }

                // 🌟 2. K线合成引擎：用收盘的 1m K线动态维护 1h K线
                Update1hHistoryBuffer(msg);
            }

            if (buffer.Count == 0) return;

            // 3. 实时成交量爆发检查 (当前 K 线累积量 对比 过去1小时均量)
            decimal avgVolume = buffer.Average(k => k.Volume);
            decimal volMultiplier = avgVolume > 0 ? msg.Volume / avgVolume : 0;

            if (volMultiplier >= 3.0m && volMultiplier <= 8.0m)
            {
                // 防线：只有当前没有持仓时，才进入 Tick 碰撞逻辑
                if (!_positionManager.HasActivePosition(msg.Symbol))
                {
                    ExecuteRealtimeTickLogic(msg, volMultiplier);
                }
            }
        }

        // ==========================================
        // 🌟 K线合成核心：永不落后的 1h 结构
        // ==========================================
        private void Update1hHistoryBuffer(KlineMessage msg1m)
        {
            string symbol = msg1m.Symbol;
            var buffer1h = _1hHistoryBuffer.GetOrAdd(symbol, _ => new List<KlineMessage>());

            lock (buffer1h)
            {
                // 币安时间戳是毫秒，向下取整到最近的小时起点 (例如 10:24分 会被计算为 10:00分)
                long hourOpenTime = msg1m.OpenTime - (msg1m.OpenTime % 3600000);
                var last1h = buffer1h.LastOrDefault();

                if (last1h != null && last1h.OpenTime == hourOpenTime)
                {
                    // 当前 1h K 线尚未走完，持续扩张它的高低点并累加成交量
                    last1h.High = Math.Max(last1h.High, msg1m.High);
                    last1h.Low = Math.Min(last1h.Low, msg1m.Low);
                    last1h.Close = msg1m.Close;
                    last1h.Volume += msg1m.Volume;
                }
                else
                {
                    // 跨入了新的一小时，生成一根新的 1h K 线
                    buffer1h.Add(new KlineMessage
                    {
                        Symbol = symbol,
                        OpenTime = hourOpenTime,
                        High = msg1m.High,
                        Low = msg1m.Low,
                        Close = msg1m.Close,
                        Volume = msg1m.Volume,
                        IsClosed = true
                    });

                    // 维持 500 根的上限
                    if (buffer1h.Count > 500) buffer1h.RemoveAt(0);
                }
            }

            // 🌟 每收到一根 1m K线，就利用最新的 1h 缓冲区重新计算支撑/压力位
            RecalculatePivotLevels(symbol);
        }

        private void RecalculatePivotLevels(string symbol)
        {
            if (!_1hHistoryBuffer.TryGetValue(symbol, out var buffer1h)) return;

            List<decimal> highs;
            List<decimal> lows;

            lock (buffer1h)
            {
                highs = buffer1h.Select(k => k.High).ToList();
                lows = buffer1h.Select(k => k.Low).ToList();
            }

            // 调用纯数学工具，提取局部极值点
            var (peaksIdx, valleysIdx) = PivotHelper.CalculatePeaks(highs, lows, 5, 5);

            var peakPrices = peaksIdx.Select(i => highs[i]).ToList();
            var valleyPrices = valleysIdx.Select(i => lows[i]).ToList();

            _pivotLevels[symbol] = (peakPrices, valleyPrices);
        }

        private void ExecuteRealtimeTickLogic(KlineMessage msg, decimal volMultiplier)
        {
            if (!_pivotLevels.TryGetValue(msg.Symbol, out var levels)) return;

            decimal nearestHigh = levels.Peaks.OrderBy(p => Math.Abs(p - msg.Close)).FirstOrDefault();
            decimal nearestLow = levels.Valleys.OrderBy(v => Math.Abs(v - msg.Close)).FirstOrDefault();

            bool isLong = false;
            bool isShort = false;
            string reason = "";

            if (msg.Close > nearestHigh && nearestHigh > 0)
            {
                isLong = true; reason = $"[Tick突破] 穿透 1h 高点 {nearestHigh:F4}";
            }
            else if (msg.Close > nearestLow && Math.Abs(msg.Close - nearestLow) / nearestLow < 0.0010m)
            {
                isLong = true; reason = $"[Tick回踩] 触碰 1h 支撑位 {nearestLow:F4}";
            }
            else if (msg.Close < nearestLow && nearestLow > 0)
            {
                isShort = true; reason = $"[Tick跌破] 穿透 1h 低点 {nearestLow:F4}";
            }
            else if (msg.Close < nearestHigh && Math.Abs(msg.Close - nearestHigh) / nearestHigh < 0.0010m)
            {
                isShort = true; reason = $"[Tick受阻] 触碰 1h 压力位 {nearestHigh:F4}";
            }

            if (isLong || isShort)
            {
                _logger.LogWarning($"⚡ [Tick爆发] {msg.Symbol} 量能 {volMultiplier:F1}X, {reason}");

                _ = Task.Run(async () =>
                {
                    await PlaceOrderWithLeverageRiskAsync(
                        msg.Symbol, isLong, msg.Close, 1.5m, 5.0m, 0.05m, 0.025m, "Tick_Pivot_Breakout"
                    );
                });
            }
        }

        protected override async Task InitializeStrategyDataAsync(List<string> symbols)
        {
            foreach (var sym in symbols)
            {
                try
                {
                    // 🌟 系统启动时，先拉取 500 根 1h 历史 K 线作为底层基底
                    string json = await _wsService.GetHistoricalKlinesAsync(sym, "1h", 500);
                    using var doc = JsonDocument.Parse(json);

                    var historyList = new List<KlineMessage>();
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        historyList.Add(new KlineMessage
                        {
                            Symbol = sym,
                            OpenTime = item[0].GetInt64(), // 必须提取 OpenTime
                            High = decimal.Parse(item[2].GetString()),
                            Low = decimal.Parse(item[3].GetString()),
                            Close = decimal.Parse(item[4].GetString()),
                            Volume = decimal.Parse(item[5].GetString()),
                            IsClosed = true
                        });
                    }

                    _1hHistoryBuffer[sym] = historyList;

                    // 启动时初始化计算一次高低点
                    RecalculatePivotLevels(sym);
                    _logger.LogInformation($"✅ {sym} 1h 级支撑压力位初始化完成。");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ {sym} 初始化失败: {ex.Message}");
                }
            }
        }
    }
}