using System;
using System.Threading.Channels;

namespace TradingTerminal.Services
{
    // 快照任务实体
    public class SnapshotTask
    {
        public string Symbol { get; set; }
        public string TradeType { get; set; }     // "OPEN" (开仓) 或 "CLOSE" (平仓)
        public string OrderSide { get; set; }     // "BUY" 或 "SELL"
        public string StrategyName { get; set; }  // 触发策略
        public decimal? RealizedPnl { get; set; } // 如果是平仓，记录当时的盈亏
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// 高性能快照任务队列 (单例)
    /// </summary>
    public class SnapshotChannel
    {
        private readonly Channel<SnapshotTask> _channel;

        public SnapshotChannel()
        {
            var options = new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait
            };
            _channel = Channel.CreateBounded<SnapshotTask>(options);
        }

        public bool TryWrite(SnapshotTask task)
        {
            return _channel.Writer.TryWrite(task);
        }

        public ChannelReader<SnapshotTask> Reader => _channel.Reader;
    }
}