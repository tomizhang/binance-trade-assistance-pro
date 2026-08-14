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
        private readonly List<double> _replayPrices = new List<double>();
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
            //_replayer.OnTickPushed += Replayer_OnTickPushed;
            _replayer.OnPlaybackCompleted += Replayer_OnPlaybackCompleted;
            _replayer.OnLog += msg => EnqueueLog($"[回放引擎] {msg}");
        }

        private void InitUiTimer()
        {
            // 每 100ms 批量合并数据渲染至 UI 线程 (抗高频刷新，彻底解决 UI 界面卡死问题)
            _uiRenderTimer.Interval = 100;
            _uiRenderTimer.Tick += UiRenderTimer_Tick;
            _uiRenderTimer.Start();
        }

        private void InitializePlot()
        {
            double[] ys = ScottPlot.Generate.Sin(50);
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
            // 1. 批量渲染日志文本 (避免高频 Invoke 卡死消息循环)
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

                    // 控制 RichTextBox 最大字符长度，防止超大字符串导致 WinForms 原生文本框卡顿
                    if (rtbLog.TextLength > 300000)
                    {
                        rtbLog.Text = rtbLog.Text.Substring(rtbLog.TextLength - 100000);
                    }

                    rtbLog.SelectionStart = rtbLog.TextLength;
                    rtbLog.ScrollToCaret();
                }
            }

            // 2. 批量渲染 ScottPlot 图表
            if (_needChartRefresh)
            {
                _needChartRefresh = false;
                double[] currentArray;
                lock (_replayPrices)
                {
                    currentArray = _replayPrices.ToArray();
                }

                if (currentArray.Length > 0)
                {
                    formsPlot1.Plot.Clear();
                    formsPlot1.Plot.Add.Signal(currentArray);
                    formsPlot1.Plot.Title(_chartTitle);
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

                // 3. 复位图表并启动回放引擎
                lock (_replayPrices)
                {
                    _replayPrices.Clear();
                }

                formsPlot1.Plot.Clear();
                formsPlot1.Plot.Title($"[{_currentSymbol}] 行情回放准备完毕 (共 {klines.Length} 帧)");
                formsPlot1.Refresh();

                AppendLog($"▶ 启动行情回放 | K线总帧数: {klines.Length} | Tick推送: {(enableTickPush ? "开启" : "关闭")} | 步进间隔: {intervalMs}ms");
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

        #region 回放事件响应 (无锁入队，彻底消除 UI 界面卡死)

        private void Replayer_OnKlinePushed(Kline kline, int current, int total)
        {
            lock (_replayPrices)
            {
                _replayPrices.Add((double)kline.ClosePrice);
            }

            _chartTitle = $"[{_currentSymbol}] 动态回放中 ({current}/{total}) - {kline.OpenTime:yyyy-MM-dd HH:mm:ss}";
            _needChartRefresh = true;

            //EnqueueLog($"[K线帧 {current}/{total}] {kline.OpenTime:yyyy-MM-dd HH:mm:ss} | 开:{kline.OpenPrice} 高:{kline.HighPrice} 低:{kline.LowPrice} 收:{kline.ClosePrice} 量:{kline.Volume}");
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
