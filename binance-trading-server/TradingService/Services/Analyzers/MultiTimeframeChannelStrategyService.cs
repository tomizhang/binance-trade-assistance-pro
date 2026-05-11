using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TradingTerminal.Hubs;
using TradingTerminal.Models;
using TradingTerminal.Utils;

namespace TradingTerminal.Services
{
    public class MultiTimeframeChannelStrategyService : StrategyBase
    {
        // 策略核心参数（暴露给后续UI或配置文件修改）
        private readonly decimal[] _slopes = new decimal[] { 0.0002m, 0.0005m, 0.0010m }; // 每1分钟价格波动的百分比斜率 (替代物理度数30/60/80)
        private readonly decimal _channelWidthPercent = 0.01m; // 默认通道总波动 1% (即上下各平移 0.5%)
        private readonly decimal _stopLossPercent = 0.005m; // 固定止损百分比 0.5%
        private readonly decimal _accelStopLossPercent = 0.008m; // 加速追单止损 0.8%

        private class TimeframeData
        {
            public List<KlineMessage> Buffer { get; set; } = new();
            public KlineMessage LatestPeak { get; set; }
            public KlineMessage LatestValley { get; set; }
        }

        private class ChannelCalc
        {
            public string Timeframe { get; set; }
            public long AnchorTime { get; set; }
            public decimal AnchorPrice { get; set; }
            public decimal Slope { get; set; } // 每分钟的绝对价格变化量
            public decimal CurrentCenterPrice { get; set; }
        }

        // 缓冲区与数据源
        private readonly ConcurrentDictionary<string, List<KlineMessage>> _1mBuffer = new();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeframeData>> _timeframeData = new();
        private readonly string[] _timeframes = new[] { "15m", "30m", "1h", "1d" };

        private readonly PositionManagementService _positionManager;
        private readonly ChartPublishService _chartPublishService;
        private readonly ConcurrentDictionary<string, DateTime> _lastTradeTime = new();

        public MultiTimeframeChannelStrategyService(
            ILogger<MultiTimeframeChannelStrategyService> logger,
            IHubContext<MarketHub> hubContext,
            MarketEventBus eventBus,
            BinanceWebSocketService wsService,
            OrderChannel orderChannel,
            BinanceTradeWsService tradeWsService,
            PositionManagementService positionManager,
            ChartPublishService chartPublishService)
            : base(logger, hubContext, eventBus, wsService, orderChannel, tradeWsService)
        {
            _positionManager = positionManager;
            _chartPublishService = chartPublishService;
            this.IsOrderEnabled = true;
        }

        protected override void OnKlineReceived(KlineMessage msg)
        {
            if (!msg.IsClosed) return;

            string symbol = msg.Symbol;

            // 1. 维护 1m 基础缓冲区 (用于策略判定与画图映射)
            var buffer1m = _1mBuffer.GetOrAdd(symbol, _ => new List<KlineMessage>());
            lock (buffer1m)
            {
                buffer1m.Add(msg);
                if (buffer1m.Count > 1500) buffer1m.RemoveAt(0); // 保存足够长以满足最新极值点的显示
            }

            // 2. 动态维护各个大周期 K 线并计算高低点
            foreach (var tf in _timeframes)
            {
                UpdateTimeframeBuffer(symbol, tf, msg);
            }

            if (buffer1m.Count < 2) return;

            // 3. 执行核心策略
            CheckStrategy(symbol, msg, buffer1m);
        }

