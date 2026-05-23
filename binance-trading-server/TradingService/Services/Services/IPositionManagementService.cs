using System;
using TradingTerminal.Models;

namespace TradingTerminal.Services
{
    public interface IPositionManagementService
    {
        bool HasActivePosition(string symbol);
        bool HasAnyActivePosition();
        bool TryGetPosition(string symbol, out PositionTracker position);
        void RegisterPositionStrategy(string symbol, string strategyName, decimal entryPrice, decimal takeProfitPrice);
    }
}
