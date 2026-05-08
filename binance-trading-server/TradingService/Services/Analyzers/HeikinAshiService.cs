using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingTerminal.Hubs;
using TradingTerminal.Models;
using TradingTerminal.Utils;

namespace TradingTerminal.Services
{
    public class HeikinAshiService : BackgroundService
    {
        private readonly ILogger<HeikinAshiService> _logger;
        private readonly HeikinAshiEngine _engine;
        private readonly IHubContext<MarketHub> _hubContext;
        private readonly MarketEventBus _eventBus;
        private readonly BinanceWebSocketService _wsService;
        private readonly OrderChannel _orderChannel;

        // 🌟 K 线价格缓冲区，用于计算均线平滑的高低点
        private readonly ConcurrentDictionary<string, List<KlineMessage>> _klineBuffer = new();
        private const int BUFFER_SIZE = 50;

        private readonly HashSet<string> _watchList = new();
        private readonly string[] _timeframes = { "2m" };
        private readonly SemaphoreSlim _lock = new(1, 1);

        public HeikinAshiService(
            ILogger<HeikinAshiService> logger,
            HeikinAshiEngine engine,
            IHubContext<MarketHub> hubContext,
            MarketEventBus eventBus,
            BinanceWebSocketService wsService,
            OrderChannel orderChannel)
        {
            _logger = logger;
            _engine = engine;
            _hubContext = hubContext;
            _eventBus = eventBus;
            _wsService = wsService;
            _orderChannel = orderChannel;

            _eventBus.OnKlineReceived += HandleKlineReceived;
        }

        // ==========================================
        // 🌟 核心过滤：简单移动平均线 (SMA) 平滑处理
        // ==========================================
        private List<decimal> SmoothData(List<decimal> rawData, int period = 3)
        {
            var smoothed = new List<decimal>(rawData.Count);
            for (int i = 0; i < rawData.Count; i++)
            {
                if (i < period - 1)
                {
                    smoothed.Add(rawData[i]);
                    continue;
                }
                decimal sum = 0;
                for (int j = 0; j < period; j++) sum += rawData[i - j];
                smoothed.Add(sum / period);
            }
            return smoothed;
        }

        public async Task UpdateWatchListAsync(IEnumerable<string> symbols)
        {
            var requested = symbols.Select(s => s.ToUpper()).ToList();
            List<string> toAdd, toRemove;

            await _lock.WaitAsync();
            try
            {
                toAdd = requested.Except(_watchList).ToList();
                toRemove = _watchList.Except(requested).ToList();

                foreach (var sym in toAdd) _watchList.Add(sym);
                foreach (var sym in toRemove) _watchList.Remove(sym);
            }
            finally { _lock.Release(); }

            if (toRemove.Any())
            {
                var streamsToRemove = toRemove.SelectMany(sym => _timeframes.Select(tf => $"{sym.ToLower()}@kline_{tf}")).ToList();
                await _wsService.UnsubscribeBackendAsync(streamsToRemove);
            }

            if (toAdd.Any())
            {
                _logger.LogInformation($"✨ [HA雷达] 锁定新目标: {string.Join(", ", toAdd)}，正在拉取历史数据...");
                await SyncHistoricalDataAsync(toAdd, CancellationToken.None);
                var streamsToAdd = toAdd.SelectMany(sym => _timeframes.Select(tf => $"{sym.ToLower()}@kline_{tf}")).ToList();
                await _wsService.SubscribeBackendAsync(streamsToAdd);
            }
        }

