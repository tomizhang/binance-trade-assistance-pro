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
        private readonly string[] _timeframes = { "2m", "4m", "6m", "8m", "10m", "1h", "1d" };
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

            bool isReversed = _engine.ProcessLiveKlineAndCheckReversal(
                msg.Symbol, msg.Interval, msg.Open, msg.Close, msg.High, msg.Low, msg.OpenTime, msg.IsClosed);

            if (isReversed)
            {
                bool isBullish = _engine.GetCurrentDirection(msg.Symbol, msg.Interval);

                // 1. 发送前端警报 (原有功能)
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

                // 🌟 2. 蓝图落地：如果是 2m 周期的反转，直接触发自动化实盘交易！
                if (msg.Interval == "2m")
                {
                    // ⚠️ 注意：使用 _ = Task.Run() 丢入线程池异步执行，绝对不阻塞当前 K 线解析的主线程！
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
                _logger.LogInformation($"🤖 [策略触发] 检测到 {symbol} 2m 级别 HA 反转 ({(isBullish ? "多" : "空")})，准备发射订单...");

                string side = isBullish ? "BUY" : "SELL";
                string positionSide = isBullish ? "LONG" : "SHORT";

                // 🌟 统一资金管理参数，防止开平仓数量计算不一致！
                decimal tradeMarginUsdt = 2m; // 每次下注本金
                decimal tradeLeverage = 5m;   // 杠杆倍数

                // ==============================
                // 第一步：计算止损价与止盈价
                // 采用 1:2 经典盈亏比：1% 止损，2% 止盈
                // ==============================
                decimal stopLossPrice = isBullish
                    ? currentPrice * 0.99m  // 做多：跌 1% 止损
                    : currentPrice * 1.01m; // 做空：涨 1% 止损

                decimal takeProfitPrice = isBullish
                    ? currentPrice * 1.01m  // 做多：涨 2% 止盈
                    : currentPrice * 0.99m; // 做空：跌 2% 止盈

                // ==============================
                // 第二步：将“开仓指令”扔进管道
                // ==============================
                var openSignal = new OrderSignal
                {
                    Symbol = symbol,
                    Action = OrderAction.OpenMarket,
                    Side = side,
                    IsUsdtMargin = true,
                    UsdtAmount = tradeMarginUsdt,
                    Leverage = tradeLeverage,
                    StrategyName = "HA_2m_Reversal",
                    Reason = $"2m 级别出现 {(isBullish ? "多" : "空")} 头反转，当前价格 {currentPrice}",
                    Message = "系统自动执行 2m HA 突破策略"
                };
                await _orderChannel.WriteAsync(openSignal);

                // ⚠️ 极其关键的细节：稍微等待 500 毫秒，确保开仓单先被撮合
                await Task.Delay(500);

                // ==============================
                // 第三步：将“止损指令”扔进管道 (防爆盾)
                // ==============================
                var stopLossSignal = new OrderSignal
                {
                    Symbol = symbol,
                    Action = OrderAction.StopLossMarket,
                    Side = positionSide,
                    IsUsdtMargin = true,
                    UsdtAmount = tradeMarginUsdt, // 保持与开仓一致
                    Leverage = tradeLeverage,     // 保持与开仓一致
                    StopPrice = stopLossPrice,
                    StrategyName = "HA_2m_Reversal_SL",
                    Reason = $"开仓保护: 止损价设为 {stopLossPrice:F4}",
                    Message = "系统自动挂载保护性止损"
                };
                await _orderChannel.WriteAsync(stopLossSignal);

                // ==============================
                // 第四步：将“止盈指令”扔进管道 (利润收割机)
                // ==============================
                var takeProfitSignal = new OrderSignal
                {
                    Symbol = symbol,
                    Action = OrderAction.TakeProfitMarket,
                    Side = positionSide,
                    IsUsdtMargin = true,
                    UsdtAmount = tradeMarginUsdt, // 保持与开仓一致
                    Leverage = tradeLeverage,     // 保持与开仓一致
                    StopPrice = takeProfitPrice,
                    StrategyName = "HA_2m_Reversal_TP",
                    Reason = $"利润锁定: 止盈价设为 {takeProfitPrice:F4}",
                    Message = "系统自动挂载目标止盈"
                };
                await _orderChannel.WriteAsync(takeProfitSignal);

                _logger.LogInformation($"✅ [策略执行完毕] {symbol} 开仓+止损+止盈 OCO指令组已全部投递！");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ [策略执行失败] {symbol} 自动下单过程中发生错误: {ex.Message}");
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