using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using TradingTerminal.Models;
using TradingTerminal.Services;

namespace TradingTerminal.Services.Backtest.Services
{
    public class BacktestPositionManager : IPositionManagementService
    {
        private readonly ConcurrentDictionary<string, PositionTracker> _positions = new(StringComparer.OrdinalIgnoreCase);

        public bool HasActivePosition(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return false;
            return _positions.ContainsKey(symbol);
        }

        public bool HasAnyActivePosition()
        {
            return !_positions.IsEmpty;
        }

        public bool TryGetPosition(string symbol, out PositionTracker position)
        {
            position = null;
            if (string.IsNullOrEmpty(symbol)) return false;
            return _positions.TryGetValue(symbol, out position);
        }

        public void RegisterPositionStrategy(string symbol, string strategyName, decimal entryPrice, decimal takeProfitPrice)
        {
            if (_positions.TryGetValue(symbol, out var pos))
            {
                pos.StrategyName = strategyName;
                pos.TakeProfitPrice = takeProfitPrice;
            }
        }

        // ==========================================
        // 🌟 回测运行器专用的操作接口 (内存沙箱修改)
        // ==========================================

        public void SetPosition(string symbol, PositionTracker position)
        {
            _positions[symbol] = position;
        }

        public void RemovePosition(string symbol)
        {
            _positions.TryRemove(symbol, out _);
        }

        public void Clear()
        {
            _positions.Clear();
        }

        public Dictionary<string, PositionTracker> GetActivePositions()
        {
            return new Dictionary<string, PositionTracker>(_positions, StringComparer.OrdinalIgnoreCase);
        }
    }
}
