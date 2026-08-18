using Common.Models;
using System;

namespace Common.Interfaces
{
    /// <summary>
    /// 统一量化交易策略接口 (支持 Kline 与 Tick 数据双通道处理，实盘与回测无缝复用)
    /// </summary>
    public interface IStrategy
    {
        /// <summary>
        /// 策略唯一标识
        /// </summary>
        string StrategyId { get; }

        /// <summary>
        /// 策略名称
        /// </summary>
        string Name { get; set; }

        /// <summary>
        /// 策略监控交易对 (如 BTCUSDT)
        /// </summary>
        string Symbol { get; set; }

        /// <summary>
        /// 策略运行主周期 (如 30m, 1h)
        /// </summary>
        string Interval { get; set; }

        /// <summary>
        /// 策略是否已启用
        /// </summary>
        bool IsEnabled { get; set; }

        #region 事件通知

        /// <summary>
        /// 当策略生成买卖/平仓信号时触发
        /// </summary>
        event Action<StrategySignal>? OnSignal;

        /// <summary>
        /// 当策略输出日志信息时触发
        /// </summary>
        event Action<string>? OnLog;

        #endregion

        #region 数据接收与处理接口

        /// <summary>
        /// 接收并处理新 K 线数据
        /// </summary>
        void OnKlineUpdate(MarketKline kline);

        /// <summary>
        /// 接收并处理新 Tick/Trade 逐笔成交数据
        /// </summary>
        void OnTickUpdate(MarketTick tick);

        #endregion

        #region 生命周期管理

        /// <summary>
        /// 绑定市场数据提供者 (自动挂载 OnKline 与 OnTick 事件)
        /// </summary>
        void Bind(IMarketDataProvider dataProvider);

        /// <summary>
        /// 解绑市场数据提供者
        /// </summary>
        void Unbind();

        /// <summary>
        /// 启动策略
        /// </summary>
        void Start();

        /// <summary>
        /// 停止策略
        /// </summary>
        void Stop();

        /// <summary>
        /// 重置策略内部状态与指标缓存
        /// </summary>
        void Reset();

        #endregion
    }
}