        // ==========================================
        // 🌟 实时心跳：处理 K 线与动态止损提取
        // ==========================================
        private void HandleKlineReceived(KlineMessage msg)
        {
            if (!_watchList.Contains(msg.Symbol)) return;
            if (!_timeframes.Contains(msg.Interval)) return;

            LiveHaResult liveHa = _engine.ProcessLiveKlineAndCheckReversal(
                msg.Symbol, msg.Interval, msg.Open, msg.Close, msg.High, msg.Low, msg.OpenTime, msg.IsClosed);

            if (msg.IsClosed)
            {
                string key = $"{msg.Symbol}_{msg.Interval}";

                var buffer = _klineBuffer.GetOrAdd(key, _ => new List<KlineMessage>());
                buffer.Add(msg);
                if (buffer.Count > BUFFER_SIZE) buffer.RemoveAt(0);

                if (liveHa.IsReversed)
                {
                    bool isBullish = liveHa.IsBullish;
                    bool confirmPivot = false;
                    decimal pivotPrice = 0m; // 🌟 记录反转前的极值点 (防守位)

                    if (buffer.Count >= 10)
                    {
                        var smoothedHighs = SmoothData(buffer.Select(k => k.High).ToList(), 3);
                        var smoothedLows = SmoothData(buffer.Select(k => k.Low).ToList(), 3);

                        var (peaks, valleys) = PivotHelper.CalculatePeaks(smoothedHighs, smoothedLows, leftLen: 3, rightLen: 1);

                        int currentIndex = buffer.Count - 1;

                        if (isBullish)
                        {
                            // 📈 做多：要求前方是支撑低点，并提取该低点的真实 Low 价格
                            if (valleys.Contains(currentIndex - 1)) { confirmPivot = true; pivotPrice = buffer[currentIndex - 1].Low; }
                            else if (valleys.Contains(currentIndex - 2)) { confirmPivot = true; pivotPrice = buffer[currentIndex - 2].Low; }

                            if (!confirmPivot) _logger.LogDebug($"🛡️ [结构过滤] {msg.Symbol} 缺乏均线支撑底结构，拒绝做多。");
                        }
                        else
                        {
                            // 📉 做空：要求前方是压制高点，并提取该高点的真实 High 价格
                            if (peaks.Contains(currentIndex - 1)) { confirmPivot = true; pivotPrice = buffer[currentIndex - 1].High; }
                            else if (peaks.Contains(currentIndex - 2)) { confirmPivot = true; pivotPrice = buffer[currentIndex - 2].High; }

                            if (!confirmPivot) _logger.LogDebug($"🛡️ [结构过滤] {msg.Symbol} 缺乏均线压制顶结构，拒绝做空。");
                        }
                    }

                    if (confirmPivot && pivotPrice > 0)
                    {
                        var alert = new
                        {
                            symbol = msg.Symbol,
                            timeframe = msg.Interval,
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            type = "HA_REVERSAL_CONFIRMED",
                            direction = isBullish ? "多头" : "空头",
                            action = isBullish ? "做多 ↗" : "做空 ↘",
                            isBullish = isBullish
                        };
                        _hubContext.Clients.All.SendAsync("ReceiveHaAlert", alert);

                        if (msg.Interval == "2m")
                        {
                            _logger.LogWarning($"🔥 [终极结构爆发] {msg.Symbol} 底层动能反转 + 结构吻合，获取到防守极值点: {pivotPrice:F6}");
                            // 🌟 将 pivotPrice 传递给策略执行器
                            _ = Task.Run(() => ExecuteTradeStrategyAsync(msg.Symbol, isBullish, msg.Close, pivotPrice));
                        }
                    }
                }
            }
        }

