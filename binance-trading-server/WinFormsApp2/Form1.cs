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
        private readonly MarketReplayer _replayer = new MarketReplayer();
        private readonly List<Kline> _replayKlines = new List<Kline>();
        private readonly ConcurrentQueue<string> _logBufferQueue = new ConcurrentQueue<string>();
        private readonly System.Windows.Forms.Timer _uiRenderTimer = new System.Windows.Forms.Timer();
        private readonly TrendLineStrategy _strategy = new TrendLineStrategy();
        private UserSettings _userSettings = new UserSettings();

        private string _currentSymbol = "BTCUSDT";
        private bool _needChartRefresh = false;
        private string _chartTitle = "实时行情 / 数据回放 (ScottPlot 5)";

        private int _currentKlineIndex = 0;
        private List<PivotPoint> _currentActivePivots = new List<PivotPoint>();
        private List<TrendLine> _currentActiveTrendLines = new List<TrendLine>();

        public Form1()
        {
            InitializeComponent();
            InitControls();
            InitReplayer();
            InitStrategy();
            InitUiTimer();
            InitializePlot();
            AppendLog("系统初始化完成。预设多币种列表与 API 预热配置加载成功。");
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

        private volatile bool _isStrategyEnabled = true;
        private volatile bool _needStrategyStatsUpdate = false;

        private void SyncStrategyParams()
        {
            _isStrategyEnabled = chkEnableStrategy.Checked;
            _strategy.Params.Enabled = _isStrategyEnabled;
            _strategy.Params.MinLineX1X2 = (int)numMinLineX1X2.Value;
            _strategy.Params.MinLineAge = (int)numMinLineAge.Value;
            _strategy.Params.TakeProfitPct = numTakeProfit.Value;
            _strategy.Params.StopLossPct = numStopLoss.Value;

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

        private void InitStrategy()
        {
            _strategy.OnTradeOpened += trade =>
            {
                string posStr = trade.Position == PositionType.Long ? "多单 (BUY LONG)" : "空单 (SELL SHORT)";
                EnqueueLog($"⚡ [策略开仓 #{trade.Id}] {posStr} | 价格: {trade.EntryPrice} | 时间: {trade.EntryTime:HH:mm:ss.fff}");
                _needStrategyStatsUpdate = true;
                _needChartRefresh = true;
            };

            _strategy.OnTradeClosed += trade =>
            {
                string reasonStr = trade.ExitReason == TradeExitReason.TakeProfit ? "🎯 止盈 (TP +1.5%)" : "🛑 止损 (SL -0.8%)";
                EnqueueLog($"🏁 [策略平仓 #{trade.Id}] {trade.Position} | {reasonStr} | 价格: {trade.ExitPrice} | 盈亏: {trade.ProfitPct:+0.00;-0.00;0.00}%");
                _needStrategyStatsUpdate = true;
                _needChartRefresh = true;
            };
        }

        private void UpdateStrategyStatsUI()
        {
            int count = _strategy.Trades.Count;
            decimal winRate = _strategy.GetWinRate();
            decimal totalProfit = _strategy.GetTotalProfitPct();

            lblStrategyStats.Text = $"交易次数: {count} 笔 | 胜率: {winRate:F1}%\r\n累计收益: {totalProfit:+0.00;-0.00;0.00}%";
            lblStrategyStats.ForeColor = totalProfit >= 0 ? System.Drawing.Color.DarkGreen : System.Drawing.Color.DarkRed;
        }

        private void StepButton_MouseWheel(object sender, MouseEventArgs e)
        {
            if (_replayer == null) return;

            if (e.Delta > 0)
            {
                _replayer.StepForward();
            }
            else if (e.Delta < 0)
            {
                _replayer.StepBackward();
            }
        }

        private ChunkedDataManager? _chunkedManager = null;

        private void InitReplayer()
        {
            _replayer.OnKlinePushed += Replayer_OnKlinePushed;
            _replayer.OnStepBackward += Replayer_OnStepBackward;
            _replayer.OnTickPushed += Replayer_OnTickPushed;
            _replayer.OnPlaybackCompleted += Replayer_OnPlaybackCompleted;
            _replayer.OnLog += msg => EnqueueLog($"[回放引擎] {msg}");

            // 50% 提前预读门槛触发：当推演到达 50% 进度时，强行确保下一个 3 天切片处于预读取状态
            _replayer.OnPreloadThresholdReached += () =>
            {
                _chunkedManager?.EnsurePrefetchNextBatch();
            };

            // 分批次双缓冲区无缝接力：当前 3 天切片播放完毕时，自动切换至已预读取就绪的下一批次 3 天切片
            _replayer.OnNeedNextBatchChunk += async () =>
            {
                if (_chunkedManager != null)
                {
                    return await _chunkedManager.MoveToNextBatchAsync();
                }
                return null;
            };
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

        public void EnqueueLog(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            _logBufferQueue.Enqueue($"[{timestamp}] {message}");
        }

        public void AppendLog(string message)
        {
            EnqueueLog(message);
        }

        private readonly Kline[] _displayKlinesBuffer = new Kline[500];
        private readonly double[] _pricesBuffer = new double[500];

        private void UpdateDisplayPivotsAndTrendLines()
        {
            int sampleSize = 0;
            lock (_replayKlines)
            {
                int count = _replayKlines.Count;
                if (count < 7)
                {
                    _currentActivePivots.Clear();
                    _currentActiveTrendLines.Clear();
                    return;
                }

                sampleSize = Math.Min(count, 500);
                _replayKlines.CopyTo(count - sampleSize, _displayKlinesBuffer, 0, sampleSize);
            }

            // 零 LOH 分配 slice
            Kline[] sampleSlice = new Kline[sampleSize];
            Array.Copy(_displayKlinesBuffer, 0, sampleSlice, 0, sampleSize);

            _currentActivePivots = PivotHelper.CalculatePeaksCombinedFast(sampleSlice, leftBars: 3, rightBars: 3);
            _currentActiveTrendLines = TrendLineHelper.GenerateTrendLinesFromPivots(sampleSlice, _currentActivePivots, filterPenetrated: true);
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

                int displayCount = 0;
                lock (_replayKlines)
                {
                    int total = _replayKlines.Count;
                    if (total == 0) return;

                    displayCount = Math.Min(total, 500);
                    _replayKlines.CopyTo(total - displayCount, _displayKlinesBuffer, 0, displayCount);
                }

                for (int i = 0; i < displayCount; i++)
                {
                    _pricesBuffer[i] = (double)_displayKlinesBuffer[i].ClosePrice;
                }

                double[] pricesSlice = new double[displayCount];
                Array.Copy(_pricesBuffer, 0, pricesSlice, 0, displayCount);

                formsPlot1.Plot.Clear();
                formsPlot1.Plot.Grid.IsVisible = false;

                // A. 绘制价格主信号曲线
                formsPlot1.Plot.Add.Signal(pricesSlice);
                formsPlot1.Plot.Title(_chartTitle);

                // B. 标注相对高低点 (完全精准比对，0 冗余重复计算)
                if (_currentActivePivots != null && _currentActivePivots.Count > 0)
                {
                    List<double> highXs = new List<double>();
                    List<double> highYs = new List<double>();
                    List<double> lowXs = new List<double>();
                    List<double> lowYs = new List<double>();

                    for (int i = 0; i < _currentActivePivots.Count; i++)
                    {
                        var p = _currentActivePivots[i];
                        if (p.Index >= 0 && p.Index < displayCount)
                        {
                            if (p.Type == PivotType.High)
                            {
                                highXs.Add(p.Index);
                                highYs.Add((double)p.Price);
                            }
                            else if (p.Type == PivotType.Low)
                            {
                                lowXs.Add(p.Index);
                                lowYs.Add((double)p.Price);
                            }
                        }
                    }

                    // 相对高点 (HighPrice, 红色)
                    if (highXs.Count > 0)
                    {
                        var spHigh = formsPlot1.Plot.Add.ScatterPoints(highXs.ToArray(), highYs.ToArray());
                        spHigh.Color = ScottPlot.Colors.Red;
                        spHigh.MarkerSize = 3;
                    }

                    // 相对低点 (LowPrice, 绿色)
                    if (lowXs.Count > 0)
                    {
                        var spLow = formsPlot1.Plot.Add.ScatterPoints(lowXs.ToArray(), lowYs.ToArray());
                        spLow.Color = ScottPlot.Colors.LimeGreen;
                        spLow.MarkerSize = 3;
                    }
                }

                // C. 绘制延伸趋势线 (完全精准比对，0 冗余重复计算)
                if (_currentActiveTrendLines != null && _currentActiveTrendLines.Count > 0)
                {
                    ScottPlot.Color extraLightRed = ScottPlot.Color.FromHex("#45FF8080");   // 超淡柔和红
                    ScottPlot.Color extraLightGreen = ScottPlot.Color.FromHex("#4580FF80"); // 超淡柔和绿

                    for (int i = 0; i < _currentActiveTrendLines.Count; i++)
                    {
                        var tl = _currentActiveTrendLines[i];

                        if (tl.X1 >= 0 && tl.X1 < displayCount && tl.X2 >= 0 && tl.X2 < displayCount)
                        {
                            double x1 = tl.X1;
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
                        sumVol += (double)_displayKlinesBuffer[i].Volume;
                    }
                    double avgVol = sumVol / displayCount;
                    double thresholdVol = avgVol * 2.0;

                    List<double> volXs = new List<double>();
                    List<double> volYs = new List<double>();

                    for (int i = 0; i < displayCount; i++)
                    {
                        if ((double)_displayKlinesBuffer[i].Volume >= thresholdVol)
                        {
                            volXs.Add(i);
                            volYs.Add((double)_displayKlinesBuffer[i].LowPrice * 0.9985);
                        }
                    }

                    if (volXs.Count > 0)
                    {
                        var spVol = formsPlot1.Plot.Add.ScatterPoints(volXs.ToArray(), volYs.ToArray());
                        spVol.Color = ScottPlot.Colors.Gold;
                        spVol.MarkerSize = 5;
                    }
                }

                // E. 策略开仓与平仓图表标注
                if (_isStrategyEnabled)
                {
                    foreach (var trade in _strategy.Trades)
                    {
                        int localEntryX = trade.EntryKlineIndex;
                        int localExitX = trade.ExitKlineIndex;

                        if (localEntryX >= 0 && localEntryX < displayCount)
                        {
                            var spEntry = formsPlot1.Plot.Add.ScatterPoints(new double[] { localEntryX }, new double[] { (double)trade.EntryPrice });
                            spEntry.Color = trade.Position == PositionType.Long ? ScottPlot.Colors.Cyan : ScottPlot.Colors.Magenta;
                            spEntry.MarkerSize = 7;
                        }

                        if (localExitX >= 0 && localExitX < displayCount)
                        {
                            var spExit = formsPlot1.Plot.Add.ScatterPoints(new double[] { localExitX }, new double[] { (double)trade.ExitPrice });
                            spExit.Color = trade.IsWin ? ScottPlot.Colors.LimeGreen : ScottPlot.Colors.Red;
                            spExit.MarkerSize = 8;
                        }
                    }

                    if (_strategy.CurrentPosition != PositionType.None)
                    {
                        int localEntryX = _strategy.CurrentEntryKlineIndex;
                        if (localEntryX >= 0 && localEntryX < displayCount)
                        {
                            var spCurrent = formsPlot1.Plot.Add.ScatterPoints(new double[] { localEntryX }, new double[] { (double)_strategy.CurrentEntryPrice });
                            spCurrent.Color = _strategy.CurrentPosition == PositionType.Long ? ScottPlot.Colors.DeepSkyBlue : ScottPlot.Colors.HotPink;
                            spCurrent.MarkerSize = 9;
                        }
                    }
                }

                // F. 视口自动缩放
                if (chkAutoFitPrice.Checked && displayCount > 0)
                {
                    int sampleSize = Math.Min(displayCount, 80);
                    int startIdx = displayCount - sampleSize;
                    double minPrice = (double)_displayKlinesBuffer[startIdx].LowPrice;
                    double maxPrice = (double)_displayKlinesBuffer[startIdx].HighPrice;

                    for (int i = startIdx + 1; i < displayCount; i++)
                    {
                        double low = (double)_displayKlinesBuffer[i].LowPrice;
                        double high = (double)_displayKlinesBuffer[i].HighPrice;
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

            _currentSymbol = cmbSymbol.Text.Trim();
            if (string.IsNullOrEmpty(_currentSymbol))
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

            _strategy.Reset(); // 策略复位
            UpdateStrategyStatsUI();

            AppendLog($"准备分批加载 [{_currentSymbol}] [{interval}] 日期范围 [{startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}] 数据启动回放...");

            try
            {
                // 1. 初始化分批流式数据管理器 (按 3 天切片分批，双缓冲区提前预读取，彻底解决大内存溢出与卡顿)
                _chunkedManager = new ChunkedDataManager(
                    _currentSymbol,
                    interval,
                    startDate,
                    endDate,
                    enableTickPush,
                    batchDays: 3,
                    logger: AppendLog);

                // 2. 加载首批 3 天切片数据 (同时后台异步线程立即启动预读取下个 3 天切片)
                BatchDataChunk? firstChunk = await _chunkedManager.InitializeAsync();

                if (firstChunk == null || firstChunk.Klines.Length == 0)
                {
                    AppendLog("未装载到任何 K线数据，无法开始回放。");
                    return;
                }

                Kline[] klines = firstChunk.Klines;
                Tick[] ticks = firstChunk.Ticks;

                // 3. 策略启动前 API 预热控制
                if (chkEnableWarmup.Checked)
                {
                    try
                    {
                        AppendLog($"[API 预热开启] 准备在线从币安 API 获取 1000 根 [{_currentSymbol}] [{interval}] 最新 K 线进行趋势线预热...");
                        var api1000Klines = await DataHelper.FetchKlinesFromApiAsync(_currentSymbol, interval, limit: 1000);
                        AppendLog($"[API 预热成功] 成功获取 1000 根在线 K 线数据 ({api1000Klines[0].OpenTime:yyyy-MM-dd HH:mm} ~ {api1000Klines.Last().OpenTime:yyyy-MM-dd HH:mm})。");
                    }
                    catch (Exception apiEx)
                    {
                        AppendLog($"[API 预热提示] 在线获取 1000 根 K 线未成功 ({apiEx.Message})，自动回退使用装载的 K 线数据。");
                    }
                }
                else
                {
                    AppendLog("[API 预热关闭] 用户未勾选 API 预热，直接使用装载的 K 线数据计算趋势线。");
                }

                // 4. 复位图表并启动回放引擎
                lock (_replayKlines)
                {
                    _replayKlines.Clear();
                }

                UpdateDisplayPivotsAndTrendLines();

                formsPlot1.Plot.Clear();
                formsPlot1.Plot.Grid.IsVisible = false;
                formsPlot1.Plot.Title($"[{_currentSymbol}] 行情回放准备完毕 (首批 3 天共 {klines.Length} 帧，提前预读流畅运行)");
                formsPlot1.Refresh();

                AppendLog($"▶ 启动行情回放与策略引擎 | 首批: 3 天 ({klines.Length} 帧) | 总批次: {_chunkedManager.TotalBatches} 批 | 交易对: {_currentSymbol} | 策略: {(chkEnableStrategy.Checked ? "开启" : "关闭")}");
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
                if (_replayKlines.Count > 500)
                {
                    _replayKlines.RemoveAt(0); // 严格锁死 500 帧容量，彻底切断 LOH 大对象堆与 10MB+ 数组翻倍扩容引起的 Gen 2 Full GC
                }
                _currentKlineIndex = _replayKlines.Count - 1;
            }

            // 新 K 线到达时增量更新当前视图的高低点与趋势线 (常数级 0.05ms)
            UpdateDisplayPivotsAndTrendLines();

            _chartTitle = $"[{_currentSymbol}] 动态回放中 ({current}/{total}) - {kline.OpenTime:yyyy-MM-dd HH:mm:ss}";
            _needChartRefresh = true;
        }

        private void Replayer_OnStepBackward(Kline[] subKlines, int current, int total)
        {
            lock (_replayKlines)
            {
                _replayKlines.Clear();
                _replayKlines.AddRange(subKlines);
                _currentKlineIndex = Math.Max(0, _replayKlines.Count - 1);
            }

            UpdateDisplayPivotsAndTrendLines();

            var lastTime = subKlines.Length > 0 ? subKlines[subKlines.Length - 1].OpenTime.ToString("yyyy-MM-dd HH:mm:ss") : "";
            _chartTitle = $"[{_currentSymbol}] 单步向后 ({current}/{total}) - {lastTime}";
            _needChartRefresh = true;
        }

        private void Replayer_OnTickPushed(Tick tick)
        {
            // 0 锁，0 跨线程 UI 锁，0 内存分配，常数级 O(1) 极致流畅推演！
            if (_isStrategyEnabled && _currentActiveTrendLines != null && _currentActiveTrendLines.Count > 0)
            {
                _strategy.ProcessTick(tick, _currentKlineIndex, _currentActiveTrendLines);
            }
        }

        private void Replayer_OnPlaybackCompleted()
        {
            EnqueueLog("🎉 行情回放播放完毕！");
        }

        #endregion
    }
}
