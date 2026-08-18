using Common.Helper;
using Common.Interfaces;
using Common.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Common.Cursor
{
    /// <summary>
    /// 紧凑无装箱 K 线行原生值结构体 (0 字节托管堆分配)
    /// </summary>
    public readonly struct RawKlineRow
    {
        public readonly long OpenTimeMs;
        public readonly decimal Open;
        public readonly decimal High;
        public readonly decimal Low;
        public readonly decimal Close;
        public readonly decimal Volume;
        public readonly long CloseTimeMs;
        public readonly decimal QuoteVolume;
        public readonly long TradesCount;
        public readonly decimal TakerBuyBaseVolume;
        public readonly decimal TakerBuyQuoteVolume;

        public RawKlineRow(
            long openTimeMs, decimal open, decimal high, decimal low, decimal close, decimal volume,
            long closeTimeMs, decimal quoteVolume, long tradesCount, decimal takerBuyBaseVolume, decimal takerBuyQuoteVolume)
        {
            OpenTimeMs = openTimeMs;
            Open = open;
            High = high;
            Low = low;
            Close = close;
            Volume = volume;
            CloseTimeMs = closeTimeMs;
            QuoteVolume = quoteVolume;
            TradesCount = tradesCount;
            TakerBuyBaseVolume = takerBuyBaseVolume;
            TakerBuyQuoteVolume = takerBuyQuoteVolume;
        }
    }

    /// <summary>
    /// 紧凑无装箱 Tick/Trade 行原生值结构体 (0 字节托管堆分配)
    /// </summary>
    public readonly struct RawTradeRow
    {
        public readonly long TradeId;
        public readonly decimal Price;
        public readonly decimal Quantity;
        public readonly decimal QuoteQuantity;
        public readonly long TimeMs;
        public readonly bool IsBuyerMaker;
        public readonly bool IsBestMatch;

        public RawTradeRow(
            long tradeId, decimal price, decimal quantity, decimal quoteQuantity,
            long timeMs, bool isBuyerMaker, bool isBestMatch)
        {
            TradeId = tradeId;
            Price = price;
            Quantity = quantity;
            QuoteQuantity = quoteQuantity;
            TimeMs = timeMs;
            IsBuyerMaker = isBuyerMaker;
            IsBestMatch = isBestMatch;
        }
    }

    /// <summary>
    /// 🌟 基于高性能有界管道队列 (Channel) 的流式数据游标
    /// 解决一次性全量加载大数据造成的界面卡顿与内存爆满问题
    /// 支持后台逐文件生产预取、背压控制、双向后退前进与零托管堆开销
    /// </summary>
    public class StreamingQueueRawDataCursor : IRawDataCursor
    {
        private readonly bool _isKline;
        private readonly ChannelReader<RawKlineRow>? _klineReader;
        private readonly ChannelReader<RawTradeRow>? _tradeReader;
        private readonly CancellationTokenSource _cts;
        private readonly Task? _producerTask;

        // 历史原生结构体缓存 (连续内存、零装箱对象头，安全支持双向 MovePrevious / Seek)
        private readonly List<RawKlineRow>? _klineHistory;
        private readonly List<RawTradeRow>? _tradeHistory;

        private int _currentIndex = -1;
        private int _totalEstimatedCount = 0;
        private bool _isCompleted = false;
        private bool _disposed = false;

        private RawKlineRow _currentKline;
        private RawTradeRow _currentTrade;

        public int CurrentIndex => _currentIndex;
        public int TotalCount => _totalEstimatedCount;
        public int FieldCount => _isKline ? 11 : 7;
        public bool HasNext => !_isCompleted || (_isKline ? _currentIndex + 1 < _klineHistory!.Count : _currentIndex + 1 < _tradeHistory!.Count);
        public bool HasPrevious => _currentIndex > 0;

        #region 工厂构造方法

        /// <summary>
        /// 创建流式 K 线队列游标
        /// </summary>
        public static StreamingQueueRawDataCursor CreateKlineStreamingCursor(
            Func<ChannelWriter<RawKlineRow>, CancellationToken, Task> producerFunc,
            int queueCapacity = 2000,
            int totalEstimatedCount = 0)
        {
            var options = new BoundedChannelOptions(queueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = true
            };
            var channel = Channel.CreateBounded<RawKlineRow>(options);
            var cts = new CancellationTokenSource();

            var task = Task.Run(async () =>
            {
                try
                {
                    await producerFunc(channel.Writer, cts.Token).ConfigureAwait(false);
                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                }
            }, cts.Token);

            return new StreamingQueueRawDataCursor(channel.Reader, cts, task, totalEstimatedCount);
        }

        /// <summary>
        /// 创建流式 Tick/Trade 队列游标
        /// </summary>
        public static StreamingQueueRawDataCursor CreateTradeStreamingCursor(
            Func<ChannelWriter<RawTradeRow>, CancellationToken, Task> producerFunc,
            int queueCapacity = 10000,
            int totalEstimatedCount = 0)
        {
            var options = new BoundedChannelOptions(queueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = true
            };
            var channel = Channel.CreateBounded<RawTradeRow>(options);
            var cts = new CancellationTokenSource();

            var task = Task.Run(async () =>
            {
                try
                {
                    await producerFunc(channel.Writer, cts.Token).ConfigureAwait(false);
                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                }
            }, cts.Token);

            return new StreamingQueueRawDataCursor(channel.Reader, cts, task, totalEstimatedCount);
        }

        #endregion

        private StreamingQueueRawDataCursor(
            ChannelReader<RawKlineRow> reader,
            CancellationTokenSource cts,
            Task producerTask,
            int totalEstimatedCount)
        {
            _isKline = true;
            _klineReader = reader;
            _cts = cts;
            _producerTask = producerTask;
            _klineHistory = new List<RawKlineRow>(2048);
            _totalEstimatedCount = totalEstimatedCount;
        }

        private StreamingQueueRawDataCursor(
            ChannelReader<RawTradeRow> reader,
            CancellationTokenSource cts,
            Task producerTask,
            int totalEstimatedCount)
        {
            _isKline = false;
            _tradeReader = reader;
            _cts = cts;
            _producerTask = producerTask;
            _tradeHistory = new List<RawTradeRow>(16384);
            _totalEstimatedCount = totalEstimatedCount;
        }

        #region 游标导航

        public bool MoveNext()
        {
            if (_disposed) return false;

            int nextIndex = _currentIndex + 1;

            if (_isKline)
            {
                // 1. 若已经在历史缓存中存在该索引，直接从缓存读取 (支持后退后的再次前进)
                if (nextIndex < _klineHistory!.Count)
                {
                    _currentIndex = nextIndex;
                    _currentKline = _klineHistory[_currentIndex];
                    return true;
                }

                // 2. 从流式管道中出队读取下一条数据
                try
                {
                    if (_klineReader!.WaitToReadAsync(_cts.Token).AsTask().GetAwaiter().GetResult())
                    {
                        if (_klineReader.TryRead(out var row))
                        {
                            _klineHistory.Add(row);
                            _currentIndex = _klineHistory.Count - 1;
                            _currentKline = row;

                            if (_currentIndex + 1 > _totalEstimatedCount)
                            {
                                _totalEstimatedCount = _currentIndex + 1;
                            }
                            return true;
                        }
                    }
                }
                catch (OperationCanceledException) { }

                _isCompleted = true;
                return false;
            }
            else
            {
                // 1. 若已经在历史缓存中存在该索引，直接从缓存读取
                if (nextIndex < _tradeHistory!.Count)
                {
                    _currentIndex = nextIndex;
                    _currentTrade = _tradeHistory[_currentIndex];
                    return true;
                }

                // 2. 从流式管道中出队读取下一条数据
                try
                {
                    if (_tradeReader!.WaitToReadAsync(_cts.Token).AsTask().GetAwaiter().GetResult())
                    {
                        if (_tradeReader.TryRead(out var row))
                        {
                            _tradeHistory.Add(row);
                            _currentIndex = _tradeHistory.Count - 1;
                            _currentTrade = row;

                            if (_currentIndex + 1 > _totalEstimatedCount)
                            {
                                _totalEstimatedCount = _currentIndex + 1;
                            }
                            return true;
                        }
                    }
                }
                catch (OperationCanceledException) { }

                _isCompleted = true;
                return false;
            }
        }

        public bool MovePrevious()
        {
            if (_currentIndex > 0)
            {
                _currentIndex--;
                if (_isKline)
                {
                    if (_currentIndex < _klineHistory!.Count)
                    {
                        _currentKline = _klineHistory[_currentIndex];
                    }
                }
                else
                {
                    if (_currentIndex < _tradeHistory!.Count)
                    {
                        _currentTrade = _tradeHistory[_currentIndex];
                    }
                }
                return true;
            }
            else if (_currentIndex == 0)
            {
                _currentIndex = -1;
                return true;
            }
            return false;
        }

        public void Reset()
        {
            _currentIndex = -1;
        }

        public bool Seek(int index)
        {
            if (_isKline)
            {
                if (index >= 0 && index < _klineHistory!.Count)
                {
                    _currentIndex = index;
                    _currentKline = _klineHistory[_currentIndex];
                    return true;
                }
            }
            else
            {
                if (index >= 0 && index < _tradeHistory!.Count)
                {
                    _currentIndex = index;
                    _currentTrade = _tradeHistory[_currentIndex];
                    return true;
                }
            }
            return false;
        }

        #endregion

        #region 列数据读取 (零装箱与O(1)连续结构体访问)

        public long GetInt64(int ordinal)
        {
            EnsureRowValid();
            if (_isKline)
            {
                return ordinal switch
                {
                    0 => _currentKline.OpenTimeMs,
                    6 => _currentKline.CloseTimeMs,
                    8 => _currentKline.TradesCount,
                    _ => throw new ArgumentOutOfRangeException(nameof(ordinal))
                };
            }
            else
            {
                return ordinal switch
                {
                    0 => _currentTrade.TradeId,
                    4 => _currentTrade.TimeMs,
                    _ => throw new ArgumentOutOfRangeException(nameof(ordinal))
                };
            }
        }

        public int GetInt32(int ordinal) => (int)GetInt64(ordinal);

        public decimal GetDecimal(int ordinal)
        {
            EnsureRowValid();
            if (_isKline)
            {
                return ordinal switch
                {
                    1 => _currentKline.Open,
                    2 => _currentKline.High,
                    3 => _currentKline.Low,
                    4 => _currentKline.Close,
                    5 => _currentKline.Volume,
                    7 => _currentKline.QuoteVolume,
                    9 => _currentKline.TakerBuyBaseVolume,
                    10 => _currentKline.TakerBuyQuoteVolume,
                    _ => throw new ArgumentOutOfRangeException(nameof(ordinal))
                };
            }
            else
            {
                return ordinal switch
                {
                    1 => _currentTrade.Price,
                    2 => _currentTrade.Quantity,
                    3 => _currentTrade.QuoteQuantity,
                    _ => throw new ArgumentOutOfRangeException(nameof(ordinal))
                };
            }
        }

        public double GetDouble(int ordinal) => (double)GetDecimal(ordinal);

        public bool GetBoolean(int ordinal)
        {
            EnsureRowValid();
            if (_isKline) return true;
            return ordinal switch
            {
                5 => _currentTrade.IsBuyerMaker,
                6 => _currentTrade.IsBestMatch,
                _ => throw new ArgumentOutOfRangeException(nameof(ordinal))
            };
        }

        public DateTime GetDateTime(int ordinal)
        {
            long ms = GetInt64(ordinal);
            return TimeHelper.FromUnixTimeMilliseconds(ms);
        }

        public string GetString(int ordinal) => GetDecimal(ordinal).ToString();

        public bool IsDBNull(int ordinal) => false;

        #endregion

        #region 实体快速装配

        public MarketKline ReadCurrentKline(string symbol, string interval)
        {
            EnsureRowValid();
            return new MarketKline
            {
                Symbol = symbol.ToUpper(),
                Interval = interval,
                OpenTime = TimeHelper.FromUnixTimeMilliseconds(_currentKline.OpenTimeMs),
                CloseTime = TimeHelper.FromUnixTimeMilliseconds(_currentKline.CloseTimeMs),
                Open = _currentKline.Open,
                High = _currentKline.High,
                Low = _currentKline.Low,
                Close = _currentKline.Close,
                Volume = _currentKline.Volume,
                QuoteVolume = _currentKline.QuoteVolume,
                TradesCount = _currentKline.TradesCount,
                TakerBuyBaseVolume = _currentKline.TakerBuyBaseVolume,
                TakerBuyQuoteVolume = _currentKline.TakerBuyQuoteVolume,
                IsClosed = true
            };
        }

        public MarketTick ReadCurrentTick(string symbol)
        {
            EnsureRowValid();
            return new MarketTick
            {
                Symbol = symbol.ToUpper(),
                TradeId = _currentTrade.TradeId,
                Price = _currentTrade.Price,
                Quantity = _currentTrade.Quantity,
                QuoteQuantity = _currentTrade.QuoteQuantity,
                Time = TimeHelper.FromUnixTimeMilliseconds(_currentTrade.TimeMs),
                IsBuyerMaker = _currentTrade.IsBuyerMaker,
                IsBestMatch = _currentTrade.IsBestMatch
            };
        }

        #endregion

        private void EnsureRowValid()
        {
            if (_currentIndex < 0)
            {
                throw new InvalidOperationException($"游标未定位在有效数据行 (CurrentIndex: {_currentIndex})。请先调用 MoveNext()。");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _cts.Cancel();
                _cts.Dispose();
            }
        }
    }
}
