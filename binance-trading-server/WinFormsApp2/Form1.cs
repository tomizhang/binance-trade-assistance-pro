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

        private static readonly double[] _tradeXBuffer = new double[1];
        private static readonly double[] _tradeYBuffer = new double[1];
        private readonly LiveFeedManager _liveFeedManager = new LiveFeedManager();
        private readonly MultiSymbolQueuePipeline _multiSymbolPipeline = new MultiSymbolQueuePipeline();

        public Form1()
        {
            InitializeComponent();
            InitControls();
            InitReplayer();
            InitStrategy();
            InitLiveFeed();
            InitUiTimer();
            InitializePlot();
            PrintConfigurationSummary();
        }

        private void InitLiveFeed()
        {
            _liveFeedManager.OnLog += AppendLog;
            _liveFeedManager.OnStatusChanged += status =>
            {
                _chartTitle = status;
                _needChartRefresh = true;
            };
            _liveFeedManager.OnLiveKlinePushed += LiveFeed_OnKlinePushed;
            _liveFeedManager.OnLiveTickPushed += LiveFeed_OnTickPushed;

            // 绑定多币种排队限流实盘管道事件
            _multiSymbolPipeline.OnLog += AppendLog;
            _multiSymbolPipeline.OnSymbolInitialized += (symbol, ctx) =>
            {
                if (symbol.Equals(_currentSymbol, StringComparison.OrdinalIgnoreCase))
                {
                    lock (_replayKlines)
                    {
                        _replayKlines.Clear();
                        _replayKlines.AddRange(ctx.Klines);
                        _currentKlineIndex = Math.Max(0, _replayKlines.Count - 1);
                    }
                    UpdateDisplayPivotsAndTrendLines();
                    _needChartRefresh = true;
                }
            };
            _multiSymbolPipeline.OnSymbolKlineUpdated += (symbol, liveKline) =>
            {
                if (symbol.Equals(_currentSymbol, StringComparison.OrdinalIgnoreCase))
                {
                    lock (_replayKlines)
                    {
                        if (_replayKlines.Count > 0 && _replayKlines.Last().OpenTime == liveKline.OpenTime)
                        {
                            _replayKlines[_replayKlines.Count - 1] = liveKline;
                        }
                        else
                        {
                            _replayKlines.Add(liveKline);
                            if (_replayKlines.Count > 500) _replayKlines.RemoveAt(0);
                        }
                        _currentKlineIndex = _replayKlines.Count - 1;
                    }
                    UpdateDisplayPivotsAndTrendLines();
                    _chartTitle = $"🟢 币安多币种实盘盯盘中 [{symbol}] - {liveKline.CloseTime:yyyy-MM-dd HH:mm:ss}";
                    _needChartRefresh = true;
                }
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

            // 3. 绑定参数控件变动自动保存与多币种图表视角切换逻辑
            cmbSymbol.TextChanged += (s, e) => SaveCurrentSettings();
            cmbSymbol.SelectedIndexChanged += (s, e) =>
            {
                SaveCurrentSettings();
                string selected = cmbSymbol.SelectedItem?.ToString() ?? cmbSymbol.Text;
                SwitchActiveChartSymbol(selected);
            };
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

        /// <summary>
        /// 格式化日志输出当前系统参数与多币种差异化配置信息总览
        /// </summary>
        private void PrintConfigurationSummary()
        {
            var symbolConfigs = _userSettings.SymbolConfigs.Where(c => c.Enabled).ToList();
            if (symbolConfigs.Count == 0)
            {
                symbolConfigs = UserSettings.GetDefaultSymbolConfigs();
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("======================================================================");
            sb.AppendLine("📋 [系统参数配置总览 - 已全量加载]");
            sb.AppendLine("----------------------------------------------------------------------");
            sb.AppendLine($"▶ 交易模式: {(_userSettings.IsLiveTrading ? "🟢 币安真实合约实盘下单" : "🟡 本地模拟挂单匹配 (Simulated)")}");
            sb.AppendLine($"▶ API 凭证状态: {(string.IsNullOrWhiteSpace(_userSettings.ApiKey) ? "❌ 未配置" : "✅ 已设置 (" + _userSettings.ApiKey.Length + " 位)")}");
            sb.AppendLine($"▶ 策略风控参数: 止盈 +{_userSettings.TakeProfitPct}% | 止损 -{_userSettings.StopLossPct}% | 趋势线跨度: {_userSettings.MinLineX1X2} | 最小寿命: {_userSettings.MinLineAge} 根");
            sb.AppendLine($"▶ 差异化多币种列表 (共 {symbolConfigs.Count} 个使能币种):");

            for (int i = 0; i < symbolConfigs.Count; i++)
            {
                var cfg = symbolConfigs[i];
                string levStr = cfg.Leverage <= 0 ? "-1 (自动最大杠杆)" : $"{cfg.Leverage}x";
                sb.AppendLine($"   [{i + 1}] {cfg.Symbol,-10} | 周期: {cfg.KlineInterval,-4} | 杠杆: {levStr,-16} | 单笔资金: {cfg.OrderQuantityUsdt} USDT");
            }
            sb.AppendLine("======================================================================");

            AppendLog(sb.ToString());
        }

        /// <summary>
        /// 切换图表当前视口渲染币种 (实盘多币种盯盘时支持随时切换观察)
        /// </summary>
        private void SwitchActiveChartSymbol(string newSymbol)
        {
            if (string.IsNullOrWhiteSpace(newSymbol)) return;
            string cleanSymbol = OrderExecutionQueue.SanitizeSymbol(newSymbol);
            if (string.IsNullOrEmpty(cleanSymbol)) return;

            _currentSymbol = cleanSymbol;

            if (_multiSymbolPipeline.IsRunning)
            {
                var ctx = _multiSymbolPipeline.GetContext(cleanSymbol);
                if (ctx != null && ctx.IsInitialized)
                {
                    lock (_replayKlines)
                    {
                        _replayKlines.Clear();
                        _replayKlines.AddRange(ctx.Klines);
                        _currentKlineIndex = Math.Max(0, _replayKlines.Count - 1);
                    }
                    _currentActivePivots = ctx.ActivePivots;
                    _currentActiveTrendLines = ctx.ActiveTrendLines;
                    _chartTitle = $"🟢 币安多币种实盘盯盘中 [{cleanSymbol}] - 视口渲染中";
                    UpdateDisplayPivotsAndTrendLines();
                    _needChartRefresh = true;
                    AppendLog($"🔄 [图表视角切换] 当前渲染币种已成功切换至: [{cleanSymbol}] (已有 K线: {ctx.Klines.Count} 根, 趋势线: {ctx.ActiveTrendLines.Count} 条)");
                }
            }
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
                string posStr = trade.Position == PositionType.Long ? "[买入做多 BUY LONG]" : "[卖出做空 SELL SHORT]";
                string openLog = $"[策略开仓信号] #{trade.Id} {posStr}\r\n" +
                                 $"  └─ Tick 成交价格: {trade.EntryTickPrice}\r\n" +
                                 $"  └─ Tick 成交时间: {trade.EntryTickTime:yyyy-MM-dd HH:mm:ss.fff}\r\n" +
                                 $"  └─ 归属 K线时间: {trade.EntryKlineOpenTime:yyyy-MM-dd HH:mm:ss} ~ {trade.EntryKlineCloseTime:yyyy-MM-dd HH:mm:ss} (帧索引: #{trade.EntryKlineIndex})\r\n" +
                                 $"  └─ 趋势线关键价格: {trade.EntryTrendLinePrice:F2}";
                EnqueueLog(openLog);
                _needStrategyStatsUpdate = true;
                _needChartRefresh = true;
            };

            _strategy.OnTradeClosed += trade =>
            {
                string reasonStr = trade.ExitReason == TradeExitReason.TakeProfit ? "[止盈平仓 TAKE PROFIT (+1.5%)]" : "[止损平仓 STOP LOSS (-0.8%)]";
                string closeLog = $"[策略平仓信号] #{trade.Id} {reasonStr}\r\n" +
                                  $"  └─ 平仓离场价格: {trade.ExitPrice}\r\n" +
                                  $"  └─ 平仓离场时间: {trade.ExitTime:yyyy-MM-dd HH:mm:ss.fff}\r\n" +
                                  $"  └─ 最终结算收益: {trade.ProfitPct:+0.00;-0.00;0.00}%\r\n" +
                                  $"  └─ 持仓开仓时间: {trade.EntryTime:yyyy-MM-dd HH:mm:ss.fff}";
                EnqueueLog(closeLog);
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

        private BatchQueueManager? _batchQueueManager = null;

        private void InitReplayer()
        {
            _replayer.OnKlinePushed += Replayer_OnKlinePushed;
            _replayer.OnStepBackward += Replayer_OnStepBackward;
            _replayer.OnTickPushed += Replayer_OnTickPushed;
            _replayer.OnPlaybackCompleted += Replayer_OnPlaybackCompleted;
            _replayer.OnLog += msg => EnqueueLog($"[回放引擎] {msg}");

            // FIFO 队列管道无缝出队接力：当前 3 天切片播放完毕时，自动从容量上限为 5 的队列中出队下一批次数据
            _replayer.OnNeedNextBatchChunk += async () =>
            {
                if (_batchQueueManager != null)
                {
                    return await _batchQueueManager.DequeueNextBatchAsync();
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

            // 自动异步将所有运行与策略事件日志落盘存入本地文件 (Config.TickDataRoot/logs)
            Logger.Log(message);
        }

        public void AppendLog(string message)
        {
            EnqueueLog(message);
        }

        private Kline[] _warmupKlinesBuffer = Array.Empty<Kline>();
        private readonly List<Kline> _combinedHistoryList = new List<Kline>(1500);
        private readonly Kline[] _displayKlinesBuffer = new Kline[500];
        private readonly double[] _pricesBuffer = new double[500];

        private void UpdateDisplayPivotsAndTrendLines()
        {
            int sampleSize = 0;
            lock (_replayKlines)
            {
                _combinedHistoryList.Clear();
                if (_warmupKlinesBuffer != null && _warmupKlinesBuffer.Length > 0)
                {
                    _combinedHistoryList.AddRange(_warmupKlinesBuffer);
                }
                _combinedHistoryList.AddRange(_replayKlines);

                int totalCombined = _combinedHistoryList.Count;
                if (totalCombined < 7)
                {
                    _currentActivePivots.Clear();
                    _currentActiveTrendLines.Clear();
                    return;
                }

                sampleSize = Math.Min(totalCombined, 500);
                _combinedHistoryList.CopyTo(totalCombined - sampleSize, _displayKlinesBuffer, 0, sampleSize);
            }

            // 零 LOH 分配 slice (已融合历史预热 K 线上下文)
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
                    _combinedHistoryList.Clear();
                    if (_warmupKlinesBuffer != null && _warmupKlinesBuffer.Length > 0)
                    {
                        _combinedHistoryList.AddRange(_warmupKlinesBuffer);
                    }
                    _combinedHistoryList.AddRange(_replayKlines);

                    int totalCombined = _combinedHistoryList.Count;
                    if (totalCombined == 0) return;

                    displayCount = Math.Min(totalCombined, 500);
                    _combinedHistoryList.CopyTo(totalCombined - displayCount, _displayKlinesBuffer, 0, displayCount);
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

                // B. 标注相对高低点 (基于 OpenTime 严格对齐视口 K 线，100% 绝对精准)
                if (_currentActivePivots != null && _currentActivePivots.Count > 0)
                {
                    List<double> highXs = new List<double>();
                    List<double> highYs = new List<double>();
                    List<double> lowXs = new List<double>();
                    List<double> lowYs = new List<double>();

                    for (int i = 0; i < _currentActivePivots.Count; i++)
                    {
                        var p = _currentActivePivots[i];
                        for (int k = 0; k < displayCount; k++)
                        {
                            if (_displayKlinesBuffer[k].OpenTime == p.Time)
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

                    // 相对高点 (HighPrice, 红色)
                    if (highXs.Count > 0)
                    {
                        var spHigh = formsPlot1.Plot.Add.ScatterPoints(highXs.ToArray(), highYs.ToArray());
                        spHigh.Color = ScottPlot.Colors.Red;
                        spHigh.MarkerSize = 4;
                    }

                    // 相对低点 (LowPrice, 绿色)
                    if (lowXs.Count > 0)
                    {
                        var spLow = formsPlot1.Plot.Add.ScatterPoints(lowXs.ToArray(), lowYs.ToArray());
                        spLow.Color = ScottPlot.Colors.LimeGreen;
                        spLow.MarkerSize = 4;
                    }
                }

                // C. 绘制延伸趋势线 (基于 Time1/Time2 严格匹配视口 K 线，100% 坐标精准)
                if (_currentActiveTrendLines != null && _currentActiveTrendLines.Count > 0)
                {
                    ScottPlot.Color extraLightRed = ScottPlot.Color.FromHex("#45FF8080");   // 超淡柔和红
                    ScottPlot.Color extraLightGreen = ScottPlot.Color.FromHex("#4580FF80"); // 超淡柔和绿

                    for (int i = 0; i < _currentActiveTrendLines.Count; i++)
                    {
                        var tl = _currentActiveTrendLines[i];
                        int localX1 = -1;
                        int localX2 = -1;

                        for (int k = 0; k < displayCount; k++)
                        {
                            DateTime kTime = _displayKlinesBuffer[k].OpenTime;
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

                // E. 策略开仓与平仓图表标注 (按 K线 OpenTime 精准匹配视口 x 坐标，0 下标偏移)
                if (_isStrategyEnabled && _strategy.Trades.Count > 0)
                {
                    for (int i = _strategy.Trades.Count - 1; i >= 0; i--)
                    {
                        var trade = _strategy.Trades[i];
                        int localEntryX = -1;
                        int localExitX = -1;

                        for (int k = 0; k < displayCount; k++)
                        {
                            DateTime kTime = _displayKlinesBuffer[k].OpenTime;
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

                    if (_strategy.CurrentPosition != PositionType.None)
                    {
                        int localEntryX = -1;
                        for (int k = 0; k < displayCount; k++)
                        {
                            if (_displayKlinesBuffer[k].OpenTime == _strategy.CurrentTrade.EntryKlineOpenTime)
                            {
                                localEntryX = k;
                                break;
                            }
                        }

                        if (localEntryX >= 0 && localEntryX < displayCount)
                        {
                            _tradeXBuffer[0] = localEntryX;
                            _tradeYBuffer[0] = (double)_strategy.CurrentEntryPrice;
                            var spCurrent = formsPlot1.Plot.Add.ScatterPoints(_tradeXBuffer, _tradeYBuffer);
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

            string rawInput = cmbSymbol.Text.Trim().ToUpper();
            string[] symbols = rawInput.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (symbols.Length == 0)
            {
                MessageBox.Show("请输入或选择有效的交易对名称 (如 BTCUSDT)", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _currentSymbol = symbols[0].Trim().ToUpper();

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
                // 1. 初始化 FIFO 流式数据队列管道 (容量最大限制为 5 批次，每批次 3 天，后台自动做生产者补齐)
                _batchQueueManager?.Stop();
                _batchQueueManager = new BatchQueueManager(
                    _currentSymbol,
                    interval,
                    startDate,
                    endDate,
                    enableTickPush,
                    maxQueueCapacity: 3,
                    batchDays: 1,
                    logger: AppendLog);

                // 2. 启动队列流水线并出队首批 3 天切片数据
                BatchDataChunk? firstChunk = await _batchQueueManager.StartQueuePipelineAsync();

                if (firstChunk == null || firstChunk.Klines.Length == 0)
                {
                    AppendLog("未装载到任何 K线数据，无法开始回放。");
                    return;
                }

                Kline[] klines = firstChunk.Klines;
                Tick[] ticks = firstChunk.Ticks;

                // 3. 策略启动前 API 预热控制 (只计算高低点与趋势线，不推演不跳帧)
                _warmupKlinesBuffer = Array.Empty<Kline>();
                if (chkEnableWarmup.Checked)
                {
                    try
                    {
                        DateTime warmupEndDate = startDate.AddTicks(-1);
                        AppendLog($"[API 预热开启] 准备获取 [{_currentSymbol}] [{interval}] 回放起点前 1000 根历史预热 K 线 (截止 {warmupEndDate:yyyy-MM-dd HH:mm:ss})...");
                        var fetchedWarmup = await DataHelper.FetchKlinesFromApiAsync(_currentSymbol, interval, endTime: warmupEndDate, limit: 1000);
                        if (fetchedWarmup != null && fetchedWarmup.Length > 0)
                        {
                            _warmupKlinesBuffer = fetchedWarmup;
                            AppendLog($"[API 预热成功] 成功装载 {fetchedWarmup.Length} 根历史预热 K 线 ({fetchedWarmup[0].OpenTime:yyyy-MM-dd HH:mm} ~ {fetchedWarmup.Last().OpenTime:yyyy-MM-dd HH:mm})，用于高低点与趋势线提前预热。");
                        }
                    }
                    catch (Exception apiEx)
                    {
                        AppendLog($"[API 预热提示] 获取历史预热 K 线未成功 ({apiEx.Message})，自动使用回放 K 线独立计算。");
                    }
                }
                else
                {
                    AppendLog("[API 预热关闭] 用户未勾选 API 预热，直接使用装载的回放 K 线计算趋势线。");
                }

                // 4. 复位图表并启动回放引擎
                lock (_replayKlines)
                {
                    _replayKlines.Clear();
                }

                UpdateDisplayPivotsAndTrendLines();

                formsPlot1.Plot.Clear();
                formsPlot1.Plot.Grid.IsVisible = false;
                formsPlot1.Plot.Title($"[{_currentSymbol}] 行情回放准备完毕 (首批 3 天共 {klines.Length} 帧，FIFO 5 队列管道极速运行)");
                formsPlot1.Refresh();

                AppendLog($"▶ 启动行情回放与策略引擎 | 首批: 3 天 ({klines.Length} 帧) | 总批次: {_batchQueueManager.TotalBatches} 批 (FIFO 5 队列管道) | 交易对: {_currentSymbol} | 策略: {(chkEnableStrategy.Checked ? "开启" : "关闭")}");
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
                Kline currentKline = default;
                int currentSampleIndex = 0;
                lock (_replayKlines)
                {
                    if (_currentKlineIndex >= 0 && _currentKlineIndex < _replayKlines.Count)
                    {
                        currentKline = _replayKlines[_currentKlineIndex];
                    }
                    currentSampleIndex = Math.Max(0, Math.Min(_combinedHistoryList.Count, 500) - 1);
                }
                _strategy.ProcessTick(tick, currentSampleIndex, _currentActiveTrendLines, currentKline);
            }
        }

        private void Replayer_OnPlaybackCompleted()
        {
            EnqueueLog("🎉 行情回放播放完毕！");
        }

        #endregion

        #region 币安实盘 WebSocket + API 接口对接 (Binance Real-Time Live Feed)

        private async void btnLiveMode_Click(object sender, EventArgs e)
        {
            if (_multiSymbolPipeline.IsRunning || _liveFeedManager.IsRunning)
            {
                _multiSymbolPipeline.StopPipeline();
                await _liveFeedManager.StopLiveFeedAsync();
                btnLiveMode.Text = "📡 启动币安实盘行情 (Live Stream)";
                btnLiveMode.ForeColor = Color.DarkGreen;
                AppendLog("⏹ 多币种实盘行情与管道模式已停止。");
                return;
            }

            // 1. 停止当前历史回演
            _replayer.StopPlayback();

            // 2. 确定配置列表 (优先从 settings.json 读取多币种差异化配置)
            var symbolConfigs = _userSettings.SymbolConfigs.Where(c => c.Enabled).ToList();
            if (symbolConfigs.Count == 0)
            {
                symbolConfigs = UserSettings.GetDefaultSymbolConfigs();
            }

            // 更新下拉框选项，列出当前实盘运行的全部币种方便用户点击切换渲染
            cmbSymbol.Items.Clear();
            foreach (var cfg in symbolConfigs)
            {
                cmbSymbol.Items.Add(cfg.Symbol);
            }
            if (cmbSymbol.Items.Count > 0)
            {
                cmbSymbol.SelectedIndex = 0;
            }

            string primarySymbol = symbolConfigs[0].Symbol;
            _currentSymbol = primarySymbol;
            _strategy.Reset();
            UpdateStrategyStatsUI();

            // 格式化输出实盘/管道启动时的参数配置信息总览
            PrintConfigurationSummary();

            btnLiveMode.Enabled = false;
            try
            {
                // 3. 启动多币种排队限流实盘管道 (历史 K 线排队抓取、高低点独立计算、趋势线推算、下单队列与 WebSocket 分发)
                await _multiSymbolPipeline.StartPipelineAsync(
                    symbolConfigs,
                    _userSettings.IsLiveTrading,
                    _userSettings.ApiKey,
                    _userSettings.ApiSecret);

                btnLiveMode.Text = "🛑 停止币安实盘行情 (Stop Live)";
                btnLiveMode.ForeColor = Color.Red;
            }
            catch (Exception ex)
            {
                AppendLog($"❌ 启动多币种实盘行情管道失败: {ex.Message}");
                MessageBox.Show($"启动多币种实盘行情失败: {ex.Message}", "实盘错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnLiveMode.Enabled = true;
            }
        }

        private void LiveFeed_OnKlinePushed(Kline liveKline)
        {
            lock (_replayKlines)
            {
                if (_replayKlines.Count > 0 && _replayKlines.Last().OpenTime == liveKline.OpenTime)
                {
                    _replayKlines[_replayKlines.Count - 1] = liveKline; // 实时刷新当前最新未完结 K 线柱
                }
                else
                {
                    _replayKlines.Add(liveKline); // 新 K 线柱完结生成
                    if (_replayKlines.Count > 500)
                    {
                        _replayKlines.RemoveAt(0);
                    }
                }
                _currentKlineIndex = _replayKlines.Count - 1;
            }

            UpdateDisplayPivotsAndTrendLines();
            _chartTitle = $"🟢 币安 [{_liveFeedManager.CurrentSymbol}] 实盘行情推送 - {liveKline.CloseTime:yyyy-MM-dd HH:mm:ss}";
            _needChartRefresh = true;
        }

        private void LiveFeed_OnTickPushed(Tick tick)
        {
            // 0 延迟直投实盘 Tick 进策略引擎
            if (_isStrategyEnabled && _currentActiveTrendLines != null && _currentActiveTrendLines.Count > 0)
            {
                Kline currentKline = default;
                int currentSampleIndex = 0;
                lock (_replayKlines)
                {
                    if (_currentKlineIndex >= 0 && _currentKlineIndex < _replayKlines.Count)
                    {
                        currentKline = _replayKlines[_currentKlineIndex];
                    }
                    currentSampleIndex = Math.Max(0, Math.Min(_combinedHistoryList.Count, 500) - 1);
                }
                _strategy.ProcessTick(tick, currentSampleIndex, _currentActiveTrendLines, currentKline);
            }
        }

        #endregion
    }
}
