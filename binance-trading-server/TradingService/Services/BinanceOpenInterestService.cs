using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingTerminal.Hubs;

namespace TradingTerminal.Services
{
    /// <summary>
    /// 币安雷达专属数据采集服务 (动态寻找全网最热币种 + 双核数据引擎)
    /// </summary>
    public class BinanceOpenInterestService : BackgroundService
    {
        private readonly ILogger<BinanceOpenInterestService> _logger;
        private readonly MarketEventBus _eventBus;
        private readonly IHubContext<MarketHub> _marketHubContext;
        private readonly HttpClient _httpClient;
        private readonly int DefaultTake = 100;
        private const string WsBaseUrl = "wss://fstream.binance.com/market/stream?streams=";

        // 🌟 核心：动态监控名单，不再写死
        private HashSet<string> _dynamicSymbols = new HashSet<string>();
        // 互斥锁，防止 OI 轮询和名单刷新时发生并发冲突
        private readonly SemaphoreSlim _symbolsLock = new SemaphoreSlim(1, 1);

        public BinanceOpenInterestService(
            ILogger<BinanceOpenInterestService> logger,
            MarketEventBus eventBus,
            IHubContext<MarketHub> marketHubContext)
        {
            _logger = logger;
            _eventBus = eventBus;
            _marketHubContext = marketHubContext;

            var handler = new SocketsHttpHandler
            {
#if DEBUG
                Proxy = new System.Net.WebProxy("socks5://127.0.0.1:10808"),
                UseProxy = true,
#endif
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            _httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://fapi.binance.com") };
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 [后台雷达] 动态热币捕捉 + 数据采集服务启动...");

            // 首次启动，先强制拉取一次热门名单
            await RefreshTopSymbolsAsync(stoppingToken);

            // 启动两个并发任务：WS 管 K 线，REST 管 OI
            var wsTask = MaintainDynamicKlineWsAsync(stoppingToken);
            var restTask = PollOpenInterestAsync(stoppingToken);

            await Task.WhenAll(wsTask, restTask);
        }

        // ==========================================
        // 🌟 核心 1：动态拉取 成交量Top10 + 涨幅Top10
        // ==========================================
        private async Task RefreshTopSymbolsAsync(CancellationToken stoppingToken)
        {
            try
            {
                var response = await _httpClient.GetAsync("/fapi/v1/ticker/24hr", stoppingToken);
                if (!response.IsSuccessStatusCode) return;

                var json = await response.Content.ReadAsStringAsync(stoppingToken);
                using var doc = JsonDocument.Parse(json);

                // 过滤出正常的 USDT 本位合约 (排除交割和其他结算币种)
                var validTickers = doc.RootElement.EnumerateArray()
                    .Where(x => x.GetProperty("symbol").GetString().EndsWith("USDT"))
                    .ToList();

                // 1. 获取成交额 (quoteVolume) 前 10 名
                var topVolume = validTickers
                    .OrderByDescending(x => decimal.Parse(x.GetProperty("quoteVolume").GetString()))
                    .Take(DefaultTake)
                    .Select(x => x.GetProperty("symbol").GetString().ToUpper());

                // 2. 获取涨幅 (priceChangePercent) 前 10 名
                var topGainers = validTickers
                    .OrderByDescending(x => decimal.Parse(x.GetProperty("priceChangePercent").GetString()))
                    .Take(DefaultTake)
                    .Select(x => x.GetProperty("symbol").GetString().ToUpper());

                await _symbolsLock.WaitAsync(stoppingToken);
                try
                {
                    // 3. 合并去重 (如果某币既是成交量前10又是涨幅前10，HashSet 会自动去重)
                    _dynamicSymbols = new HashSet<string>(topVolume.Concat(topGainers));
                    _logger.LogInformation($"🔥 [雷达更新] 最新锁定的资金战场 (共 {_dynamicSymbols.Count} 个): {string.Join(", ", _dynamicSymbols)}");
                }
                finally
                {
                    _symbolsLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ 刷新热门币种名单失败: {ex.Message}");
            }
        }

        // ==========================================
        // 核心 2：REST 轮询 OI (双核架构)
        // ==========================================
        private async Task PollOpenInterestAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                List<string> currentSymbols;
                await _symbolsLock.WaitAsync(stoppingToken);
                try { currentSymbols = _dynamicSymbols.ToList(); }
                finally { _symbolsLock.Release(); }

                if (!currentSymbols.Any()) continue;

                var tasks = currentSymbols.Select(async sym =>
                {
                    try
                    {
                        var response = await _httpClient.GetAsync($"/fapi/v1/openInterest?symbol={sym}", stoppingToken);
                        if (response.IsSuccessStatusCode)
                        {
                            var json = await response.Content.ReadAsStringAsync(stoppingToken);
                            using var doc = JsonDocument.Parse(json);
                            decimal oi = decimal.Parse(doc.RootElement.GetProperty("openInterest").GetString());

                            _eventBus.PublishOpenInterest(sym, oi);

                            // 瞒天过海：伪装成 WS 数据推给前端
                            var mockJson = JsonSerializer.Serialize(new
                            {
                                stream = $"{sym.ToLower()}@openInterest",
                                data = new { e = "openInterest", s = sym, oI = oi.ToString() }
                            });
                            await _marketHubContext.Clients.All.SendAsync("ReceiveMarketData", mockJson, stoppingToken);
                        }
                    }
                    catch { /* 忽略个别请求失败 */ }
                });

                await Task.WhenAll(tasks);
            }
        }

