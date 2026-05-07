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
using TradingTerminal.Utils; // 🌟 引入 PivotHelper 和 CumulativeHeikinAshiHelper

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

        // 🌟 K线与方向滑动窗口缓冲区
        private readonly ConcurrentDictionary<string, List<KlineMessage>> _klineBuffer = new();
        private readonly ConcurrentDictionary<string, List<bool>> _haDirectionBuffer = new();
        private const int BUFFER_SIZE = 30; // 缓存最近30根K线，足够过滤和计算极值点

        private readonly HashSet<string> _watchList = new();
        private readonly string[] _timeframes = { "2m" }; // 当前专注于 2m 级别核心突破
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
                    smoothed.Add(rawData[i]); // 前几根数据不足，直接用原值
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
        // 🌟 实时心跳：处理 K 线与双重共振防洗盘逻辑
        // ==========================================
        private void HandleKlineReceived(KlineMessage msg)
        {
            if (!_watchList.Contains(msg.Symbol)) return;
            if (!_timeframes.Contains(msg.Interval)) return;

            // 1. 实时的 tick 数据喂给 HA 引擎
            // 1. 获取完整的、包含当前最新价格的 HA 状态！
            LiveHaResult liveHa = _engine.ProcessLiveKlineAndCheckReversal(
                msg.Symbol, msg.Interval, msg.Open, msg.Close, msg.High, msg.Low, msg.OpenTime, msg.IsClosed);

            // 🌟 2. 绝对防重绘：必须且仅当这根 K 线彻底走完时，才承认信号！
            if (msg.IsClosed)
            {
                string key = $"{msg.Symbol}_{msg.Interval}";

                // 维护 K 线价格缓冲区
                var buffer = _klineBuffer.GetOrAdd(key, _ => new List<KlineMessage>());
                buffer.Add(msg);
                if (buffer.Count > BUFFER_SIZE) buffer.RemoveAt(0);

                // 维护 HA 方向缓冲区
                bool isBullish = _engine.GetCurrentDirection(msg.Symbol, msg.Interval);
                var dirBuffer = _haDirectionBuffer.GetOrAdd(key, _ => new List<bool>());
                dirBuffer.Add(isBullish);
                if (dirBuffer.Count > BUFFER_SIZE) dirBuffer.RemoveAt(0);

                // 3. 发生 HA 第一根反转，进入“多重风控”确认流程
                if (liveHa.IsReversed)
                {
                    bool isTrendValid = false;   // 第一关：原趋势是否结实
                    bool confirmPivot = false;   // 第二关：是否有结构支撑

                    // ==========================================
                    // 🛡️ 第一关：趋势过滤 (至少连续 4 根同向 K 线)
                    // ==========================================
                    if (dirBuffer.Count >= 5)
                    {
                        int lastIdx = dirBuffer.Count - 1; // 当前刚刚收盘的反转 K 线
                        bool expectedPrevDir = !isBullish; // 前方应该具备的趋势方向

                        // 严格检查前方 4 根 K 线是否全部保持同一种方向
                        if (dirBuffer[lastIdx - 1] == expectedPrevDir &&
                            dirBuffer[lastIdx - 2] == expectedPrevDir &&
                            dirBuffer[lastIdx - 3] == expectedPrevDir &&
                            dirBuffer[lastIdx - 4] == expectedPrevDir)
                        {
                            isTrendValid = true;
                        }
                        else
                        {
                            _logger.LogDebug($"🛡️ [趋势过滤] {msg.Symbol} 出现反转，但前方连续同向 K 线不足 4 根 (盘整洗盘)，拒绝入场！");
                        }
                    }

                    // ==========================================
                    // 🛡️ 第二关：均线顶底结构共振
                    // ==========================================
                    if (isTrendValid && buffer.Count >= 10)
                    {
                        // 提取基础数据，使用 3周期 MA 进行平滑过滤插针
                        var smoothedHighs = SmoothData(buffer.Select(k => k.High).ToList(), 3);
                        var smoothedLows = SmoothData(buffer.Select(k => k.Low).ToList(), 3);

                        // 寻找极值点 (左3根确认趋势，右1根确认收口)
                        var (peaks, valleys) = PivotHelper.CalculatePeaks(smoothedHighs, smoothedLows, leftLen: 3, rightLen: 1);

                        int currentIndex = buffer.Count - 1;
                        if (isBullish)
                        {
                            // 📈 做多：要求前方 1 到 2 根必须是均线的“支撑低点”
                            confirmPivot = valleys.Contains(currentIndex - 1) || valleys.Contains(currentIndex - 2);
                            if (!confirmPivot) _logger.LogDebug($"🛡️ [结构过滤] {msg.Symbol} 缺乏均线支撑结构，拒绝半山腰入场。");
                        }
                        else
                        {
                            // 📉 做空：要求前方 1 到 2 根必须是均线的“压制高点”
                            confirmPivot = peaks.Contains(currentIndex - 1) || peaks.Contains(currentIndex - 2);
                            if (!confirmPivot) _logger.LogDebug($"🛡️ [结构过滤] {msg.Symbol} 缺乏均线压制结构，拒绝半山腰入场。");
                        }
                    }

                    // ==========================================
                    // 🚀 最终双重共振成立：发射订单！
                    // ==========================================
                    if (isTrendValid /*&& confirmPivot*/)
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
                            _logger.LogWarning($"🔥 [终极共振爆发] {msg.Symbol} 原趋势极其扎实 + 均线结构吻合 + 动能首根反转，执行重拳出击！");
                            _ = Task.Run(() => ExecuteTradeStrategyAsync(msg.Symbol, isBullish, msg.Close));
                        }
                    }
                }
            }
        }

        // ==========================================
        // 🌟 自动化交易策略执行核心 (原子级连招)
        // ==========================================
        private async Task ExecuteTradeStrategyAsync(string symbol, bool isBullish, decimal currentPrice)
        {
            try
            {
                // ⚠️ 修正：多头对应 BUY，空头对应 SELL
                string side = isBullish ? "BUY" : "SELL";

                decimal tradeMarginUsdt = 1.5m; // 每次开仓保证金 (U)
                decimal tradeLeverage = 5m;   // 杠杆倍数

                // ==========================================
                // 🛡️ 杠杆自适应 ROE 止盈止损计算
                // ==========================================
                // 设定你的“本金盈亏目标”。例如：止盈赚取本金的 10%，止损容忍本金的 5%
                decimal targetRoeTp = 0.10m; // 目标 ROE: +10%
                decimal riskRoeSl = 0.08m;   // 止损 ROE: -5%

                // 标的资产实际需要变动的百分比 = 预期 ROE / 杠杆倍数
                decimal priceChangeTp = targetRoeTp / tradeLeverage;
                decimal priceChangeSl = riskRoeSl / tradeLeverage;

                decimal stopLossPrice;
                decimal takeProfitPrice;

                // 严格根据实际开仓方向计算上下边界
                if (side == "BUY")
                {
                    // 做多：止损在买入价下方，止盈在买入价上方
                    stopLossPrice = currentPrice * (1m - priceChangeSl);
                    takeProfitPrice = currentPrice * (1m + priceChangeTp);
                }
                else
                {
                    // 做空：止损在卖出价上方，止盈在卖出价下方
                    stopLossPrice = currentPrice * (1m + priceChangeSl);
                    takeProfitPrice = currentPrice * (1m - priceChangeTp);
                }

                // 核心重构：开仓、止损、止盈 合并为 1 个超级包裹发给消费者
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

                    StrategyName = "HA_2m_Reversal_Resonance",
                    Reason = $"建仓价 {currentPrice:F4}，杠杆 {tradeLeverage}X，SL:{stopLossPrice:F4} (-{riskRoeSl:P0})，TP:{takeProfitPrice:F4} (+{targetRoeTp:P0})",
                    Message = "执行防骗炮突破连招，附带杠杆自适应双边保护"
                };

                // 投递至交易网关的消费管道
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

        // ==========================================
        // 🌟 历史数据同步与双缓冲区预热
        // ==========================================
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

                        // 初始化引擎
                        _engine.InitializeFromHistory(sym, tf, klines);

                        // 截取最后 BUFFER_SIZE 根填入历史 K 线字典
                        if (bufferList.Count > BUFFER_SIZE)
                            bufferList = bufferList.Skip(bufferList.Count - BUFFER_SIZE).ToList();
                        _klineBuffer[$"{sym}_{tf}"] = bufferList;

                        // 🌟 利用工具类一次性计算历史 HA 方向，预热方向字典，防止系统刚启动时没数据错失良机
                        var haResults = CumulativeHeikinAshiHelper.Calculate(bufferList);
                        _haDirectionBuffer[$"{sym}_{tf}"] = haResults.Select(h => h.IsBullish).ToList();
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