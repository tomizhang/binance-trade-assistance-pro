using Common.Helper;
using Common.Interfaces;
using Common.Models;
using System;
using System.Collections.Generic;

namespace Common.Strategies
{
    /// <summary>
    /// 策略抽象基类 (内置 100 条滑动历史缓存，统一接收处理 Kline 和 Tick 双通道行情数据)
    /// </summary>
    public abstract class StrategyBase : IStrategy
    {
        private readonly List<MarketKline> _klineBuffer;
        private readonly List<MarketTick> _tickBuffer;
        private readonly object _syncLock = new object();
        private IMarketDataProvider? _dataProvider;

        public string StrategyId { get; }
        public string Name { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public string Interval { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public int BufferCapacity { get; }

        public event Action<StrategySignal>? OnSignal;
        public event Action<string>? OnLog;

        /// <summary>
        /// 当前最新的 100 根历史 K 线滑窗缓存 (按时间由旧到新排序)
        /// </summary>
        public IReadOnlyList<MarketKline> KlineHistory
        {
            get
            {
                lock (_syncLock)
                {
                    return _klineBuffer.ToArray();
                }
            }
        }

        /// <summary>
        /// 当前最新的 100 笔历史 Tick/Trade 滑窗缓存
        /// </summary>
        public IReadOnlyList<MarketTick> TickHistory
        {
            get
            {
                lock (_syncLock)
                {
                    return _tickBuffer.ToArray();
                }
            }
        }

        /// <summary>
        /// 最新收到的 K 线
        /// </summary>
        public MarketKline? LatestKline { get; private set; }

        /// <summary>
        /// 最新收到的 Tick/Trade
        /// </summary>
        public MarketTick? LatestTick { get; private set; }

        protected StrategyBase(string name, string symbol = "", string interval = "", int bufferCapacity = 100)
        {
            StrategyId = Guid.NewGuid().ToString("N");
            Name = string.IsNullOrWhiteSpace(name) ? GetType().Name : name;
            Symbol = symbol.ToUpper();
            Interval = interval;
            BufferCapacity = Math.Max(10, bufferCapacity);

            _klineBuffer = new List<MarketKline>(BufferCapacity);
            _tickBuffer = new List<MarketTick>(BufferCapacity);
        }

        #region 行情接收与 100 条滑窗维护

        public void OnKlineUpdate(MarketKline kline)
        {
            if (!IsEnabled || kline == null) return;

            // 若策略指定了 Symbol，进行过滤匹配
            if (!string.IsNullOrEmpty(Symbol) && !string.Equals(Symbol, kline.Symbol, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // 若策略指定了 Interval，进行过滤匹配
            if (!string.IsNullOrEmpty(Interval) && !string.Equals(Interval, kline.Interval, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            IReadOnlyList<MarketKline> snapshot;
            lock (_syncLock)
            {
                LatestKline = kline;

                // 若处于实盘未收盘更新 (同一根 K 线时间戳)，更新末尾元素；否则追加新 K 线
                if (_klineBuffer.Count > 0 && _klineBuffer[^1].OpenTime == kline.OpenTime)
                {
                    _klineBuffer[^1] = kline;
                }
                else
                {
                    _klineBuffer.Add(kline);
                    if (_klineBuffer.Count > BufferCapacity)
                    {
                        _klineBuffer.RemoveAt(0);
                    }
                }

                snapshot = _klineBuffer.ToArray();
            }

            try
            {
                // 调用派生策略的核心计算逻辑
                OnKline(kline, snapshot);
            }
            catch (Exception ex)
            {
                Log($"[策略异常] 处理 K线时出错: {ex.Message}");
            }
        }

        public void OnTickUpdate(MarketTick tick)
        {
            if (!IsEnabled || tick == null) return;

            if (!string.IsNullOrEmpty(Symbol) && !string.Equals(Symbol, tick.Symbol, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            IReadOnlyList<MarketTick> snapshot;
            lock (_syncLock)
            {
                LatestTick = tick;
                _tickBuffer.Add(tick);
                if (_tickBuffer.Count > BufferCapacity)
                {
                    _tickBuffer.RemoveAt(0);
                }

                snapshot = _tickBuffer.ToArray();
            }

            try
            {
                OnTick(tick, snapshot);
            }
            catch (Exception ex)
            {
                Log($"[策略异常] 处理 Tick 时出错: {ex.Message}");
            }
        }

        #endregion

        #region 派生策略重写入口 (Hook Methods)

        /// <summary>
        /// 当接收到新 K 线时的核心计算处理 (派生策略必须实现)
        /// </summary>
        /// <param name="kline">当前最新 K 线</param>
        /// <param name="klineHistory">当前持有的最近历史 K 线滑窗缓存 (最多 100 根)</param>
        protected abstract void OnKline(MarketKline kline, IReadOnlyList<MarketKline> klineHistory);

        /// <summary>
        /// 当接收到新 Tick/Trade 逐笔成交时的计算处理 (可选重写)
        /// </summary>
        /// <param name="tick">当前最新逐笔成交</param>
        /// <param name="tickHistory">当前持有的最近历史 Tick 滑窗缓存 (最多 100 笔)</param>
        protected virtual void OnTick(MarketTick tick, IReadOnlyList<MarketTick> tickHistory)
        {
        }

        protected virtual void OnInit() { }
        protected virtual void OnStart() { }
        protected virtual void OnStop() { }
        protected virtual void OnReset() { }

        #endregion

        #region 信号生成与日志辅助方法

        /// <summary>
        /// 触发交易信号
        /// </summary>
        protected void EmitSignal(
            SignalType type,
            decimal price,
            decimal quantity = 0,
            string reason = "",
            decimal? stopLoss = null,
            decimal? takeProfit = null,
            object? extra = null)
        {
            var signal = new StrategySignal
            {
                StrategyId = StrategyId,
                StrategyName = Name,
                Symbol = string.IsNullOrEmpty(Symbol) ? (LatestKline?.Symbol ?? LatestTick?.Symbol ?? "UNKNOWN") : Symbol,
                Time = LatestKline?.OpenTime ?? LatestTick?.Time ?? DateTime.UtcNow,
                Type = type,
                Price = price,
                Quantity = quantity,
                StopLossPrice = stopLoss,
                TakeProfitPrice = takeProfit,
                Reason = reason,
                ExtraData = extra
            };

            Log($"[产生信号] {signal.Type} | 价格: {signal.Price:F2} | 原因: {signal.Reason}");
            OnSignal?.Invoke(signal);
        }

        /// <summary>
        /// 记录策略日志
        /// </summary>
        protected void Log(string message)
        {
            string formatted = $"[{Name}] {message}";
            OnLog?.Invoke(formatted);
        }

        #endregion

        #region 常用指标快速计算工具函数 (直接基于 100 条滑窗计算)

        /// <summary>
        /// 计算简单移动平均线 (SMA)
        /// </summary>
        protected decimal? CalculateSma(IReadOnlyList<MarketKline> history, int period)
        {
            if (history == null || history.Count < period || period <= 0) return null;

            decimal sum = 0;
            int start = history.Count - period;
            for (int i = start; i < history.Count; i++)
            {
                sum += history[i].Close;
            }
            return sum / period;
        }

        /// <summary>
        /// 计算指数移动平均线 (EMA)
        /// </summary>
        protected decimal? CalculateEma(IReadOnlyList<MarketKline> history, int period)
        {
            if (history == null || history.Count < period || period <= 0) return null;

            decimal multiplier = 2m / (period + 1m);
            decimal ema = history[0].Close;

            for (int i = 1; i < history.Count; i++)
            {
                ema = (history[i].Close - ema) * multiplier + ema;
            }

            return ema;
        }

        #endregion

        #region 生命周期控制

        public void Bind(IMarketDataProvider dataProvider)
        {
            Unbind();
            _dataProvider = dataProvider;
            if (_dataProvider != null)
            {
                _dataProvider.OnKline += OnKlineUpdate;
                _dataProvider.OnTick += OnTickUpdate;
                OnInit();
                Log($"已成功绑定市场数据提供者 ({_dataProvider.GetType().Name})");
            }
        }

        public void Unbind()
        {
            if (_dataProvider != null)
            {
                _dataProvider.OnKline -= OnKlineUpdate;
                _dataProvider.OnTick -= OnTickUpdate;
                _dataProvider = null;
                Log("已解除数据提供者绑定");
            }
        }

        public void Start()
        {
            IsEnabled = true;
            OnStart();
            Log("策略已启动");
        }

        public void Stop()
        {
            IsEnabled = false;
            OnStop();
            Log("策略已停止");
        }

        public void Reset()
        {
            lock (_syncLock)
            {
                _klineBuffer.Clear();
                _tickBuffer.Clear();
                LatestKline = null;
                LatestTick = null;
            }
            OnReset();
            Log("策略状态与指标缓存已重置");
        }

        #endregion
    }
}
