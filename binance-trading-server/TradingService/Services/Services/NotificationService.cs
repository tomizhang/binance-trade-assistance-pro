using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;

namespace TradingTerminal.Services
{
    /// <summary>
    /// 邮件消费者：后台无情发信机器，完全剥离于交易和风控线程
    /// </summary>
    public class NotificationService : BackgroundService
    {
        private readonly ILogger<NotificationService> _logger;
        private readonly RiskControlManager _riskManager;
        private readonly NotificationChannel _notificationChannel; // 🌟 注入邮件管道
        private readonly IConfiguration _config;

        public NotificationService(
            ILogger<NotificationService> logger,
            RiskControlManager riskManager,
            NotificationChannel notificationChannel,
            IConfiguration config)
        {
            _logger = logger;
            _riskManager = riskManager;
            _notificationChannel = notificationChannel;
            _config = config;

            // 🌟 订阅风控事件 (此时 NotificationService 充当生产者，把报警塞入管道)
            _riskManager.OnSymbolBlacklisted += HandleSymbolBlacklisted;
            _riskManager.OnGlobalMeltdown += HandleGlobalMeltdown;
        }

        // ==============================
        // 1. 生产者逻辑：把事件转化为邮件扔进管道
        // ==============================
        private void HandleSymbolBlacklisted(string symbol, int lossCount)
        {
            string subject = $"🚨 [单币熔断警告] {symbol} 已被拉入黑名单";
            string body = $@"
                <h3>量化系统自动报警</h3>
                <p><strong>交易对：</strong> <span style='color:red'>{symbol}</span></p>
                <p><strong>触发原因：</strong> 已连续亏损达到 {lossCount} 次。</p>
                <p><strong>系统动作：</strong> 已自动拦截该币种的所有新开仓请求。持有的单子仍可正常平仓。</p>
                <br/><p><small>通知时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}</small></p>";

            // 瞬间写入管道，不阻塞风控主线程，耗时几乎为 0
            _notificationChannel.TryWrite(new EmailMessage { Subject = subject, HtmlBody = body });
        }

        private void HandleGlobalMeltdown(string reason)
        {
            string subject = $"💥 [全局熔断] 系统已停止所有新开仓！";
            string body = $@"
                <h2 style='color:darkred'>全局风控熔断已被触发！</h2>
                <p><strong>触发原因：</strong> {reason}</p>
                <p><strong>系统动作：</strong> 整个量化终端已被全盘死锁，任何策略发出的开仓指令都将被强行驳回！</p>
                <br/><p><small>通知时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}</small></p>";

            _notificationChannel.TryWrite(new EmailMessage { Subject = subject, HtmlBody = body });
        }

        // ==============================
        // 2. 消费者逻辑：后台死循环排队发信
        // ==============================
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("📧 [通知消费者] 邮件发送队列已启动，正在后台监听发送任务...");

            // 只要管道里有信，就按顺序一封封发出去
            await foreach (var email in _notificationChannel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await SendEmailAsync(email.Subject, email.HtmlBody);
                }
                catch (Exception ex)
                {
                    // 发件失败不能让队列挂掉，记录日志后继续处理下一封
                    _logger.LogError($"❌ [通知服务] 邮件报警发送失败: {ex.Message}");
                }
            }
        }

        private async Task SendEmailAsync(string subject, string htmlBody)
        {
            var emailConfig = _config.GetSection("EmailConfig");
            string senderEmail = emailConfig["SenderEmail"];
            string senderPassword = emailConfig["SenderPassword"];
            string receiverEmail = emailConfig["ReceiverEmail"];

            using var mailMessage = new MailMessage
            {
                From = new MailAddress(senderEmail, emailConfig["SenderName"]),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true
            };
            mailMessage.To.Add(receiverEmail);

            using var smtpClient = new SmtpClient(emailConfig["SmtpServer"], int.Parse(emailConfig["SmtpPort"]))
            {
                Credentials = new NetworkCredential(senderEmail, senderPassword),
                EnableSsl = bool.Parse(emailConfig["EnableSsl"])
            };

            await smtpClient.SendMailAsync(mailMessage);
            _logger.LogInformation($"✅ [通知服务] 成功投递一封报警邮件: {subject}");
        }

        public override void Dispose()
        {
            // 防止内存泄漏
            _riskManager.OnSymbolBlacklisted -= HandleSymbolBlacklisted;
            _riskManager.OnGlobalMeltdown -= HandleGlobalMeltdown;
            base.Dispose();
        }
    }
}