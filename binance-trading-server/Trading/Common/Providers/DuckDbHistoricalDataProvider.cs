using Binance.Net.Enums;
using Common.Cursor;
using Common.Interfaces;
using Common.Models;
using Common.Storage;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Providers
{
    /// <summary>
    /// 基于 DuckDB 的历史回测与重放数据提供者 (实现 IMarketDataProvider 统一接口)
    /// </summary>
    public class DuckDbHistoricalDataProvider : IMarketDataProvider
    {
        private readonly DuckDbDataEngine _dataEngine;
        private readonly int _cursorBufferCapacity;
        private CancellationTokenSource? _replayCts;
        private bool _isRunning = false;

        public event Action<MarketKline>? OnKline;
        public event Action<MarketTick>? OnTick;

        public bool IsRunning => _isRunning;

        public DuckDbHistoricalDataProvider(int cursorBufferCapacity = 100)
        {
            _dataEngine = new DuckDbDataEngine();
            _cursorBufferCapacity = cursorBufferCapacity;
        }

        #region 游标拉取模式 (按需切片加载并以 100 条双向缓存提供)

        public ICursor<MarketKline> GetKlineCursor(string symbol, KlineInterval interval, DateTime startUtc, DateTime endUtc)
        {
            return GetKlineCursor(symbol, interval.ToIntervalString(), startUtc, endUtc);
        }

        public ICursor<MarketKline> GetKlineCursor(string symbol, string interval, DateTime startUtc, DateTime endUtc)
        {
            var klines = _dataEngine.LoadKlines(symbol, interval, startUtc, endUtc);
            return new MarketDataCursor<MarketKline>(klines, _cursorBufferCapacity);
        }

        public ICursor<MarketTick> GetTickCursor(string symbol, DateTime startUtc, DateTime endUtc)
        {
            var trades = _dataEngine.LoadTrades(symbol, startUtc, endUtc);
            return new MarketDataCursor<MarketTick>(trades, _cursorBufferCapacity);
        }

        #endregion

        #region 事件推送与回测重放驱动

        /// <summary>
        /// 异步顺序回放 K 线数据至 OnKline 事件 (供策略事件驱动回测)
        /// </summary>
        public async Task ReplayKlinesAsync(string symbol, string interval, DateTime startUtc, DateTime endUtc, int delayMs = 0, CancellationToken token = default)
        {
            var cursor = GetKlineCursor(symbol, interval, startUtc, endUtc);
            _isRunning = true;

            try
            {
                while (cursor.MoveNext() && !token.IsCancellationRequested)
                {
                    OnKline?.Invoke(cursor.Current);
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, token).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _isRunning = false;
            }
        }

        /// <summary>
        /// 异步顺序回放 Tick/Trade 数据至 OnTick 事件
        /// </summary>
        public async Task ReplayTicksAsync(string symbol, DateTime startUtc, DateTime endUtc, int delayMs = 0, CancellationToken token = default)
        {
            var cursor = GetTickCursor(symbol, startUtc, endUtc);
            _isRunning = true;

            try
            {
                while (cursor.MoveNext() && !token.IsCancellationRequested)
                {
                    OnTick?.Invoke(cursor.Current);
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, token).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _isRunning = false;
            }
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            _replayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _isRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            _replayCts?.Cancel();
            _isRunning = false;
            return Task.CompletedTask;
        }

        #endregion

        public void Dispose()
        {
            StopAsync().Wait();
            _dataEngine?.Dispose();
        }
    }
}
