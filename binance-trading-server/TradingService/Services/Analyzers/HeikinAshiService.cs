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

        // 🌟 只保留 K 线价格缓冲区，用于计算均线平滑的高低点
        private readonly ConcurrentDictionary<string, List<KlineMessage>> _klineBuffer = new();
        private const int BUFFER_SIZE = 30;

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
        // 🌟 核心过滤：简单移动平均线 (SMA) 平滑处理，抹平单根插针
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
        // 🌟 实时心跳：简化版，完全信任引擎，只做均线结构判断
        // ==========================================
        private void HandleKlineReceived(KlineMessage msg)
        {
            if (!_watchList.Contains(msg.Symbol)) return;
            if (!_timeframes.Contains(msg.Interval)) return;

            // 1. 获取引擎处理结果 (底层的防十字星、波动率、历史连续性过滤已在引擎内部完成)
            LiveHaResult liveHa = _engine.ProcessLiveKlineAndCheckReversal(
                msg.Symbol, msg.Interval, msg.Open, msg.Close, msg.High, msg.Low, msg.OpenTime, msg.IsClosed);

            // 2. 绝对防重绘：必须且仅当这根 K 线彻底走完时，才介入结构判定
            if (msg.IsClosed)
            {
                string key = $"{msg.Symbol}_{msg.Interval}";

                // 维护 K 线价格缓冲区
                var buffer = _klineBuffer.GetOrAdd(key, _ => new List<KlineMessage>());
                buffer.Add(msg);
                if (buffer.Count > BUFFER_SIZE) buffer.RemoveAt(0);

                // 3. 只要底层引擎吐出了 True，说明动能、趋势长度都完美符合条件！
                if (liveHa.IsReversed)
                {
                    bool isBullish = liveHa.IsBullish;
                    bool confirmPivot = false;

                    // ==========================================
                    // 🛡️ 唯一保留的风控关卡：均线顶底结构共振 (防半山腰)
                    // ==========================================
                    if (buffer.Count >= 10)
                    {
                        // 提取基础数据，使用 3周期 MA 进行平滑过滤插针
                        var smoothedHighs = SmoothData(buffer.Select(k => k.High).ToList(), 3);
                        var smoothedLows = SmoothData(buffer.Select(k => k.Low).ToList(), 3);

                        // 寻找极值点 (左3根确认趋势，右1根确认收口)
                        var (peaks, valleys) = PivotHelper.CalculatePeaks(smoothedHighs, smoothedLows, leftLen: 3, rightLen: 1);

                        int currentIndex = buffer.Count - 1; // 当前 K 线索引

                        if (isBullish)
                        {
                            // 📈 做多：要求前方 1 到 2 根必须是均线的“支撑低点”
                            confirmPivot = valleys.Contains(currentIndex - 1) || valleys.Contains(currentIndex - 2);
                            if (!confirmPivot) _logger.LogDebug($"🛡️ [结构过滤] {msg.Symbol} 缺乏均线支撑底结构，拒绝半山腰做多。");
                        }
                        else
                        {
                            // 📉 做空：要求前方 1 到 2 根必须是均线的“压制高点”
                            confirmPivot = peaks.Contains(currentIndex - 1) || peaks.Contains(currentIndex - 2);
                            if (!confirmPivot) _logger.LogDebug($"🛡️ [结构过滤] {msg.Symbol} 缺乏均线压制顶结构，拒绝半山腰做空。");
                        }
                    }

                    // ==========================================
                    // 🚀 最终双重共振成立：发射订单！
                    // ==========================================
                    if (confirmPivot)
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
                            _logger.LogWarning($"🔥 [终极结构爆发] {msg.Symbol} 底层动能反转 + 均线顶底结构完全吻合，执行重拳出击！");
                            _ = Task.Run(() => ExecuteTradeStrategyAsync(msg.Symbol, isBullish, msg.Close));
                        }
                    }
                }
            }
        }

        // ==========================================
        // 🌟 自动化交易策略执行核心
        // ==========================================
        private async Task ExecuteTradeStrategyAsync(string symbol, bool isBullish, decimal currentPrice)
        {
            try
            {
                // ⚠️ 致命修复：去掉原本多余的 "!" 感叹号！多头就是 BUY，空头就是 SELL
                string side = isBullish ? "BUY" : "SELL";

                decimal tradeMarginUsdt = 1.5m;
                decimal tradeLeverage = 5m;

                decimal targetRoeTp = 0.055m; // 目标 ROE: +10%
                decimal riskRoeSl = 0.04m;   // 止损 ROE: -8%

                decimal priceChangeTp = targetRoeTp / tradeLeverage;
                decimal priceChangeSl = riskRoeSl / tradeLeverage;

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
                    Reason = $"建仓价 {currentPrice:F4}，杠杆 {tradeLeverage}X，SL:{stopLossPrice:F4} (-{riskRoeSl:P0})，TP:{takeProfitPrice:F4} (+{targetRoeTp:P0})",
                    Message = "均线结构共振反转连招"
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