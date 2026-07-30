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
        readonly System.Windows.Forms.Timer UpdatePlotTimer = new() { Interval = 30, Enabled = true };

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

                    if (totalCount >= 500)
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
            foreach (var item in _currentOverlayPlottables)
            {
                formsPlot1.Plot.Remove(item);
            }
            _currentOverlayPlottables.Clear();

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

            PivotHelper.CalculatePeaksFast(
                _highsCache.AsSpan(0, length),
                _lowsCache.AsSpan(0, length),
                _peaksBuffer,
                _valleysBuffer,
                leftLen: 5,
                rightLen: 5
            );

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

            DrawExtensionLines(_peaksBuffer, streamer1Data, nextIndex, length, ScottPlot.Colors.Red.WithAlpha(0.6));
            DrawExtensionLines(_valleysBuffer, streamer1Data, nextIndex, length, ScottPlot.Colors.Green.WithAlpha(0.6));
        }


        private void DrawExtensionLines(List<int> pivotIndices, double[] rawData, int nextIndex, int length, ScottPlot.Color lineColor)
        {
            if (pivotIndices.Count < 2) return;

            for (int i = 0; i < pivotIndices.Count - 1; i++)
            {
                int idx1 = pivotIndices[i];
                int idx2 = pivotIndices[i + 1];

                double x1 = idx1;
                double y1 = rawData[(nextIndex + idx1) % length];

                double x2 = idx2;
                double y2 = rawData[(nextIndex + idx2) % length];

                if (Math.Abs(x2 - x1) < 1e-5) continue;

                double k = (y2 - y1) / (x2 - x1);
                double xEnd = length - 1;
                double yEnd = y1 + k * (xEnd - x1);

                var line = formsPlot1.Plot.Add.Line(x1, y1, xEnd, yEnd);
                line.LineStyle.Color = lineColor;
                line.LineStyle.Width = 1f;
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