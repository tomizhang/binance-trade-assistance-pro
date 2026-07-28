// 假设 PivotHelper 位于 ConsoleApp1 命名空间中
using ConsoleApp1;
using ScottPlot;
using ScottPlot.Colormaps;
using ScottPlot.Plottables;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace WinFormsApp1
{
    public partial class Form1 : Form
    {
        readonly System.Windows.Forms.Timer AddNewDataTimer = new() { Interval = 10, Enabled = true };
        readonly System.Windows.Forms.Timer UpdatePlotTimer = new() { Interval = 50, Enabled = true };

        // 声明类成员变量
        private DataStreamer Streamer1;
        private VerticalLine VLine;

        // 【新增变量】用于移动时显示点位的 Crosshair (十字准星) 和文本标注
        private Crosshair MyCrosshair;
        private ScottPlot.Plottables.Text MyTooltipText;

        // 模拟数据生成器
        private RandomWalker Walker1 = new RandomWalker(seed: 0, mult: 1);

        // --- 为了性能优化而复用的内存缓冲区，避免 GC 频繁触发 ---
        private readonly List<int> _peaksBuffer = new(64);
        private readonly List<int> _valleysBuffer = new(64);

        // 用于管理当前画面中绘制的 Marker 和延伸线
        private readonly List<IPlottable> _currentOverlayPlottables = new(256);

        private decimal[] _highsCache = new decimal[1000];
        private decimal[] _lowsCache = new decimal[1000];

        public Form1()
        {
            InitializeComponent();

            // 1. 创建 DataStreamer，容量设置为 1000 个数据点
            Streamer1 = formsPlot1.Plot.Add.DataStreamer(1000);

            // 2. 默认设置为【向左平滑滚动模式】
            Streamer1.ViewScrollLeft();

            // 3. 样式配置
            Streamer1.LineStyle.Color = ScottPlot.Colors.Blue;
            Streamer1.LegendText = "信号通道 A (高点检测)";

            // 4. 添加垂直指示线
            VLine = formsPlot1.Plot.Add.VerticalLine(0, 2, ScottPlot.Colors.Red);
            VLine.IsVisible = false;

            // 【修复问题 1】：启用交互（允许鼠标滚轮放大缩小、右键拖拽缩放等）
            formsPlot1.UserInputProcessor.Enable();

            // 【功能 2】：添加 Crosshair 十字指示线
            MyCrosshair = formsPlot1.Plot.Add.Crosshair(0, 0);
            MyCrosshair.IsVisible = false; // 初始隐藏
            MyCrosshair.LineColor = ScottPlot.Colors.DarkGray.WithAlpha(0.8);
            MyCrosshair.LineWidth = 1f;

            // 【修复问题 2】：创建独立的 Text 对象用于在准星旁显示坐标点位信息
            MyTooltipText = formsPlot1.Plot.Add.Text("", 0, 0);
            MyTooltipText.IsVisible = false;
            MyTooltipText.LabelFontSize = 12;
            MyTooltipText.LabelFontColor = ScottPlot.Colors.Black;
            MyTooltipText.LabelBackgroundColor = ScottPlot.Colors.Yellow.WithAlpha(0.8);

            // 【功能 2】：绑定鼠标移动与离开事件
            formsPlot1.MouseMove += FormsPlot1_MouseMove;
            formsPlot1.MouseLeave += FormsPlot1_MouseLeave;

            // 7. Timer 1：数据采集定时器
            AddNewDataTimer.Interval = 20; // 20ms
            AddNewDataTimer.Tick += (s, e) =>
            {
                int count = 5; // 每次追加 5 个点
                Streamer1.AddRange(Walker1.Next(count));
            };

            // 8. Timer 2：UI 渲染刷新定时器
            UpdatePlotTimer.Interval = 30; // 30ms (~33 FPS)
            UpdatePlotTimer.Tick += (s, e) =>
            {
                if (Streamer1.HasNewData)
                {
                    long totalCount = Streamer1.Data.CountTotal;
                    formsPlot1.Plot.Title($"已处理数据点: {totalCount:N0}");

                    // 当数据量达到 500 个以上时，实时计算高低点并绘制延伸线
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

                    formsPlot1.Refresh();
                }
            };

            AddNewDataTimer.Start();
            UpdatePlotTimer.Start();
        }

        #region 【功能 2 交互扩展】：鼠标移动显示点位信息

        private void FormsPlot1_MouseMove(object sender, MouseEventArgs e)
        {
            // 转换为 ScottPlot 坐标系的 Pixel 格式
            Pixel mousePixel = new Pixel(e.X, e.Y);

            // 获取鼠标在当前图表中的逻辑数据坐标 (X, Y)
            Coordinates coordinates = formsPlot1.Plot.GetCoordinates(mousePixel);

            // 更新 Crosshair 位置
            MyCrosshair.Position = coordinates;
            MyCrosshair.IsVisible = true;

            // 更新文本框位置与显示内容
            MyTooltipText.Location = coordinates;
            MyTooltipText.LabelText = $" X: {coordinates.X:F1}, Y: {coordinates.Y:F2} ";
            MyTooltipText.IsVisible = true;

            formsPlot1.Refresh();
        }

        private void FormsPlot1_MouseLeave(object sender, EventArgs e)
        {
            // 鼠标移出控件范围时隐藏提示线和文字
            MyCrosshair.IsVisible = false;
            MyTooltipText.IsVisible = false;
            formsPlot1.Refresh();
        }

        #endregion

        /// <summary>
        /// 实时计算高低点，并在当前窗口中绘制所有高高连线/低低连线的延伸线
        /// </summary>
        private void UpdatePivotMarkersAndLines()
        {
            // 1. 清除上一帧绘制的所有 Marker 和延伸线
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

            // 2. 映射当前视口物理数据
            for (int i = 0; i < length; i++)
            {
                int physicalIndex = (nextIndex + i) % length;
                _highsCache[i] = SafeToDecimal(streamer1Data[physicalIndex]);
                _lowsCache[i] = SafeToDecimal(streamer1Data[physicalIndex]);
            }

            // 3. 计算 Peak 和 Valley 索引
            PivotHelper.CalculatePeaksFast(
                _highsCache.AsSpan(0, length),
                _lowsCache.AsSpan(0, length),
                _peaksBuffer,
                _valleysBuffer,
                leftLen: 5,
                rightLen: 5
            );

            // 4. 标注 Peak Marker
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

            // 5. 标注 Valley Marker
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

            // 6. 基于当前窗口内的所有 Peak 两两绘制延伸趋势线
            DrawExtensionLines(_peaksBuffer, streamer1Data, nextIndex, length, ScottPlot.Colors.Red.WithAlpha(0.6));

            // 7. 基于当前窗口内的所有 Valley 两两绘制延伸趋势线
            DrawExtensionLines(_valleysBuffer, streamer1Data, nextIndex, length, ScottPlot.Colors.Green.WithAlpha(0.6));
        }

        /// <summary>
        /// 根据两点计算延伸线，并将线绘制扩展到右边界 (x = length - 1)
        /// </summary>
        private void DrawExtensionLines(List<int> pivotIndices, double[] rawData, int nextIndex, int length, ScottPlot.Color lineColor)
        {
            if (pivotIndices.Count < 2) return;

            // 依次遍历当前窗口中的相邻两个高点/低点，绘制延伸线
            for (int i = 0; i < pivotIndices.Count - 1; i++)
            {
                int idx1 = pivotIndices[i];
                int idx2 = pivotIndices[i + 1];

                double x1 = idx1;
                double y1 = rawData[(nextIndex + idx1) % length];

                double x2 = idx2;
                double y2 = rawData[(nextIndex + idx2) % length];

                // 防止两点 X 重叠引发除零错误
                if (Math.Abs(x2 - x1) < 1e-5) continue;

                // 计算斜率 k
                double k = (y2 - y1) / (x2 - x1);

                // 将连线从起点 x1 向右延长至视口最右端 (x = length - 1)
                double xEnd = length - 1;
                double yEnd = y1 + k * (xEnd - x1);

                // 添加线段 (Line) 到图表中
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

        #region 数据流控制接口 (暂停 / 加速 / 减速 / 恢复)

        private const int MIN_INTERVAL = 1;   // 最高速度 (数据采集间隔 1ms)
        private const int MAX_INTERVAL = 200; // 最低速度 (数据采集间隔 200ms)
        private const int STEP_INTERVAL = 5;  // 每次加减速调节的步长 (ms)

        public void TogglePause()
        {
            AddNewDataTimer.Enabled = !AddNewDataTimer.Enabled;
        }

        public void Pause(bool pause)
        {
            AddNewDataTimer.Enabled = !pause;
        }

        public void SpeedUp()
        {
            int newInterval = AddNewDataTimer.Interval - STEP_INTERVAL;
            AddNewDataTimer.Interval = Math.Max(MIN_INTERVAL, newInterval);
        }

        public void SlowDown()
        {
            int newInterval = AddNewDataTimer.Interval + STEP_INTERVAL;
            AddNewDataTimer.Interval = Math.Min(MAX_INTERVAL, newInterval);
        }

        public string GetCurrentSpeedInfo()
        {
            bool isRunning = AddNewDataTimer.Enabled;
            return isRunning
                ? $"状态: 运行中 | 采集间隔: {AddNewDataTimer.Interval} ms (约 {1000.0 / AddNewDataTimer.Interval:F0} 次/秒)"
                : "状态: 已暂停";
        }

        #endregion

        private void btn_puase_Click(object sender, EventArgs e)
        {
            TogglePause();
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