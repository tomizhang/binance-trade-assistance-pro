using Common.Helper;
using Common.Interfaces;
using Common.Models;
using System;
using System.Collections.Generic;

namespace Common.Cursor
{
    /// <summary>
    /// 高性能列式原始行情数据游标实现
    /// 支持基于列序号进行原生类型直接提取，彻底杜绝 ToString 与中间字符串解析
    /// </summary>
    public class RawMarketDataCursor : IRawDataCursor
    {
        private readonly List<MarketKline>? _klineList;
        private readonly List<MarketTick>? _tickList;
        private readonly int _totalCount;
        private int _currentIndex = -1;
        private bool _disposed = false;

        public int CurrentIndex => _currentIndex;
        public int TotalCount => _totalCount;
        public int FieldCount => _klineList != null ? 11 : (_tickList != null ? 7 : 0);
        public bool HasNext => _currentIndex + 1 < _totalCount;
        public bool HasPrevious => _currentIndex > 0;

        public RawMarketDataCursor(List<MarketKline> klines)
        {
            _klineList = klines ?? throw new ArgumentNullException(nameof(klines));
            _totalCount = _klineList.Count;
        }

        public RawMarketDataCursor(List<MarketTick> ticks)
        {
            _tickList = ticks ?? throw new ArgumentNullException(nameof(ticks));
            _totalCount = _tickList.Count;
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

        #region 列数据读取 (零装箱直接返回基元类型)

        public long GetInt64(int ordinal)
        {
            EnsureRowValid();
            if (_klineList != null)
            {
                var k = _klineList[_currentIndex];
                return ordinal switch
                {
                    0 => k.OpenTimeMs,
                    6 => k.CloseTimeMs,
                    8 => k.TradesCount,
                    _ => throw new ArgumentOutOfRangeException(nameof(ordinal))
                };
            }
            else if (_tickList != null)
            {
                var t = _tickList[_currentIndex];
                return ordinal switch
                {
                    0 => t.TradeId,
                    4 => t.TimeMs,
                    _ => throw new ArgumentOutOfRangeException(nameof(ordinal))
                };
            }
            throw new InvalidOperationException("游标未包含有效数据源。");
        }

        public int GetInt32(int ordinal)
        {
            return (int)GetInt64(ordinal);
        }

        public decimal GetDecimal(int ordinal)
        {
            EnsureRowValid();
            if (_klineList != null)
            {
                var k = _klineList[_currentIndex];
                return ordinal switch
                {
                    1 => k.Open,
                    2 => k.High,
                    3 => k.Low,
                    4 => k.Close,
                    5 => k.Volume,
                    7 => k.QuoteVolume,
                    9 => k.TakerBuyBaseVolume,
                    10 => k.TakerBuyQuoteVolume,
                    _ => throw new ArgumentOutOfRangeException(nameof(ordinal))
                };
            }
            else if (_tickList != null)
            {
                var t = _tickList[_currentIndex];
                return ordinal switch
                {
                    1 => t.Price,
                    2 => t.Quantity,
                    3 => t.QuoteQuantity,
                    _ => throw new ArgumentOutOfRangeException(nameof(ordinal))
                };
            }
            throw new InvalidOperationException("游标未包含有效数据源。");
        }

        public double GetDouble(int ordinal)
        {
            return (double)GetDecimal(ordinal);
        }

        public bool GetBoolean(int ordinal)
        {
            EnsureRowValid();
            if (_klineList != null)
            {
                var k = _klineList[_currentIndex];
                return ordinal switch
                {
                    11 => k.IsClosed,
                    _ => throw new ArgumentOutOfRangeException(nameof(ordinal))
                };
            }
            else if (_tickList != null)
            {
                var t = _tickList[_currentIndex];
                return ordinal switch
                {
                    5 => t.IsBuyerMaker,
                    6 => t.IsBestMatch,
                    _ => throw new ArgumentOutOfRangeException(nameof(ordinal))
                };
            }
            throw new InvalidOperationException("游标未包含有效数据源。");
        }

        public DateTime GetDateTime(int ordinal)
        {
            EnsureRowValid();
            if (_klineList != null)
            {
                var k = _klineList[_currentIndex];
                return ordinal switch
                {
                    0 => k.OpenTime,
                    6 => k.CloseTime,
                    _ => throw new ArgumentOutOfRangeException(nameof(ordinal))
                };
            }
            else if (_tickList != null)
            {
                var t = _tickList[_currentIndex];
                return ordinal switch
                {
                    4 => t.Time,
                    _ => throw new ArgumentOutOfRangeException(nameof(ordinal))
                };
            }
            throw new InvalidOperationException("游标未包含有效数据源。");
        }

        public string GetString(int ordinal)
        {
            EnsureRowValid();
            if (_klineList != null)
            {
                var k = _klineList[_currentIndex];
                return ordinal switch
                {
                    12 => k.Symbol,
                    13 => k.Interval,
                    _ => string.Empty
                };
            }
            else if (_tickList != null)
            {
                var t = _tickList[_currentIndex];
                return ordinal switch
                {
                    7 => t.Symbol,
                    _ => string.Empty
                };
            }
            return string.Empty;
        }

        public bool IsDBNull(int ordinal)
        {
            return false;
        }

        #endregion

        #region 实体快速装配方法

        public MarketKline ReadCurrentKline(string symbol, string interval)
        {
            EnsureRowValid();
            if (_klineList != null)
            {
                return _klineList[_currentIndex];
            }

            throw new InvalidOperationException("当前游标非 K 线数据源。");
        }

        public MarketTick ReadCurrentTick(string symbol)
        {
            EnsureRowValid();
            if (_tickList != null)
            {
                return _tickList[_currentIndex];
            }

            throw new InvalidOperationException("当前游标非 Tick 数据源。");
        }

        #endregion

        private void EnsureRowValid()
        {
            if (_currentIndex < 0 || _currentIndex >= _totalCount)
            {
                throw new InvalidOperationException($"游标当前位置无效: {_currentIndex} (有效范围: 0 ~ {_totalCount - 1})，请先调用 MoveNext()。");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }
}