        private void CheckStrategy(string symbol, KlineMessage current1m, List<KlineMessage> buffer1m)
        {
            // 防抖与仓位过滤
            if (_positionManager.HasActivePosition(symbol)) return;
            if (_lastTradeTime.TryGetValue(symbol, out var lastTime) && (DateTime.Now - lastTime).TotalMinutes < 5) return;

            var activeChannels = GenerateCurrentChannels(symbol, current1m.OpenTime);
            if (!activeChannels.Any()) return;

            KlineMessage prev1m = buffer1m[buffer1m.Count - 2];
            bool triggered = false;
            string triggerReason = "";
            bool isLong = false;
            decimal stopLoss = 0m;
            decimal takeProfit = 0m;
            ChannelCalc triggeredChannel = null;

            // 准备图表发布对象
            var chartItem = new ChartPublishItem
            {
                Symbol = symbol,
                StrategyName = "MultiTFAngleChannel",
                Klines = buffer1m.TakeLast(300).ToList() // 推送最近的 300 根 1m K线作为底图
            };

            foreach (var channel in activeChannels)
            {
                // 通道中线的实时价格
                decimal channelTop = channel.CurrentCenterPrice * (1 + _channelWidthPercent / 2);
                decimal channelBottom = channel.CurrentCenterPrice * (1 - _channelWidthPercent / 2);

                // --- 策略判定逻辑 ---

                // 1. 向上突破通道上轨 (做多)
                if (prev1m.Close <= channelTop && current1m.Close > channelTop)
                {
                    triggered = true;
                    isLong = true;
                    triggerReason = $"向上突破 {channel.Timeframe} 角度通道上轨";
                    stopLoss = channelTop * (1 - _stopLossPercent); 
                    takeProfit = current1m.Close * (1 + (_channelWidthPercent / 2)); // 止盈前通道高度一半
                    triggeredChannel = channel;
                    break;
                }
                
                // 2. 向下突破通道下轨 (做空)
                if (prev1m.Close >= channelBottom && current1m.Close < channelBottom)
                {
                    triggered = true;
                    isLong = false;
                    triggerReason = $"向下跌破 {channel.Timeframe} 角度通道下轨";
                    stopLoss = channelBottom * (1 + _stopLossPercent); 
                    takeProfit = current1m.Close * (1 - (_channelWidthPercent / 2));
                    triggeredChannel = channel;
                    break;
                }

                // 3. 触碰通道上轨后受阻回落 (回弹做空)
                if (current1m.High >= channelTop && current1m.Close < channelTop && current1m.Close < current1m.Open)
                {
                    triggered = true;
                    isLong = false;
                    triggerReason = $"触碰 {channel.Timeframe} 通道上轨受阻回落";
                    stopLoss = channelTop * (1 + _stopLossPercent);
                    takeProfit = channel.CurrentCenterPrice; // 止盈为通道中线
                    triggeredChannel = channel;
                    break;
                }

                // 4. 触碰通道下轨后受支撑反弹 (回弹做多)
                if (current1m.Low <= channelBottom && current1m.Close > channelBottom && current1m.Close > current1m.Open)
                {
                    triggered = true;
                    isLong = true;
                    triggerReason = $"触碰 {channel.Timeframe} 通道下轨受支撑反弹";
                    stopLoss = channelBottom * (1 - _stopLossPercent);
                    takeProfit = channel.CurrentCenterPrice;
                    triggeredChannel = channel;
                    break;
                }
            }

            if (triggered)
            {
                _lastTradeTime[symbol] = DateTime.Now;
                _logger.LogWarning($"🎯 [{symbol}] {triggerReason}！触发入场。SL: {stopLoss:F4}, TP: {takeProfit:F4}");

                // 向图表附加展示元素
                long chartStartTime = chartItem.Klines.First().OpenTime;
                foreach (var c in activeChannels)
                {
                    int idx1 = chartItem.Klines.FindIndex(k => k.OpenTime == c.AnchorTime);
                    if (idx1 < 0) idx1 = 0;
                    int idx2 = chartItem.Klines.Count - 1;

                    decimal price1 = c.AnchorPrice + ((chartItem.Klines[idx1].OpenTime - c.AnchorTime) / 60000m) * c.Slope;
                    decimal price2 = c.AnchorPrice + ((chartItem.Klines[idx2].OpenTime - c.AnchorTime) / 60000m) * c.Slope;
                    
                    // 利用 ChartPublishService 优化的垂直通道进行无变形绘制
                    chartItem.PriceChannels.Add((idx1, price1, idx2, price2, (float)_channelWidthPercent, SkiaSharp.SKColors.DarkBlue));
                }

                int currentKIndex = chartItem.Klines.Count - 1;

                // 标记开仓点位和原因
                chartItem.Points.Add((currentKIndex, current1m.Close, isLong ? SkiaSharp.SKColors.Green : SkiaSharp.SKColors.Red, 8f));
                chartItem.Texts.Add((triggerReason, Math.Max(0, currentKIndex - 30), current1m.High * 1.002m, SkiaSharp.SKColors.Yellow));

                // 标记止损线和文字 (红色)
                chartItem.Lines.Add((currentKIndex, stopLoss, currentKIndex + 20, stopLoss, SkiaSharp.SKColors.Red, 2f));
                chartItem.Texts.Add(($"SL: {stopLoss:F4}", currentKIndex + 2, stopLoss, SkiaSharp.SKColors.Red));

                // 标记止盈线和文字 (绿色)
                chartItem.Lines.Add((currentKIndex, takeProfit, currentKIndex + 20, takeProfit, SkiaSharp.SKColors.Green, 2f));
                chartItem.Texts.Add(($"TP: {takeProfit:F4}", currentKIndex + 2, takeProfit, SkiaSharp.SKColors.Green));

                // 在图表左上角标记触发的具体通道信息
                if (triggeredChannel != null)
                {
                    decimal maxHigh = chartItem.Klines.Max(k => k.High);
                    chartItem.Texts.Add(($"[通道信号] 周期: {triggeredChannel.Timeframe} | 斜率: {triggeredChannel.Slope:F6} | 波动率: {_channelWidthPercent:P}", 10, maxHigh, SkiaSharp.SKColors.Cyan));
                }

                // 异步发布图表
                _ = _chartPublishService.PublishChartAsync(chartItem);

                // 发送订单请求
                decimal tpPercent = Math.Abs(takeProfit - current1m.Close) / current1m.Close * 100m;
                decimal slPercent = Math.Abs(stopLoss - current1m.Close) / current1m.Close * 100m;

                _ = Task.Run(async () =>
                {
                    await PlaceOrderWithLeverageRiskAsync(
                        symbol, isLong, current1m.Close, tpPercent, 5.0m, 0.05m, slPercent, "MultiTFAngleChannel"
                    );
                });
            }
        }

