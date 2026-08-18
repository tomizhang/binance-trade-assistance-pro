using Common.Helper;
using Common.Interfaces;
using Common.Models;
using DuckDB.NET.Data;
using System;
using System.Data;

namespace Common.Cursor
{
    /// <summary>
    /// 基于 DuckDB 的高性能原生列式数据游标
    /// 直接从 DuckDB 查询结果构建列式原生内存存储，不进行时间过滤，支持零装箱/零ToString/双向O(1)寻址
    /// </summary>
    public class DuckDbRawDataCursor : IRawDataCursor
    {
        private readonly bool _isKline;
        private readonly int _totalCount;
        private int _currentIndex = -1;
        private bool _disposed = false;

        // 🌟 K 线列式连续原生内存数组 (Zero Boxing, Zero Heavy Object Allocation)
        private readonly long[]? _klineOpenTimeMs;
        private readonly decimal[]? _klineOpen;
        private readonly decimal[]? _klineHigh;
        private readonly decimal[]? _klineLow;
        private readonly decimal[]? _klineClose;
        private readonly decimal[]? _klineVolume;
        private readonly long[]? _klineCloseTimeMs;
        private readonly decimal[]? _klineQuoteVolume;
        private readonly long[]? _klineTradesCount;
        private readonly decimal[]? _klineTakerBaseVol;
        private readonly decimal[]? _klineTakerQuoteVol;

        // 🌟 Tick / Trade 列式连续原生内存数组
        private readonly long[]? _tickTradeId;
        private readonly decimal[]? _tickPrice;
        private readonly decimal[]? _tickQty;
        private readonly decimal[]? _tickQuoteQty;
        private readonly long[]? _tickTimeMs;
        private readonly bool[]? _tickIsBuyerMaker;
        private readonly bool[]? _tickIsBestMatch;

        public int CurrentIndex => _currentIndex;
        public int TotalCount => _totalCount;
        public int FieldCount => _isKline ? 11 : 7;
        public bool HasNext => _currentIndex + 1 < _totalCount;
        public bool HasPrevious => _currentIndex > 0;

        /// <summary>
        /// 从 DuckDBDataReader 直接读取并构建列式 K 线游标 (无时间过滤)
        /// </summary>
        public static DuckDbRawDataCursor CreateFromKlineReader(DuckDBDataReader reader)
        {
            // 预估或动态扩容容量
            int capacity = 1024;
            int count = 0;

            long[] openTimeMs = new long[capacity];
            decimal[] open = new decimal[capacity];
            decimal[] high = new decimal[capacity];
            decimal[] low = new decimal[capacity];
            decimal[] close = new decimal[capacity];
            decimal[] volume = new decimal[capacity];
            long[] closeTimeMs = new long[capacity];
            decimal[] quoteVol = new decimal[capacity];
            long[] tradesCount = new long[capacity];
            decimal[] takerBase = new decimal[capacity];
            decimal[] takerQuote = new decimal[capacity];

            while (reader.Read())
            {
                if (count >= capacity)
                {
                    capacity *= 2;
                    Array.Resize(ref openTimeMs, capacity);
                    Array.Resize(ref open, capacity);
                    Array.Resize(ref high, capacity);
                    Array.Resize(ref low, capacity);
                    Array.Resize(ref close, capacity);
                    Array.Resize(ref volume, capacity);
                    Array.Resize(ref closeTimeMs, capacity);
                    Array.Resize(ref quoteVol, capacity);
                    Array.Resize(ref tradesCount, capacity);
                    Array.Resize(ref takerBase, capacity);
                    Array.Resize(ref takerQuote, capacity);
                }

                openTimeMs[count] = Convert.ToInt64(reader[0]);
                open[count] = Convert.ToDecimal(reader[1]);
                high[count] = Convert.ToDecimal(reader[2]);
                low[count] = Convert.ToDecimal(reader[3]);
                close[count] = Convert.ToDecimal(reader[4]);
                volume[count] = Convert.ToDecimal(reader[5]);
                closeTimeMs[count] = Convert.ToInt64(reader[6]);
                quoteVol[count] = reader.FieldCount > 7 ? Convert.ToDecimal(reader[7]) : 0m;
                tradesCount[count] = reader.FieldCount > 8 ? Convert.ToInt64(reader[8]) : 0L;
                takerBase[count] = reader.FieldCount > 9 ? Convert.ToDecimal(reader[9]) : 0m;
                takerQuote[count] = reader.FieldCount > 10 ? Convert.ToDecimal(reader[10]) : 0m;

                count++;
            }

            // 紧凑缩减至实际大小
            if (count < capacity)
            {
                Array.Resize(ref openTimeMs, count);
                Array.Resize(ref open, count);
                Array.Resize(ref high, count);
                Array.Resize(ref low, count);
                Array.Resize(ref close, count);
                Array.Resize(ref volume, count);
                Array.Resize(ref closeTimeMs, count);
                Array.Resize(ref quoteVol, count);
                Array.Resize(ref tradesCount, count);
                Array.Resize(ref takerBase, count);
                Array.Resize(ref takerQuote, count);
            }

            return new DuckDbRawDataCursor(
                count,
                openTimeMs, open, high, low, close, volume, closeTimeMs, quoteVol, tradesCount, takerBase, takerQuote);
        }

