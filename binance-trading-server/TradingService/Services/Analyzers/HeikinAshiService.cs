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

namespace TradingTerminal.Services
{
    public class HeikinAshiService : BackgroundService
    {
        private readonly ILogger<HeikinAshiService> _logger;
        private readonly HeikinAshiEngine _engine;
        private readonly IHubContext<MarketHub> _hubContext;
        private readonly MarketEventBus _eventBus;
        private readonly BinanceWebSocketService _wsService; // 🌟 注入唯一的网关

        private readonly HashSet<string> _watchList = new();
        private readonly string[] _timeframes = { "2m", "4m", "6m", "8m", "10m", "1h", "1d" };
        private readonly SemaphoreSlim _lock = new(1, 1);

        public HeikinAshiService(
            ILogger<HeikinAshiService> logger,
            HeikinAshiEngine engine,
            IHubContext<MarketHub> hubContext,
            MarketEventBus eventBus,
            BinanceWebSocketService wsService)
        {
            _logger = logger;
            _engine = engine;
            _hubContext = hubContext;
            _eventBus = eventBus;
            _wsService = wsService;

            // 核心：直接挂载到总线，坐等数据喂到嘴里
            _eventBus.OnKlineReceived += HandleKlineReceived;
        }

        // ==============================
        // 动态更新监听名单 (Diff 算法)
        // ==============================
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

            // 1. 剔除不要的币种
            if (toRemove.Any())
            {
                var streamsToRemove = toRemove.SelectMany(sym => _timeframes.Select(tf => $"{sym.ToLower()}@kline_{tf}")).ToList();
                await _wsService.UnsubscribeBackendAsync(streamsToRemove); // 命令网关取消后端订阅
            }

            // 2. 新增需要监控的币种
            if (toAdd.Any())
            {
                _logger.LogInformation($"✨ [HA雷达] 锁定新目标: {string.Join(", ", toAdd)}，正在拉取历史数据...");

                // 先补齐 1000 条历史 K 线
                await SyncHistoricalDataAsync(toAdd, CancellationToken.None);

                var streamsToAdd = toAdd.SelectMany(sym => _timeframes.Select(tf => $"{sym.ToLower()}@kline_{tf}")).ToList();
                await _wsService.SubscribeBackendAsync(streamsToAdd); // 命令网关追加后端订阅
            }
        }

        // ==============================
        // 总线数据消费回调
        // ==============================
        private void HandleKlineReceived(KlineMessage msg)
        {
            // 过滤掉不在监听名单里的数据
            if (!_watchList.Contains(msg.Symbol)) return;

            // 过滤掉我们不关心的周期
            if (!_timeframes.Contains(msg.Interval)) return;

            // 送入引擎判定
            bool isReversed = _engine.ProcessLiveKlineAndCheckReversal(
                msg.Symbol, msg.Interval, msg.Open, msg.Close, msg.High, msg.Low, msg.OpenTime, msg.IsClosed);

            if (isReversed)
            {
                // 🌟 核心优化：获取反转后的最新方向
                bool isBullish = _engine.GetCurrentDirection(msg.Symbol, msg.Interval);

                var alert = new
                {
                    symbol = msg.Symbol,
                    timeframe = msg.Interval,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    type = "HA_REVERSAL",
                    // 🌟 新增字段：明确说明当前趋势和做单动作
                    direction = isBullish ? "多头" : "空头",
                    action = isBullish ? "做多 ↗" : "做空 ↘",
                    isBullish = isBullish // 传给前端，方便前端用绿色/红色做高亮渲染
                };

                _hubContext.Clients.All.SendAsync("ReceiveHaAlert", alert);
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
                    if (list is null)
                    {
                        Thread.Sleep(1000);
                    }
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