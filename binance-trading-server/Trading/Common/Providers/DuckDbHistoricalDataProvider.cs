using Binance.Net.Enums;
using Common.Cursor;
using Common.Helper;
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
    /// 基于 DuckDB 的历史回测与重放数据引擎 (实现 IMarketDataProvider 统一标准接口)
    /// 核心采用 K线循环嵌套Tick循环 + 事件推流模式 (Event-Driven Pipeline)
    /// </summary>
    public class DuckDbHistoricalDataProvider : IMarketDataProvider
    {
        private readonly DuckDbDataEngine _dataEngine;
        private readonly int _cursorBufferCapacity;
        private IRawDataCursor? _rawKlineCursor;
        private IRawDataCursor? _rawTickCursor;
        private string _currentSymbol = string.Empty;
        private string _currentInterval = string.Empty;

        private readonly List<MarketKline> _historyKlines = new List<MarketKline>();
        private readonly object _lock = new object();
        private CancellationTokenSource? _replayCts;
        private bool _isRunning = false;

        public event Action<MarketKline>? OnKline;
        public event Action<MarketTick>? OnTick;

        public bool IsRunning => _isRunning;

        public IRawDataCursor? RawKlineCursor => _rawKlineCursor;
        public IRawDataCursor? RawTickCursor => _rawTickCursor;
        public int TotalKlineCount => _rawKlineCursor?.TotalCount ?? 0;
        public int CurrentKlineIndex => _rawKlineCursor?.CurrentIndex ?? -1;
        public IReadOnlyList<MarketKline> HistoryKlines => _historyKlines;

        public DuckDbHistoricalDataProvider(int cursorBufferCapacity = 500)
        {
            _dataEngine = new DuckDbDataEngine();
            _cursorBufferCapacity = cursorBufferCapacity;
        }

        #region 🌟 初始化历史数据源 (支持流式管道队列与直接DuckDB游标)

        /// <summary>
        /// 加载并初始化指定币种、周期和时间范围的 K线与Tick 游标
        /// </summary>
        public bool LoadData(string symbol, string interval, DateTime startUtc, DateTime endUtc, bool useStreamingQueue = true)
        {
            StopAsync().Wait();

            _currentSymbol = symbol.ToUpper();
            _currentInterval = interval;
            _historyKlines.Clear();

            _rawKlineCursor?.Dispose();
            _rawTickCursor?.Dispose();

            if (useStreamingQueue)
            {
                _rawKlineCursor = _dataEngine.QueryStreamingKlineCursor(_currentSymbol, _currentInterval, startUtc, endUtc, queueCapacity: 2000);
                _rawTickCursor = _dataEngine.QueryStreamingTradeCursor(_currentSymbol, startUtc, endUtc, queueCapacity: 10000);
            }
            else
            {
                _rawKlineCursor = _dataEngine.QueryRawKlineCursor(_currentSymbol, _currentInterval, startUtc, endUtc);
                _rawTickCursor = _dataEngine.QueryRawTradeCursor(_currentSymbol, startUtc, endUtc);
            }

            return _rawKlineCursor != null;
        }

        #endregion

        #region 🌟 核心引擎：K线循环套Tick循环 + 事件推送

        /// <summary>
        /// 单步推进一根 K 线周期：
        /// 1. 推进 K 线游标获取当前周期的 [OpenTimeMs, CloseTimeMs]；
        /// 2. 内部循环检索该周期的所有真实 Tick 数据并触发 OnTick 事件 (含闭区间提前早退与同价去重)；
        /// 3. 完成全部 Tick 推送后触发 OnKline 事件模拟真实交易收盘。
        /// </summary>
        public bool StepForward()
        {
            if (_rawKlineCursor == null) return false;

            if (_rawKlineCursor.MoveNext())
            {
                long openTimeMs = _rawKlineCursor.GetInt64(0);
                long closeTimeMs = _rawKlineCursor.GetInt64(6);

                var kline = _rawKlineCursor.ReadCurrentKline(_currentSymbol, _currentInterval);
                lock (_historyKlines)
                {
                    _historyKlines.Add(kline);
                }

                // 🌟 1. 内层循环：遍历属于本周期的真实 Tick 并通过 OnTick 事件推送
                decimal? lastTickPriceInPeriod = null;
                if (_rawTickCursor != null)
                {
                    while (_rawTickCursor.MoveNext())
                    {
                        long tickTimeMs = _rawTickCursor.GetInt64(4); // 4 为 trade_time

                        // 若未到开盘时间，继续向前推
                        if (tickTimeMs < openTimeMs)
                        {
                            continue;
                        }

                        // 🌟 闭区间提前早退：超过收盘时间立即回退该 Tick 并跳出内层循环
                        if (tickTimeMs > closeTimeMs)
                        {
                            _rawTickCursor.MovePrevious();
                            break;
                        }

                        // 🌟 价格无变动去重优化：同一价格不重复推送
                        decimal tickPrice = _rawTickCursor.GetDecimal(1);
                        if (lastTickPriceInPeriod.HasValue && tickPrice == lastTickPriceInPeriod.Value)
                        {
                            continue;
                        }
                        lastTickPriceInPeriod = tickPrice;

                        var tick = _rawTickCursor.ReadCurrentTick(_currentSymbol);
                        OnTick?.Invoke(tick);
                    }
                }

                // 🌟 2. 完成本周期内全部 Tick 推送后，触发 OnKline 事件模拟收盘
                OnKline?.Invoke(kline);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 单步回退一根 K 线周期
        /// </summary>
        public bool StepBackward()
        {
            if (_rawKlineCursor == null || !_rawKlineCursor.HasPrevious) return false;

            if (_rawKlineCursor.MovePrevious())
            {
                lock (_historyKlines)
                {
                    if (_historyKlines.Count > 0)
                    {
                        _historyKlines.RemoveAt(_historyKlines.Count - 1);
                    }
                }

                var kline = _rawKlineCursor.ReadCurrentKline(_currentSymbol, _currentInterval);
                OnKline?.Invoke(kline);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 重置数据引擎游标至初始状态
        /// </summary>
        public void Reset()
        {
            StopAsync().Wait();
            _rawKlineCursor?.Reset();
            _rawTickCursor?.Reset();
            lock (_historyKlines)
            {
                _historyKlines.Clear();
            }
        }

        #endregion

        #region 🌟 真实交易连续仿真重放 (K线套Tick双层嵌套)

        /// <summary>
        /// 按照真实交易时序进行周期K线与微观Tick双层嵌套重放与推送：
        /// for 循环每根周期 K 线:
        ///   for 循环该周期的真实 Tick 数据 -> 执行 OnTick 事件推送
        ///   完成 Tick 推送后推送该周期 K 线 (OnKline)
        /// </summary>
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
            _isRunning = true;
            try
            {
                LoadData(symbol, interval, startUtc, endUtc, useStreamingQueue: true);

                while (!token.IsCancellationRequested && StepForward())
                {
                    if (klineDelayMs > 0)
                    {
                        await Task.Delay(klineDelayMs, token).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _isRunning = false;
            }
        }

        #endregion

        #region 游标拉取模式 (实现 IMarketDataProvider 接口兼容)

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

        public IRawDataCursor GetRawKlineCursor(string symbol, string interval, DateTime startUtc, DateTime endUtc)
        {
            return _dataEngine.QueryRawKlineCursor(symbol, interval, startUtc, endUtc);
        }

        public IRawDataCursor GetRawTickCursor(string symbol, DateTime startUtc, DateTime endUtc)
        {
            return _dataEngine.QueryRawTradeCursor(symbol, startUtc, endUtc);
        }

        public IRawDataCursor GetStreamingRawKlineCursor(string symbol, string interval, DateTime startUtc, DateTime endUtc, int queueCapacity = 2000)
        {
            return _dataEngine.QueryStreamingKlineCursor(symbol, interval, startUtc, endUtc, queueCapacity);
        }

        public IRawDataCursor GetStreamingRawTickCursor(string symbol, DateTime startUtc, DateTime endUtc, int queueCapacity = 10000)
        {
            return _dataEngine.QueryStreamingTradeCursor(symbol, startUtc, endUtc, queueCapacity);
        }

        #endregion

        #region 生命周期控制

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

        public void Dispose()
        {
            StopAsync().Wait();
            _rawKlineCursor?.Dispose();
            _rawTickCursor?.Dispose();
            _dataEngine?.Dispose();
            _replayCts?.Dispose();
        }

        #endregion
    }
}
