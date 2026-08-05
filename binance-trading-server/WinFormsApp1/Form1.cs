using ConsoleApp1;
using ScottPlot;
using ScottPlot.Colormaps;
using ScottPlot.Plottables;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Color = System.Drawing.Color;

namespace WinFormsApp1
{
    public partial class Form1 : Form
    {
        readonly System.Windows.Forms.Timer AddNewDataTimer = new() { Interval = 20, Enabled = true };
        readonly System.Windows.Forms.Timer UpdatePlotTimer = new() { Interval = 60, Enabled = true };

        // ScottPlot 变量
        private DataStreamer Streamer1;
        private VerticalLine VLine;

        // 十字准星与 Text 标注
        private Crosshair MyCrosshair;
        private ScottPlot.Plottables.Text MyTooltipText;

        // 随机数据备用发生器
        private RandomWalker Walker1 = new RandomWalker(seed: 0, mult: 1);

        // --- 队列方式接入 ScottPlot ---
        private readonly ConcurrentQueue<BinanceFuturesKlineItem> _klineQueue = new();
        private readonly SymbolDataProvider _dataProvider = new SymbolDataProvider(maxDegreeOfParallelism: 5);
        private CancellationTokenSource? _fetchCts;
        private long _totalEnqueuedCount = 0;
        private string _currentSymbol = "BTCUSDT";

        // --- 内存缓存区 ---
        private readonly List<int> _peaksBuffer = new(64);
        private readonly List<int> _valleysBuffer = new(64);
        private readonly List<IPlottable> _currentOverlayPlottables = new(256);
        private decimal[] _highsCache = new decimal[1000];
        private decimal[] _lowsCache = new decimal[1000];
        // --- 预计算数据结果缓存 (数据计算与 UI 渲染彻底解耦) ---
        private readonly List<AngleTrendLineInfo> _cachedLinesToDraw = new(128);
        private int _cachedActiveRedCount = 0;
        private int _cachedActiveGreenCount = 0;

        // 按需/事件驱动图层渲染签名
        private string _lastPivotSignature = string.Empty;
        private bool _forceUpdatePivotOverlays = false;

        readonly System.Windows.Forms.Timer RewindTimer = new() { Interval = 20, Enabled = false };
        private readonly List<BinanceFuturesKlineItem> _historyKlines = new(10000);
        private bool _isRewinding = false;
        private readonly TrendlineStrategyEngine _strategyEngine = new();

        

        public Form1()
        {
            InitializeComponent();

            // 1. 初始化 ComboBox 默认选项
            InitControls();

            // 2. 初始化 ScottPlot DataStreamer (1000 数据点)
            Streamer1 = formsPlot1.Plot.Add.DataStreamer(1000);
            Streamer1.ViewScrollLeft();
            Streamer1.ManageAxisLimits = false;
            Streamer1.LineStyle.Color = ScottPlot.Colors.Blue;
            Streamer1.LegendText = "收盘价 (Close Price)";

            // 3. 指示线与十字星
            VLine = formsPlot1.Plot.Add.VerticalLine(0, 2, ScottPlot.Colors.Red);
            VLine.IsVisible = false;

            formsPlot1.UserInputProcessor.Enable();

            MyCrosshair = formsPlot1.Plot.Add.Crosshair(0, 0);
            MyCrosshair.IsVisible = false;
            MyCrosshair.LineColor = ScottPlot.Colors.DarkGray.WithAlpha(0.8);
            MyCrosshair.LineWidth = 1f;

            MyTooltipText = formsPlot1.Plot.Add.Text("", 0, 0);
            MyTooltipText.IsVisible = false;
            MyTooltipText.LabelFontSize = 12;
            MyTooltipText.LabelFontColor = ScottPlot.Colors.Black;
            MyTooltipText.LabelBackgroundColor = ScottPlot.Colors.Yellow.WithAlpha(0.8);

            formsPlot1.MouseMove += FormsPlot1_MouseMove;
            formsPlot1.MouseLeave += FormsPlot1_MouseLeave;

            // 回退定时器
            RewindTimer.Tick += (s, e) => PerformRewindStep();

            // 注册策略开平仓事件，向 RichTextBox 实时追加彩色日志
            _strategyEngine.OnTradeOpened += (type, price, barIndex) =>
            {
                if (type == StrategyPositionType.Long)
                {
                    AppendLog($"[BUY LONG] Open @ {price:F1} (TP: {_strategyEngine.TakeProfitPrice:F1} | SL: {_strategyEngine.StopLossPrice:F1})", Color.DarkGreen, true);
                }
                else
                {
                    AppendLog($"[SELL SHORT] Open @ {price:F1} (TP: {_strategyEngine.TakeProfitPrice:F1} | SL: {_strategyEngine.StopLossPrice:F1})", Color.DarkRed, true);
                }
            };

            _strategyEngine.OnTradeClosed += (trade) =>
            {
                int total = _strategyEngine.CompletedTrades.Count;
                int win = _strategyEngine.CompletedTrades.Count(t => t.IsProfit);
                int loss = total - win;
                double rate = total > 0 ? (double)win / total * 100 : 0;

                string statsSuffix = $" | WinRate: {rate:F0}% (Total: {total}, Win: {win}, Loss: {loss})";

                if (trade.IsProfit)
                {
                    AppendLog($"[PROFIT EXIT] {trade.ExitReason} @ {trade.ExitPrice:F1} | PnL: +{trade.ProfitPct:F2}%{statsSuffix}", Color.DarkGoldenrod, true);
                }
                else
                {
                    AppendLog($"[LOSS EXIT] {trade.ExitReason} @ {trade.ExitPrice:F1} | PnL: {trade.ProfitPct:F2}%{statsSuffix}", Color.Purple, true);
                }
            };

            // 绑定鼠标移入与滚轮事件，滚动鼠标滚轮时自动触发单步向前（向上滚）与单步回退（向下滚）
            btnRewind.MouseEnter += (s, e) => btnRewind.Focus();
            btnStepForward.MouseEnter += (s, e) => btnStepForward.Focus();

            btnRewind.MouseWheel += OnControlMouseWheel;
            btnStepForward.MouseWheel += OnControlMouseWheel;

            AppendLog("系统就绪：请选择币种与周期后点击【获取币种历史数据】（支持在按钮上滚动鼠标滚轮触发单步步进/回退）", Color.DimGray);

            // 4. 定时器 1：根据选择的播放速度倍速，从 ConcurrentQueue 队列中消费 K 线数据并接入 ScottPlot
            AddNewDataTimer.Interval = 100; // 20ms 默认
            AddNewDataTimer.Tick += (s, e) =>
            {
                if (!_isRewinding && !_klineQueue.IsEmpty)
                {
                    int speedIndex = cmbPlaySpeed.SelectedIndex >= 0 ? cmbPlaySpeed.SelectedIndex : 1;

                    // 动态调整定时器间隔 (0.5x=35ms, 1.0x=20ms, 2.0x=15ms, 5.0x/10.0x/全速=10ms)
                    int targetInterval = speedIndex switch
                    {
                        0 => 150,
                        1 => 20,
                        2 => 150,
                        _ => 10
                    };
                    if (AddNewDataTimer.Interval != targetInterval)
                    {
                        AddNewDataTimer.Interval = targetInterval;
                    }

                    // 动态计算每次提取的 BatchSize 批量大小
                    int batchSize = speedIndex switch
                    {
                        0 => 1,                 // 0.5x (慢速: 每 Tick 1 点)
                        1 => 2,                 // 1.0x (标准: 每 Tick 2 点)
                        2 => 5,                 // 2.0x (快速: 每 Tick 5 点)
                        3 => 15,                // 5.0x (极速: 每 Tick 15 点)
                        4 => 40,                // 10.0x (飞速: 每 Tick 40 点)
                        5 => _klineQueue.Count, // 全速 (瞬时全量完成)
                        _ => 2
                    };

                    List<double> valuesToAdd = new();
                    for (int i = 0; i < batchSize && _klineQueue.TryDequeue(out var kline); i++)
                    {
                        valuesToAdd.Add((double)kline.Close);
                        _historyKlines.Add(kline);
                    }

                    if (valuesToAdd.Count > 0)
                    {
                        Streamer1.AddRange(valuesToAdd);
                        
                        // 核心架构优化：高低点位计算、趋势线运算与策略评测彻底放在数据 Tick 中完成 (计算与 UI 彻底解耦)
                        PerformTrendlineAnalysisAndEvaluation();
                    }
                }
            };

            // 5. 定时器 2：UI 渲染刷新 (UI 仅负责轻量级画布刷新，计算与 Overlay 图层生成已在数据 Tick 原子完成)
            UpdatePlotTimer.Interval = 30; // 30ms (~33 FPS)
            UpdatePlotTimer.Tick += (s, e) =>
            {
                if (Streamer1.HasNewData)
                {
                    long totalCount = Streamer1.Data.CountTotal;
                    formsPlot1.Plot.Title($"[{_currentSymbol}] 已接入数据点: {totalCount:N0} | 队列剩余: {_klineQueue.Count}");

                    if (Streamer1.Renderer is ScottPlot.DataViews.Wipe)
                    {
                        VLine.IsVisible = true;
                        VLine.Position = Streamer1.Data.NextIndex * Streamer1.Data.SamplePeriod + Streamer1.Data.OffsetX;
                    }
                    else
                    {
                        VLine.IsVisible = false;
                    }
                    UpdateYAxisLimits();
                    formsPlot1.Refresh();
                }
            };

            AddNewDataTimer.Start();
            UpdatePlotTimer.Start();
        }

