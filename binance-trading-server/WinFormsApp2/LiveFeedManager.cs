using Binance.Net.Clients;
using Binance.Net.Enums;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Sockets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WinFormsApp2
{
    /// <summary>
    /// 币安多币种并发实盘 WebSocket 行情对接引擎 (Multi-Symbol Live Feed Manager)
    /// 支持同时订阅并并发推送多个交易对的 0 延迟 Tick 与 K 线流
    /// </summary>
    public class LiveFeedManager
    {
        private BinanceSocketClient? _socketClient;

        public bool IsRunning { get; private set; }
        public List<string> SubscribedSymbols { get; private set; } = new List<string>();
        public KlineInterval CurrentInterval { get; private set; }

        public event Action<string, Kline>? OnMultiLiveKlinePushed;
        public event Action<string, Tick>? OnMultiLiveTickPushed;
        public event Action<string>? OnStatusChanged;
        public event Action<string>? OnLog;

        /// <summary>
        /// 启动多币种并发实盘行情订阅 (如 BTCUSDT, ETHUSDT, SOLUSDT, BNBUSDT)
        /// </summary>
        public async Task StartMultiLiveFeedAsync(IEnumerable<string> symbols, KlineInterval interval)
        {
            await StopLiveFeedAsync().ConfigureAwait(false);

            SubscribedSymbols = symbols.Select(s => s.Trim().ToUpper()).Distinct().Where(s => !string.IsNullOrEmpty(s)).ToList();
            CurrentInterval = interval;

            if (SubscribedSymbols.Count == 0)
            {
                throw new ArgumentException("订阅币种列表不能为空！");
            }

            string symbolsStr = string.Join(", ", SubscribedSymbols);
            OnLog?.Invoke($"📡 [多币种实盘连接] 准备建立币安 WebSocket 长连接，并发订阅 {SubscribedSymbols.Count} 个币种 [{symbolsStr}] [{CurrentInterval}]...");
            OnStatusChanged?.Invoke($"🟡 正在连接币安实盘 [{symbolsStr}]...");

            try
            {
                _socketClient = new BinanceSocketClient();

                foreach (var symbol in SubscribedSymbols)
                {
                    string currentSym = symbol;

                    // 1. 订阅该币种实盘 K 线 (仅在 Final == true 时推送)
                    var klineResult = await _socketClient.SpotApi.ExchangeData.SubscribeToKlineUpdatesAsync(
                        currentSym, CurrentInterval, data =>
                        {
                            var k = data.Data.Data;
                            if (k.Final)
                            {
                                Kline liveKline = new Kline
                                {
                                    OpenTime = k.OpenTime,
                                    OpenPrice = k.OpenPrice,
                                    HighPrice = k.HighPrice,
                                    LowPrice = k.LowPrice,
                                    ClosePrice = k.ClosePrice,
                                    Volume = k.Volume,
                                    CloseTime = k.CloseTime,
                                    QuoteVolume = k.QuoteVolume,
                                    TradeCount = k.TradeCount
                                };
                                OnMultiLiveKlinePushed?.Invoke(currentSym, liveKline);
                            }
                        }).ConfigureAwait(false);

                    if (!klineResult.Success)
                    {
                        OnLog?.Invoke($"⚠️ 币种 [{currentSym}] 订阅 K线失败: {klineResult.Error?.Message}");
                    }

                    // 2. 订阅该币种原生 Trade Tick 逐笔成交
                    var tradeResult = await _socketClient.SpotApi.ExchangeData.SubscribeToTradeUpdatesAsync(
                        currentSym, data =>
                        {
                            var t = data.Data;
                            Tick liveTick = new Tick(t.TradeTime, t.Price, t.Quantity);
                            OnMultiLiveTickPushed?.Invoke(currentSym, liveTick);
                        }).ConfigureAwait(false);

                    if (!tradeResult.Success)
                    {
                        OnLog?.Invoke($"⚠️ 币种 [{currentSym}] 订阅 Tick 逐笔数据流失败: {tradeResult.Error?.Message}");
                    }
                }

                IsRunning = true;
                OnLog?.Invoke($"🟢 [多币种实盘成功] 成功开启 {SubscribedSymbols.Count} 个币种 [{symbolsStr}] 0 延迟并发盯盘！");
                OnStatusChanged?.Invoke($"🟢 多币种实盘中 [{SubscribedSymbols.Count} 个币种]");
            }
            catch (Exception ex)
            {
                IsRunning = false;
                OnLog?.Invoke($"❌ [多币种连接失败] {ex.Message}");
                OnStatusChanged?.Invoke($"🔴 实盘连接失败: {ex.Message}");
                await StopLiveFeedAsync().ConfigureAwait(false);
                throw;
            }
        }

        public Task StartLiveFeedAsync(string symbol, KlineInterval interval)
        {
            return StartMultiLiveFeedAsync(new[] { symbol }, interval);
        }

        /// <summary>
        /// 停止所有多币种实盘行情长连接
        /// </summary>
        public async Task StopLiveFeedAsync()
        {
            IsRunning = false;

            try
            {
                if (_socketClient != null)
                {
                    await _socketClient.UnsubscribeAllAsync().ConfigureAwait(false);
                    _socketClient.Dispose();
                    _socketClient = null;
                }

                OnStatusChanged?.Invoke("⚪ 实盘已停止");
                OnLog?.Invoke("⏹ [实盘已断开] 所有币种 WebSocket 实时行情接口已安全关闭。");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"停止实盘连接异常: {ex.Message}");
            }
        }
    }
}
