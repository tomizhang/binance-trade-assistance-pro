using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinFormsApp2
{
    /// <summary>
    /// 腾讯企业微信群机器人 Webhook 推送服务 (WeCom Notifier)
    /// 专职负责将实盘成交、止盈止损、系统启动/停止及异常报警实时推送到企业微信
    /// </summary>
    public class WeComNotifier : IDisposable
    {
        private static readonly Lazy<WeComNotifier> _instance = new Lazy<WeComNotifier>(() => new WeComNotifier());
        public static WeComNotifier Instance => _instance.Value;

        private readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        private readonly BlockingCollection<string> _messageQueue = new BlockingCollection<string>(1000);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task? _workerTask;

        public bool IsEnabled { get; set; } = true;
        public string WebhookUrl { get; set; } = "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=15746205-c9a8-4ebc-9a19-0e96a4998af1";

        public event Action<string>? OnLog;

        public WeComNotifier()
        {
            _workerTask = Task.Run(() => ProcessMessageQueueAsync(_cts.Token));
        }

        /// <summary>
        /// 配置企业微信 Webhook 参数
        /// </summary>
        public void Configure(bool isEnabled, string? webhookUrl)
        {
            IsEnabled = isEnabled;
            if (!string.IsNullOrWhiteSpace(webhookUrl))
            {
                WebhookUrl = webhookUrl.Trim();
            }
        }

        /// <summary>
        /// 发送 Markdown 格式富文本消息
        /// </summary>
        public void SendMarkdown(string markdownContent)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(WebhookUrl)) return;
            if (string.IsNullOrWhiteSpace(markdownContent)) return;

            var payload = new
            {
                msgtype = "markdown",
                markdown = new
                {
                    content = markdownContent
                }
            };

            string json = JsonSerializer.Serialize(payload);
            _messageQueue.TryAdd(json);
        }

        /// <summary>
        /// 发送纯文本消息
        /// </summary>
        public void SendText(string textContent)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(WebhookUrl)) return;
            if (string.IsNullOrWhiteSpace(textContent)) return;

            var payload = new
            {
                msgtype = "text",
                text = new
                {
                    content = textContent
                }
            };

            string json = JsonSerializer.Serialize(payload);
            _messageQueue.TryAdd(json);
        }

        /// <summary>
        /// 发送订单成交通知 (开仓 / 平仓 / 止盈止损挂单)
        /// </summary>
        public void SendOrderNotification(OrderResult result, OrderRequest request, bool isLiveTrading)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(WebhookUrl)) return;

            string modeTag = isLiveTrading ? "🟢 币安实盘成交" : "🟡 模拟挂单成交";
            string typeDesc = request.Type switch
            {
                OrderType.BuyLongOpen => "做多开仓 (Buy Long)",
                OrderType.SellShortOpen => "做空开仓 (Sell Short)",
                OrderType.CloseLong => "平多止盈/止损 (Close Long)",
                OrderType.CloseShort => "平空止盈/止损 (Close Short)",
                _ => request.Type.ToString()
            };

            string typeColor = (request.Type == OrderType.BuyLongOpen || request.Type == OrderType.CloseShort) ? "info" : "warning";

            var sb = new StringBuilder();
            sb.AppendLine($"### 🚀 **{modeTag}**");
            sb.AppendLine($"> **币种**: <font color=\"info\">{request.Symbol}</font>");
            sb.AppendLine($"> **操作**: <font color=\"{typeColor}\">{typeDesc}</font>");
            sb.AppendLine($"> **成交价格**: `{result.ExecutedPrice:F4} USDT`");
            sb.AppendLine($"> **成交数量**: `{result.ExecutedQuantity}`");

            if (result.ExecutedPrice > 0 && result.ExecutedQuantity > 0)
            {
                decimal notional = result.ExecutedPrice * result.ExecutedQuantity;
                sb.AppendLine($"> **持仓价值**: `约 {notional:F2} USDT`");
            }

            if (request.TakeProfitPrice > 0)
            {
                sb.AppendLine($"> **🎯 止盈目标**: <font color=\"info\">{request.TakeProfitPrice:F4} (+{request.TakeProfitPct:F1}%)</font>");
            }
            if (request.StopLossPrice > 0)
            {
                sb.AppendLine($"> **🛡 止损目标**: <font color=\"warning\">{request.StopLossPrice:F4} (-{request.StopLossPct:F1}%)</font>");
            }

            if (!string.IsNullOrEmpty(result.OrderId))
            {
                sb.AppendLine($"> **订单单号**: `#{result.OrderId}`");
            }
            sb.AppendLine($"> **执行耗时**: `⚡ {result.ElapsedMs} ms`");
            sb.AppendLine($"> **发生时间**: `{DateTime.Now:yyyy-MM-dd HH:mm:ss}`");

            SendMarkdown(sb.ToString());
        }

        /// <summary>
        /// 发送系统状态广播通知 (实盘启动 / 停止 / 异常报警)
        /// </summary>
        public void SendSystemStatus(string title, string message, bool isAlert = false)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(WebhookUrl)) return;

            string colorTag = isAlert ? "warning" : "info";
            var sb = new StringBuilder();
            sb.AppendLine($"### <font color=\"{colorTag}\">{(isAlert ? "⚠️" : "📢")} {title}</font>");
            sb.AppendLine($"> {message}");
            sb.AppendLine($"> **时间**: `{DateTime.Now:yyyy-MM-dd HH:mm:ss}`");

            SendMarkdown(sb.ToString());
        }

        private async Task ProcessMessageQueueAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_messageQueue.TryTake(out string? jsonPayload, 1000, token))
                    {
                        if (!string.IsNullOrWhiteSpace(jsonPayload) && !string.IsNullOrWhiteSpace(WebhookUrl))
                        {
                            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                            var response = await _httpClient.PostAsync(WebhookUrl, content, token).ConfigureAwait(false);
                            if (response.IsSuccessStatusCode)
                            {
                                string respStr = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (!respStr.Contains("\"errcode\":0"))
                                {
                                    OnLog?.Invoke($"⚠️ [企业微信推送返回] {respStr}");
                                }
                            }
                            else
                            {
                                OnLog?.Invoke($"⚠️ [企业微信推送失败] HTTP 状态码: {response.StatusCode}");
                            }

                            // 遵循企业微信频率限制 (每分钟最多 20 条，每条间隔至少 150ms)
                            await Task.Delay(150, token).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ [企业微信推送异常] {ex.Message}");
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _httpClient.Dispose();
            _messageQueue.Dispose();
            _cts.Dispose();
        }
    }
}