        /// <summary>
        /// 从 DuckDBDataReader 直接读取并构建列式 Trade 游标 (无时间过滤)
        /// </summary>
        public static DuckDbRawDataCursor CreateFromTradeReader(DuckDBDataReader reader)
        {
            int capacity = 4096;
            int count = 0;

            long[] tradeId = new long[capacity];
            decimal[] price = new decimal[capacity];
            decimal[] qty = new decimal[capacity];
            decimal[] quoteQty = new decimal[capacity];
            long[] timeMs = new long[capacity];
            bool[] isBuyerMaker = new bool[capacity];
            bool[] isBestMatch = new bool[capacity];

            while (reader.Read())
            {
                if (count >= capacity)
                {
                    capacity *= 2;
                    Array.Resize(ref tradeId, capacity);
                    Array.Resize(ref price, capacity);
                    Array.Resize(ref qty, capacity);
                    Array.Resize(ref quoteQty, capacity);
                    Array.Resize(ref timeMs, capacity);
                    Array.Resize(ref isBuyerMaker, capacity);
                    Array.Resize(ref isBestMatch, capacity);
                }

                tradeId[count] = Convert.ToInt64(reader[0]);
                price[count] = Convert.ToDecimal(reader[1]);
                qty[count] = Convert.ToDecimal(reader[2]);
                quoteQty[count] = Convert.ToDecimal(reader[3]);
                timeMs[count] = Convert.ToInt64(reader[4]);
                isBuyerMaker[count] = Convert.ToBoolean(reader[5]);
                isBestMatch[count] = reader.FieldCount > 6 ? Convert.ToBoolean(reader[6]) : true;

                count++;
            }

            if (count < capacity)
            {
                Array.Resize(ref tradeId, count);
                Array.Resize(ref price, count);
                Array.Resize(ref qty, count);
                Array.Resize(ref quoteQty, count);
                Array.Resize(ref timeMs, count);
                Array.Resize(ref isBuyerMaker, count);
                Array.Resize(ref isBestMatch, count);
            }

            return new DuckDbRawDataCursor(
                count,
                tradeId, price, qty, quoteQty, timeMs, isBuyerMaker, isBestMatch);
        }

        private DuckDbRawDataCursor(
            int count,
            long[] openTimeMs, decimal[] open, decimal[] high, decimal[] low, decimal[] close,
            decimal[] volume, long[] closeTimeMs, decimal[] quoteVol, long[] tradesCount,
            decimal[] takerBase, decimal[] takerQuote)
        {
            _isKline = true;
            _totalCount = count;
            _klineOpenTimeMs = openTimeMs;
            _klineOpen = open;
            _klineHigh = high;
            _klineLow = low;
            _klineClose = close;
            _klineVolume = volume;
            _klineCloseTimeMs = closeTimeMs;
            _klineQuoteVolume = quoteVol;
            _klineTradesCount = tradesCount;
            _klineTakerBaseVol = takerBase;
            _klineTakerQuoteVol = takerQuote;
        }

        private DuckDbRawDataCursor(
            int count,
            long[] tradeId, decimal[] price, decimal[] qty, decimal[] quoteQty,
            long[] timeMs, bool[] isBuyerMaker, bool[] isBestMatch)
        {
            _isKline = false;
            _totalCount = count;
            _tickTradeId = tradeId;
            _tickPrice = price;
            _tickQty = qty;
            _tickQuoteQty = quoteQty;
            _tickTimeMs = timeMs;
            _tickIsBuyerMaker = isBuyerMaker;
            _tickIsBestMatch = isBestMatch;
        }

        public bool MoveNext()
        {
            if (HasNext)
            {
                _currentIndex++;
                return true;
            }
            return false;
        }

