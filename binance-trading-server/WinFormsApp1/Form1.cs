using ConsoleApp1;
using ScottPlot;
using ScottPlot.Colormaps;
using ScottPlot.Plottables;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

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

        // 按需/事件驱动图层渲染签名
        private string _lastPivotSignature = string.Empty;
        private bool _forceUpdatePivotOverlays = false;

        public Form1()
        {
            InitializeComponent();

            // 1. 初始化 ComboBox 默认选项
            InitControls();

            // 2. 初始化 ScottPlot DataStreamer (1000 数据点)
            Streamer1 = formsPlot1.Plot.Add.DataStreamer(1000);
            Streamer1.ViewScrollLeft();
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

            // 4. 定时器 1：从 ConcurrentQueue 队列中消费 K 线数据并接入 ScottPlot
            AddNewDataTimer.Interval = 15; // 15ms
            AddNewDataTimer.Tick += (s, e) =>
            {
                if (!_klineQueue.IsEmpty)
                {
                    // 批量从队列中提取数据点接入 ScottPlot
                    int batchSize = chkAutoPlay.Checked ? 3 : _klineQueue.Count;
                    List<double> valuesToAdd = new();

                    for (int i = 0; i < batchSize && _klineQueue.TryDequeue(out var kline); i++)
                    {
                        valuesToAdd.Add((double)kline.Close);
                    }

                    if (valuesToAdd.Count > 0)
                    {
                        Streamer1.AddRange(valuesToAdd);
                    }
                }
                //else if (chkAutoPlay.Checked && _totalEnqueuedCount == 0)
                //{
                //    // 如果队列为空且没有在线请求，生成备用演示数据
                //    Streamer1.AddRange(Walker1.Next(2));
                //}
            };

            // 5. 定时器 2：UI 渲染刷新
            UpdatePlotTimer.Interval = 30; // 30ms (~33 FPS)
            UpdatePlotTimer.Tick += (s, e) =>
            {
                if (Streamer1.HasNewData)
                {
                    long totalCount = Streamer1.Data.CountTotal;
                    formsPlot1.Plot.Title($"[{_currentSymbol}] 已接入数据点: {totalCount:N0} | 队列剩余: {_klineQueue.Count}");

                    if (totalCount >= 100)
                    {
                        UpdatePivotMarkersAndLines();
                    }

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
            if (cmbInterval.SelectedIndex < 0) cmbInterval.SelectedIndex = 3; // 默认 15m
            if (cmbTimeRange.SelectedIndex < 0) cmbTimeRange.SelectedIndex = 2; // 默认 最近24小时
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
            lblStatus.Text = $"正在通过多线程并发下载 [{symbol}] {intervalStr} 历史数据...";

            // 清空当前队列
            while (_klineQueue.TryDequeue(out _)) { }
            _totalEnqueuedCount = 0;

            try
            {
                int fetchedCount = 0;
                var progress = new Progress<FetchStatusReport>(report =>
                {
                    lblStatus.Text = $"状态: {report.Message}\n正在排队/下载: {report.Symbol}\n" +
                                     $"时间段: {report.StartTime:MM-dd HH:mm} ~ {report.EndTime:MM-dd HH:mm}";
                });

                // 使用【生产者-消费者模式】有序流式拉取数据，并实时压入 ConcurrentQueue 队列
                await foreach (var kline in _dataProvider.StreamSymbolDataChronologicalAsync(
                    symbol, interval, startTime, endTime, token))
                {
                    _klineQueue.Enqueue(kline);
                    Interlocked.Increment(ref _totalEnqueuedCount);
                    fetchedCount++;

                    if (fetchedCount % 100 == 0)
                    {
                        lblStatus.Text = $"已通过生产者-消费者队列接收 [{symbol}] {fetchedCount} 条历史 K 线...";
                    }
                }

                lblStatus.Text = $"数据获取成功！\n币种: {symbol}\n周期: {intervalStr}\n总计: {fetchedCount} 条 K 线\n正在接入 ScottPlot 实时播放...";
            }
            catch (OperationCanceledException)
            {
                lblStatus.Text = "已取消获取数据";
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"获取出错: {ex.Message}";
                MessageBox.Show($"获取历史数据失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnFetch.Enabled = true;
            }
        }

        /// <summary>
        /// 重置/复位图表按钮事件 handler
        /// </summary>
        private void button1_Click(object sender, EventArgs e)
        {
            // 1. 取消在途的网络下载任务
            _fetchCts?.Cancel();

            // 2. 清空 ConcurrentQueue 数据队列
            while (_klineQueue.TryDequeue(out _)) { }
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

            // 5. 复位视图与自动缩放
            //formsPlot1.Plot.Axes.Autoscale();
            formsPlot1.Plot.Title("图表已重置");

            // 6. 恢复按钮与状态
            btnFetch.Enabled = true;
            lblStatus.Text = "图表与队列数据已重置完成，请重新选择币种后点击【获取币种历史数据】。";

            // 7. 刷新界面
            formsPlot1.Refresh();
        }
        /// <summary>
        /// 动态设置 Y 轴可见范围为当前数据 Y 最小值 - 1000 到 Y 最大值 + 1000
        /// </summary>
        private void UpdateYAxisLimits()
        {
            double[] streamer1Data = Streamer1.Data.Data;
            if (streamer1Data == null || streamer1Data.Length == 0) return;

            double yMin = double.MaxValue;
            double yMax = double.MinValue;

            for (int i = 0; i < streamer1Data.Length; i++)
            {
                double val = streamer1Data[i];
                if (val != 0 && !double.IsNaN(val) && !double.IsInfinity(val))
                {
                    if (val < yMin) yMin = val;
                    if (val > yMax) yMax = val;
                }
            }

            if (yMin <= yMax && yMin != double.MaxValue)
            {
                double targetMin = yMin - 1000;
                double targetMax = yMax + 1000;
                formsPlot1.Plot.Axes.SetLimitsY(targetMin, targetMax);
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

        private void UpdatePivotMarkersAndLines()
        {
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

            // 2. 检查极值点结构签名 (生成由高低点个数与最新极值点索引组成的 key)
            string currentSignature = $"{_peaksBuffer.Count}_{(_peaksBuffer.Count > 0 ? _peaksBuffer[^1] : 0)}_{_valleysBuffer.Count}_{(_valleysBuffer.Count > 0 ? _valleysBuffer[^1] : 0)}";

            // 3. 【按需/事件驱动懒渲染】：若高低点结构没有任何改变，无需重复擦除与绘制趋势线，直接跳过！
            if (currentSignature == _lastPivotSignature && !_forceUpdatePivotOverlays)
            {
                return;
            }

            _lastPivotSignature = currentSignature;
            _forceUpdatePivotOverlays = false;

            // 4. 仅当有新高点/低点出现时，重新刷新渲染 Marker 标记与趋势延伸线
            foreach (var item in _currentOverlayPlottables)
            {
                formsPlot1.Plot.Remove(item);
            }
            _currentOverlayPlottables.Clear();

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

            // 5. 绘制产生夹角的最近高点向下趋势线与低点向上趋势线
            DrawAngleTrendLines(_peaksBuffer, _valleysBuffer, streamer1Data, nextIndex, length);
        }

        private class AngleTrendLineInfo
        {
            public double X1 { get; set; }
            public double Y1 { get; set; }
            public double X2 { get; set; }
            public double Y2 { get; set; }
            public double K { get; set; }
            public bool IsPeak { get; set; }
            public bool Keep { get; set; }
            public bool IsLatest { get; set; }
        }

        /// <summary>
        /// 趋势线规则：
        /// 1. 收集所有高点向下趋势线 (Resistance: Slope < 0) 与低点向上趋势线 (Support: Slope > 0)
        /// 2. 凡是与对侧趋势线产生夹角 (楔形/三角形收敛交汇) 的历史趋势线与最新趋势线均予以保留绘制
        /// </summary>
        private void DrawAngleTrendLines(List<int> peakIndices, List<int> valleyIndices, double[] rawData, int nextIndex, int length)
        {
            if (peakIndices == null || valleyIndices == null) return;

            var downPeakLines = new List<AngleTrendLineInfo>();
            var upValleyLines = new List<AngleTrendLineInfo>();

            // 1. 收集所有高点向下的历史趋势线 (Slope < 0)
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

                    if (Math.Abs(x2 - x1) < 1e-5) continue;
                    double k = (y2 - y1) / (x2 - x1);

                    if (k < 0) // 高点向下
                    {
                        downPeakLines.Add(new AngleTrendLineInfo
                        {
                            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, K = k, IsPeak = true, Keep = false
                        });
                    }
                }
            }

            // 2. 收集所有低点向上的历史趋势线 (Slope > 0)
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

                    if (Math.Abs(x2 - x1) < 1e-5) continue;
                    double k = (y2 - y1) / (x2 - x1);

                    if (k > 0) // 低点向上
                    {
                        upValleyLines.Add(new AngleTrendLineInfo
                        {
                            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, K = k, IsPeak = false, Keep = false
                        });
                    }
                }
            }

            // 3. 匹配夹角：当任意高点向下线与低点向上线产生收敛交汇（交点 X >= 起始点）时，标记保留
            foreach (var down in downPeakLines)
            {
                foreach (var up in upValleyLines)
                {
                    double xIntersect = (up.Y1 - down.Y1 + down.K * down.X1 - up.K * up.X1) / (down.K - up.K);

                    double validStart = Math.Min(down.X1, up.X1);
                    if (xIntersect >= validStart)
                    {
                        down.Keep = true;
                        up.Keep = true;
                    }
                }
            }

            // 标记最新的线以使用高亮样式
            if (downPeakLines.Any(d => d.Keep))
            {
                downPeakLines.Where(d => d.Keep).Last().IsLatest = true;
            }
            if (upValleyLines.Any(u => u.Keep))
            {
                upValleyLines.Where(u => u.Keep).Last().IsLatest = true;
            }

            // 4. 渲染所有保留的历史与最新夹角趋势线
            var allKeepLines = downPeakLines.Where(d => d.Keep).Concat(upValleyLines.Where(u => u.Keep));

            foreach (var lineData in allKeepLines)
            {
                double xLeft = -5000;
                double yLeft = lineData.Y1 + lineData.K * (xLeft - lineData.X1);
                double xRight = length - 1 + 5000;
                double yRight = lineData.Y1 + lineData.K * (xRight - lineData.X1);

                var line = formsPlot1.Plot.Add.Line(xLeft, yLeft, xRight, yRight);

                if (lineData.IsPeak)
                {
                    line.LineStyle.Color = lineData.IsLatest
                        ? ScottPlot.Colors.Red.WithAlpha(0.9)
                        : ScottPlot.Colors.Red.WithAlpha(0.35);
                    line.LineStyle.Width = lineData.IsLatest ? 1.8f : 1.0f;
                }
                else
                {
                    line.LineStyle.Color = lineData.IsLatest
                        ? ScottPlot.Colors.Green.WithAlpha(0.9)
                        : ScottPlot.Colors.Green.WithAlpha(0.35);
                    line.LineStyle.Width = lineData.IsLatest ? 1.8f : 1.0f;
                }

                line.LineStyle.Pattern = LinePattern.Solid;
                _currentOverlayPlottables.Add(line);
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