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
        private IRawDataCursor? _rawKlineCursor;
        private IRawDataCursor? _rawTickCursor;
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

            _dataProvider = new DuckDbHistoricalDataProvider(cursorBufferCapacity: 500);

            _replayTimer = new System.Windows.Forms.Timer();
            _replayTimer.Interval = (int)numSpeed.Value;
            _replayTimer.Tick += ReplayTimer_Tick;

            // 默认日期与周期初始化
            cboInterval.SelectedIndex = 4; // 30m
            dtpStartDate.Value = new DateTime(2026, 1, 1);
            dtpEndDate.Value = new DateTime(2026, 1, 5);

            SetupChartStyle();
            InitializeStrategy();

            AppendLog("系统初始化就绪。统一时间标准: UTC+0 | 启用真实数据列式游标 (IRawDataCursor)");
            AppendLog($"数据根目录: {Config.GetRootPath()}");
            AppendLog($"Tick/Trade 目录: {Config.GetTradeDataPath(_currentSymbol)}");
        }

        private void InitializeStrategy()
        {
            _trendLineStrategy?.Unbind();

            // 🌟 初始化趋势线策略 (滑窗容量支持 2000 根，拟合跨度支持 500 根以上，保留丰富趋势线)
            _trendLineStrategy = new TrendLineReboundStrategy(
                symbol: _currentSymbol,
                interval: _currentInterval,
                minLineX1X2: 10,
                minLineAge: 3,
                reboundTicksWindow: 5,
                bufferCapacity: 2000,
                maxSpan: 500);

            _trendLineStrategy.IsEnabled = chkEnableStrategy.Checked;

            _trendLineStrategy.OnSignal += signal =>
            {
                lock (_triggeredSignals)
                {
                    _triggeredSignals.Add(signal);
                }
                AppendLog($"🔥 [策略信号触发] {signal.Type} | 价格: {signal.Price:F2} | 原因: {signal.Reason}");
            };

            _trendLineStrategy.OnTrendLinesUpdated += validLines =>
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() =>
                    {
                        lblActiveLines.Text = $"监控中存活趋势线: {validLines.Count} 条";
                    }));
                }
                else
                {
                    lblActiveLines.Text = $"监控中存活趋势线: {validLines.Count} 条";
                }
            };

            _trendLineStrategy.OnLog += logMsg =>
            {
                AppendLog($"📝 {logMsg}");
            };
        }

        private void SetupChartStyle()
        {
            formsPlot1.Plot.Clear();
            formsPlot1.Plot.Title("Binance 行情回放 - 趋势线与高低极值点 (X轴数字/自由缩放)");
            formsPlot1.Plot.XLabel("K线序号 (Bar Index)");
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

        #region 数据加载 (从 Config 数据目录中加载真实 K 线与真实 Tick/Trade 数据文件)

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

            string klineDir = Config.GetKlineDataPath(_currentSymbol, _currentInterval);
            string tradeDir = Config.GetTradeDataPath(_currentSymbol);

            AppendLog($"正在从本地数据文件加载: {_currentSymbol} {_currentInterval} ({startUtc.ToUtc0String()} 至 {endUtc.ToUtc0String()})...");
            AppendLog($"  ↳ K线目录: {klineDir}");
            AppendLog($"  ↳ Trade/Tick 目录: {tradeDir}");

            Cursor = Cursors.WaitCursor;
            try
            {
                // 🌟 使用 Channel 有界队列管道流式游标加载 (后台逐文件预取生产，背压控制，内存恒定，零界面卡顿)
                _rawKlineCursor = _dataProvider.GetStreamingRawKlineCursor(_currentSymbol, _currentInterval, startUtc, endUtc, queueCapacity: 2000);
                _rawTickCursor = _dataProvider.GetStreamingRawTickCursor(_currentSymbol, startUtc, endUtc, queueCapacity: 10000);

                AppendLog($"[管道就绪] 已成功建立流式队列管道 (K线队列容量: 2000, Tick队列容量: 10000)。");
                AppendLog($"  ↳ 零内存阻塞架构：后台线程按需预取并自动背压控制，界面零卡顿。");

                InitializeStrategy();
                ResetReplayState();
                MessageBox.Show(
                    $"流式数据管道已成功就绪！\n\n已启用后台队列预取与滑动窗口缓冲机制，内存占用极低。\n点击【▶ 开始回放】即可流畅回放。",
                    "数据管道就绪",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
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
            if (_rawKlineCursor == null || _rawKlineCursor.TotalCount == 0)
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
                AppendLog($"[回放结束] 全部 {_rawKlineCursor?.TotalCount ?? 0} 根 K 线数据回放完毕。");
                MessageBox.Show("数据回放已到达末尾！", "回放完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void btnStepNext_Click(object sender, EventArgs e)
        {
            PauseReplay();
            if (_rawKlineCursor == null) return;
            StepForward(isAutoReplay: false);
        }

        private void btnStepPrev_Click(object sender, EventArgs e)
        {
            PauseReplay();
            if (_rawKlineCursor == null) return;
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

        #region 🌟 步进与图表渲染 (基于真实数据列式游标与真实 Tick 优先推送)

        private bool StepForward(bool isAutoReplay = false)
        {
            if (_rawKlineCursor == null) return false;

            if (_rawKlineCursor.MoveNext())
            {
                // 🌟 1. 高性能列式游标直读基元列值 (零中间字符串与装箱分配)
                long openTimeMs = _rawKlineCursor.GetInt64(0);
                decimal close = _rawKlineCursor.GetDecimal(4);
                long closeTimeMs = _rawKlineCursor.GetInt64(6);
                DateTime openTime = _rawKlineCursor.GetDateTime(0);

                var kline = _rawKlineCursor.ReadCurrentKline(_currentSymbol, _currentInterval);

                _replayedDates.Add(openTime);
                _replayedCloses.Add((double)close);
                _replayedKlines.Add(kline);

                // 🌟 2. 真实交易仿真时序：
                // for 循环周期 K 线 (例如 30 分钟)
                //   for 循环周期的真实 tick 数据 -> 执行推送真实 tick
                //   完成真实 tick 推送后推送周期 K 线以模拟真实交易收盘
                if (_trendLineStrategy != null && chkEnableStrategy.Checked)
                {
                    int tickCountForPeriod = 0;
                    decimal? lastTickPriceInPeriod = null;

                    // 严格从真实 Tick 数据游标中检索属于本 K 线周期的时间窗口 [openTimeMs, closeTimeMs]
                    if (_rawTickCursor != null)
                    {
                        while (_rawTickCursor.MoveNext())
                        {
                            long tickTimeMs = _rawTickCursor.GetInt64(4); // 4 为 trade_time

                            // 若 tick 时间尚未到达本根 K 线开盘时间，继续向前推
                            if (tickTimeMs < openTimeMs)
                            {
                                continue;
                            }

                            // 🌟 1. 超过该 K 线的时间闭区间直接中断跳出 (提前早退，不浪费无谓循环)
                            if (tickTimeMs > closeTimeMs)
                            {
                                _rawTickCursor.MovePrevious();
                                break;
                            }

                            // 🌟 2. 价格无变动去重优化：如果新 Tick 价格与上一 Tick 价格完全一致，则直接跳过推送
                            decimal tickPrice = _rawTickCursor.GetDecimal(1); // 1 为 price
                            if (lastTickPriceInPeriod.HasValue && tickPrice == lastTickPriceInPeriod.Value)
                            {
                                continue;
                            }
                            lastTickPriceInPeriod = tickPrice;

                            tickCountForPeriod++;
                            var tick = _rawTickCursor.ReadCurrentTick(_currentSymbol);
                            _trendLineStrategy.OnTickUpdate(tick);
                        }
                    }

                    // 完成该周期全部真实 Tick 推送后，推送该周期 K 线触发收盘
                    _trendLineStrategy.OnKlineUpdate(kline);
                }

                UpdateChartAndLabels(kline);

                if (!isAutoReplay || _rawKlineCursor.CurrentIndex % 10 == 0 || _rawKlineCursor.CurrentIndex == _rawKlineCursor.TotalCount - 1)
                {
                    AppendLog($"[推进 { _rawKlineCursor.CurrentIndex + 1}/{_rawKlineCursor.TotalCount}] {kline.FormattedOpenTime} | C:{kline.Close:F2} | 存活趋势线:{_trendLineStrategy?.ActiveLinesCount ?? 0}");
                }

                return true;
            }

            return false;
        }

        private bool StepBackward()
        {
            if (_rawKlineCursor == null || !_rawKlineCursor.HasPrevious) return false;

            if (_rawKlineCursor.MovePrevious())
            {
                if (_replayedDates.Count > 0)
                {
                    _replayedDates.RemoveAt(_replayedDates.Count - 1);
                    _replayedCloses.RemoveAt(_replayedCloses.Count - 1);
                    _replayedKlines.RemoveAt(_replayedKlines.Count - 1);
                }

                // 策略状态重置并按当前历史重新喂入
                ReplayStrategyToCurrent();

                var kline = _rawKlineCursor.ReadCurrentKline(_currentSymbol, _currentInterval);
                UpdateChartAndLabels(kline);
                AppendLog($"[回退 { _rawKlineCursor.CurrentIndex + 1}/{_rawKlineCursor.TotalCount}] {kline.FormattedOpenTime} | C:{kline.Close:F2}");
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
            int currentIndex = _replayedKlines.Count - 1;

            // 1. 更新右侧面板状态信息
            lblProgress.Text = $"进度: {_rawKlineCursor!.CurrentIndex + 1} / {_rawKlineCursor.TotalCount}";
            lblTime.Text = $"时间: {current.FormattedOpenTime}";
            lblPrice.Text = $"最新收盘价: {current.Close:F2} USDT";
            lblHighLow.Text = $"开/高/低: {current.Open:F2} / {current.High:F2} / {current.Low:F2}";
            lblVolume.Text = $"成交量: {current.Volume:F2}";
            lblBuffer.Text = $"策略滑窗: {_trendLineStrategy?.KlineHistory.Count ?? 0} / 2000";
            lblActiveLines.Text = $"监控中存活趋势线: {_trendLineStrategy?.ActiveLinesCount ?? 0} 条";

            // 2. 清空并重新构建 ScottPlot 图表 (X 轴使用纯数字序号)
            formsPlot1.Plot.Clear();
            formsPlot1.Plot.Title($"{_currentSymbol} {_currentInterval} 行情回放 (当前第 {currentIndex + 1} 根 | 界面保留最新 2000 点)");
            formsPlot1.Plot.XLabel("K线序号 (Bar Index)");
            formsPlot1.Plot.YLabel("价格 (USDT)");

            if (_replayedKlines.Count > 0)
            {
                // 界面显示保留最多 2000 个点
                const int maxDisplayPoints = 2000;
                int totalPoints = _replayedKlines.Count;
                int startIndex = Math.Max(0, totalPoints - maxDisplayPoints);
                int displayCount = totalPoints - startIndex;

                double[] xs = new double[displayCount];
                double[] ys = new double[displayCount];

                for (int i = 0; i < displayCount; i++)
                {
                    // 🌟 X 轴纯数字序号 (从 startIndex 到 totalPoints - 1)
                    xs[i] = startIndex + i;
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

                // 建立时间戳到全局索引的快速映射，保证无论来自滑窗策略还是全局历史，点位与趋势线坐标 100% 绝对对齐
                var timeToIndex = new Dictionary<DateTime, int>(_replayedKlines.Count);
                for (int i = 0; i < _replayedKlines.Count; i++)
                {
                    timeToIndex[_replayedKlines[i].OpenTime] = i;
                }

                // 3. 计算并绘制高低极值点 (Pivot Points, 直径大小统一为 4)
                var (peaks, valleys) = PivotHelper.CalculatePeaks(_replayedKlines, leftLen: 3, rightLen: 3);

                if (chkShowPivots.Checked)
                {
                    // 绘制波峰高点 ▲ (红色，位于 K 线的最高价 High 处)
                    foreach (var peak in peaks)
                    {
                        if (!timeToIndex.TryGetValue(peak.Time, out int px))
                        {
                            px = peak.Index;
                        }

                        if (px < startIndex) continue;

                        double py = (double)peak.Price;
                        var peakMarker = formsPlot1.Plot.Add.Marker((double)px, py);
                        peakMarker.Color = PeakMarkerColor;
                        peakMarker.Size = 4;
                        peakMarker.Shape = MarkerShape.FilledTriangleUp;
                    }

                    // 绘制波谷低点 ▼ (绿色，位于 K 线的最低价 Low 处)
                    foreach (var valley in valleys)
                    {
                        if (!timeToIndex.TryGetValue(valley.Time, out int vx))
                        {
                            vx = valley.Index;
                        }

                        if (vx < startIndex) continue;

                        double vy = (double)valley.Price;
                        var valleyMarker = formsPlot1.Plot.Add.Marker((double)vx, vy);
                        valleyMarker.Color = ValleyMarkerColor;
                        valleyMarker.Size = 4;
                        valleyMarker.Shape = MarkerShape.FilledTriangleDown;
                    }
                }

                // 4. 绘制趋势线 (TrendLines, 严格通过时间戳映射保证与极值高低点 100% 精确对齐)
                if (chkShowTrendLines.Checked)
                {
                    var linesToDraw = new List<TrendLine>();

                    if (_trendLineStrategy != null && chkEnableStrategy.Checked)
                    {
                        // 🌟 启用策略时，通过策略提供的 GetAllValidTrendLines() 方法直接获取当前所有存活有效趋势线
                        linesToDraw.AddRange(_trendLineStrategy.GetAllValidTrendLines());
                    }
                    else
                    {
                        var (resLines, supLines) = TrendLineHelper.FindActiveTrendLines(_replayedKlines, leftLen: 3, rightLen: 3, maxSpan: 500);
                        linesToDraw.AddRange(resLines);
                        linesToDraw.AddRange(supLines);
                    }

                    foreach (var line in linesToDraw)
                    {
                        // 🌟 精准匹配时间戳以获得图表全局 X 坐标
                        int x1 = -1;
                        int x2 = -1;

                        if (timeToIndex.TryGetValue(line.Time1, out int mapped1))
                        {
                            x1 = mapped1;
                        }
                        if (timeToIndex.TryGetValue(line.Time2, out int mapped2))
                        {
                            x2 = mapped2;
                        }

                        // 若时间未找到，使用滑窗偏移校正
                        if (x1 == -1 || x2 == -1)
                        {
                            int windowOffset = Math.Max(0, currentIndex - 99);
                            x1 = windowOffset + line.X1;
                            x2 = windowOffset + line.X2;
                        }

                        if (x1 < 0 || x2 < 0 || x2 <= x1 || x2 < startIndex) continue;

                        double y1 = (double)line.Y1;
                        double y2 = (double)line.Y2;

                        // 🌟 颜色严格区分：高点阻力线为红色 (连接高点)，低点支撑线为绿色 (连接低点)
                        var color = line.IsResistance ? ResistanceLineColor : SupportLineColor;

                        // 绘制核心线段 (X1 -> X2, 线宽统一为 0.8)
                        var linePlot = formsPlot1.Plot.Add.ScatterLine(new double[] { (double)x1, (double)x2 }, new double[] { y1, y2 }, color);
                        linePlot.LineWidth = 0.8f;

                        // 绘制向后延伸至当前最新 K 线的延长线 (线宽统一为 0.8)
                        if (currentIndex > x2)
                        {
                            double yExt = (double)(line.Y1 + ((line.Y2 - line.Y1) / (x2 - x1)) * (currentIndex - x1));
                            var extPlot = formsPlot1.Plot.Add.ScatterLine(new double[] { (double)x2, (double)currentIndex }, new double[] { y2, yExt }, color);
                            extPlot.LineWidth = 0.8f;
                        }
                    }
                }

                // 5. 绘制策略触发的买卖交易信号标记 (直径大小为 6)
                lock (_triggeredSignals)
                {
                    foreach (var sig in _triggeredSignals)
                    {
                        if (!timeToIndex.TryGetValue(sig.Time, out int sigIndex))
                        {
                            sigIndex = currentIndex;
                        }

                        if (sigIndex < startIndex) continue;

                        double sx = sigIndex;
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
            _rawKlineCursor?.Reset();
            _rawTickCursor?.Reset();
            _replayedDates.Clear();
            _replayedCloses.Clear();
            _replayedKlines.Clear();
            _trendLineStrategy?.Reset();

            lock (_triggeredSignals)
            {
                _triggeredSignals.Clear();
            }

            lblProgress.Text = $"进度: 0 / {_rawKlineCursor?.TotalCount ?? 0}";
            lblTime.Text = "时间: --";
            lblPrice.Text = "最新收盘价: --";
            lblHighLow.Text = "高 / 低: -- / --";
            lblVolume.Text = "成交量: --";
            lblBuffer.Text = "策略滑窗: 0 / 2000";
            lblActiveLines.Text = "监控中存活趋势线: 0 条";

            SetupChartStyle();
        }

        #endregion

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopReplay();
            _trendLineStrategy?.Unbind();
            _rawKlineCursor?.Dispose();
            _rawTickCursor?.Dispose();
            _dataProvider?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
