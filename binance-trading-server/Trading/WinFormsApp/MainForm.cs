using Binance.Net.Enums;
using Common;
using Common.Cursor;
using Common.Helper;
using Common.Interfaces;
using Common.Models;
using Common.Providers;
using Common.Strategies;
using ScottPlot;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace WinFormsApp
{
    public partial class MainForm : Form
    {
        private readonly DuckDbHistoricalDataProvider _dataProvider;
        private ICursor<MarketKline>? _klineCursor;
        private readonly System.Windows.Forms.Timer _replayTimer;

        // 回放已呈现的数据点列表
        private readonly List<DateTime> _replayedDates = new List<DateTime>();
        private readonly List<double> _replayedCloses = new List<double>();
        private readonly List<MarketKline> _replayedKlines = new List<MarketKline>();

        // 统一淡蓝色主线条颜色 (Light Blue, RGB: 100, 181, 246)
        private static readonly ScottPlot.Color LightBlueColor = new ScottPlot.Color(100, 181, 246);
        private static readonly ScottPlot.Color MarkerHighlightColor = new ScottPlot.Color(41, 128, 185);

        // 🌟 极值点与趋势线样式配色 (高低点及其生成的趋势线严格颜色区分)
        // 1. 高点与高点生成的阻力趋势线：鲜艳珊瑚红 (Red / Coral)
        private static readonly ScottPlot.Color PeakMarkerColor = new ScottPlot.Color(255, 82, 82);
        private static readonly ScottPlot.Color ResistanceLineColor = new ScottPlot.Color(255, 82, 82);

        // 2. 低点与低点生成的支撑趋势线：鲜艳青翠绿 (Spring Green / Emerald)
        private static readonly ScottPlot.Color ValleyMarkerColor = new ScottPlot.Color(0, 230, 118);
        private static readonly ScottPlot.Color SupportLineColor = new ScottPlot.Color(0, 230, 118);

        // 3. 交易信号标记颜色
        private static readonly ScottPlot.Color SignalBuyColor = new ScottPlot.Color(0, 230, 118);       // 买入信号 (绿)
        private static readonly ScottPlot.Color SignalSellColor = new ScottPlot.Color(255, 82, 82);      // 卖出信号 (红)

        // 趋势线策略实例
        private TrendLineReboundStrategy? _trendLineStrategy;
        private readonly List<StrategySignal> _triggeredSignals = new List<StrategySignal>();

        private string _currentSymbol = "BTCUSDT";
        private string _currentInterval = "30m";
        private bool _isPlaying = false;
        private bool _isFirstRender = true;

        public MainForm()
        {
            InitializeComponent();

            _dataProvider = new DuckDbHistoricalDataProvider(cursorBufferCapacity: 100);

            _replayTimer = new System.Windows.Forms.Timer();
            _replayTimer.Interval = (int)numSpeed.Value;
            _replayTimer.Tick += ReplayTimer_Tick;

            // 默认日期与周期初始化
            cboInterval.SelectedIndex = 4; // 30m
            dtpStartDate.Value = new DateTime(2026, 1, 1);
            dtpEndDate.Value = new DateTime(2026, 1, 5);

            SetupChartStyle();
            InitializeStrategy();

            AppendLog("系统初始化就绪。统一时间标准: UTC+0");
            AppendLog($"数据根目录: {Config.GetRootPath()}");
        }

        private void InitializeStrategy()
        {
            _trendLineStrategy?.Unbind();

            // 初始化趋势线策略 (跨度 > 40, 寿命 > 4, 5-Tick 回弹, 穿透即删除)
            _trendLineStrategy = new TrendLineReboundStrategy(
                symbol: _currentSymbol,
                interval: _currentInterval,
                minLineX1X2: 20, // 视窗便于在短区间快速观察
                minLineAge: 3,
                reboundTicksWindow: 5);

            _trendLineStrategy.IsEnabled = chkEnableStrategy.Checked;

            _trendLineStrategy.OnSignal += signal =>
            {
                lock (_triggeredSignals)
                {
                    _triggeredSignals.Add(signal);
                }
                AppendLog($"🔥 [策略信号触发] {signal.Type} | 价格: {signal.Price:F2} | 原因: {signal.Reason}");
            };

            _trendLineStrategy.OnLog += logMsg =>
            {
                AppendLog($"📝 {logMsg}");
            };
        }

        private void SetupChartStyle()
        {
            formsPlot1.Plot.Clear();
            formsPlot1.Plot.Axes.DateTimeTicksBottom();
            formsPlot1.Plot.Title("Binance 行情回放 - 趋势线与高低极值点");
            formsPlot1.Plot.XLabel("时间 (UTC+0)");
            formsPlot1.Plot.YLabel("价格 (USDT)");
            formsPlot1.Plot.Axes.AutoScale();
            formsPlot1.Refresh();
        }

        #region 日志输出

        public void AppendLog(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendLog), message);
                return;
            }

            string timestamp = DateTime.UtcNow.ToUtc0String();
            string line = $"[{timestamp}] {message}\r\n";

            if (txtLogs.TextLength > 50000)
            {
                txtLogs.Clear();
            }

            txtLogs.AppendText(line);

            if (chkAutoScroll.Checked)
            {
                txtLogs.SelectionStart = txtLogs.TextLength;
                txtLogs.ScrollToCaret();
            }
        }

        private void btnClearLogs_Click(object sender, EventArgs e)
        {
            txtLogs.Clear();
        }

        #endregion

        #region 数据加载

        private void btnLoadData_Click(object sender, EventArgs e)
        {
            StopReplay();

            _currentSymbol = txtSymbol.Text.Trim().ToUpper();
            _currentInterval = cboInterval.SelectedItem?.ToString() ?? "30m";

            DateTime startUtc = DateTime.SpecifyKind(dtpStartDate.Value.Date, DateTimeKind.Utc);
            DateTime endUtc = DateTime.SpecifyKind(dtpEndDate.Value.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Utc);

            if (startUtc > endUtc)
            {
                MessageBox.Show("开始日期不能晚于结束日期！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            AppendLog($"正在加载历史数据: {_currentSymbol} {_currentInterval} ({startUtc.ToUtc0String()} 至 {endUtc.ToUtc0String()})...");

            Cursor = Cursors.WaitCursor;
            try
            {
                _klineCursor = _dataProvider.GetKlineCursor(_currentSymbol, _currentInterval, startUtc, endUtc);

                if (_klineCursor.TotalCount == 0)
                {
                    AppendLog($"[提示] 本地目录未检索到对应周期的真实数据文件。");
                    var result = MessageBox.Show(
                        $"在本地数据目录未找到 [{_currentSymbol} {_currentInterval}] 从 {startUtc.ToUtc0String()} 至 {endUtc.ToUtc0String()} 的历史文件。\n\n是否自动生成该区间的模拟真实 K 线数据，以便立即体验回放与趋势线分析？",
                        "未找到本地数据",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (result == DialogResult.Yes)
                    {
                        var mockKlines = GenerateMockKlines(_currentSymbol, _currentInterval, startUtc, endUtc);
                        _klineCursor = new MarketDataCursor<MarketKline>(mockKlines, bufferCapacity: 100);
                        AppendLog($"[仿真数据] 已成功生成 {_klineCursor.TotalCount} 根模拟 K 线用于回放测试。");
                    }
                    else
                    {
                        ResetReplayState();
                        return;
                    }
                }
                else
                {
                    AppendLog($"[加载成功] 通过 DuckDB 成功读取 {_klineCursor.TotalCount} 根历史 K 线数据。");
                }

                InitializeStrategy();
                ResetReplayState();
                MessageBox.Show($"成功加载 {_klineCursor.TotalCount} 根 K 线数据！点击【▶ 开始回放】即可开始播放。", "加载成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendLog($"[错误] 加载数据失败: {ex.Message}");
                MessageBox.Show($"加载数据时发生错误: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        #endregion

        #region 回放控制逻辑

        private void btnStartPause_Click(object sender, EventArgs e)
        {
            if (_klineCursor == null || _klineCursor.TotalCount == 0)
            {
                MessageBox.Show("请先点击【加载/检索历史数据】！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_isPlaying)
            {
                PauseReplay();
            }
            else
            {
                StartReplay();
            }
        }

        private void StartReplay()
        {
            _isPlaying = true;
            btnStartPause.Text = "⏸ 暂停回放";
            btnStartPause.BackColor = System.Drawing.Color.FromArgb(230, 126, 34);
            _replayTimer.Interval = (int)numSpeed.Value;
            _replayTimer.Start();
            AppendLog($"▶ 开始行情回放 (间隔: {_replayTimer.Interval} ms/步)...");
        }

        private void PauseReplay()
        {
            _isPlaying = false;
            btnStartPause.Text = "▶ 开始回放";
            btnStartPause.BackColor = System.Drawing.Color.FromArgb(52, 152, 219);
            _replayTimer.Stop();
        }

        private void StopReplay()
        {
            PauseReplay();
        }

        private void ReplayTimer_Tick(object? sender, EventArgs e)
        {
            if (!StepForward(isAutoReplay: true))
            {
                PauseReplay();
                AppendLog($"[回放结束] 全部 {_klineCursor?.TotalCount ?? 0} 根 K 线数据回放完毕。");
                MessageBox.Show("数据回放已到达末尾！", "回放完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void btnStepNext_Click(object sender, EventArgs e)
        {
            PauseReplay();
            if (_klineCursor == null) return;
            StepForward(isAutoReplay: false);
        }

        private void btnStepPrev_Click(object sender, EventArgs e)
        {
            PauseReplay();
            if (_klineCursor == null) return;
            StepBackward();
        }

        private void btnReset_Click(object sender, EventArgs e)
        {
            ResetReplayState();
            AppendLog("⏹ 回放状态与图表已重置。");
        }

        private void numSpeed_ValueChanged(object sender, EventArgs e)
        {
            _replayTimer.Interval = (int)numSpeed.Value;
        }

        private void chkLayer_CheckedChanged(object sender, EventArgs e)
        {
            if (_replayedKlines.Count > 0)
            {
                UpdateChartAndLabels(_replayedKlines[^1]);
            }
        }

        private void chkStrategy_CheckedChanged(object sender, EventArgs e)
        {
            if (_trendLineStrategy != null)
            {
                _trendLineStrategy.IsEnabled = chkEnableStrategy.Checked;
                AppendLog($"[策略配置] 趋势线策略已 {(chkEnableStrategy.Checked ? "启用" : "禁用")}");
            }
        }

        private void chkAutoScale_CheckedChanged(object sender, EventArgs e)
        {
            if (chkAutoScale.Checked && _replayedKlines.Count > 0)
            {
                formsPlot1.Plot.Axes.AutoScale();
                formsPlot1.Refresh();
            }
        }

        private void btnResetView_Click(object sender, EventArgs e)
        {
            formsPlot1.Plot.Axes.AutoScale();
            formsPlot1.Refresh();
            AppendLog("🔍 图表视窗已自适应重置全景。");
        }

        #endregion

        #region 步进与图表渲染 (包含高低极值点、趋势线与策略信号)

        private bool StepForward(bool isAutoReplay = false)
        {
            if (_klineCursor == null) return false;

            if (_klineCursor.MoveNext())
            {
                var kline = _klineCursor.Current;
                _replayedDates.Add(kline.OpenTime);
                _replayedCloses.Add((double)kline.Close);
                _replayedKlines.Add(kline);

                // 1. 将 K 线与微观模拟 Tick 注入策略
                if (_trendLineStrategy != null && chkEnableStrategy.Checked)
                {
                    _trendLineStrategy.OnKlineUpdate(kline);

                    // 模拟生成 1 笔收盘 Tick 触发 Tick 级别微观检测
                    var tick = new MarketTick
                    {
                        Symbol = kline.Symbol,
                        Time = kline.CloseTime,
                        Price = kline.Close,
                        Quantity = kline.Volume > 0 ? kline.Volume / 10m : 1m
                    };
                    _trendLineStrategy.OnTickUpdate(tick);
                }

                UpdateChartAndLabels(kline);

                if (!isAutoReplay || _klineCursor.CurrentIndex % 10 == 0 || _klineCursor.CurrentIndex == _klineCursor.TotalCount - 1)
                {
                    AppendLog($"[推进 { _klineCursor.CurrentIndex + 1}/{_klineCursor.TotalCount}] {kline.FormattedOpenTime} | C:{kline.Close:F2} | 缓存:{_klineCursor.GetBuffer().Count}/100 | 存活趋势线:{_trendLineStrategy?.ActiveLinesCount ?? 0}");
                }

                return true;
            }

            return false;
        }

        private bool StepBackward()
        {
            if (_klineCursor == null || !_klineCursor.HasPrevious) return false;

            if (_klineCursor.MovePrevious())
            {
                if (_replayedDates.Count > 0)
                {
                    _replayedDates.RemoveAt(_replayedDates.Count - 1);
                    _replayedCloses.RemoveAt(_replayedCloses.Count - 1);
                    _replayedKlines.RemoveAt(_replayedKlines.Count - 1);
                }

                // 策略状态重置并按当前历史重新喂入
                ReplayStrategyToCurrent();

                var kline = _klineCursor.Current;
                UpdateChartAndLabels(kline);
                AppendLog($"[回退 { _klineCursor.CurrentIndex + 1}/{_klineCursor.TotalCount}] {kline.FormattedOpenTime} | C:{kline.Close:F2} | 缓存:{_klineCursor.GetBuffer().Count}/100");
                return true;
            }

            return false;
        }

        private void ReplayStrategyToCurrent()
        {
            if (_trendLineStrategy == null) return;

            _trendLineStrategy.Reset();
            lock (_triggeredSignals)
            {
                _triggeredSignals.Clear();
            }

            foreach (var k in _replayedKlines)
            {
                _trendLineStrategy.OnKlineUpdate(k);
            }
        }

        private void UpdateChartAndLabels(MarketKline current)
        {
            // 1. 更新右侧面板状态信息
            lblProgress.Text = $"进度: {_klineCursor!.CurrentIndex + 1} / {_klineCursor.TotalCount}";
            lblTime.Text = $"时间: {current.FormattedOpenTime}";
            lblPrice.Text = $"最新收盘价: {current.Close:F2} USDT";
            lblHighLow.Text = $"开/高/低: {current.Open:F2} / {current.High:F2} / {current.Low:F2}";
            lblVolume.Text = $"成交量: {current.Volume:F2}";
            lblBuffer.Text = $"100条缓存: {_klineCursor.GetBuffer().Count} / 100";
            lblActiveLines.Text = $"监控中存活趋势线: {_trendLineStrategy?.ActiveLinesCount ?? 0} 条";

            // 2. 清空并重新构建 ScottPlot 图表
            formsPlot1.Plot.Clear();
            formsPlot1.Plot.Axes.DateTimeTicksBottom();
            formsPlot1.Plot.Title($"{_currentSymbol} {_currentInterval} 行情回放 (当前第 {_klineCursor.CurrentIndex + 1} 根 | 界面保留最新 2000 点)");
            formsPlot1.Plot.XLabel("时间 (UTC+0)");
            formsPlot1.Plot.YLabel("价格 (USDT)");

            if (_replayedDates.Count > 0)
            {
                // 界面显示保留最多 2000 个点
                const int maxDisplayPoints = 2000;
                int totalPoints = _replayedDates.Count;
                int startIndex = Math.Max(0, totalPoints - maxDisplayPoints);
                int displayCount = totalPoints - startIndex;

                DateTime minDisplayTime = _replayedDates[startIndex];
                double[] xs = new double[displayCount];
                double[] ys = new double[displayCount];

                for (int i = 0; i < displayCount; i++)
                {
                    xs[i] = _replayedDates[startIndex + i].ToOADate();
                    ys[i] = _replayedCloses[startIndex + i];
                }

                // 核心 1: 显示线条统一为 0.8 宽，淡蓝色显示
                var scatter = formsPlot1.Plot.Add.ScatterLine(xs, ys, LightBlueColor);
                scatter.LineWidth = 0.8f;

                // 在最新一根价格点绘制标记 (大小为 4)
                double lastX = xs[^1];
                double lastY = ys[^1];
                var marker = formsPlot1.Plot.Add.Marker(lastX, lastY);
                marker.Color = MarkerHighlightColor;
                marker.Size = 4;
                marker.Shape = MarkerShape.FilledCircle;

                // 3. 计算并绘制高低极值点 (Pivot Points, 直径大小统一为 4)
                var (peaks, valleys) = PivotHelper.CalculatePeaks(_replayedKlines, leftLen: 3, rightLen: 3);

                if (chkShowPivots.Checked)
                {
                    // 绘制波峰高点 ▲ (红色，直径大小为 4)
                    foreach (var peak in peaks)
                    {
                        if (peak.Time < minDisplayTime) continue;

                        double px = peak.Time.ToOADate();
                        double py = (double)peak.Price;
                        var peakMarker = formsPlot1.Plot.Add.Marker(px, py);
                        peakMarker.Color = PeakMarkerColor;
                        peakMarker.Size = 4;
                        peakMarker.Shape = MarkerShape.FilledTriangleUp;
                    }

                    // 绘制波谷低点 ▼ (绿色，直径大小为 4)
                    foreach (var valley in valleys)
                    {
                        if (valley.Time < minDisplayTime) continue;

                        double vx = valley.Time.ToOADate();
                        double vy = (double)valley.Price;
                        var valleyMarker = formsPlot1.Plot.Add.Marker(vx, vy);
                        valleyMarker.Color = ValleyMarkerColor;
                        valleyMarker.Size = 4;
                        valleyMarker.Shape = MarkerShape.FilledTriangleDown;
                    }
                }

                // 4. 绘制趋势线 (TrendLines, 统一线宽为 0.8，高低点生成严格颜色区分)
                if (chkShowTrendLines.Checked)
                {
                    var linesToDraw = new List<TrendLine>();

                    if (_trendLineStrategy != null && _trendLineStrategy.ActiveLinesCount > 0)
                    {
                        linesToDraw.AddRange(_trendLineStrategy.ActiveLines);
                    }
                    else
                    {
                        var (resLines, supLines) = TrendLineHelper.FindActiveTrendLines(_replayedKlines, leftLen: 3, rightLen: 3, maxSpan: 100);
                        linesToDraw.AddRange(resLines);
                        linesToDraw.AddRange(supLines);
                    }

                    int currentIndex = _replayedKlines.Count - 1;

                    foreach (var line in linesToDraw)
                    {
                        if (line.Time2 < minDisplayTime) continue;

                        double x1 = line.Time1.ToOADate();
                        double y1 = (double)line.Y1;
                        double x2 = line.Time2.ToOADate();
                        double y2 = (double)line.Y2;

                        // 🌟 颜色严格区分：高点阻力线为红色 (ResistanceLineColor)，低点支撑线为绿色 (SupportLineColor)
                        var color = line.IsResistance ? ResistanceLineColor : SupportLineColor;

                        // 绘制核心线段 (X1 -> X2, 线宽统一为 0.8)
                        var linePlot = formsPlot1.Plot.Add.ScatterLine(new double[] { x1, x2 }, new double[] { y1, y2 }, color);
                        linePlot.LineWidth = 0.8f;

                        // 绘制向后延伸至当前最新 K 线的延长线 (线宽统一为 0.8)
                        if (currentIndex > line.X2)
                        {
                            double xExt = _replayedDates[^1].ToOADate();
                            double yExt = (double)line.GetPriceAt(currentIndex);
                            var extPlot = formsPlot1.Plot.Add.ScatterLine(new double[] { x2, xExt }, new double[] { y2, yExt }, color);
                            extPlot.LineWidth = 0.8f;
                        }
                    }
                }

                // 5. 绘制策略触发的买卖交易信号标记 (直径大小为 6)
                lock (_triggeredSignals)
                {
                    foreach (var sig in _triggeredSignals)
                    {
                        if (sig.Time < minDisplayTime) continue;

                        double sx = sig.Time.ToOADate();
                        double sy = (double)sig.Price;

                        var sigMarker = formsPlot1.Plot.Add.Marker(sx, sy);
                        sigMarker.Size = 6;

                        if (sig.Type == SignalType.Buy)
                        {
                            sigMarker.Color = SignalBuyColor;
                            sigMarker.Shape = MarkerShape.OpenTriangleUp;
                        }
                        else
                        {
                            sigMarker.Color = SignalSellColor;
                            sigMarker.Shape = MarkerShape.OpenTriangleDown;
                        }
                    }
                }
            }

            // 🌟 自由视窗调整机制：仅当勾选「自动跟随最新视窗」或首次渲染时才强制 AutoScale
            if (chkAutoScale.Checked || _isFirstRender)
            {
                formsPlot1.Plot.Axes.AutoScale();
                _isFirstRender = false;
            }

            formsPlot1.Refresh();
        }

        private void ResetReplayState()
        {
            StopReplay();
            _isFirstRender = true;
            _klineCursor?.Reset();
            _replayedDates.Clear();
            _replayedCloses.Clear();
            _replayedKlines.Clear();
            _trendLineStrategy?.Reset();

            lock (_triggeredSignals)
            {
                _triggeredSignals.Clear();
            }

            lblProgress.Text = $"进度: 0 / {_klineCursor?.TotalCount ?? 0}";
            lblTime.Text = "时间: --";
            lblPrice.Text = "最新收盘价: --";
            lblHighLow.Text = "高 / 低: -- / --";
            lblVolume.Text = "成交量: --";
            lblBuffer.Text = "100条缓存: 0 / 100";
            lblActiveLines.Text = "监控中存活趋势线: 0 条";

            SetupChartStyle();
        }

        #endregion

        #region 模拟数据生成器 (当本地无历史文件时支持开箱即用体验)

        private List<MarketKline> GenerateMockKlines(string symbol, string interval, DateTime startUtc, DateTime endUtc)
        {
            var list = new List<MarketKline>();
            int intervalMinutes = interval switch
            {
                "1m" => 1,
                "3m" => 3,
                "5m" => 5,
                "15m" => 15,
                "30m" => 30,
                "1h" => 60,
                "2h" => 120,
                "4h" => 240,
                "1d" => 1440,
                _ => 30
            };

            Random rand = new Random(42);
            decimal price = 65000m;
            DateTime current = startUtc;

            while (current <= endUtc)
            {
                decimal change = (decimal)(rand.NextDouble() * 400 - 190);
                decimal open = price;
                decimal close = Math.Max(1000m, open + change);
                decimal high = Math.Max(open, close) + (decimal)(rand.NextDouble() * 80);
                decimal low = Math.Min(open, close) - (decimal)(rand.NextDouble() * 80);
                decimal volume = (decimal)(rand.NextDouble() * 500 + 50);

                list.Add(new MarketKline
                {
                    Symbol = symbol,
                    Interval = interval,
                    OpenTime = current,
                    CloseTime = current.AddMinutes(intervalMinutes),
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    Volume = volume,
                    QuoteVolume = volume * close,
                    TradesCount = rand.Next(500, 3000),
                    IsClosed = true
                });

                price = close;
                current = current.AddMinutes(intervalMinutes);
            }

            return list;
        }

        #endregion

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopReplay();
            _trendLineStrategy?.Unbind();
            _dataProvider?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
