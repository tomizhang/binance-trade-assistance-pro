using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using TradingTerminal.Models;
using TradingTerminal.Utils;

namespace TradingTerminal.Services
{
    /// <summary>
    /// 图表发布对象 (包含用于绘制的所有原始数据，而非已经渲染好的图片)
    /// </summary>
    public class ChartPublishItem
    {
        public string Symbol { get; set; }
        public string StrategyName { get; set; }
        public DateTime Timestamp { get; set; }
        
        // 绘图基础数据：K线列表
        public List<IKline> Klines { get; set; } = new();
        
        // 标注点位: Index (K线索引, 代表X轴), Price (价格, 代表Y轴), Color, 半径
        public List<(int Index, decimal Price, SKColor Color, float Radius)> Points { get; set; } = new();
        
        // 标注文字: Text, Index (X轴), Price (Y轴), Color
        public List<(string Text, int Index, decimal Price, SKColor Color)> Texts { get; set; } = new();
        
        // 标注斜线: 起点(Index1, Price1), 终点(Index2, Price2), Color, 线宽
        public List<(int Index1, decimal Price1, int Index2, decimal Price2, SKColor Color, float StrokeWidth)> Lines { get; set; } = new();
        
        // 标注价格平行通道: 起点(Index1, Price1), 终点(Index2, Price2), 纵向像素宽度, Color
        public List<(int Index1, decimal Price1, int Index2, decimal Price2, float VerticalPixelWidth, SKColor Color)> PriceChannels { get; set; } = new();
    }

    /// <summary>
    /// 图片生产与消费服务 (后台服务)
    /// 策略端只负责生成原始数据点位，由本服务在后台线程中异步调用绘图组件进行高耗时的图片渲染和落盘
    /// 极大减轻了策略线程的负担
    /// </summary>
    public class ChartPublishService : BackgroundService
    {
        private readonly ILogger<ChartPublishService> _logger;
        private readonly Channel<ChartPublishItem> _channel;
        private readonly string _snapshotDir;

        public ChartPublishService(ILogger<ChartPublishService> logger)
        {
            _logger = logger;
            // 设定容量上限，防止突发大量绘图请求导致内存溢出
            var options = new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            };
            _channel = Channel.CreateBounded<ChartPublishItem>(options);
            
            _snapshotDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Snapshots");
            if (!Directory.Exists(_snapshotDir))
            {
                Directory.CreateDirectory(_snapshotDir);
            }
        }

        /// <summary>
        /// 生产者 API：策略端调用此方法推送绘图原始数据
        /// </summary>
        public async ValueTask PublishChartAsync(ChartPublishItem item)
        {
            if (item == null || item.Klines == null || item.Klines.Count == 0) return;
            
            item.Timestamp = DateTime.Now;
            // 写入队列
            await _channel.Writer.WriteAsync(item);
        }

        /// <summary>
        /// 消费者循环：后台统一渲染和处理图片
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("📸 [ChartPublishService] 图表生产消费服务已启动，准备接收原始数据并后台渲染...");

            try
            {
                await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
                {
                    try
                    {
                        // 异步放入线程池处理，防止单次渲染过慢阻塞消费者队列
                        _ = Task.Run(() => RenderAndSaveChart(item), stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"❌ [ChartPublishService] 调度渲染任务发生异常: {item.Symbol} - {item.StrategyName}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常关闭时不抛异常
            }
            
            _logger.LogInformation("📸 [ChartPublishService] 图表生产消费服务已停止。");
        }

        /// <summary>
        /// 执行具体的绘制与落盘逻辑 (在后台独立线程中执行)
        /// </summary>
        private void RenderAndSaveChart(ChartPublishItem item)
        {
            int klineCount = item.Klines.Count;
            if (klineCount == 0) return;

            decimal minPrice = item.Klines.Min(k => k.Low);
            decimal maxPrice = item.Klines.Max(k => k.High);

            // 预留一些上下边距的价格空间，防止贴边
            decimal padding = (maxPrice - minPrice) * 0.05m;
            if (padding == 0) padding = minPrice * 0.001m;
            minPrice -= padding;
            maxPrice += padding;

            using var helper = new ChartDrawingHelper(1920, 1080);
            
            // 1. 画 K线
            helper.DrawKlines(item.Klines, minPrice, maxPrice);

            // 2. 画通道
            if (item.PriceChannels.Any())
            {
                var mappedChannels = item.PriceChannels.Select(c => (
                    helper.MapX(c.Index1, klineCount),
                    helper.MapY(c.Price1, minPrice, maxPrice),
                    helper.MapX(c.Index2, klineCount),
                    helper.MapY(c.Price2, minPrice, maxPrice),
                    c.VerticalPixelWidth,
                    c.Color
                )).ToList();
                helper.DrawPriceChannels(mappedChannels);
            }

            // 3. 画斜线
            if (item.Lines.Any())
            {
                var mappedLines = item.Lines.Select(l => (
                    helper.MapX(l.Index1, klineCount),
                    helper.MapY(l.Price1, minPrice, maxPrice),
                    helper.MapX(l.Index2, klineCount),
                    helper.MapY(l.Price2, minPrice, maxPrice),
                    l.Color,
                    l.StrokeWidth
                )).ToList();
                helper.DrawLines(mappedLines);
            }

            // 4. 画点位
            if (item.Points.Any())
            {
                var mappedPoints = item.Points.Select(p => (
                    helper.MapX(p.Index, klineCount),
                    helper.MapY(p.Price, minPrice, maxPrice),
                    p.Color
                )).ToList();
                helper.DrawPoints(mappedPoints); // 如果有多种半径，这里可以扩展 DrawPoints 支持半径数组
            }

            // 5. 画文字
            if (item.Texts.Any())
            {
                var mappedTexts = item.Texts.Select(t => (
                    t.Text,
                    helper.MapX(t.Index, klineCount),
                    helper.MapY(t.Price, minPrice, maxPrice) - 20f, // 文字稍微上移一点，避免被点位遮挡
                    t.Color
                )).ToList();
                helper.DrawTexts(mappedTexts);
            }

            // 导出并保存
            byte[] imageBytes = helper.ToPngBytes();
            string fileName = $"{item.Timestamp:yyyyMMdd_HHmmss}_{item.Symbol}_{item.StrategyName}.png";
            string filePath = Path.Combine(_snapshotDir, fileName);
            File.WriteAllBytes(filePath, imageBytes);

            _logger.LogInformation($"✅ [图表落地] 成功后台渲染并保存 {item.Symbol} 的 {item.StrategyName} 策略快照至: {fileName}");
        }
    }
}
