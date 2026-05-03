using System.Threading.Channels;
using System.Threading.Tasks;
using TradingTerminal.Models;

namespace TradingTerminal.Services
{
    /// <summary>
    /// 高性能内存消息队列 (单例)
    /// </summary>
    public class OrderChannel
    {
        // 建立一个最大容量为 1000 的管道，防止内存溢出
        private readonly Channel<OrderSignal> _channel;

        public OrderChannel()
        {
            var options = new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait // 如果管道满了，生产者稍微等一下 (极少发生)
            };
            _channel = Channel.CreateBounded<OrderSignal>(options);
        }

        /// <summary>
        /// 暴露给生产者 (如策略引擎)：写入订单信号
        /// </summary>
        public async Task WriteAsync(OrderSignal signal)
        {
            await _channel.Writer.WriteAsync(signal);
        }

        /// <summary>
        /// 暴露给消费者：读取订单信号
        /// </summary>
        public ChannelReader<OrderSignal> Reader => _channel.Reader;
    }
}