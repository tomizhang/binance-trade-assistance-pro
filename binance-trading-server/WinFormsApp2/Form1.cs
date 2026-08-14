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
            cmbKlineInterval.Items.Add(new { Text = "1分钟 (OneMinute)", Value = KlineInterval.OneMinute });
            cmbKlineInterval.Items.Add(new { Text = "15分钟 (FifteenMinutes)", Value = KlineInterval.FifteenMinutes });
            cmbKlineInterval.Items.Add(new { Text = "1小时 (OneHour)", Value = KlineInterval.OneHour });
            cmbKlineInterval.Items.Add(new { Text = "1天 (OneDay)", Value = KlineInterval.OneDay });
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
            _strategy.Params.Enabled = chkEnableStrategy.Checked;
            _strategy.Params.MinLineX1X2 = (int)numMinLineX1X2.Value;
            _strategy.Params.MinLineAge = (int)numMinLineAge.Value;
            _strategy.Params.TakeProfitPct = numTakeProfit.Value;
            _strategy.Params.StopLossPct = numStopLoss.Value;

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
                UpdateStrategyStatsUI();
                _needChartRefresh = true;
            };

            _strategy.OnTradeClosed += trade =>
            {
                string reasonStr = trade.ExitReason == TradeExitReason.TakeProfit ? "🎯 止盈 (TP +1.5%)" : "🛑 止损 (SL -0.8%)";
                EnqueueLog($"🏁 [策略平仓 #{trade.Id}] {trade.Position} | {reasonStr} | 价格: {trade.ExitPrice} | 盈亏: {trade.ProfitPct:+0.00;-0.00;0.00}%");
                UpdateStrategyStatsUI();
                _needChartRefresh = true;
            };
        }

        private void UpdateStrategyStatsUI()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(UpdateStrategyStatsUI));
                return;
            }

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

        private void InitReplayer()
        {
            _replayer.OnKlinePushed += Replayer_OnKlinePushed;
            _replayer.OnStepBackward += Replayer_OnStepBackward;
            _replayer.OnTickPushed += Replayer_OnTickPushed;
            _replayer.OnPlaybackCompleted += Replayer_OnPlaybackCompleted;
            _replayer.OnLog += msg => EnqueueLog($"[回放引擎] {msg}");
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

        private void UiRenderTimer_Tick(object sender, EventArgs e)
        {
            // 1. 批量渲染日志文本
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

            // 2. 批量渲染 ScottPlot 图表、枢轴高低点、趋势线与策略交易开平仓标注
            if (_needChartRefresh)
            {
                _needChartRefresh = false;
                Kline[] fullArray;
                lock (_replayKlines)
                {
                    fullArray = _replayKlines.ToArray();
                }

                if (fullArray.Length > 0)
                {
                    const int maxDisplayKlines = 500;
                    Kline[] klineArray = fullArray.Length > maxDisplayKlines 
                        ? fullArray.Skip(fullArray.Length - maxDisplayKlines).ToArray() 
                        : fullArray;

                    int startIndexInFull = fullArray.Length - klineArray.Length;

                    double[] currentPrices = klineArray.Select(k => (double)k.ClosePrice).ToArray();

                    formsPlot1.Plot.Clear();
                    formsPlot1.Plot.Grid.IsVisible = false;

                    // A. 绘制价格主信号曲线
                    formsPlot1.Plot.Add.Signal(currentPrices);
                    formsPlot1.Plot.Title(_chartTitle);

                    // B. 计算并标注相对高低点与延长趋势线
                    if (klineArray.Length >= 7)
                    {
                        var pivots = PivotHelper.CalculatePeaksCombinedFast(klineArray, leftBars: 3, rightBars: 3);

                        // 相对高点 (HighPrice, 红色)
                        var highs = pivots.Where(p => p.Type == PivotType.High).ToList();
                        if (highs.Count > 0)
                        {
                            double[] highXs = highs.Select(p => (double)p.Index).ToArray();
                            double[] highYs = highs.Select(p => (double)p.Price).ToArray();
                            var spHigh = formsPlot1.Plot.Add.ScatterPoints(highXs, highYs);
                            spHigh.Color = ScottPlot.Colors.Red;
                            spHigh.MarkerSize = 3;
                        }

                        // 相对低点 (LowPrice, 绿色)
                        var lows = pivots.Where(p => p.Type == PivotType.Low).ToList();
                        if (lows.Count > 0)
                        {
                            double[] lowXs = lows.Select(p => (double)p.Index).ToArray();
                            double[] lowYs = lows.Select(p => (double)p.Price).ToArray();
                            var spLow = formsPlot1.Plot.Add.ScatterPoints(lowXs, lowYs);
                            spLow.Color = ScottPlot.Colors.LimeGreen;
                            spLow.MarkerSize = 3;
                        }

                        // C. 绘制延伸趋势线 (超淡半透明红/绿)
                        var trendLines = TrendLineHelper.GenerateTrendLinesFromPivots(klineArray, pivots, filterPenetrated: true);
                        
                        var visibleTrendLines = trendLines.Where(tl => 
                            tl.X1 >= 0 && tl.X1 < klineArray.Length &&
                            tl.X2 >= 0 && tl.X2 < klineArray.Length
                        );

                        var displayLines = visibleTrendLines.OrderByDescending(tl => tl.LineX1X2);

                        ScottPlot.Color extraLightRed = ScottPlot.Color.FromHex("#45FF8080");   // 超淡柔和红
                        ScottPlot.Color extraLightGreen = ScottPlot.Color.FromHex("#4580FF80"); // 超淡柔和绿

                        foreach (var tl in displayLines)
                        {
                            double x1 = tl.X1;
                            double y1 = (double)tl.Y1;
                            double x2 = klineArray.Length - 1;
                            double y2 = (double)tl.GetPriceAt((int)x2);

                            var linePlot = formsPlot1.Plot.Add.Line(x1, y1, x2, y2);
                            linePlot.Color = tl.Type == PivotType.High ? extraLightRed : extraLightGreen;
                            linePlot.LineWidth = 0.8f;
                        }
                    }

                    // D. 高成交量 K 线标记标注 (金黄色)
                    if (chkHighlightHighVolume.Checked && klineArray.Length > 0)
                    {
                        double avgVol = (double)klineArray.Average(k => k.Volume);
                        double thresholdVol = avgVol * 2.0;

                        List<double> volXs = new List<double>();
                        List<double> volYs = new List<double>();

                        for (int i = 0; i < klineArray.Length; i++)
                        {
                            if ((double)klineArray[i].Volume >= thresholdVol)
                            {
                                volXs.Add(i);
                                volYs.Add((double)klineArray[i].LowPrice * 0.9985);
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
                    if (chkEnableStrategy.Checked)
                    {
                        // 1. 标注历史完成交易
                        foreach (var trade in _strategy.Trades)
                        {
                            int localEntryX = trade.EntryKlineIndex - startIndexInFull;
                            int localExitX = trade.ExitKlineIndex - startIndexInFull;

                            // 开仓标记 (青色/品红)
                            if (localEntryX >= 0 && localEntryX < klineArray.Length)
                            {
                                var spEntry = formsPlot1.Plot.Add.ScatterPoints(new double[] { localEntryX }, new double[] { (double)trade.EntryPrice });
                                spEntry.Color = trade.Position == PositionType.Long ? ScottPlot.Colors.Cyan : ScottPlot.Colors.Magenta;
                                spEntry.MarkerSize = 7;
                            }

                            // 平仓标记 (绿色止盈 / 红色止损)
                            if (localExitX >= 0 && localExitX < klineArray.Length)
                            {
                                var spExit = formsPlot1.Plot.Add.ScatterPoints(new double[] { localExitX }, new double[] { (double)trade.ExitPrice });
                                spExit.Color = trade.IsWin ? ScottPlot.Colors.LimeGreen : ScottPlot.Colors.Red;
                                spExit.MarkerSize = 8;
                            }
                        }

                        // 2. 标注当前持仓点位
                        if (_strategy.CurrentPosition != PositionType.None)
                        {
                            int localEntryX = _strategy.CurrentEntryKlineIndex - startIndexInFull;
                            if (localEntryX >= 0 && localEntryX < klineArray.Length)
                            {
                                var spCurrent = formsPlot1.Plot.Add.ScatterPoints(new double[] { localEntryX }, new double[] { (double)_strategy.CurrentEntryPrice });
                                spCurrent.Color = _strategy.CurrentPosition == PositionType.Long ? ScottPlot.Colors.DeepSkyBlue : ScottPlot.Colors.HotPink;
                                spCurrent.MarkerSize = 9;
                            }
                        }
                    }

                    // F. 视口自动缩放/聚焦最新价格附近 (当勾选 chkAutoFitPrice 时)
                    if (chkAutoFitPrice.Checked && klineArray.Length > 0)
                    {
                        int sampleSize = Math.Min(klineArray.Length, 80);
                        var recentKlines = klineArray.Skip(klineArray.Length - sampleSize).ToArray();
                        double minPrice = (double)recentKlines.Min(k => k.LowPrice);
                        double maxPrice = (double)recentKlines.Max(k => k.HighPrice);
                        double margin = (maxPrice - minPrice) * 0.12;
                        if (margin == 0) margin = maxPrice * 0.01;
                        if (margin == 0) margin = 1.0;

                        formsPlot1.Plot.Axes.SetLimitsY(minPrice - margin, maxPrice + margin);
                    }

                    formsPlot1.Refresh();
                }
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

            AppendLog($"准备加载 [{_currentSymbol}] [{interval}] 日期范围 [{startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}] 数据以启动回放...");

            try
            {
                // 1. 多线程并发装载 K线数据 (后台完整处理数据队列保持全量)
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

                // 3. 策略启动前：控制是否从币安 API 在线预热获取 1000 根最新 K 线用于计算基准趋势线
                Kline[] api1000Klines = Array.Empty<Kline>();
                if (chkEnableWarmup.Checked)
                {
                    try
                    {
                        AppendLog($"[API 预热开启] 准备在线从币安 API 获取 1000 根 [{_currentSymbol}] [{interval}] 最新 K 线进行趋势线预热...");
                        api1000Klines = await DataHelper.FetchKlinesFromApiAsync(_currentSymbol, interval, limit: 1000);
                        AppendLog($"[API 预热成功] 成功获取 1000 根在线 K 线数据 ({api1000Klines[0].OpenTime:yyyy-MM-dd HH:mm} ~ {api1000Klines.Last().OpenTime:yyyy-MM-dd HH:mm})。");
                    }
                    catch (Exception apiEx)
                    {
                        AppendLog($"[API 预热提示] 在线获取 1000 根 K 线未成功 ({apiEx.Message})，自动回退使用下载装载的 K 线数据。");
                    }
                }
                else
                {
                    AppendLog("[API 预热关闭] 用户未勾选 API 预热，直接使用下载装载的 K 线数据计算趋势线。");
                }

                // 优先使用在线拉取的 1000 根 K 线计算基准枢轴高低点与趋势线；若关闭或失败则回退使用装载的 K 线数据
                Kline[] baselineKlines = (api1000Klines != null && api1000Klines.Length >= 40) 
                    ? api1000Klines 
                    : (klines.Length > 1000 ? klines.Skip(klines.Length - 1000).ToArray() : klines);

                var pivots = PivotHelper.CalculatePeaksCombinedFast(baselineKlines, leftBars: 3, rightBars: 3);
                int highCount = pivots.Count(p => p.Type == PivotType.High);
                int lowCount = pivots.Count(p => p.Type == PivotType.Low);
                AppendLog($"[Pivot 枢轴计算] 基于 1000 根 K 线 (跨度=3) 分析完成: 相对高点 (HighPrice, 红色) {highCount} 个，相对低点 (LowPrice, 绿色) {lowCount} 个。");

                var trendLines = TrendLineHelper.GenerateTrendLinesFromPivots(baselineKlines, pivots, filterPenetrated: true);
                AppendLog($"[TrendLine 趋势线交互] 基于 1000 根 K 线已计算生成未破位有效基准趋势线 {trendLines.Count} 条 (供策略在回放开始前直接使用)。");

                // 4. 复位图表并启动回放引擎
                lock (_replayKlines)
                {
                    _replayKlines.Clear();
                }

                formsPlot1.Plot.Clear();
                formsPlot1.Plot.Grid.IsVisible = false;
                formsPlot1.Plot.Title($"[{_currentSymbol}] 行情回放准备完毕 (后台总数据量 {klines.Length} 帧，UI 视图默认显示最新500帧)");
                formsPlot1.Refresh();

                AppendLog($"▶ 启动行情回放与策略引擎 | 后台 K线: {klines.Length} 帧 | 交易对: {_currentSymbol} | API预热: {(chkEnableWarmup.Checked ? "开启" : "关闭")} | 策略功能: {(chkEnableStrategy.Checked ? "开启 (line_x1_x2>=40, age>=80, TP=1.5%, SL=0.8%)" : "关闭")}");
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
            }

            _chartTitle = $"[{_currentSymbol}] 动态回放中 ({current}/{total}) - {kline.OpenTime:yyyy-MM-dd HH:mm:ss}";
            _needChartRefresh = true;
        }

        private void Replayer_OnStepBackward(Kline[] subKlines, int current, int total)
        {
            lock (_replayKlines)
            {
                _replayKlines.Clear();
                _replayKlines.AddRange(subKlines);
            }

            var lastTime = subKlines.Length > 0 ? subKlines[subKlines.Length - 1].OpenTime.ToString("yyyy-MM-dd HH:mm:ss") : "";
            _chartTitle = $"[{_currentSymbol}] 单步向后 ({current}/{total}) - {lastTime}";
            _needChartRefresh = true;
        }

        private void Replayer_OnTickPushed(Tick tick)
        {
            // 将 Tick 数据驱动给趋势线回调策略引擎处理
            if (chkEnableStrategy.Checked)
            {
                Kline[] currentKlines;
                lock (_replayKlines)
                {
                    currentKlines = _replayKlines.ToArray();
                }

                if (currentKlines.Length >= 7)
                {
                    var pivots = PivotHelper.CalculatePeaksCombinedFast(currentKlines, leftBars: 3, rightBars: 3);
                    var trendLines = TrendLineHelper.GenerateTrendLinesFromPivots(currentKlines, pivots, filterPenetrated: true);

                    _strategy.ProcessTick(tick, currentKlines, trendLines);
                }
            }
        }

        private void Replayer_OnPlaybackCompleted()
        {
            EnqueueLog("🎉 行情回放播放完毕！");
        }

        #endregion
    }
}