        public bool MovePrevious()
        {
            if (HasPrevious)
            {
                _currentIndex--;
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
            if (index >= 0 && index < _totalCount)
            {
                _currentIndex = index;
                return true;
            }
            return false;
        }

        #region 列数据读取 (零装箱与O(1)原生数组访问)

        public long GetInt64(int ordinal)
        {
            EnsureRowValid();
            if (_isKline)
            {
                return ordinal switch
                {
                    0 => _klineOpenTimeMs![_currentIndex],
                    6 => _klineCloseTimeMs![_currentIndex],
                    8 => _klineTradesCount![_currentIndex],
                    _ => throw new ArgumentOutOfRangeException(nameof(ordinal), $"K线列序号 {ordinal} 不是 Int64 类型")
                };
            }
            else
            {
                return ordinal switch
                {
                    0 => _tickTradeId![_currentIndex],
                    4 => _tickTimeMs![_currentIndex],
                    _ => throw new ArgumentOutOfRangeException(nameof(ordinal), $"Trade列序号 {ordinal} 不是 Int64 类型")
                };
            }
        }

        public int GetInt32(int ordinal)
        {
            return (int)GetInt64(ordinal);
        }

        public decimal GetDecimal(int ordinal)
        {
            EnsureRowValid();
            if (_isKline)
            {
                return ordinal switch
                {
                    1 => _klineOpen![_currentIndex],
                    2 => _klineHigh![_currentIndex],
                    3 => _klineLow![_currentIndex],
                    4 => _klineClose![_currentIndex],
                    5 => _klineVolume![_currentIndex],
                    7 => _klineQuoteVolume![_currentIndex],
                    9 => _klineTakerBaseVol![_currentIndex],
                    10 => _klineTakerQuoteVol![_currentIndex],
                    _ => throw new ArgumentOutOfRangeException(nameof(ordinal), $"K线列序号 {ordinal} 不是 Decimal 类型")
                };
            }
            else
            {
                return ordinal switch
                {
                    1 => _tickPrice![_currentIndex],
                    2 => _tickQty![_currentIndex],
                    3 => _tickQuoteQty![_currentIndex],
                    _ => throw new ArgumentOutOfRangeException(nameof(ordinal), $"Trade列序号 {ordinal} 不是 Decimal 类型")
                };
            }
        }

        public double GetDouble(int ordinal)
        {
            return (double)GetDecimal(ordinal);
        }

        public bool GetBoolean(int ordinal)
        {
            EnsureRowValid();
            if (_isKline)
            {
                return true;
            }
            else
            {
                return ordinal switch
                {
                    5 => _tickIsBuyerMaker![_currentIndex],
                    6 => _tickIsBestMatch![_currentIndex],
                    _ => throw new ArgumentOutOfRangeException(nameof(ordinal), $"Trade列序号 {ordinal} 不是 Boolean 类型")
                };
            }
        }

        public DateTime GetDateTime(int ordinal)
        {
            long ms = GetInt64(ordinal);
            return TimeHelper.FromUnixTimeMilliseconds(ms);
        }

        public string GetString(int ordinal)
        {
            EnsureRowValid();
            return GetDecimal(ordinal).ToString();
        }

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
                OpenTime = TimeHelper.FromUnixTimeMilliseconds(_klineOpenTimeMs![_currentIndex]),
                CloseTime = TimeHelper.FromUnixTimeMilliseconds(_klineCloseTimeMs![_currentIndex]),
                Open = _klineOpen![_currentIndex],
                High = _klineHigh![_currentIndex],
                Low = _klineLow![_currentIndex],
                Close = _klineClose![_currentIndex],
                Volume = _klineVolume![_currentIndex],
                QuoteVolume = _klineQuoteVolume![_currentIndex],
                TradesCount = _klineTradesCount![_currentIndex],
                TakerBuyBaseVolume = _klineTakerBaseVol![_currentIndex],
                TakerBuyQuoteVolume = _klineTakerQuoteVol![_currentIndex],
                IsClosed = true
            };
        }

        public MarketTick ReadCurrentTick(string symbol)
        {
            EnsureRowValid();
            return new MarketTick
            {
                Symbol = symbol.ToUpper(),
                TradeId = _tickTradeId![_currentIndex],
                Price = _tickPrice![_currentIndex],
                Quantity = _tickQty![_currentIndex],
                QuoteQuantity = _tickQuoteQty![_currentIndex],
                Time = TimeHelper.FromUnixTimeMilliseconds(_tickTimeMs![_currentIndex]),
                IsBuyerMaker = _tickIsBuyerMaker![_currentIndex],
                IsBestMatch = _tickIsBestMatch![_currentIndex]
            };
        }

        #endregion

        private void EnsureRowValid()
        {
            if (_currentIndex < 0 || _currentIndex >= _totalCount)
            {
                throw new InvalidOperationException($"游标当前位置无效 (CurrentIndex: {_currentIndex}, Total: {_totalCount})。请先调用 MoveNext()。");
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
