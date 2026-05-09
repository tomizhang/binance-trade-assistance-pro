using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TradingTerminal.Hubs;
using TradingTerminal.Models;

namespace TradingTerminal.Services
{
    /// <summary>
    /// 1分钟量价突破策略：1h窗口极值点 + 3-5倍量能爆发
    /// </summary>
    public class BreakoutStrategyService : StrategyBase
    {
        private readonly ConcurrentDictionary<string, List<KlineMessage>> _historyBuffer = new();
        private const int LOOKBACK_WINDOW = 60; // 1小时 = 60根1m K线

        public BreakoutStrategyService(
            ILogger<BreakoutStrategyService> logger,
            IHubContext<MarketHub> hubContext,
            MarketEventBus eventBus,
            BinanceWebSocketService wsService,
            OrderChannel orderChannel,
            BinanceTradeWsService tradeWsService)
            : base(logger, hubContext, eventBus, wsService, orderChannel, tradeWsService)
        {
        }

        protected override void OnKlineReceived(KlineMessage msg)
        {
            string key = msg.Symbol;
            var buffer = _historyBuffer.GetOrAdd(key, _ => new List<KlineMessage>());

            // 1. 实时维护 1 小时滑动窗口
            if (msg.IsClosed)
            {
                buffer.Add(msg);
                if (buffer.Count > LOOKBACK_WINDOW) buffer.RemoveAt(0);
            }

            // 仅在收盘时判定，确保成交量和收盘价数据的真实性
            if (!msg.IsClosed || buffer.Count < 30) return;

            // 2. 成交量爆发检查 (3-5 倍)
            decimal avgVolume = buffer.Take(buffer.Count - 1).Average(k => k.Volume);
            decimal volMultiplier = avgVolume > 0 ? msg.Volume / avgVolume : 0;

            if (volMultiplier >= 3.0m && volMultiplier <= 5.0m)
            {
                ExecuteBreakoutLogic(msg, buffer, volMultiplier);
            }
        }

        private void ExecuteBreakoutLogic(KlineMessage msg, List<KlineMessage> buffer, decimal volMultiplier)
        {
            // 3. 提取 1 小时内的极值点
            decimal maxHigh = buffer.Max(k => k.High);
            decimal minLow = buffer.Min(k => k.Low);

            bool isLong = false;
            bool isShort = false;
            string reason = "";

            // --- 多头逻辑 ---
            if (msg.Close > maxHigh) // 🚀 价格在最高点之上：突破做多
            {
                isLong = true;
                reason = $"放量突破1h高点 {maxHigh:F4}";
            }
            else if (msg.Close > minLow && Math.Abs(msg.Close - minLow) / minLow < 0.0015m) // 🧱 价格在低点之上：支撑做多
            {
                isLong = true;
                reason = $"放量回踩1h支撑位 {minLow:F4}";
            }

            // --- 空头逻辑 ---
            else if (msg.Close < minLow) // 📉 价格在最低点之下：跌破做空
            {
                isShort = true;
                reason = $"放量跌破1h低点 {minLow:F4}";
            }
            else if (msg.Close < maxHigh && Math.Abs(msg.Close - maxHigh) / maxHigh < 0.0015m) // 🛰️ 价格在高点之下：压力做空
            {
                isShort = true;
                reason = $"放量受阻1h压力位 {maxHigh:F4}";
            }

            if (isLong || isShort)
            {
                _logger.LogWarning($"🔥 [量价爆发] {msg.Symbol} 量能 {volMultiplier:F1}X, {reason}");

                // 🌟 结构化止损：基于 1h 波动率或固定比例 (建议 1.5% - 5%)
                decimal slRoe = 0.01m; // 2% 基础止损
                decimal tpRoe = 0.015m; // 4% 止盈目标

                decimal slPrice = isLong ? msg.Close * (1 - slRoe / 5) : msg.Close * (1 + slRoe / 5);
                decimal tpPrice = isLong ? msg.Close * (1 + tpRoe / 5) : msg.Close * (1 - tpRoe / 5);

                _ = Task.Run(async () =>
                {

                    // 设定参数：使用 2.0 USDT 保证金，5 倍杠杆
                    // 目标：亏损本金 2.5% 止损，盈利本金 5% 止盈
                    await PlaceOrderWithLeverageRiskAsync(
                        msg.Symbol,
                        isLong,
                        msg.Close,
                        marginUsdt: 1.5m,
                        leverage: 5.0m,
                        targetRoeTp: 0.05m,  // +5% ROE
                        riskRoeSl: 0.025m,   // -2.5% ROE
                        strategyName: "1m_Vol_Breakout"
                    );
                });
            }
        }

        protected override async Task InitializeStrategyDataAsync(List<string> symbols)
        {
            foreach (var sym in symbols)
            {
                try
                {
                    // 🌟 获取 1 小时（及以上）历史 K 线进行预热
                    string json = await _wsService.GetHistoricalKlinesAsync(sym, "1m", 500);
                    using var doc = JsonDocument.Parse(json);

                    var historyList = new List<KlineMessage>();
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        historyList.Add(new KlineMessage
                        {
                            Symbol = sym,
                            High = decimal.Parse(item[2].GetString()),
                            Low = decimal.Parse(item[3].GetString()),
                            Close = decimal.Parse(item[4].GetString()),
                            Volume = decimal.Parse(item[5].GetString()),
                            IsClosed = true
                        });
                    }
                    _historyBuffer[sym] = historyList.Skip(Math.Max(0, historyList.Count - LOOKBACK_WINDOW)).ToList();
                }
                catch (Exception ex) { _logger.LogError($"❌ {sym} 历史初始化失败: {ex.Message}"); }
            }
        }
    }
}