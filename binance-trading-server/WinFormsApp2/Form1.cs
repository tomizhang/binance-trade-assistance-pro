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
        private UserSettings _userSettings = new UserSettings();

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
            AppendLog("系统初始化完成。自动加载历史参数配置成功。");
        }

        private void InitControls()
        {
            // 1. 初始化下拉选项
            cmbKlineInterval.Items.Clear();
            cmbKlineInterval.Items.Add(new { Text = "1分钟 (OneMinute)", Value = KlineInterval.OneMinute });
            cmbKlineInterval.Items.Add(new { Text = "15分钟 (FifteenMinutes)", Value = KlineInterval.FifteenMinutes });
            cmbKlineInterval.Items.Add(new { Text = "1小时 (OneHour)", Value = KlineInterval.OneHour });
            cmbKlineInterval.Items.Add(new { Text = "1天 (OneDay)", Value = KlineInterval.OneDay });
            cmbKlineInterval.DisplayMember = "Text";
            cmbKlineInterval.ValueMember = "Value";

            // 2. 读取并应用持久化的用户右侧参数设置
            _userSettings = UserSettings.Load();

            txtSymbol.Text = string.IsNullOrWhiteSpace(_userSettings.Symbol) ? "BTCUSDT" : _userSettings.Symbol;

            int matchedIndex = 0;
            for (int i = 0; i < cmbKlineInterval.Items.Count; i++)
            {
                dynamic item = cmbKlineInterval.Items[i];
                if ((KlineInterval)item.Value == _userSettings.KlineInterval)
                {
                    matchedIndex = i;
                    break;
                }
            }
            cmbKlineInterval.SelectedIndex = matchedIndex;

            if (_userSettings.StartDate > DateTime.MinValue && _userSettings.StartDate < DateTime.MaxValue)
            {
                dtpStartDate.Value = _userSettings.StartDate;
            }
            else
            {
                dtpStartDate.Value = DateTime.Today.AddDays(-1);
            }

            if (_userSettings.EndDate > DateTime.MinValue && _userSettings.EndDate < DateTime.MaxValue)
            {
                dtpEndDate.Value = _userSettings.EndDate;
            }
            else
            {
                dtpEndDate.Value = DateTime.Today;
            }

            numInterval.Value = Math.Max(numInterval.Minimum, Math.Min(numInterval.Maximum, _userSettings.PlaybackIntervalMs));
            chkEnableTickPush.Checked = _userSettings.EnableTickPush;
            chkAutoFitPrice.Checked = _userSettings.AutoFitPrice;

            // 3. 绑定参数控件变动自动保存逻辑
            txtSymbol.TextChanged += (s, e) => SaveCurrentSettings();
            cmbKlineInterval.SelectedIndexChanged += (s, e) => SaveCurrentSettings();
            dtpStartDate.ValueChanged += (s, e) => SaveCurrentSettings();
            dtpEndDate.ValueChanged += (s, e) => SaveCurrentSettings();
            numInterval.ValueChanged += (s, e) => SaveCurrentSettings();
            chkEnableTickPush.CheckedChanged += (s, e) => SaveCurrentSettings();
            chkAutoFitPrice.CheckedChanged += (s, e) => { SaveCurrentSettings(); _needChartRefresh = true; };
            FormClosing += (s, e) => SaveCurrentSettings();
        }

        private void SaveCurrentSettings()
        {
            try
            {
                _userSettings.Symbol = txtSymbol.Text.Trim();
                if (cmbKlineInterval.SelectedItem != null)
                {
                    dynamic selectedIntervalObj = cmbKlineInterval.SelectedItem;
                    _userSettings.KlineInterval = (KlineInterval)selectedIntervalObj.Value;
                }
                _userSettings.StartDate = dtpStartDate.Value.Date;
                _userSettings.EndDate = dtpEndDate.Value.Date;
                _userSettings.PlaybackIntervalMs = (int)numInterval.Value;
                _userSettings.EnableTickPush = chkEnableTickPush.Checked;
                _userSettings.AutoFitPrice = chkAutoFitPrice.Checked;
                _userSettings.Save();
            }
            catch
            {
                // 忽略配置保存时的偶发异常
            }
        }

        private void InitReplayer()
        {
            _replayer.OnKlinePushed += Replayer_OnKlinePushed;
            _replayer.OnStepBackward += Replayer_OnStepBackward;
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
                        var trendLines = TrendLineHelper.GenerateTrendLinesFromPivots(klineArray, pivots, filterPenetrated: true);
                        
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

                    // D. 视口自动缩放/聚焦最新价格附近 (当勾选 chkAutoFitPrice 时)
                    if (chkAutoFitPrice.Checked && klineArray.Length > 0)
                    {
                        int sampleSize = Math.Min(klineArray.Length, 80);
                        var recentKlines = klineArray.Skip(klineArray.Length - sampleSize).ToArray();
                        double minPrice = (double)recentKlines.Min(k => k.LowPrice);
                        double maxPrice = (double)recentKlines.Max(k => k.HighPrice);
                        double margin = (maxPrice - minPrice) * 0.12;
                        if (margin == 0) margin = maxPrice * 0.01;
                        if (margin == 0) margin = 1.0;

                        formsPlot1.Plot.Axes.SetLimitsY(minPrice - margin, maxPrice + margin);
                    }

                    formsPlot1.Refresh();
                }
            }
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            SaveCurrentSettings(); // 点击开始回放时主动同步持久化设置

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

                var trendLines = TrendLineHelper.GenerateTrendLinesFromPivots(displayKlinesSample, pivots, filterPenetrated: true);
                AppendLog($"[TrendLine 趋势线交互] 已自动删除被后续 K 线穿透破位的趋势线。最终保留未破位有效趋势线 {trendLines.Count} 条 (淡红 #FF8080 / 淡绿 #80FF80) 已绘制于图表。示例分析:");
                foreach (var tl in trendLines.Take(3))
                {
                    AppendLog($"   ├─ [未破位{tl.Type}趋势线] 归一化斜率K: {tl.K:F4}%/bar | line_x1_x2: {tl.LineX1X2} | line_age: {tl.LineAge} | line_extension_range: {tl.LineExtensionRange}");
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

        private void btnStepForward_Click(object sender, EventArgs e)
        {
            _replayer.StepForward();
        }

        private void btnStepBackward_Click(object sender, EventArgs e)
        {
            _replayer.StepBackward();
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

            // 已注释高频 K线回放推流日志，避免刷屏
            // EnqueueLog($"[► 单步/播放向前 {current}/{total}] {kline.OpenTime:yyyy-MM-dd HH:mm:ss} | 开:{kline.OpenPrice} 高:{kline.HighPrice} 低:{kline.LowPrice} 收:{kline.ClosePrice} 量:{kline.Volume}");
        }

        private void Replayer_OnStepBackward(Kline[] subKlines, int current, int total)
        {
            lock (_replayKlines)
            {
                _replayKlines.Clear();
                _replayKlines.AddRange(subKlines);
            }

            var lastTime = subKlines.Length > 0 ? subKlines[subKlines.Length - 1].OpenTime.ToString("yyyy-MM-dd HH:mm:ss") : "";
            _chartTitle = $"[{_currentSymbol}] 单步向后 ({current}/{total}) - {lastTime}";
            _needChartRefresh = true;

            // 已注释单步向后频繁日志，避免刷屏
            // EnqueueLog($"[◄ 单步向后 {current}/{total}] 已回退至 {lastTime}");
        }

        private void Replayer_OnTickPushed(Tick tick)
        {
            // 已注释高频 Tick 细粒度推送日志，避免刷屏
            // EnqueueLog($"   └─ [Tick 细粒度推送] {tick.Time:HH:mm:ss.fff} | 成交价:{tick.LastPrice} 成交量:{tick.Volume}");
        }

        private void Replayer_OnPlaybackCompleted()
        {
            EnqueueLog("🎉 行情回放播放完毕！");
        }

        #endregion
    }
}
