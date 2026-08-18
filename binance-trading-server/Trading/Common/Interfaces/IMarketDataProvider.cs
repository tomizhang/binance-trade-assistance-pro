using Binance.Net.Enums;
using Common.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Interfaces
{
    /// <summary>
    /// 统一市场数据提供者接口 (拉取游标 + 事件推流，历史与实盘无缝切换)
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
