using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using Common.Cursor;
using Common.Helper;
using Common.Interfaces;
using Common.Models;
using CryptoExchange.Net.Objects.Sockets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Providers
{
    /// <summary>
    /// 基于 Binance.Net WebSocket 的实盘行情数据提供者
    /// 与 DuckDbHistoricalDataProvider 统一实现 IMarketDataProvider 接口，支持零代码修改无缝切换实盘
    /// </summary>
    public class BinanceLiveMarketDataProvider : IMarketDataProvider
    {
        private readonly IBinanceSocketClient _socketClient;
        private readonly bool _disposeSocketClient;
        private readonly List<UpdateSubscription> _subscriptions = new List<UpdateSubscription>();
        private readonly ConcurrentDictionary<string, List<MarketKline>> _liveKlines = new ConcurrentDictionary<string, List<MarketKline>>();
        private readonly ConcurrentDictionary<string, List<MarketTick>> _liveTicks = new ConcurrentDictionary<string, List<MarketTick>>();
        private readonly int _bufferCapacity;
        private readonly object _lock = new object();
        private bool _isRunning = false;

        public event Action<MarketKline>? OnKline;
        public event Action<MarketTick>? OnTick;

        public bool IsRunning => _isRunning;

        /// <summary>
        /// 构造函数，支持直接传入或自动创建 BinanceSocketClient
        /// </summary>
        public BinanceLiveMarketDataProvider(IBinanceSocketClient? socketClient = null, int bufferCapacity = 500)
        {
            if (socketClient == null)
            {
                _socketClient = new BinanceSocketClient();
                _disposeSocketClient = true;
            }
            else
            {
                _socketClient = socketClient;
                _disposeSocketClient = false;
            }

            _bufferCapacity = Math.Max(10, bufferCapacity);
        }

        #region 实盘 WebSocket 订阅机制

        /// <summary>
        /// 订阅现货实时 K 线推流
        /// </summary>
        public async Task<bool> SubscribeKlineAsync(string symbol, KlineInterval interval)
        {
            string key = $"{symbol.ToUpper()}_{interval.ToIntervalString()}";
            _liveKlines.TryAdd(key, new List<MarketKline>());

            var subResult = await _socketClient.SpotApi.ExchangeData.SubscribeToKlineUpdatesAsync(symbol, interval, data =>
            {
                var kline = MarketKline.FromBinanceStream(data.Data);
                UpdateLiveKlineBuffer(key, kline);
                OnKline?.Invoke(kline);
            }).ConfigureAwait(false);

            if (subResult.Success)
            {
                lock (_lock)
                {
                    _subscriptions.Add(subResult.Data);
                }
                return true;
            }

            return false;
        }

        /// <summary>
        /// 订阅现货实时逐笔成交 Trade 推流
        /// </summary>
        public async Task<bool> SubscribeTradeAsync(string symbol)
        {
            string sym = symbol.ToUpper();
            _liveTicks.TryAdd(sym, new List<MarketTick>());

            var subResult = await _socketClient.SpotApi.ExchangeData.SubscribeToTradeUpdatesAsync(symbol, data =>
            {
                var tick = MarketTick.FromBinanceStream(data.Data);
                UpdateLiveTickBuffer(sym, tick);
                OnTick?.Invoke(tick);
            }).ConfigureAwait(false);

            if (subResult.Success)
            {
                lock (_lock)
                {
                    _subscriptions.Add(subResult.Data);
                }
                return true;
            }

            return false;
        }

        private void UpdateLiveKlineBuffer(string key, MarketKline kline)
        {
            if (_liveKlines.TryGetValue(key, out var list))
            {
                lock (list)
                {
                    int lastIdx = list.Count - 1;
                    if (lastIdx >= 0 && list[lastIdx].OpenTime == kline.OpenTime)
                    {
                        list[lastIdx] = kline;
                    }
                    else
                    {
                        list.Add(kline);
                    }

                    if (list.Count > _bufferCapacity)
                    {
                        list.RemoveRange(0, list.Count - _bufferCapacity);
                    }
                }
            }
        }

        private void UpdateLiveTickBuffer(string symbol, MarketTick tick)
        {
            if (_liveTicks.TryGetValue(symbol, out var list))
            {
                lock (list)
                {
                    list.Add(tick);
                    if (list.Count > _bufferCapacity)
                    {
                        list.RemoveRange(0, list.Count - _bufferCapacity);
                    }
                }
            }
        }

        #endregion

        #region 游标拉取模式 (按需切片加载并以双向缓存提供)

        public ICursor<MarketKline> GetKlineCursor(string symbol, KlineInterval interval, DateTime startUtc, DateTime endUtc)
        {
            return GetKlineCursor(symbol, interval.ToIntervalString(), startUtc, endUtc);
        }

        public ICursor<MarketKline> GetKlineCursor(string symbol, string interval, DateTime startUtc, DateTime endUtc)
        {
            string key = $"{symbol.ToUpper()}_{interval}";
            var snapshot = new List<MarketKline>();
            if (_liveKlines.TryGetValue(key, out var list))
            {
                lock (list)
                {
                    snapshot = list.Where(k => k.OpenTime >= startUtc && k.OpenTime <= endUtc).ToList();
                }
            }
            return new MarketDataCursor<MarketKline>(snapshot, _bufferCapacity);
        }

        public ICursor<MarketTick> GetTickCursor(string symbol, DateTime startUtc, DateTime endUtc)
        {
            string sym = symbol.ToUpper();
            var snapshot = new List<MarketTick>();
            if (_liveTicks.TryGetValue(sym, out var list))
            {
                lock (list)
                {
                    snapshot = list.Where(t => t.Time >= startUtc && t.Time <= endUtc).ToList();
                }
            }
            return new MarketDataCursor<MarketTick>(snapshot, _bufferCapacity);
        }

        public IRawDataCursor GetRawKlineCursor(string symbol, string interval, DateTime startUtc, DateTime endUtc)
        {
            string key = $"{symbol.ToUpper()}_{interval}";
            var snapshot = new List<MarketKline>();
            if (_liveKlines.TryGetValue(key, out var list))
            {
                lock (list)
                {
                    snapshot = list.Where(k => k.OpenTime >= startUtc && k.OpenTime <= endUtc).ToList();
                }
            }
            return new RawMarketDataCursor(snapshot);
        }

        public IRawDataCursor GetRawTickCursor(string symbol, DateTime startUtc, DateTime endUtc)
        {
            string sym = symbol.ToUpper();
            var snapshot = new List<MarketTick>();
            if (_liveTicks.TryGetValue(sym, out var list))
            {
                lock (list)
                {
                    snapshot = list.Where(t => t.Time >= startUtc && t.Time <= endUtc).ToList();
                }
            }
            return new RawMarketDataCursor(snapshot);
        }

        public async Task ReplaySimulationAsync(
            string symbol,
            string interval,
            DateTime startUtc,
            DateTime endUtc,
            int tickDelayMs = 0,
            int klineDelayMs = 0,
            CancellationToken token = default,
            Action<MarketTick>? onTickAction = null,
            Action<MarketKline>? onKlineAction = null)
        {
            using var klineCursor = GetRawKlineCursor(symbol, interval, startUtc, endUtc);
            using var tickCursor = GetRawTickCursor(symbol, startUtc, endUtc);

            while (klineCursor.MoveNext() && !token.IsCancellationRequested)
            {
                var kline = klineCursor.ReadCurrentKline(symbol, interval);
                while (tickCursor.MoveNext() && !token.IsCancellationRequested)
                {
                    long tickTimeMs = tickCursor.GetInt64(4);
                    if (tickTimeMs < kline.OpenTimeMs) continue;
                    if (tickTimeMs > kline.CloseTimeMs) { tickCursor.MovePrevious(); break; }

                    var tick = tickCursor.ReadCurrentTick(symbol);
                    OnTick?.Invoke(tick);
                    onTickAction?.Invoke(tick);
                    if (tickDelayMs > 0) await Task.Delay(tickDelayMs, token).ConfigureAwait(false);
                }

                OnKline?.Invoke(kline);
                onKlineAction?.Invoke(kline);
                if (klineDelayMs > 0) await Task.Delay(klineDelayMs, token).ConfigureAwait(false);
            }
        }

        #endregion

        #region 生命周期控制

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            _isRunning = true;
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            _isRunning = false;
            lock (_lock)
            {
                foreach (var sub in _subscriptions)
                {
                    sub.CloseAsync().ConfigureAwait(false);
                }
                _subscriptions.Clear();
            }
            await Task.CompletedTask;
        }

        #endregion

        public void Dispose()
        {
            StopAsync().Wait();
            if (_disposeSocketClient)
            {
                _socketClient?.Dispose();
            }
        }
    }
}