        private void InitControls()
        {
            if (cmbSymbol.SelectedIndex < 0) cmbSymbol.SelectedIndex = 0;
            if (cmbInterval.SelectedIndex < 0) cmbInterval.SelectedIndex = 0; // 默认 15m
            if (cmbTimeRange.SelectedIndex < 0) cmbTimeRange.SelectedIndex = 2; // 默认 最近24小时
            if (cmbPlaySpeed.SelectedIndex < 0) cmbPlaySpeed.SelectedIndex = 0; // 默认 1.0x (标准)
        }

        /// <summary>
        /// 点击按钮：多线程获取币种历史数据并采用队列接入 ScottPlot
        /// </summary>
        private async void btnFetch_Click(object sender, EventArgs e)
        {
            string symbol = cmbSymbol.Text.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(symbol))
            {
                MessageBox.Show("请输入或选择正确的币种名称（如 BTCUSDT）", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string intervalStr = cmbInterval.SelectedItem?.ToString() ?? "15m";
            if (!FuturesKlineIntervalExtensions.TryParseInterval(intervalStr, out var interval))
            {
                interval = FuturesKlineInterval.Min15;
            }

            // 计算起始与结束时间
            DateTime endTime = DateTime.UtcNow;
            DateTime startTime = cmbTimeRange.SelectedIndex switch
            {
                0 => endTime.AddHours(-1),
                1 => endTime.AddHours(-6),
                2 => endTime.AddHours(-24),
                3 => endTime.AddDays(-3),
                4 => endTime.AddDays(-7),
                5 => endTime.AddDays(-30),
                _ => endTime.AddHours(-24)
            };

            // 取消上一次未完成的获取请求
            _fetchCts?.Cancel();
            _fetchCts = new CancellationTokenSource();
            var token = _fetchCts.Token;

            // 重置状态与队列
            _currentSymbol = symbol;
            Streamer1.LegendText = $"{symbol} {intervalStr} (Close)";
            btnFetch.Enabled = false;
            AppendLog($"[FETCH] 开始多线程拉取 [{symbol}] {intervalStr} 历史数据...", Color.DarkBlue, true);

            // 清空当前队列与历史缓存
            while (_klineQueue.TryDequeue(out _)) { }
            _historyKlines.Clear();
            _strategyEngine.Reset();
            _totalEnqueuedCount = 0;

            try
            {
                var progress = new Progress<FetchStatusReport>(report =>
                {
                    // 可选微调处理
                });

                // 1. 多线程并发拉取指定时间段的历史 K 线数据
                List<BinanceFuturesKlineItem> fetchedKlines = await _dataProvider.GetSymbolDataAsync(
                    symbol, interval, startTime, endTime, useCache: true, progress, token);

                // 2. 核心步骤：对多线程获取的数据按 OpenTimeMs 严格升序排序，确保入队绝对按时间顺序
                List<BinanceFuturesKlineItem> sortedKlines = fetchedKlines
                    .GroupBy(k => k.OpenTimeMs)
                    .Select(g => g.First())
                    .OrderBy(k => k.OpenTimeMs)
                    .ToList();

                // 3. 将排好序的数据依次压入 ConcurrentQueue 播放队列
                foreach (var kline in sortedKlines)
                {
                    _klineQueue.Enqueue(kline);
                    Interlocked.Increment(ref _totalEnqueuedCount);
                }

                int fetchedCount = sortedKlines.Count;
                AppendLog($"[FETCH OK] 成功载入 [{symbol}] {fetchedCount} 条 K 线并升序排队", Color.Blue, true);
            }
            catch (OperationCanceledException)
            {
                AppendLog("[FETCH CANCEL] 取消获取历史数据", Color.Gray);
            }
            catch (Exception ex)
            {
                AppendLog($"[FETCH ERROR] {ex.Message}", Color.Red, true);
                MessageBox.Show($"获取历史数据失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnFetch.Enabled = true;
            }
        }

        /// <summary>
        /// 彩色日志追加输出（多空盈亏与系统状态不同颜色区分）
        /// </summary>
        private void AppendLog(string message, Color color, bool bold = false)
        {
            if (rtbLog.InvokeRequired)
            {
                rtbLog.BeginInvoke(new Action(() => AppendLog(message, color, bold)));
                return;
            }

            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.SelectionColor = color;
            rtbLog.SelectionFont = new System.Drawing.Font("Consolas", 8.5F, bold ? System.Drawing.FontStyle.Bold : System.Drawing.FontStyle.Regular);

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            rtbLog.AppendText($"[{timestamp}] {message}\r\n");
            rtbLog.SelectionColor = rtbLog.ForeColor;
            rtbLog.ScrollToCaret();
        }

        /// <summary>
        /// 重置/复位图表按钮事件 handler
        /// </summary>
        private void button1_Click(object sender, EventArgs e)
        {
            // 1. 取消在途的网络下载任务
            _fetchCts?.Cancel();

            // 2. 清空 ConcurrentQueue 数据队列与历史
            while (_klineQueue.TryDequeue(out _)) { }
            _historyKlines.Clear();
            _strategyEngine.Reset();
            _totalEnqueuedCount = 0;

            // 3. 清空高低点 Marker 及延伸线 overlay
            _lastPivotSignature = string.Empty;
            _forceUpdatePivotOverlays = true;
            foreach (var item in _currentOverlayPlottables)
            {
                formsPlot1.Plot.Remove(item);
            }
            _currentOverlayPlottables.Clear();

            // 4. 重置 ScottPlot DataStreamer 数据
            Streamer1.Data.Clear();

            // 5. 复位视图与标题
            formsPlot1.Plot.Title("图表已重置");

            // 6. 恢复按钮与状态
            btnFetch.Enabled = true;
            AppendLog("图表与队列数据已重置完成，请重新选择币种后点击【获取币种历史数据】。", Color.DimGray);
            lblTrendState.Text = "策略状态: 未计算";
            lblTrendState.BackColor = Color.FromArgb(245, 245, 245);
            lblTrendState.ForeColor = Color.DimGray;

            // 7. 刷新界面
            formsPlot1.Refresh();
        }

        #region 单击步长回退数据处理逻辑

        private void btnRewind_Click(object? sender, EventArgs e)
        {
            PerformRewindStep();
        }

        private void PerformRewindStep()
        {
            if (_historyKlines.Count == 0) return;

            int speedIndex = cmbPlaySpeed.SelectedIndex >= 0 ? cmbPlaySpeed.SelectedIndex : 1;
            int rewindStep = speedIndex switch
            {
                0 => 1,                 // 0.5x 慢速回退 2 点
                1 => 4,                 // 1.0x 标准回退 4 点
                2 => 10,                // 2.0x 快速回退 10 点
                3 => 30,                // 5.0x 极速回退 30 点
                4 => 80,                // 10.0x 飞速回退 80 点
                5 => _historyKlines.Count, // 全速 (瞬时全量回退)
                _ => 4
            };

            rewindStep = Math.Min(rewindStep, _historyKlines.Count);

            // 从历史记录中弹出最后 N 个已播放的数据点
            int startIndex = _historyKlines.Count - rewindStep;
            var rewoundItems = _historyKlines.GetRange(startIndex, rewindStep);
            _historyKlines.RemoveRange(startIndex, rewindStep);

            // 将被回退的数据倒序重新压回队列最前端，以便继续正向顺序播放
            var remainingQueue = _klineQueue.ToList();
            while (_klineQueue.TryDequeue(out _)) { }

            foreach (var item in rewoundItems)
            {
                _klineQueue.Enqueue(item);
            }
            foreach (var item in remainingQueue)
            {
                _klineQueue.Enqueue(item);
            }

            // 清空 ScottPlot 并全量重新灌入剩余回退后的历史数据点
            Streamer1.Data.Clear();
            _forceUpdatePivotOverlays = true;

            if (_historyKlines.Count > 0)
            {
                Streamer1.AddRange(_historyKlines.Select(k => (double)k.Close));
                PerformTrendlineAnalysisAndEvaluation();
                RenderTrendlineOverlays();
            }
            else
            {
                foreach (var item in _currentOverlayPlottables)
                {
                    formsPlot1.Plot.Remove(item);
                }
                _currentOverlayPlottables.Clear();
                lblTrendState.Text = "趋势状态: 观望盘整";
                lblTrendState.BackColor = Color.FromArgb(245, 245, 245);
                lblTrendState.ForeColor = Color.DimGray;
            }

            // 优先计算视口可见范围 Y 轴极限，再刷新图形呈现
            UpdateYAxisLimits();
            formsPlot1.Plot.Title($"[{_currentSymbol}] 已回退步长: {rewindStep} 点 | 剩余: {_historyKlines.Count:N0} 点 | 待播放队列: {_klineQueue.Count}");
            formsPlot1.Refresh();
        }

        #endregion

        #region 单击步长向前推进处理逻辑

        private void btnStepForward_Click(object? sender, EventArgs e)
        {
            PerformForwardStep();
        }

        private void PerformForwardStep()
        {
            if (_klineQueue.IsEmpty) return;

            int speedIndex = cmbPlaySpeed.SelectedIndex >= 0 ? cmbPlaySpeed.SelectedIndex : 1;
            int forwardStep = speedIndex switch
            {
                0 => 1,                 // 0.5x 慢速向前 2 点
                1 => 4,                 // 1.0x 标准向前 4 点
                2 => 10,                // 2.0x 快速向前 10 点
                3 => 30,                // 5.0x 极速向前 30 点
                4 => 80,                // 10.0x 飞速向前 80 点
                5 => _klineQueue.Count, // 全速 (瞬时全量向前)
                _ => 4
            };

            List<double> valuesToAdd = new();
            for (int i = 0; i < forwardStep && _klineQueue.TryDequeue(out var kline); i++)
            {
                valuesToAdd.Add((double)kline.Close);
                _historyKlines.Add(kline);
            }

            if (valuesToAdd.Count > 0)
            {
                Streamer1.AddRange(valuesToAdd);
                _forceUpdatePivotOverlays = true;
                PerformTrendlineAnalysisAndEvaluation();
                RenderTrendlineOverlays();
                UpdateYAxisLimits();
                formsPlot1.Plot.Title($"[{_currentSymbol}] 已单步向前: {valuesToAdd.Count} 点 | 总计渲染: {_historyKlines.Count:N0} 点 | 队列剩余: {_klineQueue.Count}");
                formsPlot1.Refresh();
            }
        }

        /// <summary>
        /// 鼠标滚轮响应 handler：向上滚动 (Delta > 0) 触发单步向前推进；向下滚动 (Delta < 0) 触发单步回退
        /// </summary>
        private void OnControlMouseWheel(object? sender, MouseEventArgs e)
        {
            if (e.Delta > 0)
            {
                PerformForwardStep();
            }
            else if (e.Delta < 0)
            {
                PerformRewindStep();
            }
        }

        #endregion

        /// <summary>
        /// 动态设置 Y 轴可见范围为当前视口可见 K 线波幅 (含 8% 留白边距)
        /// </summary>
        private void UpdateYAxisLimits()
        {
            // 若用户取消勾选【自动更新 Y 轴范围】，则跳过坐标轴限制重置，保留用户自定义手动缩放与平移状态
            if (chkAutoFitY != null && !chkAutoFitY.Checked)
            {
                return;
            }

            double yMin = double.MaxValue;
            double yMax = double.MinValue;

            // 1. 精准取当前视口内可见的最多 1000 个 K 线数据点计算高低边界
            if (_historyKlines != null && _historyKlines.Count > 0)
            {
                int visibleCount = Math.Min(1000, _historyKlines.Count);
                int startIndex = _historyKlines.Count - visibleCount;

                for (int i = startIndex; i < _historyKlines.Count; i++)
                {
                    double close = (double)_historyKlines[i].Close;
                    if (close < yMin) yMin = close;
                    if (close > yMax) yMax = close;
                }
            }

            // 2. 如果历史数据为空，降级从 Streamer 原始数据数组计算
            if (yMin == double.MaxValue || yMax == double.MinValue)
            {
                double[] streamer1Data = Streamer1.Data.Data;
                if (streamer1Data != null && streamer1Data.Length > 0)
                {
                    for (int i = 0; i < streamer1Data.Length; i++)
                    {
                        double val = streamer1Data[i];
                        if (val != 0 && !double.IsNaN(val) && !double.IsInfinity(val))
                        {
                            if (val < yMin) yMin = val;
                            if (val > yMax) yMax = val;
                        }
                    }
                }
            }

            // 3. 动态更新 Y 轴可见极限 (按 8% 波幅留白，最小留白 10.0)
            if (yMin <= yMax && yMin != double.MaxValue)
            {
                double padding = Math.Max((yMax - yMin) * 0.08, 10.0);
                formsPlot1.Plot.Axes.SetLimitsY(yMin - padding, yMax + padding);
            }
        }

        private void FormsPlot1_MouseMove(object sender, MouseEventArgs e)
        {
            Pixel mousePixel = new Pixel(e.X, e.Y);
            Coordinates coordinates = formsPlot1.Plot.GetCoordinates(mousePixel);

            MyCrosshair.Position = coordinates;
            MyCrosshair.IsVisible = true;

            MyTooltipText.Location = coordinates;
            MyTooltipText.LabelText = $" X: {coordinates.X:F1}, Y: {coordinates.Y:F2} ";
            MyTooltipText.IsVisible = true;

            formsPlot1.Refresh();
        }

        private void FormsPlot1_MouseLeave(object sender, EventArgs e)
        {
            MyCrosshair.IsVisible = false;
            MyTooltipText.IsVisible = false;
            formsPlot1.Refresh();
        }

        /// <summary>
        /// 数据计算层：高低点 Pivot 计算、趋势线筛选算法与策略评测 (彻底与 UI 刷新解耦，在 AddNewDataTimer 数据 Tick 中完成)
        /// </summary>
        private void PerformTrendlineAnalysisAndEvaluation()
        {
            if (Streamer1.Data.CountTotal < 100) return;

            double[] streamer1Data = Streamer1.Data.Data;
            int length = streamer1Data.Length;
            int nextIndex = Streamer1.Data.NextIndex;

            if (_highsCache.Length < length)
            {
                _highsCache = new decimal[length];
                _lowsCache = new decimal[length];
            }

            for (int i = 0; i < length; i++)
            {
                int physicalIndex = (nextIndex + i) % length;
                _highsCache[i] = SafeToDecimal(streamer1Data[physicalIndex]);
                _lowsCache[i] = SafeToDecimal(streamer1Data[physicalIndex]);
            }

            // 1. 高速计算当前高低点 Pivot
            PivotHelper.CalculatePeaksFast(
                _highsCache.AsSpan(0, length),
                _lowsCache.AsSpan(0, length),
                _peaksBuffer,
                _valleysBuffer,
                leftLen: 5,
                rightLen: 5
            );

            // 2. 收集与计算高低点趋势线及策略评测
            ComputeAngleTrendLinesData(_peaksBuffer, _valleysBuffer, streamer1Data, nextIndex, length);

            // 3. 核心改进：计算与 Overlay 图层生成合并在同一次数据 Tick 中原子完成，彻底消除跨帧坐标偏移！
            RenderTrendlineOverlays();
        }

        private class AngleTrendLineInfo
        {
            public int Pivot1Index { get; set; }
            public int Pivot2Index { get; set; }
            public long Pivot1TimeMs { get; set; }
            public long Pivot2TimeMs { get; set; }
            public double X1 { get; set; }
            public double Y1 { get; set; }
            public double X2 { get; set; }
            public double Y2 { get; set; }
            public double K { get; set; }
            public double NormK { get; set; }
            public bool IsPeak { get; set; }
            public bool IsBroken { get; set; }
            public int TouchCount { get; set; } = 2; // 默认由 2 个极值点构成
            public bool Keep { get; set; }
            public bool IsLatest { get; set; }

            public double GetY(double x) => Y1 + K * (x - X1);
        }

        /// <summary>
        /// 数据层算法：收集趋势线候选集、破位校验、碰撞加权、夹角保留与策略评测
        /// </summary>
        private void ComputeAngleTrendLinesData(List<int> peakIndices, List<int> valleyIndices, double[] rawData, int nextIndex, int length)
        {
            if (peakIndices == null || valleyIndices == null) return;

            var peakLines = new List<AngleTrendLineInfo>();
            var valleyLines = new List<AngleTrendLineInfo>();

            // 1. 收集高点趋势线 (连接任意两个高点，包含 K < 0 向下与 K > 0 向上)
            for (int i = 0; i < peakIndices.Count - 1; i++)
            {
                for (int j = i + 1; j < peakIndices.Count; j++)
                {
                    int p1 = peakIndices[i];
                    int p2 = peakIndices[j];

                    double x1 = p1;
                    double y1 = rawData[(nextIndex + p1) % length];
                    double x2 = p2;
                    double y2 = rawData[(nextIndex + p2) % length];

                    if (Math.Abs(x2 - x1) < 2) continue;
                    double k = (y2 - y1) / (x2 - x1);
                    double normK = Math.Abs(k) / Math.Max(Math.Abs(y1), 1.0);

                    if (normK > 0.05) continue;

                    peakLines.Add(new AngleTrendLineInfo
                    {
                        Pivot1Index = p1,
                        Pivot2Index = p2,
                        X1 = x1,
                        Y1 = y1,
                        X2 = x2,
                        Y2 = y2,
                        K = k,
                        NormK = normK,
                        IsPeak = true
                    });
                }
            }

            // 2. 收集低点趋势线 (连接任意两个低点，包含 K > 0 向上与 K < 0 向下)
            for (int i = 0; i < valleyIndices.Count - 1; i++)
            {
                for (int j = i + 1; j < valleyIndices.Count; j++)
                {
                    int v1 = valleyIndices[i];
                    int v2 = valleyIndices[j];

                    double x1 = v1;
                    double y1 = rawData[(nextIndex + v1) % length];
                    double x2 = v2;
                    double y2 = rawData[(nextIndex + v2) % length];

                    if (Math.Abs(x2 - x1) < 2) continue;
                    double k = (y2 - y1) / (x2 - x1);
                    double normK = Math.Abs(k) / Math.Max(Math.Abs(y1), 1.0);

                    if (normK > 0.05) continue;

                    valleyLines.Add(new AngleTrendLineInfo
                    {
                        Pivot1Index = v1,
                        Pivot2Index = v2,
                        X1 = x1,
                        Y1 = y1,
                        X2 = x2,
                        Y2 = y2,
                        K = k,
                        NormK = normK,
                        IsPeak = false
                    });
                }
            }

            var allCandidates = peakLines.Concat(valleyLines).ToList();
            var allPivots = peakIndices.Select(p => (X: (double)p, Y: rawData[(nextIndex + p) % length]))
                .Concat(valleyIndices.Select(v => (X: (double)v, Y: rawData[(nextIndex + v) % length]))).ToList();

            // 3. 执行【价格穿透破位校验】与【碰撞触碰次数统计与加权】
            foreach (var line in allCandidates)
            {
                int startX = (int)Math.Max(0, line.X1);

                for (int x = startX + 1; x < length; x++)
                {
                    double price = rawData[(nextIndex + x) % length];
                    double lineY = line.GetY(x);

                    if (line.IsPeak && price > lineY + 1e-4)
                    {
                        line.IsBroken = true;
                        break;
                    }
                    else if (!line.IsPeak && price < lineY - 1e-4)
                    {
                        line.IsBroken = true;
                        break;
                    }
                }

                if (line.IsBroken) continue;

                foreach (var pivot in allPivots)
                {
                    if (Math.Abs(pivot.X - line.X1) < 1e-3 || Math.Abs(pivot.X - line.X2) < 1e-3) continue;

                    double expectedY = line.GetY(pivot.X);
                    double relDiff = Math.Abs(pivot.Y - expectedY) / Math.Max(Math.Abs(pivot.Y), 1.0);

                    if (relDiff <= 0.006)
                    {
                        line.TouchCount++;
                    }
                }

                if (line.TouchCount >= 3)
                {
                    line.Keep = true;
                }
            }

            // 4. 收敛夹角判定 (未破位且形成收敛交汇夹角的所有趋势线予以保留)
            var validPeakLines = peakLines.Where(d => !d.IsBroken).ToList();
            var validValleyLines = valleyLines.Where(u => !u.IsBroken).ToList();

            foreach (var peak in validPeakLines)
            {
                foreach (var valley in validValleyLines)
                {
                    if (Math.Abs(peak.K - valley.K) < 1e-5) continue;

                    double xIntersect = (valley.Y1 - peak.Y1 + peak.K * peak.X1 - valley.K * valley.X1) / (peak.K - valley.K);
                    double validStart = Math.Min(peak.X1, valley.X1);
                    if (xIntersect >= validStart)
                    {
                        peak.Keep = true;
                        valley.Keep = true;
                    }
                }
            }

            if (validPeakLines.Any(d => d.Keep))
            {
                validPeakLines.Where(d => d.Keep).Last().IsLatest = true;
            }
            if (validValleyLines.Any(u => u.Keep))
            {
                validValleyLines.Where(u => u.Keep).Last().IsLatest = true;
            }

            // 更新预计算出的有效线条缓存
            _cachedLinesToDraw.Clear();
            _cachedLinesToDraw.AddRange(allCandidates.Where(c => c.Keep && !c.IsBroken));

            // 5. 策略评测：统计【当前最新高点/低点】关联的有效趋势线数量并评测开仓
            double currentPrice = rawData[(nextIndex + length - 1) % length];
            _cachedActiveRedCount = validPeakLines.Count(d => d.Keep && !d.IsBroken);
            _cachedActiveGreenCount = validValleyLines.Count(u => u.Keep && !u.IsBroken);

            int latestPeakX = _peaksBuffer.Count > 0 ? _peaksBuffer[_peaksBuffer.Count - 1] : -1;
            int latestValleyX = _valleysBuffer.Count > 0 ? _valleysBuffer[_valleysBuffer.Count - 1] : -1;

            int latestPeakRedLinesCount = 0;
            if (latestPeakX >= 0)
            {
                latestPeakRedLinesCount = validPeakLines.Count(d => d.Keep && !d.IsBroken &&
                    (d.Pivot1Index == latestPeakX || d.Pivot2Index == latestPeakX));
            }

            int latestValleyGreenLinesCount = 0;
            if (latestValleyX >= 0)
            {
                latestValleyGreenLinesCount = validValleyLines.Count(u => u.Keep && !u.IsBroken &&
                    (u.Pivot1Index == latestValleyX || u.Pivot2Index == latestValleyX));
            }

            int totalKlinesCount = _historyKlines.Count;
            int latestKlineIndex = totalKlinesCount - 1;

            if (chkEnableStrategy != null && chkEnableStrategy.Checked)
            {
                _strategyEngine.Evaluate(latestKlineIndex, currentPrice, latestPeakX, latestPeakRedLinesCount, latestValleyX, latestValleyGreenLinesCount);
            }
        }

        /// <summary>
        /// UI 渲染层：轻量级读取 pre-calculated 预计算数据并渲染 UI 图层 (在 UpdatePlotTimer 中快速执行)
        /// </summary>
        private void RenderTrendlineOverlays()
        {
            string currentSignature = $"{_peaksBuffer.Count}_{(_peaksBuffer.Count > 0 ? _peaksBuffer[^1] : 0)}_{_valleysBuffer.Count}_{(_valleysBuffer.Count > 0 ? _valleysBuffer[^1] : 0)}";

            if (currentSignature == _lastPivotSignature && !_forceUpdatePivotOverlays)
            {
                return;
            }

            _lastPivotSignature = currentSignature;
            _forceUpdatePivotOverlays = false;

            double[] streamer1Data = Streamer1.Data.Data;
            int length = streamer1Data.Length;
            int nextIndex = Streamer1.Data.NextIndex;

            foreach (var item in _currentOverlayPlottables)
            {
                formsPlot1.Plot.Remove(item);
            }
            _currentOverlayPlottables.Clear();

            // 1. 渲染高点 Peak 标记 (红色圆圈)
            foreach (int peakIdx in _peaksBuffer)
            {
                int physicalIndex = (nextIndex + peakIdx) % length;
                double x = peakIdx;
                double y = streamer1Data[physicalIndex];

                var marker = formsPlot1.Plot.Add.Marker(x, y);
                marker.Shape = MarkerShape.FilledCircle;
                marker.Size = 4;
                marker.Color = ScottPlot.Colors.Red;
                _currentOverlayPlottables.Add(marker);
            }

            // 2. 渲染低点 Valley 标记 (绿色方块)
            foreach (int valleyIdx in _valleysBuffer)
            {
                int physicalIndex = (nextIndex + valleyIdx) % length;
                double x = valleyIdx;
                double y = streamer1Data[physicalIndex];

                var marker = formsPlot1.Plot.Add.Marker(x, y);
                marker.Shape = MarkerShape.FilledSquare;
                marker.Size = 4;
                marker.Color = ScottPlot.Colors.Green;
                _currentOverlayPlottables.Add(marker);
            }

            // 3. 渲染预计算出的趋势延伸线
            foreach (var lineData in _cachedLinesToDraw)
            {
                double xLeft = -5000;
                double yLeft = lineData.GetY(xLeft);
                double xRight = length - 1 + 5000;
                double yRight = lineData.GetY(xRight);

                var line = formsPlot1.Plot.Add.Line(xLeft, yLeft, xRight, yRight);
                float lineWidth = 0.5f;

                byte alpha = lineData.TouchCount switch
                {
                    >= 4 => (byte)255,
                    3 => (byte)200,
                    _ => lineData.IsLatest ? (byte)210 : (byte)110
                };

                if (lineData.IsPeak)
                {
                    line.LineStyle.Color = ScottPlot.Colors.Red.WithAlpha(alpha / 255.0f);
                    line.LineStyle.Width = lineWidth;
                }
                else
                {
                    line.LineStyle.Color = ScottPlot.Colors.Green.WithAlpha(alpha / 255.0f);
                    line.LineStyle.Width = lineWidth;
                }

                line.LineStyle.Pattern = LinePattern.Solid;
                _currentOverlayPlottables.Add(line);
            }

            // 4. 渲染交易 Marker 标记与气泡文本
            double currentPrice = streamer1Data[(nextIndex + length - 1) % length];
            int totalKlinesCount = _historyKlines.Count;
            int latestKlineIndex = totalKlinesCount - 1;

            double yMinVal = double.MaxValue;
            double yMaxVal = double.MinValue;
            if (_historyKlines != null && _historyKlines.Count > 0)
            {
                int visCount = Math.Min(1000, _historyKlines.Count);
                int startIdx = _historyKlines.Count - visCount;
                for (int i = startIdx; i < _historyKlines.Count; i++)
                {
                    double closeVal = (double)_historyKlines[i].Close;
                    if (closeVal < yMinVal) yMinVal = closeVal;
                    if (closeVal > yMaxVal) yMaxVal = closeVal;
                }
            }
            if (yMinVal == double.MaxValue || yMaxVal == double.MinValue)
            {
                yMinVal = currentPrice - 100;
                yMaxVal = currentPrice + 100;
            }
            double priceRange = Math.Max(yMaxVal - yMinVal, 10.0);
            double verticalOffset = priceRange * 0.015;

            foreach (var trade in _strategyEngine.CompletedTrades)
            {
                int entryBarsAgo = latestKlineIndex - trade.EntryKlineIndex;
                double entryX = (length - 1) - entryBarsAgo;

                if (entryX >= 0 && entryX < length)
                {
                    var openMarker = formsPlot1.Plot.Add.Marker(entryX, trade.EntryPrice);
                    openMarker.Size = 10;
                    if (trade.Type == StrategyPositionType.Long)
                    {
                        openMarker.Shape = MarkerShape.FilledTriangleUp;
                        openMarker.Color = ScottPlot.Colors.Green;
                        double textY = trade.EntryPrice - verticalOffset * 1.5;
                        var openText = formsPlot1.Plot.Add.Text($"[BUY LONG] Open @ {trade.EntryPrice:F1}", entryX, textY);
                        openText.LabelFontSize = 9;
                        openText.LabelFontColor = ScottPlot.Colors.DarkGreen;
                        openText.LabelBackgroundColor = ScottPlot.Colors.White.WithAlpha(0.9);
                        openText.LabelBorderColor = ScottPlot.Colors.DarkGreen;
                        openText.LabelBorderWidth = 1f;
                        openText.LabelAlignment = ScottPlot.Alignment.UpperCenter;
                        _currentOverlayPlottables.Add(openText);
                    }
                    else
                    {
                        openMarker.Shape = MarkerShape.FilledTriangleDown;
                        openMarker.Color = ScottPlot.Colors.Red;
                        double textY = trade.EntryPrice + verticalOffset * 1.5;
                        var openText = formsPlot1.Plot.Add.Text($"[SELL SHORT] Open @ {trade.EntryPrice:F1}", entryX, textY);
                        openText.LabelFontSize = 9;
                        openText.LabelFontColor = ScottPlot.Colors.DarkRed;
                        openText.LabelBackgroundColor = ScottPlot.Colors.White.WithAlpha(0.9);
                        openText.LabelBorderColor = ScottPlot.Colors.DarkRed;
                        openText.LabelBorderWidth = 1f;
                        openText.LabelAlignment = ScottPlot.Alignment.LowerCenter;
                        _currentOverlayPlottables.Add(openText);
                    }
                    _currentOverlayPlottables.Add(openMarker);
                }

                int exitBarsAgo = latestKlineIndex - trade.ExitKlineIndex;
                double exitX = (length - 1) - exitBarsAgo;

                if (exitX >= 0 && exitX < length)
                {
                    var exitMarker = formsPlot1.Plot.Add.Marker(exitX, trade.ExitPrice);
                    exitMarker.Size = 10;
                    if (trade.IsProfit)
                    {
                        exitMarker.Shape = MarkerShape.FilledDiamond;
                        exitMarker.Color = ScottPlot.Colors.Gold;
                        double textY = trade.ExitPrice + verticalOffset * 2.8;
                        var exitText = formsPlot1.Plot.Add.Text($"{trade.ExitReason} @ {trade.ExitPrice:F1}", exitX, textY);
                        exitText.LabelFontSize = 9;
                        exitText.LabelFontColor = ScottPlot.Colors.DarkGoldenRod;
                        exitText.LabelBackgroundColor = ScottPlot.Colors.White.WithAlpha(0.9);
                        exitText.LabelBorderColor = ScottPlot.Colors.Gold;
                        exitText.LabelBorderWidth = 1f;
                        exitText.LabelAlignment = ScottPlot.Alignment.LowerCenter;
                        _currentOverlayPlottables.Add(exitText);
                    }
                    else
                    {
                        exitMarker.Shape = MarkerShape.Cross;
                        exitMarker.Color = ScottPlot.Colors.Purple;
                        double textY = trade.ExitPrice - verticalOffset * 2.8;
                        var exitText = formsPlot1.Plot.Add.Text($"{trade.ExitReason} @ {trade.ExitPrice:F1}", exitX, textY);
                        exitText.LabelFontSize = 9;
                        exitText.LabelFontColor = ScottPlot.Colors.Purple;
                        exitText.LabelBackgroundColor = ScottPlot.Colors.White.WithAlpha(0.9);
                        exitText.LabelBorderColor = ScottPlot.Colors.Purple;
                        exitText.LabelBorderWidth = 1f;
                        exitText.LabelAlignment = ScottPlot.Alignment.UpperCenter;
                        _currentOverlayPlottables.Add(exitText);
                    }
                    _currentOverlayPlottables.Add(exitMarker);
                }
            }

            if (_strategyEngine.CurrentPosition != StrategyPositionType.None)
            {
                int activeBarsAgo = latestKlineIndex - _strategyEngine.EntryKlineIndex;
                double activeX = (length - 1) - activeBarsAgo;

                if (activeX >= 0 && activeX < length)
                {
                    var activeOpenMarker = formsPlot1.Plot.Add.Marker(activeX, _strategyEngine.EntryPrice);
                    activeOpenMarker.Size = 12;

                    if (_strategyEngine.CurrentPosition == StrategyPositionType.Long)
                    {
                        activeOpenMarker.Shape = MarkerShape.FilledTriangleUp;
                        activeOpenMarker.Color = ScottPlot.Colors.Green;
                        double textY = _strategyEngine.EntryPrice - verticalOffset * 1.5;
                        var activeText = formsPlot1.Plot.Add.Text($"[HOLD LONG] Entry @ {_strategyEngine.EntryPrice:F1}", activeX, textY);
                        activeText.LabelFontSize = 10;
                        activeText.LabelFontColor = ScottPlot.Colors.White;
                        activeText.LabelBackgroundColor = ScottPlot.Colors.Green;
                        activeText.LabelBorderColor = ScottPlot.Colors.DarkGreen;
                        activeText.LabelBorderWidth = 1f;
                        activeText.LabelAlignment = ScottPlot.Alignment.UpperCenter;
                        _currentOverlayPlottables.Add(activeText);
                    }
                    else
                    {
                        activeOpenMarker.Shape = MarkerShape.FilledTriangleDown;
                        activeOpenMarker.Color = ScottPlot.Colors.Red;
                        double textY = _strategyEngine.EntryPrice + verticalOffset * 1.5;
                        var activeText = formsPlot1.Plot.Add.Text($"[HOLD SHORT] Entry @ {_strategyEngine.EntryPrice:F1}", activeX, textY);
                        activeText.LabelFontSize = 10;
                        activeText.LabelFontColor = ScottPlot.Colors.White;
                        activeText.LabelBackgroundColor = ScottPlot.Colors.Red;
                        activeText.LabelBorderColor = ScottPlot.Colors.DarkRed;
                        activeText.LabelBorderWidth = 1f;
                        activeText.LabelAlignment = ScottPlot.Alignment.LowerCenter;
                        _currentOverlayPlottables.Add(activeText);
                    }
                    _currentOverlayPlottables.Add(activeOpenMarker);
                }

                var tpLine = formsPlot1.Plot.Add.Line(-5000, _strategyEngine.TakeProfitPrice, length + 5000, _strategyEngine.TakeProfitPrice);
                tpLine.LineStyle.Color = ScottPlot.Colors.Gold;
                tpLine.LineStyle.Pattern = LinePattern.Dashed;
                _currentOverlayPlottables.Add(tpLine);

                var slLine = formsPlot1.Plot.Add.Line(-5000, _strategyEngine.StopLossPrice, length + 5000, _strategyEngine.StopLossPrice);
                slLine.LineStyle.Color = ScottPlot.Colors.Purple;
                slLine.LineStyle.Pattern = LinePattern.Dashed;
                _currentOverlayPlottables.Add(slLine);
            }

            // 5. 统计策略状态面板与战绩 (胜率与交易统计始终显示)
            int totalTrades = _strategyEngine.CompletedTrades.Count;
            int winCount = _strategyEngine.CompletedTrades.Count(t => t.IsProfit);
            int lossCount = totalTrades - winCount;
            double winRate = totalTrades > 0 ? (double)winCount / totalTrades * 100 : 0;
            string winRateStats = $"Trades: {totalTrades} (Win: {winCount} Loss: {lossCount} WinRate: {winRate:F0}%)";

            if (chkEnableStrategy != null && !chkEnableStrategy.Checked)
            {
                lblTrendState.Text = $"[STRATEGY: DISABLED]\n(Strategy Auto-Trade Paused)\n{winRateStats}";
                lblTrendState.BackColor = Color.FromArgb(245, 245, 245);
                lblTrendState.ForeColor = Color.Gray;
            }
            else if (_strategyEngine.CurrentPosition == StrategyPositionType.Long)
            {
                lblTrendState.Text = $"[STRATEGY: LONG] Entry: {_strategyEngine.EntryPrice:F1}\nTP 1%: {_strategyEngine.TakeProfitPrice:F1} | SL 1%: {_strategyEngine.StopLossPrice:F1}\n{winRateStats}";
                lblTrendState.BackColor = Color.FromArgb(230, 255, 230);
                lblTrendState.ForeColor = Color.DarkGreen;
            }
            else if (_strategyEngine.CurrentPosition == StrategyPositionType.Short)
            {
                lblTrendState.Text = $"[STRATEGY: SHORT] Entry: {_strategyEngine.EntryPrice:F1}\nTP 1%: {_strategyEngine.TakeProfitPrice:F1} | SL 1%: {_strategyEngine.StopLossPrice:F1}\n{winRateStats}";
                lblTrendState.BackColor = Color.FromArgb(255, 230, 230);
                lblTrendState.ForeColor = Color.DarkRed;
            }
            else
            {
                lblTrendState.Text = $"[STRATEGY: MONITORING]\nRed Lines: {_cachedActiveRedCount} | Green Lines: {_cachedActiveGreenCount}\n{winRateStats}";
                lblTrendState.BackColor = Color.FromArgb(245, 245, 245);
                lblTrendState.ForeColor = Color.DimGray;
            }
        }

        private static decimal SafeToDecimal(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0m;

            if (value >= (double)decimal.MaxValue)
                return decimal.MaxValue;

            if (value <= (double)decimal.MinValue)
                return decimal.MinValue;

            return (decimal)value;
        }

        #region 运行控制 (暂停 / 恢复)

        public void TogglePause()
        {
            AddNewDataTimer.Enabled = !AddNewDataTimer.Enabled;
        }

        #endregion

        private void btn_puase_Click(object sender, EventArgs e)
        {
            TogglePause();
            btn_puase.Text = AddNewDataTimer.Enabled ? "暂停数据播放" : "恢复数据播放";
        }
    }

    #region 趋势线量化策略引擎类

    public enum StrategyPositionType
    {
        None,
        Long,
        Short
    }

    public class TradeRecord
    {
        public StrategyPositionType Type { get; set; }
        public double EntryPrice { get; set; }
        public int EntryKlineIndex { get; set; } // 对应 _historyKlines 中的绝对全局索引
        public double ExitPrice { get; set; }
        public int ExitKlineIndex { get; set; }  // 对应 _historyKlines 中的绝对全局索引
        public bool IsProfit { get; set; }
        public double ProfitPct { get; set; }
        public string ExitReason { get; set; } = string.Empty;
    }

    public class TrendlineStrategyEngine
    {
        public StrategyPositionType CurrentPosition { get; private set; } = StrategyPositionType.None;
        public double EntryPrice { get; private set; }
        public int EntryKlineIndex { get; private set; }
        public double TakeProfitPrice { get; private set; }
        public double StopLossPrice { get; private set; }

        public event Action<TradeRecord>? OnTradeClosed;
        public event Action<StrategyPositionType, double, int>? OnTradeOpened;

        private int _lastEvaluatedPeakX = -1;
        private int _lastEvaluatedValleyX = -1;

        private readonly int _triggerCount = 9;
        public List<TradeRecord> CompletedTrades { get; } = new();

        public void Reset()
        {
            CurrentPosition = StrategyPositionType.None;
            EntryPrice = 0;
            EntryKlineIndex = 0;
            TakeProfitPrice = 0;
            StopLossPrice = 0;
            _lastEvaluatedPeakX = -1;
            _lastEvaluatedValleyX = -1;
            CompletedTrades.Clear();
        }

        public void Evaluate(int currentKlineIndex, double currentPrice, int latestPeakX, int latestPeakRedLinesCount, int latestValleyX, int latestValleyGreenLinesCount)
        {
            if (currentKlineIndex < 0) return;

            // 1. 校验现有持仓的 1.0% 止盈 / 1.0% 止损
            if (CurrentPosition == StrategyPositionType.Long)
            {
                if (currentPrice >= TakeProfitPrice)
                {
                    var record = new TradeRecord
                    {
                        Type = StrategyPositionType.Long,
                        EntryPrice = EntryPrice,
                        EntryKlineIndex = EntryKlineIndex,
                        ExitPrice = currentPrice,
                        ExitKlineIndex = currentKlineIndex,
                        IsProfit = true,
                        ProfitPct = (currentPrice - EntryPrice) / EntryPrice * 100,
                        ExitReason = "[TP (+1.0%)]"
                    };
                    CompletedTrades.Add(record);
                    CurrentPosition = StrategyPositionType.None;
                    OnTradeClosed?.Invoke(record);
                }
                else if (currentPrice <= StopLossPrice)
                {
                    var record = new TradeRecord
                    {
                        Type = StrategyPositionType.Long,
                        EntryPrice = EntryPrice,
                        EntryKlineIndex = EntryKlineIndex,
                        ExitPrice = currentPrice,
                        ExitKlineIndex = currentKlineIndex,
                        IsProfit = false,
                        ProfitPct = (currentPrice - EntryPrice) / EntryPrice * 100,
                        ExitReason = "[SL (-1.0%)]"
                    };
                    CompletedTrades.Add(record);
                    CurrentPosition = StrategyPositionType.None;
                    OnTradeClosed?.Invoke(record);
                }
            }
            else if (CurrentPosition == StrategyPositionType.Short)
            {
                if (currentPrice <= TakeProfitPrice)
                {
                    var record = new TradeRecord
                    {
                        Type = StrategyPositionType.Short,
                        EntryPrice = EntryPrice,
                        EntryKlineIndex = EntryKlineIndex,
                        ExitPrice = currentPrice,
                        ExitKlineIndex = currentKlineIndex,
                        IsProfit = true,
                        ProfitPct = (EntryPrice - currentPrice) / EntryPrice * 100,
                        ExitReason = "[TP (+1.0%)]"
                    };
                    CompletedTrades.Add(record);
                    CurrentPosition = StrategyPositionType.None;
                    OnTradeClosed?.Invoke(record);
                }
                else if (currentPrice >= StopLossPrice)
                {
                    var record = new TradeRecord
                    {
                        Type = StrategyPositionType.Short,
                        EntryPrice = EntryPrice,
                        EntryKlineIndex = EntryKlineIndex,
                        ExitPrice = currentPrice,
                        ExitKlineIndex = currentKlineIndex,
                        IsProfit = false,
                        ProfitPct = (EntryPrice - currentPrice) / EntryPrice * 100,
                        ExitReason = "[SL (-1.0%)]"
                    };
                    CompletedTrades.Add(record);
                    CurrentPosition = StrategyPositionType.None;
                    OnTradeClosed?.Invoke(record);
                }
            }

            // 2. 如果当前无持仓，校验开仓规则：
            if (CurrentPosition == StrategyPositionType.None)
            {
                bool triggerShort = latestPeakX >= 0 && latestPeakX != _lastEvaluatedPeakX && latestPeakRedLinesCount >= _triggerCount;
                bool triggerLong = latestValleyX >= 0 && latestValleyX != _lastEvaluatedValleyX && latestValleyGreenLinesCount >= _triggerCount;

                if (triggerShort)
                {
                    _lastEvaluatedPeakX = latestPeakX;
                    CurrentPosition = StrategyPositionType.Short;
                    EntryPrice = currentPrice;
                    EntryKlineIndex = currentKlineIndex;
                    TakeProfitPrice = currentPrice * 0.99; // 1% 止盈
                    StopLossPrice = currentPrice * 1.01;   // 1% 止损
                    OnTradeOpened?.Invoke(StrategyPositionType.Short, currentPrice, currentKlineIndex);
                }
                else if (triggerLong)
                {
                    _lastEvaluatedValleyX = latestValleyX;
                    CurrentPosition = StrategyPositionType.Long;
                    EntryPrice = currentPrice;
                    EntryKlineIndex = currentKlineIndex;
                    TakeProfitPrice = currentPrice * 1.01; // 1% 止盈
                    StopLossPrice = currentPrice * 0.99;   // 1% 止损
                    OnTradeOpened?.Invoke(StrategyPositionType.Long, currentPrice, currentKlineIndex);
                }
            }
        }
    }

    #endregion

    public class RandomWalker
    {
        private readonly Random _rand;
        private double _lastValue = 0;
        private readonly double _mult;

        public RandomWalker(int seed = 0, double mult = 1)
        {
            _rand = new Random(seed);
            _mult = mult;
        }

        public double[] Next(int count)
        {
            double[] values = new double[count];
            for (int i = 0; i < count; i++)
            {
                _lastValue += (_rand.NextDouble() - 0.5) * _mult;
                _lastValue = Math.Clamp(_lastValue, -100000.0, 100000.0);
                values[i] = _lastValue;
            }
            return values;
        }
    }
}