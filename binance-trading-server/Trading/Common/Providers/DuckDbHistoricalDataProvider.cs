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
    /// 基于 DuckDB 的历史回测与重放数据提供者 (实现 IMarketDataProvider 统一接口)
    /// 支持列式高性能游标与 K线/Tick 双层嵌套交易真实模拟
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

        public DuckDbHistoricalDataProvider(int cursorBufferCapacity = 500)
        {
            _dataEngine = new DuckDbDataEngine();
            _cursorBufferCapacity = cursorBufferCapacity;
        }

        #region 游标拉取模式 (按需切片加载并以双向缓存提供)

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

        #region 🌟 高性能列式游标 (直接返回 DuckDB 原生列式游标，无时间过滤)

        public IRawDataCursor GetRawKlineCursor(string symbol, string interval, DateTime startUtc, DateTime endUtc)
        {
            return _dataEngine.QueryRawKlineCursor(symbol, interval, startUtc, endUtc);
        }

        public IRawDataCursor GetRawTickCursor(string symbol, DateTime startUtc, DateTime endUtc)
        {
            return _dataEngine.QueryRawTradeCursor(symbol, startUtc, endUtc);
        }

        #endregion

        #region 🌟 队列管道流式数据游标 (避免一次性加载大内存与界面卡顿)

        public IRawDataCursor GetStreamingRawKlineCursor(string symbol, string interval, DateTime startUtc, DateTime endUtc, int queueCapacity = 2000)
        {
            return _dataEngine.QueryStreamingKlineCursor(symbol, interval, startUtc, endUtc, queueCapacity);
        }

        public IRawDataCursor GetStreamingRawTickCursor(string symbol, DateTime startUtc, DateTime endUtc, int queueCapacity = 10000)
        {
            return _dataEngine.QueryStreamingTradeCursor(symbol, startUtc, endUtc, queueCapacity);
        }

        #endregion

        #region 🌟 真实交易仿真：周期 K 线与 Tick 双层嵌套重放推流

        /// <summary>
        /// 按照真实交易时序进行周期K线与微观Tick双层嵌套重放与推送：
        /// 遍历每根周期K线 (如30分钟)：
        ///   for 循环该周期的 tick 数据 -> 执行推送 tick (OnTick)
        ///   完成 tick 推送后推送周期 K 线 (OnKline) 以模拟真实交易收盘
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
                using var klineCursor = GetRawKlineCursor(symbol, interval, startUtc, endUtc);
                using var tickCursor = GetRawTickCursor(symbol, startUtc, endUtc);

                // 🌟 1. 遍历每根周期 K 线 (如 30 分钟)
                while (klineCursor.MoveNext() && !token.IsCancellationRequested)
                {
                    var kline = klineCursor.ReadCurrentKline(symbol, interval);
                    long klineOpenMs = kline.OpenTimeMs;
                    long klineCloseMs = kline.CloseTimeMs;

                    bool foundAnyTick = false;
                    decimal? lastTickPriceInPeriod = null;

                    // 🌟 2. for 循环该周期的 tick 数据并执行推送
                    while (tickCursor.MoveNext() && !token.IsCancellationRequested)
                    {
                        long tickTimeMs = tickCursor.GetInt64(4); // 4 为 trade_time

                        if (tickTimeMs < klineOpenMs)
                        {
                            continue;
                        }

                        // 🌟 超过该 K 线的时间闭区间直接中断跳出 (提前早退，不浪费无谓循环)
                        if (tickTimeMs > klineCloseMs)
                        {
                            tickCursor.MovePrevious();
                            break;
                        }

                        // 🌟 价格无变动去重优化：如果新 Tick 价格与上一 Tick 价格完全一致，则直接跳过推送
                        decimal tickPrice = tickCursor.GetDecimal(1);
                        if (lastTickPriceInPeriod.HasValue && tickPrice == lastTickPriceInPeriod.Value)
                        {
                            continue;
                        }
                        lastTickPriceInPeriod = tickPrice;

                        foundAnyTick = true;
                        var tick = tickCursor.ReadCurrentTick(symbol);

                        // 执行推送 tick
                        OnTick?.Invoke(tick);
                        onTickAction?.Invoke(tick);

                        if (tickDelayMs > 0)
                        {
                            await Task.Delay(tickDelayMs, token).ConfigureAwait(false);
                        }
                    }

                    // 若本周期内无本地物理 Tick 记录，按形态 (Open->High->Low->Close) 仿真生成微观 Tick 序列
                    if (!foundAnyTick)
                    {
                        var subTicks = GenerateSubTicks(kline);
                        foreach (var subTick in subTicks)
                        {
                            if (token.IsCancellationRequested) break;

                            OnTick?.Invoke(subTick);
                            onTickAction?.Invoke(subTick);

                            if (tickDelayMs > 0)
                            {
                                await Task.Delay(tickDelayMs, token).ConfigureAwait(false);
                            }
                        }
                    }

                    // 🌟 3. 完成 tick 推送后推送周期 K 线以模拟真实交易收盘
                    OnKline?.Invoke(kline);
                    onKlineAction?.Invoke(kline);

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

        private static List<MarketTick> GenerateSubTicks(MarketKline kline)
        {
            var ticks = new List<MarketTick>(5);
            long stepMs = Math.Max(1000, (kline.CloseTimeMs - kline.OpenTimeMs) / 5);

            // Tick 1: 开盘价
            ticks.Add(new MarketTick
            {
                Symbol = kline.Symbol,
                Time = kline.OpenTime,
                Price = kline.Open,
                Quantity = kline.Volume / 5m,
                IsBuyerMaker = false
            });

            // Tick 2: 最高价
            ticks.Add(new MarketTick
            {
                Symbol = kline.Symbol,
                Time = kline.OpenTime.AddMilliseconds(stepMs),
                Price = kline.High,
                Quantity = kline.Volume / 5m,
                IsBuyerMaker = false
            });

            // Tick 3: 最低价
            ticks.Add(new MarketTick
            {
                Symbol = kline.Symbol,
                Time = kline.OpenTime.AddMilliseconds(stepMs * 2),
                Price = kline.Low,
                Quantity = kline.Volume / 5m,
                IsBuyerMaker = true
            });

            // Tick 4: 收盘前价格
            decimal midPrice = (kline.High + kline.Low) / 2m;
            ticks.Add(new MarketTick
            {
                Symbol = kline.Symbol,
                Time = kline.OpenTime.AddMilliseconds(stepMs * 3),
                Price = midPrice,
                Quantity = kline.Volume / 5m,
                IsBuyerMaker = false
            });

            // Tick 5: 收盘价
            ticks.Add(new MarketTick
            {
                Symbol = kline.Symbol,
                Time = kline.CloseTime,
                Price = kline.Close,
                Quantity = kline.Volume / 5m,
                IsBuyerMaker = true
            });

            return ticks;
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
            _dataEngine?.Dispose();
            _replayCts?.Dispose();
        }

        #endregion
    }
}
