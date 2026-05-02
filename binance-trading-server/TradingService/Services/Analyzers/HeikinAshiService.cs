using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingTerminal.Hubs;

namespace TradingTerminal.Services
{
    // 🌟 注意：我们把它变成了纯粹的业务逻辑类，它甚至可以不再继承 BackgroundService
    // 但为了维护启动时的补齐逻辑，我们保留它的宿主身份
    public class HeikinAshiService : BackgroundService
    {
        private readonly ILogger<HeikinAshiService> _logger;
        private readonly HeikinAshiEngine _engine;
        private readonly IHubContext<MarketHub> _hubContext;
        private readonly MarketEventBus _eventBus;
        private readonly BinanceWebSocketService _wsService; // 🌟 注入唯一的网管
        private readonly HttpClient _httpClient;

        private readonly HashSet<string> _watchList = new();
        private readonly string[] _timeframes = { "1m", "3m", "5m", "15m", "30m", "1h", "1d" };
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
            _httpClient = new HttpClient { BaseAddress = new Uri("https://fapi.binance.com") };

            // 🌟 核心：直接挂载到总线，坐等数据喂到嘴里
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
                await _wsService.UnsubscribeStreamsAsync(streamsToRemove); // 🌟 命令网管取消订阅
            }

            // 2. 新增需要监控的币种
            if (toAdd.Any())
            {
                _logger.LogInformation($"✨ [HA雷达] 锁定新目标: {string.Join(", ", toAdd)}，正在拉取历史数据...");

                // 先补齐 1000 条历史 K 线
                await SyncHistoricalDataAsync(toAdd, CancellationToken.None);

                var streamsToAdd = toAdd.SelectMany(sym => _timeframes.Select(tf => $"{sym.ToLower()}@kline_{tf}")).ToList();
                await _wsService.SubscribeStreamsAsync(streamsToAdd); // 🌟 命令网管追加订阅
            }
        }

        // ==============================
        // 总线数据消费回调
        // ==============================
        private void HandleKlineReceived(KlineMessage msg)
        {
            // 过滤掉不在监听名单里的数据
            if (!_watchList.Contains(msg.Symbol)) return;

            // 过滤掉我们不关心的周期 (比如大盘的标记价格或其他周期)
            if (!_timeframes.Contains(msg.Interval)) return;

            // 送入引擎判定
            bool isReversed = _engine.ProcessLiveKlineAndCheckReversal(
                msg.Symbol, msg.Interval, msg.Open, msg.Close, msg.High, msg.Low, msg.OpenTime, msg.IsClosed);

            if (isReversed)
            {
                var alert = new
                {
                    symbol = msg.Symbol,
                    timeframe = msg.Interval,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    type = "HA_REVERSAL"
                };

                _hubContext.Clients.All.SendAsync("ReceiveHaAlert", alert);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 因为现在不维护 WebSocket，这个后台任务只需要在系统重启时
            // 负责把数据库里存的默认监听名单拉起来就行了。
            // 比如默认监听 BTC:
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

            await Task.Delay(Timeout.Infinite, stoppingToken); // 挂起，直到程序退出
        }

        private async Task SyncHistoricalDataAsync(List<string> symbols, CancellationToken ct)
        {
            foreach (var sym in symbols)
            {
                foreach (var tf in _timeframes)
                {
                    long lastTime = _engine.GetLastClosedTime(sym, tf);
                    long startTime = lastTime > 0 ? lastTime : 0;
                    string url = $"/fapi/v1/klines?symbol={sym}&interval={tf}&limit=1000";
                    if (startTime > 0) url += $"&startTime={startTime}";

                    try
                    {
                        var res = await _httpClient.GetAsync(url, ct);
                        if (!res.IsSuccessStatusCode) continue;

                        var json = await res.Content.ReadAsStringAsync(ct);
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
                    catch { /* 忽略单个失败 */ }
                }
            }
        }
    }
}