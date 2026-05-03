using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingTerminal.Hubs;
using TradingTerminal.Models; // 🌟 引入订单模型

namespace TradingTerminal.Services
{
    public class HeikinAshiService : BackgroundService
    {
        private readonly ILogger<HeikinAshiService> _logger;
        private readonly HeikinAshiEngine _engine;
        private readonly IHubContext<MarketHub> _hubContext;
        private readonly MarketEventBus _eventBus;
        private readonly BinanceWebSocketService _wsService;
        private readonly OrderChannel _orderChannel; // 🌟 1. 注入我们的订单高速管道

        private readonly HashSet<string> _watchList = new();
        //private readonly string[] _timeframes = { "2m", "4m", "6m", "8m", "10m", "1h", "1d" };
        private readonly string[] _timeframes = { "2m",  };
        private readonly SemaphoreSlim _lock = new(1, 1);

        public HeikinAshiService(
            ILogger<HeikinAshiService> logger,
            HeikinAshiEngine engine,
            IHubContext<MarketHub> hubContext,
            MarketEventBus eventBus,
            BinanceWebSocketService wsService,
            OrderChannel orderChannel) // 👈 注入
        {
            _logger = logger;
            _engine = engine;
            _hubContext = hubContext;
            _eventBus = eventBus;
            _wsService = wsService;
            _orderChannel = orderChannel;

            _eventBus.OnKlineReceived += HandleKlineReceived;
        }

        // ... (UpdateWatchListAsync 保持不变) ...
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

        private void HandleKlineReceived(KlineMessage msg)
        {
            if (!_watchList.Contains(msg.Symbol)) return;
            if (!_timeframes.Contains(msg.Interval)) return;

            // 1. 依然把实时的 tick 数据喂给引擎，让引擎保持内部状态最新
            bool isReversed = _engine.ProcessLiveKlineAndCheckReversal(
                msg.Symbol, msg.Interval, msg.Open, msg.Close, msg.High, msg.Low, msg.OpenTime, msg.IsClosed);

            // ==========================================
            // 🌟 核心修复：防重绘拦截器！
            // 必须且仅当这根 K 线彻底走完 (msg.IsClosed == true) 时，才承认反转信号！
            // 2分钟就严格等2分钟结束，10分钟就严格等10分钟结束。
            // ==========================================
            if (isReversed && msg.IsClosed)
            {
                bool isBullish = _engine.GetCurrentDirection(msg.Symbol, msg.Interval);

                // 发送前端警报
                var alert = new
                {
                    symbol = msg.Symbol,
                    timeframe = msg.Interval,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    type = "HA_REVERSAL",
                    direction = isBullish ? "多头" : "空头",
                    action = isBullish ? "做多 ↗" : "做空 ↘",
                    isBullish = isBullish
                };
                _hubContext.Clients.All.SendAsync("ReceiveHaAlert", alert);

                // 蓝图落地：确认为 2m 周期收盘级别的反转，触发自动化实盘交易！
                if (msg.Interval == "2m")
                {
                    // 传入的 msg.Close 现在是这根 2分钟 K线的绝对收盘价，绝不含糊！
                    _ = Task.Run(() => ExecuteTradeStrategyAsync(msg.Symbol, isBullish, msg.Close));
                }
            }
        }

        // ==========================================
        // 🌟 自动化交易策略执行核心 (对应蓝图 2.1 & 2.1.1)
        // ==========================================
        // ==========================================
        // 🌟 自动化交易策略执行核心 (开仓 + 止盈 + 止损)
        // ==========================================
        private async Task ExecuteTradeStrategyAsync(string symbol, bool isBullish, decimal currentPrice)
        {
            try
            {
                _logger.LogInformation($"🤖 [策略触发] 检测到 {symbol} 2m 级别反转，准备发射组合订单...");

                string side = isBullish ? "BUY" : "SELL";

                decimal tradeMarginUsdt = 2m;
                decimal tradeLeverage = 5m;

                // 计算止损价与止盈价
                decimal stopLossPrice = isBullish ? currentPrice * 0.99m : currentPrice * 1.01m;
                decimal takeProfitPrice = isBullish ? currentPrice * 1.02m : currentPrice * 0.98m;

                // 🌟 核心重构：将 3 个独立指令，合并为 1 个“连招指令”
                var comboSignal = new OrderSignal
                {
                    Symbol = symbol,
                    Action = OrderAction.OpenMarket, // 主动作依然是开仓
                    Side = side,
                    IsUsdtMargin = true,
                    UsdtAmount = tradeMarginUsdt,
                    Leverage = tradeLeverage,

                    // 🌟 把附加的止盈止损价“绑”在主订单上！
                    StopLossPrice = stopLossPrice,
                    TakeProfitPrice = takeProfitPrice,

                    StrategyName = "HA_2m_Reversal",
                    Reason = $"反转价 {currentPrice}，SL:{stopLossPrice:F4}，TP:{takeProfitPrice:F4}",
                    Message = "执行开仓，并要求消费者挂载止盈止损"
                };

                // 🌟 现在只需往管道里扔 1 次！
                await _orderChannel.WriteAsync(comboSignal);

                // (删掉原来这里的 Task.Delay 和另外两次 WriteAsync)
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ [策略打包失败] {symbol}: {ex.Message}");
            }
        }
        // ... (ExecuteAsync 和 SyncHistoricalDataAsync 保持不变) ...
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
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            klines.Add(new
                            {
                                OpenTime = item[0].GetInt64(),
                                Open = decimal.Parse(item[1].GetString()),
                                High = decimal.Parse(item[2].GetString()),
                                Low = decimal.Parse(item[3].GetString()),
                                Close = decimal.Parse(item[4].GetString())
                            });
                        }
                        _engine.InitializeFromHistory(sym, tf, klines);
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