        // ==========================================
        // 核心 3：WebSocket 监听 1m K线 + 10分钟刷新机制
        // ==========================================
        private async Task MaintainDynamicKlineWsAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // 🌟 设置 10 分钟重连机制：每 10 分钟主动断开一次，刷新热门名单，然后重新连接！
                using var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                refreshCts.CancelAfter(TimeSpan.FromMinutes(10));

                try
                {
                    using var ws = new ClientWebSocket();
#if DEBUG
                    ws.Options.Proxy = new System.Net.WebProxy("socks5://127.0.0.1:10808");
#endif
                    ws.Options.KeepAliveInterval = TimeSpan.FromMinutes(2);

                    // 提取当前需要监听的小写 symbol 名单
                    List<string> streamsToListen = new List<string>();
                    await _symbolsLock.WaitAsync(stoppingToken);
                    try { streamsToListen = _dynamicSymbols.Select(s => $"{s.ToLower()}@kline_1m").ToList(); }
                    finally { _symbolsLock.Release(); }

                    if (!streamsToListen.Any())
                    {
                        await Task.Delay(1000, stoppingToken);
                        continue;
                    }

                    var uri = new Uri(WsBaseUrl + string.Join("/", streamsToListen));
                    _logger.LogInformation("🌐 [雷达 K线] 正在连接 WS...");

                    await ws.ConnectAsync(uri, refreshCts.Token);
                    _logger.LogInformation("✅ [雷达 K线] 连接成功，等待 10 分钟后换血重连");

                    var buffer = new byte[1024 * 16];
                    var ms = new MemoryStream();

                    while (ws.State == WebSocketState.Open && !refreshCts.IsCancellationRequested)
                    {
                        ms.SetLength(0);
                        WebSocketReceiveResult result;

                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(refreshCts.Token);
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15)); // 15秒防假死

                        try
                        {
                            do
                            {
                                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), timeoutCts.Token);
                                if (result.MessageType == WebSocketMessageType.Close) break;
                                ms.Write(buffer, 0, result.Count);
                            } while (!result.EndOfMessage);
                        }
                        catch (OperationCanceledException)
                        {
                            if (refreshCts.IsCancellationRequested) break; // 10分钟到期，正常跳出
                            throw new Exception("☠️ 15秒防假死触发！");
                        }

                        if (result.MessageType == WebSocketMessageType.Close) break;

                        var rawJson = Encoding.UTF8.GetString(ms.ToArray());
                        ProcessKlineData(rawJson);
                        await _marketHubContext.Clients.All.SendAsync("ReceiveMarketData", rawJson, refreshCts.Token);
                    }
                }
                catch (OperationCanceledException) when (refreshCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    // 🌟 10 分钟换血时间到！
                    _logger.LogInformation("🔄 [雷达] 10 分钟周期到达，正在重新扫描全网资金风口...");
                    await RefreshTopSymbolsAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"⚠️ [雷达 K线] 断开: {ex.Message}，5秒后重连...");
                    await Task.Delay(1000, stoppingToken);
                }
            }
        }

        private void ProcessKlineData(string jsonMessage)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonMessage);
                if (doc.RootElement.TryGetProperty("stream", out var streamElement))
                {
                    string streamName = streamElement.GetString();
                    if (streamName.EndsWith("@kline_1m"))
                    {
                        var dataNode = doc.RootElement.GetProperty("data");
                        string symbol = dataNode.GetProperty("s").GetString().ToUpper();
                        var kNode = dataNode.GetProperty("k");
                        var msg = new KlineMessage
                        {
                            Symbol = kNode.GetProperty("s").GetString().ToUpper(),
                            Interval = kNode.GetProperty("i").GetString(),
                            IsClosed = kNode.GetProperty("x").GetBoolean(),
                            Open = decimal.Parse(kNode.GetProperty("o").GetString()),
                            Close = decimal.Parse(kNode.GetProperty("c").GetString()),
                            High = decimal.Parse(kNode.GetProperty("h").GetString()),
                            Low = decimal.Parse(kNode.GetProperty("l").GetString()),
                            Volume = decimal.Parse(kNode.GetProperty("v").GetString()),
                            OpenTime = kNode.GetProperty("t").GetInt64()
                        };

                        _eventBus.PublishKline(msg); // 统一丢到总线
                    }
                }
            }
            catch { /* 忽略非标 JSON */ }
        }
    }
}