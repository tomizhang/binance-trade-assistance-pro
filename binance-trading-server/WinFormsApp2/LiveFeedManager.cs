using Binance.Net.Clients;
using Binance.Net.Enums;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Sockets;
using System;
using System.Threading.Tasks;

namespace WinFormsApp2
{
    /// <summary>
    /// 币安实盘 WebSocket + REST 实盘行情对接引擎 (Live Exchange Market Data Manager)
    /// 实现 0 延迟实时 Tick 与 K 线推送，无缝接入策略引擎与图表渲染
    /// </summary>
    public class LiveFeedManager
    {
        private BinanceSocketClient? _socketClient;

        public bool IsRunning { get; private set; }
        public string CurrentSymbol { get; private set; } = string.Empty;
        public KlineInterval CurrentInterval { get; private set; }

        public event Action<Kline>? OnLiveKlinePushed;
        public event Action<Tick>? OnLiveTickPushed;
        public event Action<string>? OnStatusChanged;
        public event Action<string>? OnLog;

        /// <summary>
        /// 启动币安实盘行情数据推送 (实盘 WebSocket K线与 0 延迟 Tick)
        /// </summary>
        public async Task StartLiveFeedAsync(string symbol, KlineInterval interval)
        {
            await StopLiveFeedAsync().ConfigureAwait(false);

            CurrentSymbol = symbol.Trim().ToUpper();
            CurrentInterval = interval;

            OnLog?.Invoke($"📡 [实盘连接] 准备连接币安实盘 WebSocket 行情接口 [{CurrentSymbol}] [{CurrentInterval}]...");
            OnStatusChanged?.Invoke($"🟡 正在连接币安实盘 [{CurrentSymbol}]...");

            try
            {
                // 1. 初始化 BinanceSocketClient
                _socketClient = new BinanceSocketClient();

                // 2. 订阅实盘 K 线 WebSocket 行情 (仅在周期 K 线完结 Final == true 时触发回调)
                var klineResult = await _socketClient.SpotApi.ExchangeData.SubscribeToKlineUpdatesAsync(
                    CurrentSymbol, CurrentInterval, data =>
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
                            OnLiveKlinePushed?.Invoke(liveKline);
                        }
                    }).ConfigureAwait(false);

                if (!klineResult.Success)
                {
                    throw new Exception($"订阅实盘 K线失败: {klineResult.Error?.Message}");
                }

                // 3. 订阅币安官方原生 Tick 逐笔成交 WebSocket 行情 (SubscribeToTradeUpdatesAsync，绝对不对接 AggregateTrade)
                var tradeResult = await _socketClient.SpotApi.ExchangeData.SubscribeToTradeUpdatesAsync(
                    CurrentSymbol, data =>
                    {
                        var t = data.Data;
                        Tick liveTick = new Tick(t.TradeTime, t.Price, t.Quantity);
                        OnLiveTickPushed?.Invoke(liveTick);
                    }).ConfigureAwait(false);

                if (!tradeResult.Success)
                {
                    throw new Exception($"订阅实盘 Tick 逐笔数据流失败: {tradeResult.Error?.Message}");
                }

                IsRunning = true;
                OnLog?.Invoke($"🟢 [实盘连接成功] 已成功订阅币安 [{CurrentSymbol}] 实盘 K 线与 0 延迟 Tick 逐笔行情流！");
                OnStatusChanged?.Invoke($"🟢 实盘运行中 [{CurrentSymbol}] [{CurrentInterval}]");
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
