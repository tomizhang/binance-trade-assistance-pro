using Common.Models;
using System.Collections.Generic;

namespace Common.Strategies
{
    /// <summary>
    /// 双均线交叉示例策略 (演示继承 StrategyBase 同时处理 Kline 与 Tick 数据并生成交易信号)
    /// </summary>
    public class MovingAverageCrossStrategy : StrategyBase
    {
        public int FastPeriod { get; set; } = 5;
        public int SlowPeriod { get; set; } = 20;

        private decimal? _prevFastMa;
        private decimal? _prevSlowMa;
        private bool _hasPosition = false;

        public MovingAverageCrossStrategy(
            string symbol = "BTCUSDT",
            string interval = "30m",
            int fastPeriod = 5,
            int slowPeriod = 20)
            : base("双均线交叉策略", symbol, interval, bufferCapacity: 100)
        {
            FastPeriod = fastPeriod;
            SlowPeriod = slowPeriod;
        }

        protected override void OnKline(MarketKline kline, IReadOnlyList<MarketKline> klineHistory)
        {
            if (klineHistory.Count < SlowPeriod)
            {
                Log($"[预热中] K线缓存数量: {klineHistory.Count}/{SlowPeriod}");
                return;
            }

            decimal? fastMa = CalculateSma(klineHistory, FastPeriod);
            decimal? slowMa = CalculateSma(klineHistory, SlowPeriod);

            if (fastMa == null || slowMa == null) return;

            // 当具有前一根均线数据时，判断金叉/死叉
            if (_prevFastMa.HasValue && _prevSlowMa.HasValue)
            {
                // 金叉 (快线上穿慢线) -> 开多
                if (_prevFastMa.Value <= _prevSlowMa.Value && fastMa.Value > slowMa.Value)
                {
                    if (!_hasPosition)
                    {
                        decimal stopLoss = kline.Low * 0.99m; // 1% 止损
                        decimal takeProfit = kline.High * 1.02m; // 2% 止盈
                        EmitSignal(SignalType.Buy, kline.Close, quantity: 1, reason: $"MA金叉 (MA{FastPeriod}:{fastMa:F2} 上穿 MA{SlowPeriod}:{slowMa:F2})", stopLoss: stopLoss, takeProfit: takeProfit);
                        _hasPosition = true;
                    }
                }
                // 死叉 (快线下穿慢线) -> 平多 / 开空
                else if (_prevFastMa.Value >= _prevSlowMa.Value && fastMa.Value < slowMa.Value)
                {
                    if (_hasPosition)
                    {
                        EmitSignal(SignalType.CloseLong, kline.Close, reason: $"MA死叉平多 (MA{FastPeriod}:{fastMa:F2} 下穿 MA{SlowPeriod}:{slowMa:F2})");
                        _hasPosition = false;
                    }
                }
            }

            _prevFastMa = fastMa;
            _prevSlowMa = slowMa;
        }

        protected override void OnTick(MarketTick tick, IReadOnlyList<MarketTick> tickHistory)
        {
            // 可在此处使用 Tick 逐笔数据做高频成交量异动监测或毫秒级止损检查
        }

        protected override void OnReset()
        {
            _prevFastMa = null;
            _prevSlowMa = null;
            _hasPosition = false;
        }
    }
}
