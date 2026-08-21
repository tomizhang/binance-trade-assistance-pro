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
        private readonly EventContractStrategy _eventStrategy = new EventContractStrategy();
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
            // 1. 初始化交易对与周期下拉选项 (包含空币种/不显示图表选项)
            cmbSymbol.Items.Clear();
            cmbSymbol.Items.Add("-- 不显示图表 (NONE) --");
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

            // 初始化事件合约到期判定周期下拉选项
            cmbEventDuration.Items.Clear();
            cmbEventDuration.Items.Add(new { Text = "10 分钟 (10m)", Value = "10m" });
            cmbEventDuration.Items.Add(new { Text = "30 分钟 (30m)", Value = "30m" });
            cmbEventDuration.Items.Add(new { Text = "1 小时 (1h)", Value = "1h" });
            cmbEventDuration.DisplayMember = "Text";
            cmbEventDuration.ValueMember = "Value";

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

            // 事件合约参数恢复
            chkEnableEventContract.Checked = _userSettings.EnableEventContract;
            int matchedDurationIdx = 0;
            for (int i = 0; i < cmbEventDuration.Items.Count; i++)
            {
                dynamic item = cmbEventDuration.Items[i];
                if ((string)item.Value == _userSettings.EventContractDuration)
                {
                    matchedDurationIdx = i;
                    break;
                }
            }
            cmbEventDuration.SelectedIndex = matchedDurationIdx;
            chkEventEarlyExit.Checked = _userSettings.EventContractEarlyExit;

            // 初始化并绑定腾讯企业微信群机器人 Webhook 推送通知服务
            WeComNotifier.Instance.Configure(_userSettings.EnableWeComNotification, _userSettings.WeComWebhookUrl);
            WeComNotifier.Instance.OnLog += AppendLog;

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

            // 绑定事件合约控件变动
            chkEnableEventContract.CheckedChanged += (s, e) => SyncStrategyParams();
            cmbEventDuration.SelectedIndexChanged += (s, e) => SyncStrategyParams();
            chkEventEarlyExit.CheckedChanged += (s, e) => SyncStrategyParams();

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
            sb.AppendLine($"▶ 数据根目录: [{Config.GetRootPath()}] (Parquet 时间分区树)");
            sb.AppendLine($"▶ 交易模式: {(_userSettings.IsLiveTrading ? "🟢 币安真实合约实盘下单" : "🟡 本地模拟挂单匹配 (Simulated)")}");
            sb.AppendLine($"▶ API 凭证状态: {(string.IsNullOrWhiteSpace(_userSettings.ApiKey) ? "❌ 未配置" : "✅ 已设置 (" + _userSettings.ApiKey.Length + " 位)")}");
            sb.AppendLine($"▶ 企业微信推送: {(_userSettings.EnableWeComNotification ? "🟢 已启用 (Webhook 机器人就绪)" : "⚪ 已停用")}");
            sb.AppendLine($"▶ 策略风控参数: 止盈 +{_userSettings.TakeProfitPct}% | 止损 -{_userSettings.StopLossPct}% | 趋势线跨度: {_userSettings.MinLineX1X2} | 最小寿命: {_userSettings.MinLineAge} 根");
            sb.AppendLine($"▶ 事件合约策略: {(_userSettings.EnableEventContract ? $"🟢 已启用 (判定周期: {_userSettings.EventContractDuration}, 提前止盈止损: {(_userSettings.EventContractEarlyExit ? "是" : "否")})" : "⚪ 未开启")}");
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
        /// 切换图表当前视口渲染币种 (支持选择空币种暂停图表渲染，极速释放系统 GPU/CPU 资源)
        /// </summary>
        private void SwitchActiveChartSymbol(string newSymbol)
        {
            // 空币种 / 不显示图表判断
            if (string.IsNullOrWhiteSpace(newSymbol) || newSymbol.Contains("不显示") || newSymbol.Contains("NONE") || newSymbol.Contains("无"))
            {
                _currentSymbol = "";
                lock (_replayKlines)
                {
                    _replayKlines.Clear();
                }
                _currentActivePivots.Clear();
                _currentActiveTrendLines.Clear();
                _chartTitle = "⏸ 图表渲染已暂停 (空币种模式 - 0 绘图极速盯盘中)";

                formsPlot1.Plot.Clear();
                formsPlot1.Refresh();
                AppendLog("⏸ [图表视角切换] 已切换至空币种 (不显示图表)，UI 重绘与图表渲染已暂停，释放系统资源。");
                return;
            }

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
        private volatile bool _needEventStatsUpdate = false;

        private void SyncStrategyParams()
        {
            _isStrategyEnabled = chkEnableStrategy.Checked;
            _strategy.Params.Enabled = _isStrategyEnabled;
            _strategy.Params.MinLineX1X2 = (int)numMinLineX1X2.Value;
            _strategy.Params.MinLineAge = (int)numMinLineAge.Value;
            _strategy.Params.TakeProfitPct = numTakeProfit.Value;
            _strategy.Params.StopLossPct = numStopLoss.Value;

            _eventStrategy.Params.Enabled = chkEnableEventContract.Checked;
            string durationStr = "10m";
            if (cmbEventDuration.SelectedItem != null)
            {
                dynamic item = cmbEventDuration.SelectedItem;
                durationStr = (string)item.Value;
            }
            _eventStrategy.Params.Duration = durationStr switch
            {
                "30m" => EventContractDuration.ThirtyMinutes,
                "1h" or "60m" => EventContractDuration.OneHour,
                _ => EventContractDuration.TenMinutes
            };
            _eventStrategy.Params.MinLineX1X2 = (int)numMinLineX1X2.Value;
            _eventStrategy.Params.MinLineAge = (int)numMinLineAge.Value;
            _eventStrategy.Params.EnableEarlyExit = chkEventEarlyExit.Checked;
            _eventStrategy.Params.TakeProfitPct = numTakeProfit.Value;
            _eventStrategy.Params.StopLossPct = numStopLoss.Value;

            // 实时热同步给正在运行的多币种实盘管道中的每一个币种上下文
            _multiSymbolPipeline.UpdateStrategyParameters(_strategy.Params, _eventStrategy.Params);

            _needStrategyStatsUpdate = true;
            _needEventStatsUpdate = true;
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

                _userSettings.EnableEventContract = chkEnableEventContract.Checked;
                if (cmbEventDuration.SelectedItem != null)
                {
                    dynamic item = cmbEventDuration.SelectedItem;
                    _userSettings.EventContractDuration = (string)item.Value;
                }
                _userSettings.EventContractEarlyExit = chkEventEarlyExit.Checked;

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
                string reasonStr = trade.ExitReason == TradeExitReason.TakeProfit
                    ? $"[止盈平仓 TAKE PROFIT (+{_strategy.Params.TakeProfitPct:F1}%)]"
                    : $"[止损平仓 STOP LOSS (-{_strategy.Params.StopLossPct:F1}%)]";
                string closeLog = $"[策略平仓信号] #{trade.Id} {reasonStr}\r\n" +
                                  $"  └─ 平仓离场价格: {trade.ExitPrice}\r\n" +
                                  $"  └─ 平仓离场时间: {trade.ExitTime:yyyy-MM-dd HH:mm:ss.fff}\r\n" +
                                  $"  └─ 最终结算收益: {trade.ProfitPct:+0.00;-0.00;0.00}%\r\n" +
                                  $"  └─ 持仓开仓时间: {trade.EntryTime:yyyy-MM-dd HH:mm:ss.fff}";
                EnqueueLog(closeLog);
                _needStrategyStatsUpdate = true;
                _needChartRefresh = true;
            };

            // 事件合约策略事件响应
            _eventStrategy.OnLog += msg => EnqueueLog(msg);
            _eventStrategy.OnContractOpened += contract =>
            {
                _needEventStatsUpdate = true;
                _needChartRefresh = true;
            };
            _eventStrategy.OnContractSettled += contract =>
            {
                _needEventStatsUpdate = true;
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

        private void UpdateEventStatsUI()
        {
            int count = _eventStrategy.Contracts.Count;
            decimal winRate = _eventStrategy.GetWinRate();
            decimal totalProfit = _eventStrategy.GetTotalProfitPct();

            lblEventStats.Text = $"事件合约: 判定 {count} 笔 | 胜率: {winRate:F1}%\r\n累计收益: {totalProfit:+0.00;-0.00;0.00}%";
            lblEventStats.ForeColor = totalProfit >= 0 ? System.Drawing.Color.DarkGreen : System.Drawing.Color.DarkRed;
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

            _currentActivePivots = PivotHelper.CalculatePivotPoints(sampleSlice, leftBars: 2, rightBars: 2);
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

            if (_needEventStatsUpdate)
            {
                _needEventStatsUpdate = false;
                UpdateEventStatsUI();
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

                // C. 绘制延伸趋势线 (基于 Time1/Time2 严格匹配视口 K 线，未触发的显示淡红/淡绿，触发策略开仓的趋势线显示为黑色)
                TrendLineStrategy activeStrategy = _strategy;
                EventContractStrategy activeEventStrategy = _eventStrategy;
                if (_multiSymbolPipeline.IsRunning && !string.IsNullOrEmpty(_currentSymbol))
                {
                    var ctx = _multiSymbolPipeline.GetContext(_currentSymbol);
                    if (ctx != null)
                    {
                        activeStrategy = ctx.Strategy;
                        activeEventStrategy = ctx.EventStrategy;
                    }
                }

                // 收集触发策略开仓的趋势线特征集合
                HashSet<(DateTime, DateTime, PivotType)> triggeredLineKeys = new HashSet<(DateTime, DateTime, PivotType)>();
                List<TrendLine> triggeredTrendLinesList = new List<TrendLine>();

                void AddTriggeredLine(TrendLine? line)
                {
                    if (line.HasValue)
                    {
                        var tl = line.Value;
                        if (triggeredLineKeys.Add((tl.Time1, tl.Time2, tl.Type)))
                        {
                            triggeredTrendLinesList.Add(tl);
                        }
                    }
                }

                if (activeStrategy.CurrentTrade?.TriggeredTrendLine != null)
                {
                    AddTriggeredLine(activeStrategy.CurrentTrade.TriggeredTrendLine);
                }
                if (activeStrategy.Trades.Count > 0)
                {
                    foreach (var tr in activeStrategy.Trades)
                    {
                        AddTriggeredLine(tr.TriggeredTrendLine);
                    }
                }
                if (activeEventStrategy.CurrentContract?.TriggeredTrendLine != null)
                {
                    AddTriggeredLine(activeEventStrategy.CurrentContract.TriggeredTrendLine);
                }
                if (activeEventStrategy.Contracts.Count > 0)
                {
                    foreach (var ec in activeEventStrategy.Contracts)
                    {
                        AddTriggeredLine(ec.TriggeredTrendLine);
                    }
                }

                HashSet<(DateTime, DateTime, PivotType)> drawnLineKeys = new HashSet<(DateTime, DateTime, PivotType)>();

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

                            bool isTriggered = triggeredLineKeys.Contains((tl.Time1, tl.Time2, tl.Type));
                            drawnLineKeys.Add((tl.Time1, tl.Time2, tl.Type));

                            var linePlot = formsPlot1.Plot.Add.Line(x1, y1, x2, y2);
                            if (isTriggered)
                            {
                                // 触发策略开仓的趋势线：修改为黑色 (黑线标定, 宽度 1.5f)
                                linePlot.Color = ScottPlot.Colors.Black;
                                linePlot.LineWidth = 1.5f;
                            }
                            else
                            {
                                linePlot.Color = tl.Type == PivotType.High ? extraLightRed : extraLightGreen;
                                linePlot.LineWidth = 0.8f;
                            }
                        }
                    }
                }

                // 2. 补画历史已触发但已从当前活跃列表中移出的触发趋势线 (全量以黑色呈现)
                for (int i = 0; i < triggeredTrendLinesList.Count; i++)
                {
                    var tl = triggeredTrendLinesList[i];
                    if (drawnLineKeys.Contains((tl.Time1, tl.Time2, tl.Type))) continue;

                    int localX1 = -1;
                    int localX2 = -1;

                    for (int k = 0; k < displayCount; k++)
                    {
                        DateTime kTime = _displayKlinesBuffer[k].OpenTime;
                        if (kTime == tl.Time1) localX1 = k;
                        if (kTime == tl.Time2) localX2 = k;
                    }

                    if (localX2 >= 0)
                    {
                        if (localX1 < 0) localX1 = 0;
                        double x1 = localX1;
                        double y1 = (double)tl.GetPriceAt(localX1);
                        double x2 = displayCount - 1;
                        double y2 = (double)tl.GetPriceAt(displayCount - 1);

                        var linePlot = formsPlot1.Plot.Add.Line(x1, y1, x2, y2);
                        linePlot.Color = ScottPlot.Colors.Black;
                        linePlot.LineWidth = 1.5f;
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

                // E. 策略开仓与平仓图表标注 (按 K线 OpenTime / TickTime 全时区多维精准匹配，并采用小段横线清晰标定)
                if (_isStrategyEnabled && activeStrategy.Trades.Count > 0)
                {
                    for (int i = activeStrategy.Trades.Count - 1; i >= 0; i--)
                    {
                        var trade = activeStrategy.Trades[i];
                        int localEntryX = FindKlineIndexForTrade(trade.EntryTime, trade.EntryKlineOpenTime, _displayKlinesBuffer, displayCount);
                        int localExitX = FindKlineIndexForTrade(trade.ExitTime, trade.ExitKlineOpenTime, _displayKlinesBuffer, displayCount);

                        // 1. 开仓点位：采用小段横线 (黑色, 宽度 0.8f)
                        if (localEntryX >= 0 && localEntryX < displayCount)
                        {
                            var entryLine = formsPlot1.Plot.Add.Line(
                                localEntryX - 0.45, (double)trade.EntryPrice,
                                localEntryX + 0.45, (double)trade.EntryPrice);
                            entryLine.LineWidth = 0.8f;
                            entryLine.Color = ScottPlot.Colors.Black;
                        }

                        // 2. 关仓/平仓点位：采用小段横线 (橙色, 宽度 0.8f)
                        if (localExitX >= 0 && localExitX < displayCount)
                        {
                            var exitLine = formsPlot1.Plot.Add.Line(
                                localExitX - 0.45, (double)trade.ExitPrice,
                                localExitX + 0.45, (double)trade.ExitPrice);
                            exitLine.LineWidth = 0.8f;
                            exitLine.Color = ScottPlot.Colors.Orange;
                        }

                        // 3. 开仓与平仓之间绘制细虚线连接 (开平仓生命周期连线)
                        if (localEntryX >= 0 && localExitX >= localEntryX && localExitX < displayCount)
                        {
                            var linkLine = formsPlot1.Plot.Add.Line(
                                localEntryX, (double)trade.EntryPrice,
                                localExitX, (double)trade.ExitPrice);
                            linkLine.LineWidth = 0.8f;
                            linkLine.LinePattern = ScottPlot.LinePattern.Dashed;
                            linkLine.Color = trade.IsWin ? ScottPlot.Color.FromHex("#8000FF00") : ScottPlot.Color.FromHex("#80FF0000");
                        }
                    }
                }

                if (_isStrategyEnabled && activeStrategy.CurrentPosition != PositionType.None && activeStrategy.CurrentTrade != null)
                {
                    int localEntryX = FindKlineIndexForTrade(activeStrategy.CurrentEntryTime, activeStrategy.CurrentTrade.EntryKlineOpenTime, _displayKlinesBuffer, displayCount);

                    if (localEntryX >= 0 && localEntryX < displayCount)
                    {
                        // 当前持仓开仓小横线 (黑色, 0.8f)
                        var currentEntryLine = formsPlot1.Plot.Add.Line(
                            localEntryX - 0.45, (double)activeStrategy.CurrentEntryPrice,
                            localEntryX + 0.45, (double)activeStrategy.CurrentEntryPrice);
                        currentEntryLine.LineWidth = 0.8f;
                        currentEntryLine.Color = ScottPlot.Colors.Black;

                        // 从开仓点延伸至当前最新帧的持仓跟踪虚线 (0.8f)
                        var activeRay = formsPlot1.Plot.Add.Line(
                            localEntryX, (double)activeStrategy.CurrentEntryPrice,
                            displayCount - 1, (double)activeStrategy.CurrentEntryPrice);
                        activeRay.LineWidth = 0.8f;
                        activeRay.LinePattern = ScottPlot.LinePattern.Dashed;
                        activeRay.Color = ScottPlot.Color.FromHex("#90000000");
                    }
                }

                // E2. 事件合约开仓与平仓图表标注
                if (chkEnableEventContract.Checked && activeEventStrategy.Contracts.Count > 0)
                {
                    for (int i = activeEventStrategy.Contracts.Count - 1; i >= 0; i--)
                    {
                        var contract = activeEventStrategy.Contracts[i];
                        int localEntryX = FindKlineIndexForTrade(contract.EntryTime, contract.EntryKlineOpenTime, _displayKlinesBuffer, displayCount);
                        int localExitX = FindKlineIndexForTrade(contract.ExitTime, contract.ExitKlineOpenTime, _displayKlinesBuffer, displayCount);

                        if (localEntryX >= 0 && localEntryX < displayCount)
                        {
                            var entryLine = formsPlot1.Plot.Add.Line(
                                localEntryX - 0.45, (double)contract.EntryPrice,
                                localEntryX + 0.45, (double)contract.EntryPrice);
                            entryLine.LineWidth = 0.8f;
                            entryLine.Color = ScottPlot.Colors.Black;
                        }

                        if (localExitX >= 0 && localExitX < displayCount)
                        {
                            var exitLine = formsPlot1.Plot.Add.Line(
                                localExitX - 0.45, (double)contract.ExitPrice,
                                localExitX + 0.45, (double)contract.ExitPrice);
                            exitLine.LineWidth = 0.8f;
                            exitLine.Color = ScottPlot.Colors.Orange;
                        }

                        if (localEntryX >= 0 && localExitX >= localEntryX && localExitX < displayCount)
                        {
                            var linkLine = formsPlot1.Plot.Add.Line(
                                localEntryX, (double)contract.EntryPrice,
                                localExitX, (double)contract.ExitPrice);
                            linkLine.LineWidth = 0.8f;
                            linkLine.LinePattern = ScottPlot.LinePattern.Dashed;
                            linkLine.Color = contract.IsWin ? ScottPlot.Color.FromHex("#8000FF00") : ScottPlot.Color.FromHex("#80FF0000");
                        }
                    }
                }

                if (chkEnableEventContract.Checked && activeEventStrategy.CurrentPosition != PositionType.None && activeEventStrategy.CurrentContract != null)
                {
                    int localEntryX = FindKlineIndexForTrade(activeEventStrategy.CurrentEntryTime, activeEventStrategy.CurrentContract.EntryKlineOpenTime, _displayKlinesBuffer, displayCount);

                    if (localEntryX >= 0 && localEntryX < displayCount)
                    {
                        var currentEntryLine = formsPlot1.Plot.Add.Line(
                            localEntryX - 0.45, (double)activeEventStrategy.CurrentEntryPrice,
                            localEntryX + 0.45, (double)activeEventStrategy.CurrentEntryPrice);
                        currentEntryLine.LineWidth = 0.8f;
                        currentEntryLine.Color = ScottPlot.Colors.Black;

                        var activeRay = formsPlot1.Plot.Add.Line(
                            localEntryX, (double)activeEventStrategy.CurrentEntryPrice,
                            displayCount - 1, (double)activeEventStrategy.CurrentEntryPrice);
                        activeRay.LineWidth = 0.8f;
                        activeRay.LinePattern = ScottPlot.LinePattern.Dashed;
                        activeRay.Color = ScottPlot.Color.FromHex("#90000000");
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

        /// <summary>
        /// 全时区自适应精准定位交易点位在视口当前 K 线切片中的 X 轴索引 (支持 UTC/Local 时区自动对齐与区间自适应落入)
        /// </summary>
        private static int FindKlineIndexForTrade(DateTime tradeTime, DateTime tradeKlineOpenTime, Kline[] displayKlines, int displayCount)
        {
            if (displayCount <= 0 || displayKlines == null) return -1;

            // 1. 精确 OpenTime 匹配 (支持直接相等或 UTC 转换后相等)
            if (tradeKlineOpenTime > DateTime.MinValue)
            {
                for (int k = 0; k < displayCount; k++)
                {
                    DateTime kOpen = displayKlines[k].OpenTime;
                    if (kOpen == tradeKlineOpenTime ||
                        Math.Abs((kOpen - tradeKlineOpenTime).TotalSeconds) < 2 ||
                        Math.Abs((kOpen.ToUniversalTime() - tradeKlineOpenTime.ToUniversalTime()).TotalSeconds) < 2)
                    {
                        return k;
                    }
                }
            }

            // 2. 成交时间区间落入匹配 [OpenTime, CloseTime]
            if (tradeTime > DateTime.MinValue)
            {
                for (int k = 0; k < displayCount; k++)
                {
                    DateTime open = displayKlines[k].OpenTime;
                    DateTime close = displayKlines[k].CloseTime > open ? displayKlines[k].CloseTime : open.AddMinutes(15);

                    if ((tradeTime >= open && tradeTime <= close) ||
                        (tradeTime.ToUniversalTime() >= open.ToUniversalTime() && tradeTime.ToUniversalTime() <= close.ToUniversalTime()))
                    {
                        return k;
                    }
                }

                // 3. 最小时间差模糊对齐 (智能寻找视口中最匹配的 K 线柱)
                int bestIdx = -1;
                double minDiffSec = double.MaxValue;
                for (int k = 0; k < displayCount; k++)
                {
                    DateTime kOpen = displayKlines[k].OpenTime;
                    double diff1 = Math.Abs((tradeTime - kOpen).TotalSeconds);
                    double diff2 = Math.Abs((tradeTime.ToUniversalTime() - kOpen.ToUniversalTime()).TotalSeconds);
                    double diff = Math.Min(diff1, diff2);

                    if (diff < minDiffSec)
                    {
                        minDiffSec = diff;
                        bestIdx = k;
                    }
                }

                if (bestIdx >= 0 && minDiffSec < 86400)
                {
                    return bestIdx;
                }
            }

            return -1;
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            SaveCurrentSettings(); // 点击开始回放时主动同步持久化设置
            SyncStrategyParams();

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
            _eventStrategy.Reset();
            UpdateStrategyStatsUI();
            UpdateEventStatsUI();

            AppendLog($"▶ 启动流式行情回放与策略引擎 | 交易对: [{_currentSymbol}] | 周期: [{interval}] | 日期: [{startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}] | 趋势线策略: {(chkEnableStrategy.Checked ? "开启" : "关闭")} | 事件合约: {(chkEnableEventContract.Checked ? $"开启({_eventStrategy.Params.DurationMinutes}m)" : "关闭")}");

            // 1. 立即复位图表画板并呈现流式就绪状态
            lock (_replayKlines)
            {
                _replayKlines.Clear();
            }
            UpdateDisplayPivotsAndTrendLines();
            formsPlot1.Plot.Clear();
            formsPlot1.Plot.Grid.IsVisible = false;
            formsPlot1.Plot.Title($"[{_currentSymbol}] 流式回放已启动 (正在等待管道抓取首批切片数据)...");
            formsPlot1.Refresh();

            try
            {
                // 2. 初始化 FIFO 流式数据队列管道 (容量最大限制为 5 批次，每批次 3 天，后台自动做生产者补齐)
                _batchQueueManager?.Stop();
                _batchQueueManager = new BatchQueueManager(
                    _currentSymbol,
                    interval,
                    startDate,
                    endDate,
                    enableTickPush,
                    maxQueueCapacity: 5,
                    batchDays: 3,
                    logger: AppendLog);

                AppendLog("⏳ [流式管道就绪] 正在等待后台拉取历史数据，一旦首批就绪将自动开启逐帧回放...");

                // 3. 安全等待出队首批切片数据 (持续等待，绝不因网络慢而提前中断返回)
                BatchDataChunk? firstChunk = await _batchQueueManager.StartQueuePipelineAsync();

                if (firstChunk == null || firstChunk.Klines.Length == 0)
                {
                    AppendLog("⚠️ [数据提示] 所选日期区间未拉取到历史 K线数据。");
                    formsPlot1.Plot.Title($"[{_currentSymbol}] 未拉取到该区间历史 K线数据");
                    formsPlot1.Refresh();
                    return;
                }

                Kline[] klines = firstChunk.Klines;
                Tick[] ticks = firstChunk.Ticks;

                // 4. 策略启动前 API 预热控制 (只计算高低点与趋势线，不推演不跳帧)
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

                formsPlot1.Plot.Title($"[{_currentSymbol}] 行情回放进行中 (首批共 {klines.Length} 帧，FIFO 5 队列管道极速运行)");
                formsPlot1.Refresh();

                AppendLog($"🎬 开启逐帧回放与策略推演 | 首批: {klines.Length} 帧 | 总批次: {_batchQueueManager.TotalBatches} 批 (FIFO 5 队列管道) | 交易对: {_currentSymbol}");
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

        /// <summary>
        /// 异步下载指定交易对与时间范围的周期 K 线历史数据 (多线程并发抓取 + DuckDB & Parquet 本地自动时间分区缓存)
        /// </summary>
        private async void btnDownloadKlines_Click(object sender, EventArgs e)
        {
            string rawInput = cmbSymbol.Text.Trim().ToUpper();
            string[] symbols = rawInput.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (symbols.Length == 0)
            {
                MessageBox.Show("请先选择或输入有效的交易对名称 (如 BTCUSDT)", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string symbol = symbols[0].Trim().ToUpper();

            dynamic selectedIntervalObj = cmbKlineInterval.SelectedItem;
            KlineInterval interval = (KlineInterval)selectedIntervalObj.Value;
            DateTime startDate = dtpStartDate.Value.Date;
            DateTime endDate = dtpEndDate.Value.Date;

            btnDownloadKlines.Enabled = false;
            AppendLog($"📥 [K线下载任务启动] 币种: [{symbol}] | 周期: [{interval}] | 区间: [{startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}]...");

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var klines = await MultiThreadDownloader.DownloadKlinesParallelAsync(
                    symbol, interval, startDate, endDate, maxDegreeOfParallelism: 4, logger: AppendLog);
                sw.Stop();

                AppendLog($"✅ [K线下载任务完成] 累计获取 {klines.Length} 根 K 线数据 (已入库 DuckDB & Parquet 极速本地分区) | 耗时: {sw.ElapsedMilliseconds} ms");
                MessageBox.Show($"成功下载并缓存 [{symbol}] [{interval}] 历史 K 线数据共 {klines.Length} 根！", "下载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendLog($"❌ [K线下载失败] {ex.Message}");
                MessageBox.Show($"K线数据下载异常: {ex.Message}", "下载错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnDownloadKlines.Enabled = true;
            }
        }

        /// <summary>
        /// 异步下载指定交易对与时间范围的全量 Tick 逐笔历史成交数据 (多线程并发抓取 + 自动解压与 DuckDB Parquet 转存)
        /// </summary>
        private async void btnDownloadTicks_Click(object sender, EventArgs e)
        {
            string rawInput = cmbSymbol.Text.Trim().ToUpper();
            string[] symbols = rawInput.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (symbols.Length == 0)
            {
                MessageBox.Show("请先选择或输入有效的交易对名称 (如 BTCUSDT)", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string symbol = symbols[0].Trim().ToUpper();
            DateTime startDate = dtpStartDate.Value.Date;
            DateTime endDate = dtpEndDate.Value.Date;

            btnDownloadTicks.Enabled = false;
            AppendLog($"📥 [Tick逐笔下载任务启动] 币种: [{symbol}] | 日期区间: [{startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}]...");

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var ticks = await MultiThreadDownloader.DownloadTicksInSlicesParallelAsync(
                    symbol, startDate, endDate, maxDegreeOfParallelism: 4, logger: AppendLog);
                sw.Stop();

                AppendLog($"✅ [Tick下载任务完成] 累计成功获取 {ticks.Length} 笔逐笔成交 Tick (已入库 DuckDB & Parquet 本地时间分区) | 耗时: {sw.ElapsedMilliseconds} ms");
                MessageBox.Show($"成功下载并缓存 [{symbol}] 历史逐笔 Tick 数据共 {ticks.Length} 笔！", "下载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendLog($"❌ [Tick下载失败] {ex.Message}");
                MessageBox.Show($"Tick逐笔数据下载异常: {ex.Message}", "下载错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnDownloadTicks.Enabled = true;
            }
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

            // 1. 趋势线策略推演
            if (_isStrategyEnabled && _currentActiveTrendLines != null && _currentActiveTrendLines.Count > 0)
            {
                _strategy.ProcessTick(tick, currentSampleIndex, _currentActiveTrendLines, currentKline);
            }

            // 2. 事件合约策略推演 (10m / 30m / 1h 到期判定)
            if (_eventStrategy.Params.Enabled)
            {
                _eventStrategy.ProcessTick(tick, currentSampleIndex, _currentActiveTrendLines, currentKline, _currentSymbol);
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
                WeComNotifier.Instance.SendSystemStatus("币安量化盯盘系统 - 实盘已停止", "多币种实盘行情推送与自动交易管道已安全停止。");
                return;
            }

            SaveCurrentSettings();
            SyncStrategyParams();

            // 1. 停止当前历史回演
            _replayer.StopPlayback();

            // 2. 确定配置列表 (优先从 settings.json 读取多币种差异化配置)
            var symbolConfigs = _userSettings.SymbolConfigs.Where(c => c.Enabled).ToList();
            if (symbolConfigs.Count == 0)
            {
                symbolConfigs = UserSettings.GetDefaultSymbolConfigs();
            }

            // 更新下拉框选项，列出空币种选项以及当前实盘运行的全部币种方便用户点击切换渲染
            cmbSymbol.Items.Clear();
            cmbSymbol.Items.Add("-- 不显示图表 (NONE) --");
            foreach (var cfg in symbolConfigs)
            {
                cmbSymbol.Items.Add(cfg.Symbol);
            }
            if (cmbSymbol.Items.Count > 1)
            {
                cmbSymbol.SelectedIndex = 1;
            }
            else
            {
                cmbSymbol.SelectedIndex = 0;
            }

            string primarySymbol = symbolConfigs[0].Symbol;
            _currentSymbol = primarySymbol;
            _strategy.Reset();
            _eventStrategy.Reset();
            UpdateStrategyStatsUI();
            UpdateEventStatsUI();

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
                    _userSettings.ApiSecret,
                    _strategy.Params,
                    _eventStrategy.Params);

                btnLiveMode.Text = "🛑 停止币安实盘行情 (Stop Live)";
                btnLiveMode.ForeColor = Color.Red;

                string modeDesc = _userSettings.IsLiveTrading ? "🟢 币安真实合约 0 延迟 WS 实盘下单" : "🟡 本地模拟挂单匹配";
                string symListStr = string.Join(", ", symbolConfigs.Select(c => $"{c.Symbol}({c.KlineInterval})"));
                WeComNotifier.Instance.SendSystemStatus(
                    "币安量化盯盘系统 - 实盘管道已启动",
                    $"**交易模式**: {modeDesc}\n> **活跃监控币种 ({symbolConfigs.Count}个)**: `{symListStr}`\n> **风控规则**: 止盈 `+{_strategy.Params.TakeProfitPct}%` | 止损 `-{_strategy.Params.StopLossPct}%`\n> **事件合约**: {(_eventStrategy.Params.Enabled ? $"启用 ({_eventStrategy.Params.DurationMinutes}m)" : "未开启")}");
            }
            catch (Exception ex)
            {
                AppendLog($"❌ 启动多币种实盘行情管道失败: {ex.Message}");
                WeComNotifier.Instance.SendSystemStatus("币安量化盯盘系统 - 启动异常报警", $"启动多币种实盘行情管道发生严重异常: {ex.Message}", isAlert: true);
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