        private void UpdateTimeframeBuffer(string symbol, string tf, KlineMessage msg1m)
        {
            var symbolData = _timeframeData.GetOrAdd(symbol, _ => new ConcurrentDictionary<string, TimeframeData>());
            var tfData = symbolData.GetOrAdd(tf, _ => new TimeframeData());

            long intervalMs = GetIntervalMs(tf);
            long bucketTime = msg1m.OpenTime - (msg1m.OpenTime % intervalMs);

            lock (tfData.Buffer)
            {
                var lastKline = tfData.Buffer.LastOrDefault();
                if (lastKline != null && lastKline.OpenTime == bucketTime)
                {
                    lastKline.High = Math.Max(lastKline.High, msg1m.High);
                    lastKline.Low = Math.Min(lastKline.Low, msg1m.Low);
                    lastKline.Close = msg1m.Close;
                    lastKline.Volume += msg1m.Volume;
                }
                else
                {
                    tfData.Buffer.Add(new KlineMessage
                    {
                        Symbol = msg1m.Symbol,
                        OpenTime = bucketTime,
                        High = msg1m.High,
                        Low = msg1m.Low,
                        Close = msg1m.Close,
                        Volume = msg1m.Volume,
                        IsClosed = true
                    });
                    if (tfData.Buffer.Count > 100) tfData.Buffer.RemoveAt(0); // 保留最新 100 根大周期 K 线
                }

                // 计算当前周期的最近高点和低点
                if (tfData.Buffer.Count > 10)
                {
                    var highs = tfData.Buffer.Select(k => k.High).ToList();
                    var lows = tfData.Buffer.Select(k => k.Low).ToList();
                    var (peaks, valleys) = PivotHelper.CalculatePeaks(highs, lows, 3, 3);

                    if (peaks.Any())
                    {
                        int latestPeakIdx = peaks.Last();
                        tfData.LatestPeak = tfData.Buffer[latestPeakIdx];
                    }
                    if (valleys.Any())
                    {
                        int latestValleyIdx = valleys.Last();
                        tfData.LatestValley = tfData.Buffer[latestValleyIdx];
                    }
                }
            }
        }

