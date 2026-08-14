using Binance.Net.Enums;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace WinFormsApp2
{
    public partial class Form1 : Form
    {
        private readonly MarketReplayer _replayer = new MarketReplayer();
        private readonly List<Kline> _replayKlines = new List<Kline>();
        private readonly ConcurrentQueue<string> _logBufferQueue = new ConcurrentQueue<string>();
        private readonly System.Windows.Forms.Timer _uiRenderTimer = new System.Windows.Forms.Timer();

        private string _currentSymbol = "BTCUSDT";
        private bool _needChartRefresh = false;
        private string _chartTitle = "实时行情 / 数据回放 (ScottPlot 5)";

        public Form1()
        {
            InitializeComponent();
            InitControls();
            InitReplayer();
            InitUiTimer();
            InitializePlot();
            AppendLog("系统初始化完成。准备就绪。");
        }

        private void InitControls()
        {
            cmbKlineInterval.Items.Clear();
            cmbKlineInterval.Items.Add(new { Text = "1分钟 (OneMinute)", Value = KlineInterval.OneMinute });
            cmbKlineInterval.Items.Add(new { Text = "15分钟 (FifteenMinutes)", Value = KlineInterval.FifteenMinutes });
            cmbKlineInterval.Items.Add(new { Text = "1小时 (OneHour)", Value = KlineInterval.OneHour });
            cmbKlineInterval.Items.Add(new { Text = "1天 (OneDay)", Value = KlineInterval.OneDay });
            cmbKlineInterval.DisplayMember = "Text";
            cmbKlineInterval.ValueMember = "Value";
            cmbKlineInterval.SelectedIndex = 0; // 默认 1分钟

            dtpStartDate.Value = DateTime.Today.AddDays(-1);
            dtpEndDate.Value = DateTime.Today;
        }

        private void InitReplayer()
        {
            _replayer.OnKlinePushed += Replayer_OnKlinePushed;
            _replayer.OnTickPushed += Replayer_OnTickPushed;
            _replayer.OnPlaybackCompleted += Replayer_OnPlaybackCompleted;
            _replayer.OnLog += msg => EnqueueLog($"[回放引擎] {msg}");
        }

        private void InitUiTimer()
        {
            _uiRenderTimer.Interval = 100;
            _uiRenderTimer.Tick += UiRenderTimer_Tick;
            _uiRenderTimer.Start();
        }

        private void InitializePlot()
        {
            double[] ys = ScottPlot.Generate.Sin(50);
            formsPlot1.Plot.Clear();
            formsPlot1.Plot.Grid.IsVisible = false; // 隐藏网格线
            formsPlot1.Plot.Add.Signal(ys);
            formsPlot1.Plot.Title("实时行情 / 数据回放 (ScottPlot 5)");
            formsPlot1.Plot.XLabel("序列 (Frame)");
            formsPlot1.Plot.YLabel("价格 (Price)");
            formsPlot1.Refresh();
        }

        /// <summary>
        /// 无锁线程安全的日志入队方法 (非阻塞，0 UI 延迟)
        /// </summary>
        public void EnqueueLog(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            _logBufferQueue.Enqueue($"[{timestamp}] {message}");
        }

        public void AppendLog(string message)
        {
            EnqueueLog(message);
        }

        private void UiRenderTimer_Tick(object sender, EventArgs e)
        {
            // 1. 批量渲染日志文本
            if (!_logBufferQueue.IsEmpty)
            {
                StringBuilder sb = new StringBuilder();
                int processedCount = 0;
                while (_logBufferQueue.TryDequeue(out string logLine) && processedCount < 200)
                {
                    sb.AppendLine(logLine);
                    processedCount++;
                }

                if (sb.Length > 0)
                {
                    rtbLog.AppendText(sb.ToString());

                    if (rtbLog.TextLength > 300000)
                    {
                        rtbLog.Text = rtbLog.Text.Substring(rtbLog.TextLength - 100000);
                    }

                    rtbLog.SelectionStart = rtbLog.TextLength;
                    rtbLog.ScrollToCaret();
                }
            }

            // 2. 批量渲染 ScottPlot 图表、枢轴高低点及延长趋势线 (限制最多展示最新 1000 根 K 线)
            if (_needChartRefresh)
            {
                _needChartRefresh = false;
                Kline[] fullArray;
                lock (_replayKlines)
                {
                    fullArray = _replayKlines.ToArray();
                }

                if (fullArray.Length > 0)
                {
                    // 限制图表最多展示最新 1000 根 K 线信息
                    const int maxDisplayKlines = 1000;
                    Kline[] klineArray = fullArray.Length > maxDisplayKlines 
                        ? fullArray.Skip(fullArray.Length - maxDisplayKlines).ToArray() 
                        : fullArray;

                    double[] currentPrices = klineArray.Select(k => (double)k.ClosePrice).ToArray();

                    formsPlot1.Plot.Clear();
                    formsPlot1.Plot.Grid.IsVisible = false; // 隐藏/关闭图表网格线

                    // A. 绘制价格主信号曲线
                    formsPlot1.Plot.Add.Signal(currentPrices);
                    formsPlot1.Plot.Title(_chartTitle);

                    // B. 在最多 1000 根 K 线范围内计算并标注相对高低点与延长趋势线 (跨度为 3)
                    if (klineArray.Length >= 7)
                    {
                        var pivots = PivotHelper.CalculatePivotPoints(klineArray, leftBars: 3, rightBars: 3);

                        // 相对高点 (使用 HighPrice，红色标记)
                        var highs = pivots.Where(p => p.Type == PivotType.High).ToList();
                        if (highs.Count > 0)
                        {
                            double[] highXs = highs.Select(p => (double)p.Index).ToArray();
                            double[] highYs = highs.Select(p => (double)p.Price).ToArray();
                            var spHigh = formsPlot1.Plot.Add.ScatterPoints(highXs, highYs);
                            spHigh.Color = ScottPlot.Colors.Red;
                            spHigh.MarkerSize = 3;
                        }

                        // 相对低点 (使用 LowPrice，绿色标记)
                        var lows = pivots.Where(p => p.Type == PivotType.Low).ToList();
                        if (lows.Count > 0)
                        {
                            double[] lowXs = lows.Select(p => (double)p.Index).ToArray();
                            double[] lowYs = lows.Select(p => (double)p.Price).ToArray();
                            var spLow = formsPlot1.Plot.Add.ScatterPoints(lowXs, lowYs);
                            spLow.Color = ScottPlot.Colors.LimeGreen;
                            spLow.MarkerSize = 3;
                        }

                        // C. 计算并在图表上绘制延伸趋势线 (高点阻力线显示淡红 #FF8080，低点支撑线显示淡绿 #80FF80)
                        var trendLines = TrendLineHelper.GenerateTrendLinesFromPivots(klineArray, pivots);
                        
                        var displayLines = trendLines
                            .OrderByDescending(tl => tl.LineX1X2)
                            .Take(12);

                        ScottPlot.Color lightRed = ScottPlot.Color.FromHex("#FF8080");   // 淡红
                        ScottPlot.Color lightGreen = ScottPlot.Color.FromHex("#80FF80"); // 淡绿

                        foreach (var tl in displayLines)
                        {
                            double x1 = tl.X1;
                            double y1 = (double)tl.Y1;
                            
                            // 趋势线向右延长至当前 K 线窗口的最新右侧边界
                            double x2 = klineArray.Length - 1;
                            double y2 = (double)tl.GetPriceAt((int)x2);

                            var linePlot = formsPlot1.Plot.Add.Line(x1, y1, x2, y2);
                            linePlot.Color = tl.Type == PivotType.High ? lightRed : lightGreen;
                            linePlot.LineWidth = 0.8f;
                        }
                    }

                    formsPlot1.Refresh();
                }
            }
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            _currentSymbol = txtSymbol.Text.Trim();
            if (string.IsNullOrEmpty(_currentSymbol))
            {
                MessageBox.Show("请输入交易对名称 (如 BTCUSDT)", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            dynamic selectedIntervalObj = cmbKlineInterval.SelectedItem;
            KlineInterval interval = (KlineInterval)selectedIntervalObj.Value;

            DateTime startDate = dtpStartDate.Value.Date;
            DateTime endDate = dtpEndDate.Value.Date;
            int intervalMs = (int)numInterval.Value;
            bool enableTickPush = chkEnableTickPush.Checked;

            AppendLog($"准备加载 [{_currentSymbol}] [{interval}] 日期范围 [{startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}] 数据以启动回放...");

            try
            {
                // 1. 多线程并发装载 K线数据
                Kline[] klines = await MultiThreadDownloader.DownloadKlinesParallelAsync(
                    _currentSymbol,
                    interval,
                    startDate,
                    endDate,
                    maxDegreeOfParallelism: 4,
                    logger: AppendLog);

                // 2. 如果开启了 Tick 细粒度推送，并发装载 1-小时切片全量 Tick 数据
                Tick[] ticks = Array.Empty<Tick>();
                if (enableTickPush)
                {
                    ticks = await MultiThreadDownloader.DownloadTicksInSlicesParallelAsync(
                        _currentSymbol,
                        startDate,
                        endDate,
                        chunkHours: 1,
                        maxDegreeOfParallelism: 4,
                        logger: AppendLog);
                }

                if (klines.Length == 0)
                {
                    AppendLog("未装载到任何 K线数据，无法开始回放。");
                    return;
                }

                // 3. 使用 PivotHelper (跨度=3) 与 TrendLineHelper 分析 1000 根范围内的高低点与延伸趋势线
                var displayKlinesSample = klines.Length > 1000 ? klines.Skip(klines.Length - 1000).ToArray() : klines;
                var pivots = PivotHelper.CalculatePivotPoints(displayKlinesSample, leftBars: 3, rightBars: 3);
                int highCount = pivots.Count(p => p.Type == PivotType.High);
                int lowCount = pivots.Count(p => p.Type == PivotType.Low);
                AppendLog($"[Pivot 枢轴计算] 在 1000 根 K 线范围内 (跨度=3) 分析完成: 相对高点 (HighPrice, 红色) {highCount} 个，相对低点 (LowPrice, 绿色) {lowCount} 个。");

                var trendLines = TrendLineHelper.GenerateTrendLinesFromPivots(displayKlinesSample, pivots);
                AppendLog($"[TrendLine 趋势线生成] 在 1000 根 K 线范围内生成 {trendLines.Count} 条延伸趋势线 (高点淡红 #FF8080 / 低点淡绿 #80FF80) 已绘制于图表上。示例分析:");
                foreach (var tl in trendLines.Take(3))
                {
                    AppendLog($"   ├─ [{tl.Type}趋势线] 归一化斜率K: {tl.K:F4}%/bar | line_x1_x2: {tl.LineX1X2} | line_age: {tl.LineAge} | line_extension_range: {tl.LineExtensionRange}");
                }

                // 4. 复位图表并启动回放引擎
                lock (_replayKlines)
                {
                    _replayKlines.Clear();
                }

                formsPlot1.Plot.Clear();
                formsPlot1.Plot.Grid.IsVisible = false; // 隐藏网格
                formsPlot1.Plot.Title($"[{_currentSymbol}] 行情回放准备完毕 (共 {klines.Length} 帧，图表显示最新1000帧)");
                formsPlot1.Refresh();

                AppendLog($"▶ 启动行情回放 | K线总帧数: {klines.Length} | 图表限制展示: 最新 1000 帧 | Tick推送: {(enableTickPush ? "开启" : "关闭")} | 步进间隔: {intervalMs}ms");
                _replayer.StartPlayback(klines, ticks, enableTickPush, intervalMs);
            }
            catch (Exception ex)
            {
                AppendLog($"加载回放数据异常: {ex.Message}");
            }
        }

        private void btnPause_Click(object sender, EventArgs e)
        {
            if (_replayer.State == ReplayState.Playing)
            {
                _replayer.PausePlayback();
            }
            else if (_replayer.State == ReplayState.Paused)
            {
                _replayer.ResumePlayback();
            }
            else
            {
                AppendLog("当前未处于播放状态。");
            }
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            _replayer.StopPlayback();
        }

        private void btnClearLog_Click(object sender, EventArgs e)
        {
            rtbLog.Clear();
            AppendLog("日志已清空。");
        }

        #region 回放事件响应 (无锁入队，无卡顿渲染)

        private void Replayer_OnKlinePushed(Kline kline, int current, int total)
        {
            lock (_replayKlines)
            {
                _replayKlines.Add(kline);
            }

            _chartTitle = $"[{_currentSymbol}] 动态回放中 ({current}/{total}) - {kline.OpenTime:yyyy-MM-dd HH:mm:ss}";
            _needChartRefresh = true;

            EnqueueLog($"[K线帧 {current}/{total}] {kline.OpenTime:yyyy-MM-dd HH:mm:ss} | 开:{kline.OpenPrice} 高:{kline.HighPrice} 低:{kline.LowPrice} 收:{kline.ClosePrice} 量:{kline.Volume}");
        }

        private void Replayer_OnTickPushed(Tick tick)
        {
            EnqueueLog($"   └─ [Tick 细粒度推送] {tick.Time:HH:mm:ss.fff} | 成交价:{tick.LastPrice} 成交量:{tick.Volume}");
        }

        private void Replayer_OnPlaybackCompleted()
        {
            EnqueueLog("🎉 行情回放播放完毕！");
        }

        #endregion
    }
}
