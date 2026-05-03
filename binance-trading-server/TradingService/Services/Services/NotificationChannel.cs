using System.Threading.Channels;

namespace TradingTerminal.Services
{
    // 邮件消息实体
    public class EmailMessage
    {
        public string Subject { get; set; }
        public string HtmlBody { get; set; }
    }

    /// <summary>
    /// 高性能通知消息队列 (单例)
    /// </summary>
    public class NotificationChannel
    {
        private readonly Channel<EmailMessage> _channel;

        public NotificationChannel()
        {
            // 建立一个最大容量为 1000 的管道
            var options = new BoundedChannelOptions(1000)
            {
                // 如果极其罕见地满了，生产者会稍微等待
                FullMode = BoundedChannelFullMode.Wait
            };
            _channel = Channel.CreateBounded<EmailMessage>(options);
        }

        /// <summary>
        /// 提供给生产者：非阻塞写入管道 (非常适合在同步的 Event 事件中调用)
        /// </summary>
        public bool TryWrite(EmailMessage message)
        {
            return _channel.Writer.TryWrite(message);
        }

        /// <summary>
        /// 暴露给消费者：读取邮件任务
        /// </summary>
        public ChannelReader<EmailMessage> Reader => _channel.Reader;
    }
}