using Common.Interfaces;
using System;
using System.Collections;
using System.Collections.Generic;

namespace Common.Cursor
{
    /// <summary>
    /// 高性能双向市场数据游标实现 (支持 100 条滑动历史缓存)
    /// </summary>
    /// <typeparam name="T">行情数据类型</typeparam>
    public class MarketDataCursor<T> : ICursor<T>, IEnumerable<T>
    {
        private readonly IReadOnlyList<T> _sourceData;
        private readonly int _bufferCapacity;
        private int _currentIndex = -1;

        public MarketDataCursor(IReadOnlyList<T> sourceData, int bufferCapacity = 100)
        {
            _sourceData = sourceData ?? Array.Empty<T>();
            _bufferCapacity = Math.Max(1, bufferCapacity);
        }

        public T Current
        {
            get
            {
                if (_currentIndex < 0 || _currentIndex >= _sourceData.Count)
                {
                    throw new InvalidOperationException($"游标当前处于无效位置 ({_currentIndex})。请在读取前调用 MoveNext()。");
                }
                return _sourceData[_currentIndex];
            }
        }

        public int CurrentIndex => _currentIndex;

        public int TotalCount => _sourceData.Count;

        public bool HasNext => _currentIndex + 1 < _sourceData.Count;

        public bool HasPrevious => _currentIndex > 0;

        public bool MoveNext()
        {
            if (_currentIndex + 1 < _sourceData.Count)
            {
                _currentIndex++;
                return true;
            }
            return false;
        }

        public bool MovePrevious()
        {
            if (_currentIndex > 0)
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
            if (index >= 0 && index < _sourceData.Count)
            {
                _currentIndex = index;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 获取当前游标位置及之前最多 100 条缓存数据 (按时间由旧到新排序)
        /// </summary>
        public IReadOnlyList<T> GetBuffer()
        {
            if (_currentIndex < 0 || _sourceData.Count == 0)
            {
                return Array.Empty<T>();
            }

            int count = Math.Min(_currentIndex + 1, _bufferCapacity);
            int start = _currentIndex - count + 1;

            var buffer = new List<T>(count);
            for (int i = start; i <= _currentIndex; i++)
            {
                buffer.Add(_sourceData[i]);
            }
            return buffer;
        }

        /// <summary>
        /// 获取最近 count 条历史数据 (不超过缓存上限 100 条)
        /// </summary>
        public IReadOnlyList<T> GetRecent(int count)
        {
            if (_currentIndex < 0 || _sourceData.Count == 0 || count <= 0)
            {
                return Array.Empty<T>();
            }

            int availableCount = Math.Min(_currentIndex + 1, _bufferCapacity);
            int actualCount = Math.Min(availableCount, count);
            int start = _currentIndex - actualCount + 1;

            var result = new List<T>(actualCount);
            for (int i = start; i <= _currentIndex; i++)
            {
                result.Add(_sourceData[i]);
            }
            return result;
        }

        public T? PeekNext(int offset = 1)
        {
            int target = _currentIndex + offset;
            if (target >= 0 && target < _sourceData.Count)
            {
                return _sourceData[target];
            }
            return default;
        }

        public T? PeekPrevious(int offset = 1)
        {
            int target = _currentIndex - offset;
            if (target >= 0 && target < _sourceData.Count)
            {
                return _sourceData[target];
            }
            return default;
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < _sourceData.Count; i++)
            {
                yield return _sourceData[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