        // ==========================================
        // 🌟 自动化交易策略执行核心 (带动态区间止损)
        // ==========================================
        private async Task ExecuteTradeStrategyAsync(string symbol, bool isBullish, decimal currentPrice, decimal pivotPrice)
        {
            try
            {
                string side = isBullish ? "BUY" : "SELL";

                decimal tradeMarginUsdt = 1.5m;
                decimal tradeLeverage = 5m;

                // 🌟 1. 计算当前价到反转极值点（前高/前低）的绝对距离百分比
                decimal rawPriceDistance = Math.Abs(currentPrice - pivotPrice);
                decimal rawPriceChangeSl = currentPrice > 0 ? (rawPriceDistance / currentPrice) : 0;

                // 🌟 2. 将价格变动转换为本金的盈亏率 (ROE)
                decimal rawRoeSl = rawPriceChangeSl * tradeLeverage;

                // 🌟 3. 核心风控：夹紧止损范围！至少 1.5%，至多 5%
                // 如果极值点太近，强行扩展到 1.5% 防插针；如果极值点太远，强行切断在 5% 防爆仓
                decimal riskRoeSl = Math.Clamp(rawRoeSl, 0.015m, 0.05m);

                // 🌟 4. 重新反推出实际的触发价百分比
                decimal priceChangeSl = riskRoeSl / tradeLeverage;

                // 🌟 5. 动态止盈：止损是活的，止盈也得是活的 (固定 1:2 盈亏比)
                decimal targetRoeTp = riskRoeSl * 1.5m;
                decimal priceChangeTp = targetRoeTp / tradeLeverage;

                decimal stopLossPrice;
                decimal takeProfitPrice;

                if (side == "BUY")
                {
                    stopLossPrice = currentPrice * (1m - priceChangeSl);
                    takeProfitPrice = currentPrice * (1m + priceChangeTp);
                }
                else
                {
                    stopLossPrice = currentPrice * (1m + priceChangeSl);
                    takeProfitPrice = currentPrice * (1m - priceChangeTp);
                }

                var comboSignal = new OrderSignal
                {
                    Symbol = symbol,
                    Action = OrderAction.OpenMarket,
                    Side = side,
                    IsUsdtMargin = true,
                    UsdtAmount = tradeMarginUsdt,
                    Leverage = tradeLeverage,

                    StopLossPrice = stopLossPrice,
                    TakeProfitPrice = takeProfitPrice,

                    StrategyName = "HA_2m_MA_Structure",
                    Reason = $"入场 {currentPrice:F4}，极值点 {pivotPrice:F4}，SL:{stopLossPrice:F4} (-{riskRoeSl:P1})，TP:{takeProfitPrice:F4} (+{targetRoeTp:P1})",
                    Message = "均线结构共振连招，含自适应区间止损"
                };

                await _orderChannel.WriteAsync(comboSignal);
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ [策略打包失败] {symbol}: {ex.Message}");
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            List<string> list = null;
            do
            {
                try
                {
                    list = await _wsService.RefreshTopSymbolsAsync(stoppingToken);
                }
                catch (Exception e)
                {
                    _logger.LogInformation($"获取热门币种失败[{e.Message}]");
                }
                finally
                {
                    if (list is null) Thread.Sleep(1000);
                }
            } while (list is null);

            await UpdateWatchListAsync(list);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }

        private async Task SyncHistoricalDataAsync(List<string> symbols, CancellationToken ct)
        {
            foreach (var sym in symbols)
            {
                foreach (var tf in _timeframes)
                {
                    try
                    {
                        string json = await _wsService.GetHistoricalKlinesAsync(sym, tf, 1000);
                        using var doc = JsonDocument.Parse(json);

                        var klines = new List<dynamic>();
                        var bufferList = new List<KlineMessage>();

                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            var openTime = item[0].GetInt64();
                            var open = decimal.Parse(item[1].GetString());
                            var high = decimal.Parse(item[2].GetString());
                            var low = decimal.Parse(item[3].GetString());
                            var close = decimal.Parse(item[4].GetString());

                            klines.Add(new { OpenTime = openTime, Open = open, High = high, Low = low, Close = close });
                            bufferList.Add(new KlineMessage { Symbol = sym, Interval = tf, High = high, Low = low, Close = close, OpenTime = openTime });
                        }

                        _engine.InitializeFromHistory(sym, tf, klines);

                        if (bufferList.Count > BUFFER_SIZE)
                            bufferList = bufferList.Skip(bufferList.Count - BUFFER_SIZE).ToList();

                        _klineBuffer[$"{sym}_{tf}"] = bufferList;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"⚠️ [HA雷达] 同步 {sym} {tf} 历史数据失败: {ex.Message}");
                    }
                }
            }
        }
    }
}