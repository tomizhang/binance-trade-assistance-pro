using Binance.Net.Enums;
using Common.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Interfaces
{
    /// <summary>
    /// 统一市场数据提供者接口 (拉取游标 + 事件推流 + 高性能列式游标与双层嵌套回放)
    /// </summary>
    public interface IMarketDataProvider : IDisposable
    {
        /// <summary>
        /// 是否处于运行/推流状态
        /// </summary>
        bool IsRunning { get; }

        #region 事件推送模式 (实盘 WebSocket 与历史回测统一监听)

        /// <summary>
        /// 当收到新 K 线数据时触发
        /// </summary>
        event Action<MarketKline>? OnKline;

        /// <summary>
        /// 当收到新 Tick/Trade 数据时触发
        /// </summary>
        event Action<MarketTick>? OnTick;

        #endregion

        #region 游标拉取模式 (历史回测与按需切片)

        /// <summary>
        /// 获取指定币种、周期及时间范围的 K 线数据游标
        /// </summary>
        ICursor<MarketKline> GetKlineCursor(string symbol, KlineInterval interval, DateTime startUtc, DateTime endUtc);

        /// <summary>
        /// 获取指定币种、周期及时间范围的 K 线数据游标 (字符串周期重载)
        /// </summary>
        ICursor<MarketKline> GetKlineCursor(string symbol, string interval, DateTime startUtc, DateTime endUtc);

        /// <summary>
        /// 获取指定币种及时间范围的 Tick/Trade 逐笔成交游标
        /// </summary>
        ICursor<MarketTick> GetTickCursor(string symbol, DateTime startUtc, DateTime endUtc);

        #endregion

        #region 🌟 高性能原始列式游标接口 (杜绝 ToString 与大对象装箱分配)

        /// <summary>
        /// 获取原始高性能列式 K 线游标 (通过列索引直接读取原始数据，避免 ToString 与大对象分配)
        /// </summary>
        IRawDataCursor GetRawKlineCursor(string symbol, string interval, DateTime startUtc, DateTime endUtc);

        /// <summary>
        /// 获取原始高性能列式 Tick/Trade 游标
        /// </summary>
        IRawDataCursor GetRawTickCursor(string symbol, DateTime startUtc, DateTime endUtc);

        #endregion

        #region 🌟 真实交易仿真：K线与Tick双层嵌套重放与推送

        /// <summary>
        /// 按照真实交易时序进行周期K线与微观Tick双层嵌套重放与推送：
        /// 遍历每根周期K线 (如30分钟)：
        ///   for 循环该周期的 tick 数据 -> 执行推送 tick (OnTick)
        ///   完成 tick 推送后推送周期 K 线 (OnKline) 以模拟真实交易收盘
        /// </summary>
        Task ReplaySimulationAsync(
            string symbol,
            string interval,
            DateTime startUtc,
            DateTime endUtc,
            int tickDelayMs = 0,
            int klineDelayMs = 0,
            CancellationToken token = default,
            Action<MarketTick>? onTickAction = null,
            Action<MarketKline>? onKlineAction = null);

        #endregion

        #region 生命周期控制

        /// <summary>
        /// 启动数据流 (实盘建立 WebSocket 连接，或历史重放开始发流)
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 停止数据流
        /// </summary>
        Task StopAsync();

        #endregion
    }
}
