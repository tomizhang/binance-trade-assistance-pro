using Binance.Net.Enums;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace WinFormsApp2
{
    public partial class Form1 : Form
    {
        private readonly TradingServerEngine _engine = new TradingServerEngine();
        private readonly ConcurrentQueue<string> _logBufferQueue = new ConcurrentQueue<string>();
        private readonly System.Windows.Forms.Timer _uiRenderTimer = new System.Windows.Forms.Timer();
        private UserSettings _userSettings = new UserSettings();

        private bool _needChartRefresh = false;
        private volatile bool _needStrategyStatsUpdate = false;
        private string _chartTitle = "实时行情 / 数据回放 (ScottPlot 5)";

        private static readonly double[] _tradeXBuffer = new double[1];
        private static readonly double[] _tradeYBuffer = new double[1];
        private readonly double[] _pricesBuffer = new double[500];

        public Form1()
        {
            InitializeComponent();
            InitControls();
            InitEngineEvents();
            InitUiTimer();
            InitializePlot();
            AppendLog("系统初始化完成。核心交易引擎与 WinForms GUI 视口解耦完毕，完美支持 Linux 无界面部署。");
        }

        private void InitEngineEvents()
        {
            _engine.OnLog += AppendLog;
            _engine.OnChartRefreshRequired += () => _needChartRefresh = true;
            _engine.OnTradeOpened += trade =>
            {
                _needChartRefresh = true;
                _needStrategyStatsUpdate = true;
            };
            _engine.OnTradeClosed += trade =>
            {
                _needChartRefresh = true;
                _needStrategyStatsUpdate = true;
            };
        }

        private void InitControls()
        {
            // 1. 初始化交易对与周期下拉选项 (内置热门主流币种)
            cmbSymbol.Items.Clear();
            cmbSymbol.Items.Add("BTCUSDT");
            cmbSymbol.Items.Add("ETHUSDT");
            cmbSymbol.Items.Add("BNBUSDT");
            cmbSymbol.Items.Add("SOLUSDT");
            cmbSymbol.Items.Add("XRPUSDT");
            cmbSymbol.Items.Add("DOGEUSDT");
            cmbSymbol.Items.Add("ADAUSDT");
            cmbSymbol.Items.Add("AVAXUSDT");
            cmbSymbol.Items.Add("DOTUSDT");
            cmbSymbol.Items.Add("LINKUSDT");

            cmbKlineInterval.Items.Clear();
            cmbKlineInterval.Items.Add(new { Text = "1分钟 (1m)", Value = KlineInterval.OneMinute });
            cmbKlineInterval.Items.Add(new { Text = "3分钟 (3m)", Value = KlineInterval.ThreeMinutes });
            cmbKlineInterval.Items.Add(new { Text = "5分钟 (5m)", Value = KlineInterval.FiveMinutes });
            cmbKlineInterval.Items.Add(new { Text = "15分钟 (15m)", Value = KlineInterval.FifteenMinutes });
            cmbKlineInterval.Items.Add(new { Text = "30分钟 (30m)", Value = KlineInterval.ThirtyMinutes });
            cmbKlineInterval.Items.Add(new { Text = "1小时 (1h)", Value = KlineInterval.OneHour });
            cmbKlineInterval.Items.Add(new { Text = "2小时 (2h)", Value = KlineInterval.TwoHour });
            cmbKlineInterval.Items.Add(new { Text = "4小时 (4h)", Value = KlineInterval.FourHour });
            cmbKlineInterval.Items.Add(new { Text = "6小时 (6h)", Value = KlineInterval.SixHour });
            cmbKlineInterval.Items.Add(new { Text = "8小时 (8h)", Value = KlineInterval.EightHour });
            cmbKlineInterval.Items.Add(new { Text = "12小时 (12h)", Value = KlineInterval.TwelveHour });
            cmbKlineInterval.Items.Add(new { Text = "1天 (1d)", Value = KlineInterval.OneDay });
            cmbKlineInterval.Items.Add(new { Text = "3天 (3d)", Value = KlineInterval.ThreeDay });
            cmbKlineInterval.Items.Add(new { Text = "1周 (1w)", Value = KlineInterval.OneWeek });
            cmbKlineInterval.Items.Add(new { Text = "1月 (1M)", Value = KlineInterval.OneMonth });
            cmbKlineInterval.DisplayMember = "Text";
            cmbKlineInterval.ValueMember = "Value";

            // 2. 读取并应用持久化的用户右侧参数设置
            _userSettings = UserSettings.Load();

            string savedSymbol = string.IsNullOrWhiteSpace(_userSettings.Symbol) ? "BTCUSDT" : _userSettings.Symbol;
            cmbSymbol.Text = savedSymbol;

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
            chkHighlightHighVolume.Checked = _userSettings.HighlightHighVolume;

            // 策略与预热参数组件恢复
            chkEnableStrategy.Checked = _userSettings.EnableStrategy;
            chkEnableWarmup.Checked = _userSettings.EnableWarmup;
            numMinLineX1X2.Value = Math.Max(numMinLineX1X2.Minimum, Math.Min(numMinLineX1X2.Maximum, _userSettings.MinLineX1X2));
            numMinLineAge.Value = Math.Max(numMinLineAge.Minimum, Math.Min(numMinLineAge.Maximum, _userSettings.MinLineAge));
            numTakeProfit.Value = Math.Max(numTakeProfit.Minimum, Math.Min(numTakeProfit.Maximum, _userSettings.TakeProfitPct));
            numStopLoss.Value = Math.Max(numStopLoss.Minimum, Math.Min(numStopLoss.Maximum, _userSettings.StopLossPct));

            // 3. 绑定参数控件变动自动保存逻辑
            cmbSymbol.TextChanged += (s, e) => SaveCurrentSettings();
            cmbSymbol.SelectedIndexChanged += (s, e) => SaveCurrentSettings();
            cmbKlineInterval.SelectedIndexChanged += (s, e) => SaveCurrentSettings();
            dtpStartDate.ValueChanged += (s, e) => SaveCurrentSettings();
            dtpEndDate.ValueChanged += (s, e) => SaveCurrentSettings();
            numInterval.ValueChanged += (s, e) => SaveCurrentSettings();
            chkEnableTickPush.CheckedChanged += (s, e) => SaveCurrentSettings();
            chkAutoFitPrice.CheckedChanged += (s, e) => { SaveCurrentSettings(); _needChartRefresh = true; };
            chkHighlightHighVolume.CheckedChanged += (s, e) => { SaveCurrentSettings(); _needChartRefresh = true; };

            chkEnableStrategy.CheckedChanged += (s, e) => SyncStrategyParams();
            chkEnableWarmup.CheckedChanged += (s, e) => SaveCurrentSettings();
            numMinLineX1X2.ValueChanged += (s, e) => SyncStrategyParams();
            numMinLineAge.ValueChanged += (s, e) => SyncStrategyParams();
            numTakeProfit.ValueChanged += (s, e) => SyncStrategyParams();
            numStopLoss.ValueChanged += (s, e) => SyncStrategyParams();

            btnStepForward.MouseWheel += StepButton_MouseWheel;
            btnStepBackward.MouseWheel += StepButton_MouseWheel;
            FormClosing += (s, e) => SaveCurrentSettings();

            SyncStrategyParams();
        }

        private void SyncStrategyParams()
        {
            var strategy = _engine.Strategy;
            strategy.Params.Enabled = chkEnableStrategy.Checked;
            strategy.Params.MinLineX1X2 = (int)numMinLineX1X2.Value;
            strategy.Params.MinLineAge = (int)numMinLineAge.Value;
            strategy.Params.TakeProfitPct = numTakeProfit.Value;
            strategy.Params.StopLossPct = numStopLoss.Value;

            _needStrategyStatsUpdate = true;
            SaveCurrentSettings();
        }

        private void SaveCurrentSettings()
        {
            try
            {
                _userSettings.Symbol = cmbSymbol.Text.Trim();
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
                _userSettings.HighlightHighVolume = chkHighlightHighVolume.Checked;

                _userSettings.EnableStrategy = chkEnableStrategy.Checked;
                _userSettings.EnableWarmup = chkEnableWarmup.Checked;
                _userSettings.MinLineX1X2 = (int)numMinLineX1X2.Value;
                _userSettings.MinLineAge = (int)numMinLineAge.Value;
                _userSettings.TakeProfitPct = numTakeProfit.Value;
                _userSettings.StopLossPct = numStopLoss.Value;

                _userSettings.Save();
            }
            catch
            {
                // 忽略配置保存时的偶发异常
            }
        }

        private void UpdateStrategyStatsUI()
        {
            var strategy = _engine.Strategy;
            int count = strategy.Trades.Count;
            decimal winRate = strategy.GetWinRate();
            decimal totalProfit = strategy.GetTotalProfitPct();

            lblStrategyStats.Text = $"交易次数: {count} 笔 | 胜率: {winRate:F1}%\r\n累计收益: {totalProfit:+0.00;-0.00;0.00}%";
            lblStrategyStats.ForeColor = totalProfit >= 0 ? Color.DarkGreen : Color.DarkRed;
        }

        private void StepButton_MouseWheel(object sender, MouseEventArgs e)
        {
            if (e.Delta > 0)
            {
                _engine.Replayer.StepForward();
            }
            else if (e.Delta < 0)
            {
                _engine.Replayer.StepBackward();
            }
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
            formsPlot1.Plot.Grid.IsVisible = false;
            formsPlot1.Plot.Add.Signal(ys);
            formsPlot1.Plot.Title("实时行情 / 数据回放 (ScottPlot 5)");
            formsPlot1.Plot.XLabel("序列 (Frame)");
            formsPlot1.Plot.YLabel("价格 (Price)");
            formsPlot1.Refresh();
        }

        public void EnqueueLog(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            _logBufferQueue.Enqueue($"[{timestamp}] {message}");
            Logger.Log(message);
        }

        public void AppendLog(string message)
        {
            EnqueueLog(message);
        }

        private void UiRenderTimer_Tick(object sender, EventArgs e)
        {
            // 1. 策略统计面板平滑更新
            if (_needStrategyStatsUpdate)
            {
                _needStrategyStatsUpdate = false;
                UpdateStrategyStatsUI();
            }

            // 2. 批量渲染日志文本
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

            // 3. 批量渲染 ScottPlot 图表、枢轴高低点、趋势线与策略交易开平仓标注
            if (_needChartRefresh)
            {
                _needChartRefresh = false;

                var displayKlines = _engine.DisplayKlinesBuffer;
                int displayCount = displayKlines.Count;
                if (displayCount == 0) return;

                for (int i = 0; i < displayCount; i++)
                {
                    _pricesBuffer[i] = (double)displayKlines[i].ClosePrice;
                }

                double[] pricesSlice = new double[displayCount];
                Array.Copy(_pricesBuffer, 0, pricesSlice, 0, displayCount);

                formsPlot1.Plot.Clear();
                formsPlot1.Plot.Grid.IsVisible = false;

                // A. 绘制价格主信号曲线
                formsPlot1.Plot.Add.Signal(pricesSlice);
                formsPlot1.Plot.Title(_chartTitle);

                // B. 标注相对高低点 (基于 OpenTime 严格对齐视口 K 线，100% 绝对精准)
                var activePivots = _engine.ActivePivots;
                if (activePivots != null && activePivots.Count > 0)
                {
                    List<double> highXs = new List<double>();
                    List<double> highYs = new List<double>();
                    List<double> lowXs = new List<double>();
                    List<double> lowYs = new List<double>();

                    for (int i = 0; i < activePivots.Count; i++)
                    {
                        var p = activePivots[i];
                        for (int k = 0; k < displayCount; k++)
                        {
                            if (displayKlines[k].OpenTime == p.Time)
                            {
                                if (p.Type == PivotType.High)
                                {
                                    highXs.Add(k);
                                    highYs.Add((double)p.Price);
                                }
                                else if (p.Type == PivotType.Low)
                                {
                                    lowXs.Add(k);
                                    lowYs.Add((double)p.Price);
                                }
                                break;
                            }
                        }
                    }

                    if (highXs.Count > 0)
                    {
                        var spHigh = formsPlot1.Plot.Add.ScatterPoints(highXs.ToArray(), highYs.ToArray());
                        spHigh.Color = ScottPlot.Colors.Red;
                        spHigh.MarkerSize = 4;
                    }

                    if (lowXs.Count > 0)
                    {
                        var spLow = formsPlot1.Plot.Add.ScatterPoints(lowXs.ToArray(), lowYs.ToArray());
                        spLow.Color = ScottPlot.Colors.LimeGreen;
                        spLow.MarkerSize = 4;
                    }
                }

                // C. 绘制延伸趋势线 (基于 Time1/Time2 严格匹配视口 K 线，100% 坐标精准)
                var activeTrendLines = _engine.ActiveTrendLines;
                if (activeTrendLines != null && activeTrendLines.Count > 0)
                {
                    ScottPlot.Color extraLightRed = ScottPlot.Color.FromHex("#45FF8080");   // 超淡柔和红
                    ScottPlot.Color extraLightGreen = ScottPlot.Color.FromHex("#4580FF80"); // 超淡柔和绿

                    for (int i = 0; i < activeTrendLines.Count; i++)
                    {
                        var tl = activeTrendLines[i];
                        int localX1 = -1;
                        int localX2 = -1;

                        for (int k = 0; k < displayCount; k++)
                        {
                            DateTime kTime = displayKlines[k].OpenTime;
                            if (kTime == tl.Time1) localX1 = k;
                            if (kTime == tl.Time2) localX2 = k;
                        }

                        if (localX1 >= 0 && localX2 >= 0)
                        {
                            double x1 = localX1;
                            double y1 = (double)tl.Y1;
                            double x2 = displayCount - 1;
                            double y2 = (double)tl.GetPriceAt(displayCount - 1);

                            var linePlot = formsPlot1.Plot.Add.Line(x1, y1, x2, y2);
                            linePlot.Color = tl.Type == PivotType.High ? extraLightRed : extraLightGreen;
                            linePlot.LineWidth = 0.8f;
                        }
                    }
                }

                // D. 高成交量 K 线标记标注 (金黄色)
                if (chkHighlightHighVolume.Checked && displayCount > 0)
                {
                    double sumVol = 0;
                    for (int i = 0; i < displayCount; i++)
                    {
                        sumVol += (double)displayKlines[i].Volume;
                    }
                    double avgVol = sumVol / displayCount;
                    double thresholdVol = avgVol * 2.0;

                    List<double> volXs = new List<double>();
                    List<double> volYs = new List<double>();

                    for (int i = 0; i < displayCount; i++)
                    {
                        if ((double)displayKlines[i].Volume >= thresholdVol)
                        {
                            volXs.Add(i);
                            volYs.Add((double)displayKlines[i].LowPrice * 0.9985);
                        }
                    }

                    if (volXs.Count > 0)
                    {
                        var spVol = formsPlot1.Plot.Add.ScatterPoints(volXs.ToArray(), volYs.ToArray());
                        spVol.Color = ScottPlot.Colors.Gold;
                        spVol.MarkerSize = 5;
                    }
                }

                // E. 策略开仓与平仓图表标注 (按 K线 OpenTime 精准匹配视口 x 坐标，0 下标偏移)
                var strategy = _engine.Strategy;
                if (chkEnableStrategy.Checked && strategy.Trades.Count > 0)
                {
                    for (int i = strategy.Trades.Count - 1; i >= 0; i--)
                    {
                        var trade = strategy.Trades[i];
                        int localEntryX = -1;
                        int localExitX = -1;

                        for (int k = 0; k < displayCount; k++)
                        {
                            DateTime kTime = displayKlines[k].OpenTime;
                            if (kTime == trade.EntryKlineOpenTime) localEntryX = k;
                            if (kTime == trade.ExitKlineOpenTime) localExitX = k;
                        }

                        if (localEntryX >= 0 && localEntryX < displayCount)
                        {
                            _tradeXBuffer[0] = localEntryX;
                            _tradeYBuffer[0] = (double)trade.EntryPrice;
                            var spEntry = formsPlot1.Plot.Add.ScatterPoints(_tradeXBuffer, _tradeYBuffer);
                            spEntry.Color = trade.Position == PositionType.Long ? ScottPlot.Colors.Cyan : ScottPlot.Colors.Magenta;
                            spEntry.MarkerSize = 7;
                        }

                        if (localExitX >= 0 && localExitX < displayCount)
                        {
                            _tradeXBuffer[0] = localExitX;
                            _tradeYBuffer[0] = (double)trade.ExitPrice;
                            var spExit = formsPlot1.Plot.Add.ScatterPoints(_tradeXBuffer, _tradeYBuffer);
                            spExit.Color = trade.IsWin ? ScottPlot.Colors.LimeGreen : ScottPlot.Colors.Red;
                            spExit.MarkerSize = 8;
                        }
                    }

                    if (strategy.CurrentPosition != PositionType.None && strategy.CurrentTrade != null)
                    {
                        int localEntryX = -1;
                        for (int k = 0; k < displayCount; k++)
                        {
                            if (displayKlines[k].OpenTime == strategy.CurrentTrade.EntryKlineOpenTime)
                            {
                                localEntryX = k;
                                break;
                            }
                        }

                        if (localEntryX >= 0 && localEntryX < displayCount)
                        {
                            _tradeXBuffer[0] = localEntryX;
                            _tradeYBuffer[0] = (double)strategy.CurrentEntryPrice;
                            var spCurrent = formsPlot1.Plot.Add.ScatterPoints(_tradeXBuffer, _tradeYBuffer);
                            spCurrent.Color = strategy.CurrentPosition == PositionType.Long ? ScottPlot.Colors.DeepSkyBlue : ScottPlot.Colors.HotPink;
                            spCurrent.MarkerSize = 9;
                        }
                    }
                }

                // F. 视口自动缩放
                if (chkAutoFitPrice.Checked && displayCount > 0)
                {
                    int sampleSize = Math.Min(displayCount, 80);
                    int startIdx = displayCount - sampleSize;
                    double minPrice = (double)displayKlines[startIdx].LowPrice;
                    double maxPrice = (double)displayKlines[startIdx].HighPrice;

                    for (int i = startIdx + 1; i < displayCount; i++)
                    {
                        double low = (double)displayKlines[i].LowPrice;
                        double high = (double)displayKlines[i].HighPrice;
                        if (low < minPrice) minPrice = low;
                        if (high > maxPrice) maxPrice = high;
                    }

                    double margin = (maxPrice - minPrice) * 0.12;
                    if (margin == 0) margin = maxPrice * 0.01;
                    if (margin == 0) margin = 1.0;

                    formsPlot1.Plot.Axes.SetLimitsY(minPrice - margin, maxPrice + margin);
                }

                formsPlot1.Refresh();
            }
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            SaveCurrentSettings(); // 点击开始回放时主动同步持久化设置

            string symbol = cmbSymbol.Text.Trim();
            if (string.IsNullOrEmpty(symbol))
            {
                MessageBox.Show("请输入或选择交易对名称 (如 BTCUSDT)", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            dynamic selectedIntervalObj = cmbKlineInterval.SelectedItem;
            KlineInterval interval = (KlineInterval)selectedIntervalObj.Value;

            DateTime startDate = dtpStartDate.Value.Date;
            DateTime endDate = dtpEndDate.Value.Date;
            int intervalMs = (int)numInterval.Value;
            bool enableTickPush = chkEnableTickPush.Checked;

            btnStart.Enabled = false;
            try
            {
                await _engine.StartReplayAsync(
                    symbol,
                    interval,
                    startDate,
                    endDate,
                    chkEnableWarmup.Checked,
                    enableTickPush,
                    intervalMs);
            }
            catch (Exception ex)
            {
                AppendLog($"▶ 启动回播异常: {ex.Message}");
            }
            finally
            {
                btnStart.Enabled = true;
            }
        }

        private async void btnLiveMode_Click(object sender, EventArgs e)
        {
            if (_engine.Mode == ExecutionMode.LiveStream)
            {
                _engine.Stop();
                btnLiveMode.Text = "📡 启动币安实盘行情 (Live Stream)";
                btnLiveMode.ForeColor = Color.DarkGreen;
                AppendLog("⏹ 实盘行情模式已停止。");
                return;
            }

            string symbol = cmbSymbol.Text.Trim().ToUpper();
            if (string.IsNullOrEmpty(symbol))
            {
                MessageBox.Show("请先选择或输入交易对名称！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            dynamic selectedIntervalObj = cmbKlineInterval.SelectedItem;
            KlineInterval interval = (KlineInterval)selectedIntervalObj.Value;

            btnLiveMode.Enabled = false;
            try
            {
                await _engine.StartLiveStreamAsync(symbol, interval);
                btnLiveMode.Text = "🛑 停止币安实盘行情 (Stop Live)";
                btnLiveMode.ForeColor = Color.Red;
            }
            catch (Exception ex)
            {
                AppendLog($"❌ 启动实盘行情失败: {ex.Message}");
            }
            finally
            {
                btnLiveMode.Enabled = true;
            }
        }

        private void btnPause_Click(object sender, EventArgs e)
        {
            _engine.Replayer.PausePlayback();
        }

        private void btnStepForward_Click(object sender, EventArgs e)
        {
            _engine.Replayer.StepForward();
        }

        private void btnStepBackward_Click(object sender, EventArgs e)
        {
            _engine.Replayer.StepBackward();
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            _engine.Stop();
            AppendLog("⏹ 用户点击停止，交易引擎已安全复位。");
        }

        private void btnClearLog_Click(object sender, EventArgs e)
        {
            rtbLog.Clear();
            AppendLog("日志已清空。");
        }

        private async void btnDownloadKlines_Click(object sender, EventArgs e)
        {
            string symbol = cmbSymbol.Text.Trim().ToUpper();
            if (string.IsNullOrEmpty(symbol))
            {
                MessageBox.Show("请先选择或输入交易对名称！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            dynamic selectedIntervalObj = cmbKlineInterval.SelectedItem;
            KlineInterval interval = (KlineInterval)selectedIntervalObj.Value;
            DateTime startDate = dtpStartDate.Value.Date;
            DateTime endDate = dtpEndDate.Value.Date;

            btnDownloadKlines.Enabled = false;
            try
            {
                AppendLog($"[数据下载] 开始并行下载 [{symbol}] [{interval}] K线周期数据 ({startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd})...");
                await MultiThreadDownloader.DownloadAndSaveKlinesParallelAsync(symbol, interval, startDate, endDate, logger: AppendLog);
                AppendLog($"[数据下载完成] 成功下载并保存 [{symbol}] K线数据至 Parquet 存储！");
            }
            catch (Exception ex)
            {
                AppendLog($"[数据下载失败] K线数据下载异常: {ex.Message}");
            }
            finally
            {
                btnDownloadKlines.Enabled = true;
            }
        }

        private async void btnDownloadTicks_Click(object sender, EventArgs e)
        {
            string symbol = cmbSymbol.Text.Trim().ToUpper();
            if (string.IsNullOrEmpty(symbol))
            {
                MessageBox.Show("请先选择或输入交易对名称！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DateTime startDate = dtpStartDate.Value.Date;
            DateTime endDate = dtpEndDate.Value.Date;

            btnDownloadTicks.Enabled = false;
            try
            {
                AppendLog($"[数据下载] 开始并行下载 [{symbol}] Tick 逐笔数据 ({startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd})...");
                await MultiThreadDownloader.DownloadAndSaveTicksInSlicesParallelAsync(symbol, startDate, endDate, logger: AppendLog);
                AppendLog($"[数据下载完成] 成功下载并分片落盘 [{symbol}] Tick 逐笔数据！");
            }
            catch (Exception ex)
            {
                AppendLog($"[数据下载失败] Tick 逐笔数据下载异常: {ex.Message}");
            }
            finally
            {
                btnDownloadTicks.Enabled = true;
            }
        }
    }
}
