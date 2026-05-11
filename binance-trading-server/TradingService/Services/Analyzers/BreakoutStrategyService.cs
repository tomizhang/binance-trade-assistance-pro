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
    /// 实时量价突破策略：具备观察状态机，等待相反买卖量或回弹确认后再入场
    /// </summary>
    public class BreakoutStrategyService : StrategyBase
    {
        // ==========================================
        // 🌟 新增：观察状态缓存结构
        // ==========================================
        private class ObservationState
        {
            public bool IsLong { get; set; }        // 预期做多还是做空
            public decimal TriggerPrice { get; set; } // 触发观察的突破价
            public int CandlesWatched { get; set; }   // 已经观察了多少根 K 线
            public string StrategyReason { get; set; } // 记录原始触发原因
        }

        // 1分钟 K线缓冲区 (仅用于近期均量计算与波动率计算)
        private readonly ConcurrentDictionary<string, List<KlineMessage>> _historyBuffer = new();
        private const int LOOKBACK_WINDOW = 80;

        // 1小时 K线缓冲区 (存储最新的 500 根 1h K线)
        private readonly ConcurrentDictionary<string, List<KlineMessage>> _1hHistoryBuffer = new();

        // 存储 1 小时级别的关键位点位 (支撑/压力)
        private readonly ConcurrentDictionary<string, (List<decimal> Peaks, List<decimal> Valleys)> _pivotLevels = new();

        // 🌟 观察名单字典
        private readonly ConcurrentDictionary<string, ObservationState> _observationList = new();

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
            // 严格收盘判定模式：如果未收盘，直接跳过
            if (!msg.IsClosed) return;

            string key = msg.Symbol;
            var buffer = _historyBuffer.GetOrAdd(key, _ => new List<KlineMessage>());

            // 1. 维护 1 分钟 K 线缓冲区
            lock (buffer)
            {
                buffer.Add(msg);
                if (buffer.Count > LOOKBACK_WINDOW) buffer.RemoveAt(0);
            }

            // 2. 动态维护 1h K线
            Update1hHistoryBuffer(msg);

            if (buffer.Count < 2) return;

            // ==========================================
            // 🛡️ 阶段 1：观察期判定逻辑 (回踩/相反力量确认入场)
            // ==========================================
            if (_observationList.TryGetValue(msg.Symbol, out var obs))
            {
                obs.CandlesWatched++;

                // 超过 5 根 K 线（5分钟）还没给出回踩确认信号，视为错失良机，放弃
                if (obs.CandlesWatched > 5)
                {
                    _observationList.TryRemove(msg.Symbol, out _);
                    _logger.LogInformation($"⏳ [{msg.Symbol}] 观察期超时(超5分钟)，未见确认信号，放弃入场。");
                    return;
                }

                bool confirmEntry = false;
                string confirmReason = "";

                if (obs.IsLong)
                {
                    // 做多观察期：如果跌穿突破价 0.7%，说明是彻底的假突破诱多，直接取消
                    if (msg.Close < obs.TriggerPrice * 0.993m)
                    {
                        _observationList.TryRemove(msg.Symbol, out _);
                        _logger.LogInformation($"📉 [{msg.Symbol}] 跌破观察防守线，判定为假突破，取消做多计划。");
                        return;
                    }

                    // 确认线：出现阴线(相反卖量洗盘) 或者 价格出现回踩
                    if (msg.Close < msg.Open || msg.Close < obs.TriggerPrice)
                    {
                        confirmEntry = true;
                        confirmReason = "出现相反卖量(洗盘阴线)或价格回踩，多头确认";
                    }
                }
                else
                {
                    // 做空观察期：如果反弹穿透突破价 0.7%，说明是假跌破诱空，直接取消
                    if (msg.Close > obs.TriggerPrice * 1.007m)
                    {
                        _observationList.TryRemove(msg.Symbol, out _);
                        _logger.LogInformation($"📈 [{msg.Symbol}] 突破观察防守线，判定为假跌破，取消做空计划。");
                        return;
                    }

                    // 确认线：出现阳线(相反买量回弹) 或者 价格出现回升
                    if (msg.Close > msg.Open || msg.Close > obs.TriggerPrice)
                    {
                        confirmEntry = true;
                        confirmReason = "出现相反买量(回弹阳线)或价格回弹，空头确认";
                    }
                }

                if (confirmEntry)
                {
                    _observationList.TryRemove(msg.Symbol, out _);

                    if (!_positionManager.HasActivePosition(msg.Symbol))
                    {
                        _logger.LogWarning($"🎯 [{msg.Symbol}] {confirmReason}！执行最终入场。");
                        _ = Task.Run(async () =>
                        {
                            await PlaceOrderWithLeverageRiskAsync(
                                msg.Symbol, obs.IsLong, msg.Close, 1.5m, 5.0m, 0.05m, 0.045m, "Pivot_Breakout_Confirmed"
                            );
                        });
                    }
                }
                // 如果还在观察期内，不论是否确认入场，均不再处理下方新的爆量逻辑，防止重复识别
                return;
            }

            // ==========================================
            // 🚀 阶段 2：实时成交量爆发检查 (发现突破猎物)
            // ==========================================
            decimal avgVolume = buffer.Average(k => k.Volume);
            decimal volMultiplier = avgVolume > 0 ? msg.Volume / avgVolume : 0;

            if (volMultiplier >= 3.0m && volMultiplier <= 8.0m)
            {
                // 核心风控 1：近期波动率 (死水过滤)
                decimal periodHigh = buffer.Max(k => k.High);
                decimal periodLow = buffer.Min(k => k.Low);
                decimal volatility = periodLow > 0 ? (periodHigh - periodLow) / periodLow : 0m;

                if (volatility < 0.016m) return;

                // 核心风控 2：插针形态过滤 (假突破过滤)
                decimal body = Math.Abs(msg.Close - msg.Open);
                decimal range = msg.High - msg.Low;
                if (range == 0) return;

                bool isPinBar = (body / range) < 0.3m;
                if (isPinBar)
                {
                    _logger.LogDebug($"⚠️ [形态拦截] {msg.Symbol} 出现爆量，但呈现插针形态，已过滤。");
                    return;
                }

                if (!_positionManager.HasActivePosition(msg.Symbol))
                {
                    // 🌟 评估突破方向
                    var (isLong, isShort, reason) = EvaluateBreakout(msg);

                    if (isLong || isShort)
                    {
                        // 发现有效突破！但不立即下单，而是将其挂入观察者名单
                        _logger.LogWarning($"👀 [进入观察状态] {msg.Symbol} 量能 {volMultiplier:F1}X, {reason}。进入静默观察，等待相反量能或回弹确认...");

                        _observationList[msg.Symbol] = new ObservationState
                        {
                            IsLong = isLong,
                            TriggerPrice = msg.Close,
                            StrategyReason = reason,
                            CandlesWatched = 0
                        };
                    }
                }
            }
        }

        // ==========================================
        // 🌟 剥离出的纯粹判断逻辑
        // ==========================================
        private (bool isLong, bool isShort, string reason) EvaluateBreakout(KlineMessage msg)
        {
            if (!_pivotLevels.TryGetValue(msg.Symbol, out var levels)) return (false, false, "");

            decimal nearestHigh = levels.Peaks.OrderBy(p => Math.Abs(p - msg.Close)).FirstOrDefault();
            decimal nearestLow = levels.Valleys.OrderBy(v => Math.Abs(v - msg.Close)).FirstOrDefault();

            bool isLong = false;
            bool isShort = false;
            string reason = "";

            if (msg.Close > nearestHigh && nearestHigh > 0)
            {
                isLong = true; reason = $"[破点突破] 穿透 1h 高点 {nearestHigh:F4}";
            }
            else if (msg.Close > nearestLow && Math.Abs(msg.Close - nearestLow) / nearestLow < 0.0010m)
            {
                isLong = true; reason = $"[破点回踩] 触碰 1h 支撑位 {nearestLow:F4}";
            }
            else if (msg.Close < nearestLow && nearestLow > 0)
            {
                isShort = true; reason = $"[破点跌破] 穿透 1h 低点 {nearestLow:F4}";
            }
            else if (msg.Close < nearestHigh && Math.Abs(msg.Close - nearestHigh) / nearestHigh < 0.0010m)
            {
                isShort = true; reason = $"[破点受阻] 触碰 1h 压力位 {nearestHigh:F4}";
            }

            return (isLong, isShort, reason);
        }

        // ==========================================
        // K线合成引擎与历史数据初始化保持不变
        // ==========================================
        private void Update1hHistoryBuffer(KlineMessage msg1m)
        {
            string symbol = msg1m.Symbol;
            var buffer1h = _1hHistoryBuffer.GetOrAdd(symbol, _ => new List<KlineMessage>());

            lock (buffer1h)
            {
                long hourOpenTime = msg1m.OpenTime - (msg1m.OpenTime % 3600000);
                var last1h = buffer1h.LastOrDefault();

                if (last1h != null && last1h.OpenTime == hourOpenTime)
                {
                    last1h.High = Math.Max(last1h.High, msg1m.High);
                    last1h.Low = Math.Min(last1h.Low, msg1m.Low);
                    last1h.Close = msg1m.Close;
                    last1h.Volume += msg1m.Volume;
                }
                else
                {
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
                    if (buffer1h.Count > 500) buffer1h.RemoveAt(0);
                }
            }
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

            var (peaksIdx, valleysIdx) = PivotHelper.CalculatePeaks(highs, lows, 5, 5);
            var peakPrices = peaksIdx.Select(i => highs[i]).ToList();
            var valleyPrices = valleysIdx.Select(i => lows[i]).ToList();

            _pivotLevels[symbol] = (peakPrices, valleyPrices);
        }

        protected override async Task InitializeStrategyDataAsync(List<string> symbols)
        {
            foreach (var sym in symbols)
            {
                try
                {
                    string json = await _wsService.GetHistoricalKlinesAsync(sym, "1h", 500);
                    using var doc = JsonDocument.Parse(json);

                    var historyList = new List<KlineMessage>();
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        historyList.Add(new KlineMessage
                        {
                            Symbol = sym,
                            OpenTime = item[0].GetInt64(),
                            High = decimal.Parse(item[2].GetString()),
                            Low = decimal.Parse(item[3].GetString()),
                            Close = decimal.Parse(item[4].GetString()),
                            Volume = decimal.Parse(item[5].GetString()),
                            IsClosed = true
                        });
                    }

                    _1hHistoryBuffer[sym] = historyList;
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