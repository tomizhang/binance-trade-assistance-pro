using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace TradingTerminal.Services
{
    /// <summary>
    /// 快照消费者：专职负责在开平仓瞬间，定格当时的 K 线现场并持久化
    /// </summary>
    public class SnapshotManager : BackgroundService
    {
        private readonly ILogger<SnapshotManager> _logger;
        private readonly SnapshotChannel _snapshotChannel;
        private readonly BinanceWebSocketService _marketWsService; // 获取 K 线用

        public SnapshotManager(
            ILogger<SnapshotManager> logger,
            SnapshotChannel snapshotChannel,
            BinanceWebSocketService marketWsService,
            UserDataEventBus userDataBus) // 🌟 注入事件大喇叭，自动监听平仓
        {
            _logger = logger;
            _snapshotChannel = snapshotChannel;
            _marketWsService = marketWsService;

            // 🌟 自动监听平仓事件 (当收到平仓推送时，自动生产一个快照任务塞入管道)
            userDataBus.OnOrderTradeUpdated += HandleOrderTradeUpdate;

            // 确保本地快照文件夹存在
            var dir = $"{AppDomain.CurrentDomain.BaseDirectory}/Snapshots";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        private void HandleOrderTradeUpdate(OrderTradeUpdateEvent tradeEvent)
        {
            // 如果 rp (已实现盈亏) 不为 0，说明发生了一笔彻底的平仓交易
            if (tradeEvent.RealizedPnl != 0)
            {
                _snapshotChannel.TryWrite(new SnapshotTask
                {
                    Symbol = tradeEvent.Symbol,
                    TradeType = "CLOSE",
                    OrderSide = tradeEvent.OrderType,
                    RealizedPnl = tradeEvent.RealizedPnl,
                    StrategyName = "System_Close"
                });
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("📸 [快照中枢] 现场快照服务已启动，正在后台监听抓拍指令...");

            await foreach (var task in _snapshotChannel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessSnapshotAsync(task);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ [快照失败] 记录 {task.Symbol} 时发生异常: {ex.Message}");
                }
            }
        }

        private async Task ProcessSnapshotAsync(SnapshotTask task)
        {
            _logger.LogInformation($"📸 [快照抓拍] 正在保存 {task.Symbol} [{task.TradeType}] 现场...");

            // 1. 获取作案现场数据：向前拉取 100 根 2m 的 K 线
            // 这里利用了我们之前写好的完美网关，直接按需索取
            string klineJson = await _marketWsService.GetHistoricalKlinesAsync(task.Symbol, "2m", 100);

            // 2. 组装快照数据包
            var snapshotData = new
            {
                TaskInfo = task,
                CaptureTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Klines = System.Text.Json.JsonDocument.Parse(klineJson) // 格式化 JSON
            };

            string finalJson = System.Text.Json.JsonSerializer.Serialize(snapshotData, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            // 3. 落盘 (文件命名：币种_开平仓_时间戳.json)
            // 比如: BTCUSDT_OPEN_1684920192.json
            string fileName = $"Snapshots/{task.Symbol}_{task.TradeType}_{task.Timestamp}.json";

            // 异步写入磁盘，不阻塞
            await File.WriteAllTextAsync(fileName, finalJson);

            _logger.LogInformation($"💾 [快照归档] {fileName} 已安全落盘，耗时极速。");
        }

        public override void Dispose()
        {
            // (通常不需要手动取消事件，因为单例的生命周期与程序一致，但保持好习惯可以不加或加上)
            base.Dispose();
        }
    }
}