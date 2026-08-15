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
    /// 币安实盘 WebSocket + REST 实盘行情对接引擎 (Live Exchange Market Data Manager)
    /// 支持单币种与多币种并发 0 延迟实时 Tick 与 K 线推送
    /// </summary>
    public class LiveFeedManager
    {
        private BinanceSocketClient? _socketClient;

        public bool IsRunning { get; private set; }
        public string CurrentSymbol { get; private set; } = string.Empty;
        public KlineInterval CurrentInterval { get; private set; }

        public event Action<Kline>? OnLiveKlinePushed;
        public event Action<Tick>? OnLiveTickPushed;

        // 多币种并发推送事件 (附带 Symbol 属性)
        public event Action<string, Kline>? OnMultiLiveKlinePushed;
        public event Action<string, Tick>? OnMultiLiveTickPushed;

        public event Action<string>? OnStatusChanged;
        public event Action<string>? OnLog;

        /// <summary>
        /// 启动单币种币安实盘行情数据推送
        /// </summary>
        public async Task StartLiveFeedAsync(string symbol, KlineInterval interval)
        {
            await StartMultiLiveFeedAsync(new[] { symbol }, interval).ConfigureAwait(false);
        }

        /// <summary>
        /// 启动多币种并发币安实盘 WebSocket 行情接口订阅
        /// </summary>
        public async Task StartMultiLiveFeedAsync(IEnumerable<string> symbols, KlineInterval interval)
        {
            await StopLiveFeedAsync().ConfigureAwait(false);

            List<string> symbolList = symbols.Select(s => (s ?? string.Empty).Trim().ToUpper()).Distinct().Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (symbolList.Count == 0)
            {
                throw new ArgumentException("订阅币种列表不能为空");
            }

            CurrentSymbol = symbolList[0];
            CurrentInterval = interval;

            OnLog?.Invoke($"📡 [实盘并发连接] 正在连接币安 WebSocket，订阅 {symbolList.Count} 个币种 [{string.Join(", ", symbolList)}] [{CurrentInterval}]...");
            OnStatusChanged?.Invoke($"🟡 正在连接币安实盘 ({symbolList.Count} 个币种)...");

            try
            {
                _socketClient = new BinanceSocketClient();

                foreach (var sym in symbolList)
                {
                    string currentSym = sym;

                    // 1. 订阅 Spot / Futures K 线 WebSocket 行情
                    var klineResult = await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToKlineUpdatesAsync(
                        currentSym, CurrentInterval, data =>
                        {
                            var k = data.Data.Data;
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

                            if (currentSym.Equals(CurrentSymbol, StringComparison.OrdinalIgnoreCase))
                            {
                                OnLiveKlinePushed?.Invoke(liveKline);
                            }
                            OnMultiLiveKlinePushed?.Invoke(currentSym, liveKline);
                        }).ConfigureAwait(false);

                    if (!klineResult.Success)
                    {
                        OnLog?.Invoke($"⚠️ 币种 [{currentSym}] K线订阅提示: {klineResult.Error?.Message}");
                    }

                    // 2. 订阅币安原生 0 延迟 Tick 逐笔成交行情
                    var tradeResult = await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToTradeUpdatesAsync(
                        currentSym, data =>
                        {
                            var t = data.Data;
                            Tick liveTick = new Tick(t.TradeTime, t.Price, t.Quantity);

                            if (currentSym.Equals(CurrentSymbol, StringComparison.OrdinalIgnoreCase))
                            {
                                OnLiveTickPushed?.Invoke(liveTick);
                            }
                            OnMultiLiveTickPushed?.Invoke(currentSym, liveTick);
                        }).ConfigureAwait(false);

                    if (!tradeResult.Success)
                    {
                        OnLog?.Invoke($"⚠️ 币种 [{currentSym}] Tick 逐笔订阅提示: {tradeResult.Error?.Message}");
                    }
                }

                IsRunning = true;
                OnLog?.Invoke($"🟢 [实盘连接成功] 已成功建立币安 {symbolList.Count} 个币种实盘 0 延迟 WebSocket 数据流！");
                OnStatusChanged?.Invoke($"🟢 实盘运行中 ({symbolList.Count} 个币种)");
            }
            catch (Exception ex)
            {
                IsRunning = false;
                OnLog?.Invoke($"❌ [实盘连接失败] {ex.Message}");
                OnStatusChanged?.Invoke($"🔴 实盘连接失败: {ex.Message}");
                await StopLiveFeedAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// 停止实盘行情推送并断开 WebSocket 连接
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
                OnLog?.Invoke("⏹ [实盘已断开] 币安实盘 WebSocket 行情接口已安全关闭。");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"停止实盘连接异常: {ex.Message}");
            }
        }
    }
}
