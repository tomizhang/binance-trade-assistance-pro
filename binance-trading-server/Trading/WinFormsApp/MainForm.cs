using Binance.Net.Enums;
using Common;
using Common.Cursor;
using Common.Helper;
using Common.Interfaces;
using Common.Models;
using Common.Providers;
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

        // 统一淡蓝色主线条颜色 (Light Blue / Sky Blue, RGB: 100, 181, 246 / Hex: #64B5F6)
        private static readonly ScottPlot.Color LightBlueColor = new ScottPlot.Color(100, 181, 246);
        private static readonly ScottPlot.Color MarkerHighlightColor = new ScottPlot.Color(41, 128, 185);

        private string _currentSymbol = "BTCUSDT";
        private string _currentInterval = "30m";
        private bool _isPlaying = false;

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

            AppendLog("系统初始化就绪。统一时间标准: UTC+0");
            AppendLog($"数据根目录: {Config.GetRootPath()}");
        }

        private void SetupChartStyle()
        {
            formsPlot1.Plot.Clear();
            formsPlot1.Plot.Axes.DateTimeTicksBottom();
            formsPlot1.Plot.Title("Binance 行情数据回放 - 等待加载数据");
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
                        $"在本地数据目录未找到 [{_currentSymbol} {_currentInterval}] 从 {startUtc.ToUtc0String()} 至 {endUtc.ToUtc0String()} 的历史文件。\n\n是否自动生成该区间的模拟真实 K 线数据，以便立即体验回放效果？",
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

        #endregion

        #region 步进与图表渲染

        private bool StepForward(bool isAutoReplay = false)
        {
            if (_klineCursor == null) return false;

            if (_klineCursor.MoveNext())
            {
                var kline = _klineCursor.Current;
                _replayedDates.Add(kline.OpenTime);
                _replayedCloses.Add((double)kline.Close);

                UpdateChartAndLabels(kline);

                // 每 10 根或单步调试时输出日志，避免快速自动回放刷屏过快
                if (!isAutoReplay || _klineCursor.CurrentIndex % 10 == 0 || _klineCursor.CurrentIndex == _klineCursor.TotalCount - 1)
                {
                    AppendLog($"[推进 { _klineCursor.CurrentIndex + 1}/{_klineCursor.TotalCount}] {kline.FormattedOpenTime} | C:{kline.Close:F2} | 缓存:{_klineCursor.GetBuffer().Count}/100");
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
                }

                var kline = _klineCursor.Current;
                UpdateChartAndLabels(kline);
                AppendLog($"[回退 { _klineCursor.CurrentIndex + 1}/{_klineCursor.TotalCount}] {kline.FormattedOpenTime} | C:{kline.Close:F2} | 缓存:{_klineCursor.GetBuffer().Count}/100");
                return true;
            }

            return false;
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

            // 2. 渲染 ScottPlot 图表
            formsPlot1.Plot.Clear();
            formsPlot1.Plot.Axes.DateTimeTicksBottom();
            formsPlot1.Plot.Title($"{_currentSymbol} {_currentInterval} 行情回放 (当前第 {_klineCursor.CurrentIndex + 1} 根)");
            formsPlot1.Plot.XLabel("时间 (UTC+0)");
            formsPlot1.Plot.YLabel("价格 (USDT)");

            if (_replayedDates.Count > 0)
            {
                double[] xs = new double[_replayedDates.Count];
                double[] ys = _replayedCloses.ToArray();

                for (int i = 0; i < _replayedDates.Count; i++)
                {
                    xs[i] = _replayedDates[i].ToOADate();
                }

                // 核心：使用淡蓝色主线条显示 (Light Sky Blue)
                var scatter = formsPlot1.Plot.Add.ScatterLine(xs, ys, LightBlueColor);
                scatter.LineWidth = 2.5f;

                // 在最新一根价格点绘制醒目标记
                double lastX = xs[^1];
                double lastY = ys[^1];
                var marker = formsPlot1.Plot.Add.Marker(lastX, lastY);
                marker.Color = MarkerHighlightColor;
                marker.Size = 8;
                marker.Shape = MarkerShape.FilledCircle;
            }

            formsPlot1.Plot.Axes.AutoScale();
            formsPlot1.Refresh();
        }

        private void ResetReplayState()
        {
            StopReplay();
            _klineCursor?.Reset();
            _replayedDates.Clear();
            _replayedCloses.Clear();

            lblProgress.Text = $"进度: 0 / {_klineCursor?.TotalCount ?? 0}";
            lblTime.Text = "时间: --";
            lblPrice.Text = "最新收盘价: --";
            lblHighLow.Text = "高 / 低: -- / --";
            lblVolume.Text = "成交量: --";
            lblBuffer.Text = "100条缓存: 0 / 100";

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
            _dataProvider?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