        private List<ChannelCalc> GenerateCurrentChannels(string symbol, long current1mTime)
        {
            var results = new List<ChannelCalc>();
            if (!_timeframeData.TryGetValue(symbol, out var symbolData)) return results;

            foreach (var kvp in symbolData)
            {
                string tf = kvp.Key;
                var data = kvp.Value;

                if (data.LatestPeak != null)
                {
                    foreach (var s in _slopes)
                    {
                        decimal passedMinutes = (current1mTime - data.LatestPeak.OpenTime) / 60000m;
                        if (passedMinutes < 0) continue;
                        
                        // 高点向下发散的压力通道
                        decimal centerPrice = data.LatestPeak.High - (s * data.LatestPeak.High * passedMinutes);
                        results.Add(new ChannelCalc
                        {
                            Timeframe = tf,
                            AnchorTime = data.LatestPeak.OpenTime,
                            AnchorPrice = data.LatestPeak.High,
                            Slope = -(s * data.LatestPeak.High), // 负数，向下
                            CurrentCenterPrice = centerPrice
                        });
                    }
                }

                if (data.LatestValley != null)
                {
                    foreach (var s in _slopes)
                    {
                        decimal passedMinutes = (current1mTime - data.LatestValley.OpenTime) / 60000m;
                        if (passedMinutes < 0) continue;
                        
                        // 低点向上发散的支撑通道
                        decimal centerPrice = data.LatestValley.Low + (s * data.LatestValley.Low * passedMinutes);
                        results.Add(new ChannelCalc
                        {
                            Timeframe = tf,
                            AnchorTime = data.LatestValley.OpenTime,
                            AnchorPrice = data.LatestValley.Low,
                            Slope = (s * data.LatestValley.Low), // 正数，向上
                            CurrentCenterPrice = centerPrice
                        });
                    }
                }
            }
            return results;
        }

        private long GetIntervalMs(string tf)
        {
            return tf switch
            {
                "15m" => 15 * 60 * 1000,
                "30m" => 30 * 60 * 1000,
                "1h" => 60 * 60 * 1000,
                "1d" => 24 * 60 * 60 * 1000,
                _ => 60 * 1000
            };
        }

        protected override async Task InitializeStrategyDataAsync(List<string> symbols)
        {
            foreach (var sym in symbols)
            {
                foreach (var tf in _timeframes)
                {
                    try
                    {
                        // 分别获取各个周期的历史数据用于初始化极值点
                        string json = await _wsService.GetHistoricalKlinesAsync(sym, tf, 50);
                        using var doc = JsonDocument.Parse(json);

                        var historyList = new List<KlineMessage>();
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            historyList.Add(new KlineMessage
                            {
                                Symbol = sym,
                                OpenTime = item[0].GetInt64(),
                                High = decimal.Parse(item[2].GetString()),
                                Low = decimal.Parse(item[3].GetString()),
                                Close = decimal.Parse(item[4].GetString()),
                                Volume = decimal.Parse(item[5].GetString()),
                                IsClosed = true
                            });
                        }

                        var symbolData = _timeframeData.GetOrAdd(sym, _ => new ConcurrentDictionary<string, TimeframeData>());
                        var tfData = symbolData.GetOrAdd(tf, _ => new TimeframeData());
                        
                        tfData.Buffer = historyList;

                        var highs = tfData.Buffer.Select(k => k.High).ToList();
                        var lows = tfData.Buffer.Select(k => k.Low).ToList();
                        var (peaks, valleys) = PivotHelper.CalculatePeaks(highs, lows, 3, 3);

                        if (peaks.Any()) tfData.LatestPeak = tfData.Buffer[peaks.Last()];
                        if (valleys.Any()) tfData.LatestValley = tfData.Buffer[valleys.Last()];

                        _logger.LogInformation($"✅ {sym} {tf} 极值点初始化完成。");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"❌ {sym} {tf} 初始化失败: {ex.Message}");
                    }
                }
            }
        }
    }
}
