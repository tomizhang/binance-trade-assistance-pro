using ConsoleApp1;
using ScottPlot;
using ScottPlot.Colormaps;
using ScottPlot.Plottables;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static ConsoleApp1.PivotHelper;
using Color = System.Drawing.Color;

namespace WinFormsApp1
{
    public partial class Form1 : Form
    {
        readonly System.Windows.Forms.Timer AddNewDataTimer = new() { Interval = 20, Enabled = true };
        readonly System.Windows.Forms.Timer UpdatePlotTimer = new() { Interval = 60, Enabled = true };

        // ScottPlot 变量
        private DataStreamer Streamer1;
        //private DataStreamer StreamerVolume;
        private VerticalLine VLine;

        // 十字准星与 Text 标注
        private Crosshair MyCrosshair;
        private ScottPlot.Plottables.Text MyTooltipText;

        // 随机数据备用发生器
        private RandomWalker Walker1 = new RandomWalker(seed: 0, mult: 1);

        // --- 队列方式接入 ScottPlot ---
        private readonly ConcurrentQueue<BinanceFuturesKlineItem> _klineQueue = new();
        private readonly ConcurrentQueue<BinanceFuturesAggTradeItem> _tickQueue = new();
        private readonly List<BinanceFuturesAggTradeItem> _historyTicks = new(50000);
        private readonly SymbolDataProvider _dataProvider = new SymbolDataProvider(maxDegreeOfParallelism: 5);
        private readonly BinanceFuturesAggTradeHelper _aggTradeHelper = new BinanceFuturesAggTradeHelper();
        private CancellationTokenSource? _fetchCts;
        private long _totalEnqueuedCount = 0;
        private string _currentSymbol = "BTCUSDT";

        // --- 内存缓存区 ---
        private  List<int> _peaksBuffer = new(64);
        private  List<int> _valleysBuffer = new(64);
        private readonly List<IPlottable> _currentOverlayPlottables = new(256);
        private decimal[] _highsCache = new decimal[1000];
        private decimal[] _lowsCache = new decimal[1000];
        // --- 预计算数据结果缓存 (数据计算与 UI 渲染彻底解耦) ---
        private readonly List<AngleTrendLineInfo> _cachedLinesToDraw = new(128);
        private int _cachedActiveRedCount = 0;
        private int _cachedActiveGreenCount = 0;

        // 按需/事件驱动图层渲染签名
        private string _lastPivotSignature = string.Empty;
        private bool _forceUpdatePivotOverlays = false;

        readonly System.Windows.Forms.Timer RewindTimer = new() { Interval = 20, Enabled = false };
        private readonly List<BinanceFuturesKlineItem> _historyKlines = new(10000);
        private bool _isRewinding = false;
        private readonly TrendlineStrategyEngine _strategyEngine = new();

        

        public Form1()
        {
            InitializeComponent();

            // 1. 初始化 ComboBox 默认选项
            InitControls();

            // 2.1 初始化收盘价 DataStreamer (1000 数据点, 左 Y 轴)
            Streamer1 = formsPlot1.Plot.Add.DataStreamer(1000);
            Streamer1.ViewScrollLeft();
            Streamer1.ManageAxisLimits = false;
            Streamer1.LineStyle.Color = ScottPlot.Colors.Blue;
            Streamer1.LegendText = "收盘价 (Close Price)";

            // 2.2 初始化成交量 DataStreamer (1000 数据点, 右 Y 轴)
            //StreamerVolume = formsPlot1.Plot.Add.DataStreamer(1000);
            //StreamerVolume.ViewScrollLeft();
            //StreamerVolume.ManageAxisLimits = false;
            //StreamerVolume.LineStyle.Color = ScottPlot.Colors.Purple.WithAlpha(0.6f);
            //StreamerVolume.LineStyle.Width = 1.5f;
            //StreamerVolume.LegendText = "成交量 (Volume)";
            //StreamerVolume.Axes.YAxis = formsPlot1.Plot.Axes.Right;

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

            // 回退定时器
            RewindTimer.Tick += (s, e) => PerformRewindStep();

            // 注册策略开平仓事件，向 RichTextBox 实时追加彩色日志
            _strategyEngine.OnTradeOpened += (type, price, barIndex) =>
            {
                if (type == StrategyPositionType.Long)
                {
                    AppendLog($"[BUY LONG] Open @ {price:F1} (TP: {_strategyEngine.TakeProfitPrice:F1} | SL: {_strategyEngine.StopLossPrice:F1})", Color.DarkGreen, true);
                }
                else
                {
                    AppendLog($"[SELL SHORT] Open @ {price:F1} (TP: {_strategyEngine.TakeProfitPrice:F1} | SL: {_strategyEngine.StopLossPrice:F1})", Color.DarkRed, true);
                }
            };

            _strategyEngine.OnTradeClosed += (trade) =>
            {
                int total = _strategyEngine.CompletedTrades.Count;
                int win = _strategyEngine.CompletedTrades.Count(t => t.IsProfit);
                int loss = total - win;
                double rate = total > 0 ? (double)win / total * 100 : 0;

                string statsSuffix = $" | WinRate: {rate:F0}% (Total: {total}, Win: {win}, Loss: {loss})";

                if (trade.IsProfit)
                {
                    AppendLog($"[PROFIT EXIT] {trade.ExitReason} @ {trade.ExitPrice:F1} | PnL: +{trade.ProfitPct:F2}%{statsSuffix}", Color.DarkGoldenrod, true);
                }
                else
                {
                    AppendLog($"[LOSS EXIT] {trade.ExitReason} @ {trade.ExitPrice:F1} | PnL: {trade.ProfitPct:F2}%{statsSuffix}", Color.Purple, true);
                }
            };

            // 绑定鼠标移入与滚轮事件，滚动鼠标滚轮时自动触发单步向前（向上滚）与单步回退（向下滚）
            btnRewind.MouseEnter += (s, e) => btnRewind.Focus();
            btnStepForward.MouseEnter += (s, e) => btnStepForward.Focus();

            btnRewind.MouseWheel += OnControlMouseWheel;
            btnStepForward.MouseWheel += OnControlMouseWheel;

            AppendLog("系统就绪：请选择币种与周期后点击【获取币种历史数据】（支持在按钮上滚动鼠标滚轮触发单步步进/回退）", Color.DimGray);

            // 4. 定时器 1：根据选择的播放速度倍速，从 ConcurrentQueue 队列中消费 K 线数据并接入 ScottPlot
            AddNewDataTimer.Interval = 100; // 20ms 默认
            AddNewDataTimer.Tick += (s, e) =>
            {
                // 核心增加【逐笔 Tick 数据回放模式】支持
                if (!_isRewinding && chkTickReplay != null && chkTickReplay.Checked && !_tickQueue.IsEmpty)
                {
                    int speedIndex = cmbPlaySpeed.SelectedIndex >= 0 ? cmbPlaySpeed.SelectedIndex : 1;
                    int batchSize = speedIndex switch
                    {
                        0 => 1,
                        1 => 1,
                        2 => 1,
                        3 => 1,
                        4 => 1,
                        5 => _tickQueue.Count,
                        _ => 1
                    };

                    List<double> tickPrices = new();
                    List<double> tickVolumes = new();

                    for (int i = 0; i < batchSize && _tickQueue.TryDequeue(out var tick); i++)
                    {
                        tickPrices.Add(tick.Price);
                        tickVolumes.Add(tick.Quantity);
                        _historyTicks.Add(tick);

                        // 逐笔 Tick 动态更新或生成 K 线
                        if (_historyKlines.Count == 0)
                        {
                            _historyKlines.Add(new BinanceFuturesKlineItem
                            {
                                OpenTimeMs = tick.TradeTimeMs,
                                CloseTimeMs = tick.TradeTimeMs,
                                Open = (decimal)tick.Price,
                                High = (decimal)tick.Price,
                                Low = (decimal)tick.Price,
                                Close = (decimal)tick.Price,
                                Volume = (decimal)tick.Quantity
                            });
                        }
                        else
                        {
                            var currentKline = _historyKlines[^1];
                            currentKline.Close = (decimal)tick.Price;
                            if ((decimal)tick.Price > currentKline.High) currentKline.High = (decimal)tick.Price;
                            if ((decimal)tick.Price < currentKline.Low) currentKline.Low = (decimal)tick.Price;
                            currentKline.Volume += (decimal)tick.Quantity;
                        }
                    }

                    if (tickPrices.Count > 0)
                    {
                        Streamer1.AddRange(tickPrices);
                        //StreamerVolume.AddRange(tickVolumes);
                        PerformTrendlineAnalysisAndEvaluation();
                    }
                    return;
                }

                if (!_isRewinding && !_klineQueue.IsEmpty)
                {
                    int speedIndex = cmbPlaySpeed.SelectedIndex >= 0 ? cmbPlaySpeed.SelectedIndex : 1;

                    // 动态调整定时器间隔 (0.5x=35ms, 1.0x=20ms, 2.0x=15ms, 5.0x/10.0x/全速=10ms)
                    int targetInterval = speedIndex switch
                    {
                        0 => 150,
                        1 => 100,
                        2 => 50,
                        _ => 10
                    };
                    if (AddNewDataTimer.Interval != targetInterval)
                    {
                        AddNewDataTimer.Interval = targetInterval;
                    }

                    // 动态计算每次提取的 BatchSize 批量大小
                    int batchSize = speedIndex switch
                    {
                        0 => 1,                
                        1 => 1,                
                        2 => 1,                
                        3 => 1,                
                        4 => 1,                
                        5 => 1,
                        _ => 1
                    };

                    List<double> valuesToAdd = new();
                    List<double> volumesToAdd = new();
                    for (int i = 0; i < batchSize && _klineQueue.TryDequeue(out var kline); i++)
                    {
                        valuesToAdd.Add((double)kline.Close);
                        volumesToAdd.Add((double)kline.Volume);
                        _historyKlines.Add(kline);
                    }

                    if (valuesToAdd.Count > 0)
                    {
                        Streamer1.AddRange(valuesToAdd);
                        //StreamerVolume.AddRange(volumesToAdd);
                        
                        // 核心架构优化：高低点位计算、趋势线运算与策略评测彻底放在数据 Tick 中完成 (计算与 UI 彻底解耦)
                        PerformTrendlineAnalysisAndEvaluation();
                    }
                }
            };

            // 5. 定时器 2：UI 渲染刷新 (UI 仅负责轻量级画布刷新，计算与 Overlay 图层生成已在数据 Tick 原子完成)
            UpdatePlotTimer.Interval = 30; // 30ms (~33 FPS)
            UpdatePlotTimer.Tick += (s, e) =>
            {
                if (Streamer1.HasNewData)
                {
                    long totalCount = Streamer1.Data.CountTotal;
                    formsPlot1.Plot.Title($"[{_currentSymbol}] 已接入数据点: {totalCount:N0} | 队列剩余: {_klineQueue.Count}");

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
            var config = AppConfigManager.LoadConfig();

            if (cmbSymbol != null)
            {
                cmbSymbol.Items.Clear();
                foreach (var sym in config.SavedSymbols)
                {
                    cmbSymbol.Items.Add(sym);
                }
                cmbSymbol.Text = config.Symbol;
                cmbSymbol.Leave += (s, e) =>
                {
                    string typed = cmbSymbol.Text.Trim().ToUpperInvariant();
                    if (!string.IsNullOrWhiteSpace(typed))
                    {
                        cmbSymbol.Text = typed;
                        if (!cmbSymbol.Items.Contains(typed))
                        {
                            cmbSymbol.Items.Add(typed);
                        }
                    }
                    SaveCurrentConfig();
                };
                cmbSymbol.SelectedIndexChanged += (s, e) => SaveCurrentConfig();
            }

            if (cmbInterval != null)
            {
                if (cmbInterval.Items.Contains(config.Interval))
                {
                    cmbInterval.SelectedItem = config.Interval;
                }
                else if (cmbInterval.SelectedIndex < 0)
                {
                    cmbInterval.SelectedIndex = 0;
                }
                cmbInterval.SelectedIndexChanged += (s, e) => SaveCurrentConfig();
            }

            if (cmbTimeRange != null)
            {
                if (config.TimeRangeIndex >= 0 && config.TimeRangeIndex < cmbTimeRange.Items.Count)
                {
                    cmbTimeRange.SelectedIndex = config.TimeRangeIndex;
                }
                else if (cmbTimeRange.SelectedIndex < 0)
                {
                    cmbTimeRange.SelectedIndex = 2;
                }
                cmbTimeRange.SelectedIndexChanged += (s, e) => SaveCurrentConfig();
            }

            if (dtpStart != null && dtpEnd != null)
            {
                try
                {
                    dtpStart.Value = config.StartTime;
                    dtpEnd.Value = config.EndTime;
                }
                catch
                {
                    dtpStart.Value = DateTime.Now.AddDays(-1);
                    dtpEnd.Value = DateTime.Now;
                }

                dtpStart.ValueChanged += (s, e) =>
                {
                    if (cmbTimeRange != null && cmbTimeRange.SelectedIndex != 6)
                    {
                        cmbTimeRange.SelectedIndex = 6;
                    }
                    SaveCurrentConfig();
                };
                dtpEnd.ValueChanged += (s, e) =>
                {
                    if (cmbTimeRange != null && cmbTimeRange.SelectedIndex != 6)
                    {
                        cmbTimeRange.SelectedIndex = 6;
                    }
                    SaveCurrentConfig();
                };
            }

            if (cmbTimeRange != null)
            {
                cmbTimeRange.SelectedIndexChanged += (s, e) =>
                {
                    if (dtpStart == null || dtpEnd == null) return;
                    DateTime now = DateTime.Now;
                    switch (cmbTimeRange.SelectedIndex)
                    {
                        case 0:
                            dtpStart.Value = now.AddHours(-1);
                            dtpEnd.Value = now;
                            break;
                        case 1:
                            dtpStart.Value = now.AddHours(-6);
                            dtpEnd.Value = now;
                            break;
                        case 2:
                            dtpStart.Value = now.AddHours(-24);
                            dtpEnd.Value = now;
                            break;
                        case 3:
                            dtpStart.Value = now.AddDays(-3);
                            dtpEnd.Value = now;
                            break;
                        case 4:
                            dtpStart.Value = now.AddDays(-7);
                            dtpEnd.Value = now;
                            break;
                        case 5:
                            dtpStart.Value = now.AddDays(-30);
                            dtpEnd.Value = now;
                            break;
                    }
                    SaveCurrentConfig();
                };
            }

            if (cmbPlaySpeed != null)
            {
                if (config.PlaySpeedIndex >= 0 && config.PlaySpeedIndex < cmbPlaySpeed.Items.Count)
                {
                    cmbPlaySpeed.SelectedIndex = config.PlaySpeedIndex;
                }
                else if (cmbPlaySpeed.SelectedIndex < 0)
                {
                    cmbPlaySpeed.SelectedIndex = 0;
                }
                cmbPlaySpeed.SelectedIndexChanged += (s, e) => SaveCurrentConfig();
            }

            if (numTakeProfit != null) numTakeProfit.Value = config.TakeProfitPct;
            if (numStopLoss != null) numStopLoss.Value = config.StopLossPct;
            if (numExpectedProfit != null) numExpectedProfit.Value = config.ExpectedProfitPct;
            if (chkEnableStrategy != null) { chkEnableStrategy.Checked = config.EnableStrategy; chkEnableStrategy.CheckedChanged += (s, e) => SaveCurrentConfig(); }
            if (chkAutoFitY != null) { chkAutoFitY.Checked = config.AutoFitY; chkAutoFitY.CheckedChanged += (s, e) => SaveCurrentConfig(); }
            if (chkTickReplay != null) { chkTickReplay.Checked = config.TickReplay; chkTickReplay.CheckedChanged += (s, e) => SaveCurrentConfig(); }

            if (numTakeProfit != null && numStopLoss != null)
            {
                numTakeProfit.ValueChanged += (s, e) => { UpdateStrategyRiskReward(); SaveCurrentConfig(); };
                numTakeProfit.KeyUp += (s, e) => { UpdateStrategyRiskReward(); SaveCurrentConfig(); };
                numTakeProfit.TextChanged += (s, e) => { UpdateStrategyRiskReward(); SaveCurrentConfig(); };
                numTakeProfit.Leave += (s, e) => { UpdateStrategyRiskReward(); SaveCurrentConfig(); };

                numStopLoss.ValueChanged += (s, e) => { UpdateStrategyRiskReward(); SaveCurrentConfig(); };
                numStopLoss.KeyUp += (s, e) => { UpdateStrategyRiskReward(); SaveCurrentConfig(); };
                numStopLoss.TextChanged += (s, e) => { UpdateStrategyRiskReward(); SaveCurrentConfig(); };
                numStopLoss.Leave += (s, e) => { UpdateStrategyRiskReward(); SaveCurrentConfig(); };

                if (numExpectedProfit != null)
                {
                    numExpectedProfit.ValueChanged += (s, e) => { UpdateStrategyRiskReward(); SaveCurrentConfig(); };
                    numExpectedProfit.KeyUp += (s, e) => { UpdateStrategyRiskReward(); SaveCurrentConfig(); };
                    numExpectedProfit.TextChanged += (s, e) => { UpdateStrategyRiskReward(); SaveCurrentConfig(); };
                    numExpectedProfit.Leave += (s, e) => { UpdateStrategyRiskReward(); SaveCurrentConfig(); };
                }

                UpdateStrategyRiskReward();
            }
        }

        private void SaveCurrentConfig()
        {
            try
            {
                var config = new AppConfig
                {
                    Symbol = cmbSymbol?.Text?.Trim().ToUpperInvariant() ?? "BTCUSDT",
                    SavedSymbols = cmbSymbol?.Items.Cast<object>().Select(x => x.ToString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList() ?? new(),
                    Interval = cmbInterval?.SelectedItem?.ToString() ?? "15m",
                    TimeRangeIndex = cmbTimeRange?.SelectedIndex ?? 2,
                    StartTime = dtpStart?.Value ?? DateTime.Now.AddDays(-1),
                    EndTime = dtpEnd?.Value ?? DateTime.Now,
                    PlaySpeedIndex = cmbPlaySpeed?.SelectedIndex ?? 0,
                    TakeProfitPct = numTakeProfit?.Value ?? 1.0m,
                    StopLossPct = numStopLoss?.Value ?? 1.0m,
                    ExpectedProfitPct = numExpectedProfit?.Value ?? 3.0m,
                    EnableStrategy = chkEnableStrategy?.Checked ?? true,
                    AutoFitY = chkAutoFitY?.Checked ?? true,
                    TickReplay = chkTickReplay?.Checked ?? false
                };
                AppConfigManager.SaveConfig(config);
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveCurrentConfig();
            base.OnFormClosing(e);
        }

        private void UpdateStrategyRiskReward()
        {
            if (_strategyEngine != null && numTakeProfit != null && numStopLoss != null)
            {
                if (double.TryParse(numTakeProfit.Text, out double tp) && tp > 0)
                {
                    _strategyEngine.TakeProfitPct = tp;
                }
                else
                {
                    _strategyEngine.TakeProfitPct = (double)numTakeProfit.Value;
                }

                if (double.TryParse(numStopLoss.Text, out double sl) && sl > 0)
                {
                    _strategyEngine.StopLossPct = sl;
                }
                else
                {
                    _strategyEngine.StopLossPct = (double)numStopLoss.Value;
                }

                if (numExpectedProfit != null)
                {
                    if (double.TryParse(numExpectedProfit.Text, out double ep) && ep > 0)
                    {
                        _strategyEngine.ExpectedProfitPct = ep;
                    }
                    else
                    {
                        _strategyEngine.ExpectedProfitPct = (double)numExpectedProfit.Value;
                    }
                }
            }
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
            cmbSymbol.Text = symbol;
            if (!cmbSymbol.Items.Contains(symbol))
            {
                cmbSymbol.Items.Add(symbol);
            }

            string intervalStr = cmbInterval.SelectedItem?.ToString() ?? "15m";
            if (!FuturesKlineIntervalExtensions.TryParseInterval(intervalStr, out var interval))
            {
                interval = FuturesKlineInterval.Min15;
            }

            if (interval == FuturesKlineInterval.Tick1)
            {
                if (chkTickReplay != null) chkTickReplay.Checked = true;
            }

            // 计算起始与结束时间 (支持预设与自定义日期时间 DateTimePicker)
            DateTime startTime;
            DateTime endTime;

            if (cmbTimeRange.SelectedIndex == 6 && dtpStart != null && dtpEnd != null)
            {
                startTime = dtpStart.Value.ToUniversalTime();
                endTime = dtpEnd.Value.ToUniversalTime();
                if (startTime >= endTime)
                {
                    MessageBox.Show("开始时间必须小于结束时间！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            else
            {
                endTime = DateTime.UtcNow;
                startTime = cmbTimeRange.SelectedIndex switch
                {
                    0 => endTime.AddHours(-1),
                    1 => endTime.AddHours(-6),
                    2 => endTime.AddHours(-24),
                    3 => endTime.AddDays(-3),
                    4 => endTime.AddDays(-7),
                    5 => endTime.AddDays(-30),
                    _ => endTime.AddHours(-24)
                };

                if (dtpStart != null && dtpEnd != null)
                {
                    dtpStart.Value = startTime.ToLocalTime();
                    dtpEnd.Value = endTime.ToLocalTime();
                }
            }

            // 取消上一次未完成的获取请求
            _fetchCts?.Cancel();
            _fetchCts = new CancellationTokenSource();
            var token = _fetchCts.Token;

            // 重置状态与队列
            _currentSymbol = symbol;
            btnFetch.Enabled = false;

            // 清空当前队列与历史缓存
            while (_klineQueue.TryDequeue(out _)) { }
            while (_tickQueue.TryDequeue(out _)) { }
            _historyTicks.Clear();
            _historyKlines.Clear();
            _strategyEngine.Reset();
            _totalEnqueuedCount = 0;

            // 核心优化：仅在勾选 [逐笔 Tick 回放模式] 或选择 [1tick] 周期时，才采用 Tick 明细数据聚合拟合！
            bool useTickFetch = (chkTickReplay != null && chkTickReplay.Checked) || (interval == FuturesKlineInterval.Tick1);

            if (useTickFetch)
            {
                Streamer1.LegendText = $"{symbol} {intervalStr} (Tick Aggregated)";
                AppendLog($"[TICK FETCH] 勾选 Tick 模式，开始拉取 [{symbol}] 币安 AggTrade 明细并拟合合成 {intervalStr} K 线...", Color.DarkBlue, true);
            }
            else
            {
                Streamer1.LegendText = $"{symbol} {intervalStr} (Standard K-Line)";
                AppendLog($"[KLINE FETCH] 开始使用标准多线程 API 秒级拉取 [{symbol}] {intervalStr} 历史 K 线...", Color.DarkBlue, true);
            }

            try
            {
                if (useTickFetch)
                {
                    // 1. 勾选 Tick 模式：拉取 AggTrade 逐笔明细并拟合合成 K 线
                    Action<List<BinanceFuturesKlineItem>> onBackgroundChunkSynthesized = null!;
                    onBackgroundChunkSynthesized = (newKlines) =>
                    {
                        if (newKlines == null || newKlines.Count == 0) return;
                        if (this.InvokeRequired)
                        {
                            this.BeginInvoke(new Action(() => onBackgroundChunkSynthesized(newKlines)));
                            return;
                        }

                        int addedCount = 0;
                        foreach (var kline in newKlines.OrderBy(k => k.OpenTimeMs))
                        {
                            _klineQueue.Enqueue(kline);
                            Interlocked.Increment(ref _totalEnqueuedCount);
                            addedCount++;
                        }

                        AppendLog($"[TICK 流式追加] 后台并发下载完成 +{addedCount} 条 Tick 拟合 K 线，已注入队列继续播放", Color.DarkBlue);
                    };

                    DateTime pStart = endTime.AddMinutes(-20);
                    if (pStart < startTime) pStart = startTime;
                    var rawTicks = await _aggTradeHelper.GetAggTradesAsync(symbol, limit: 1000, startTime: pStart, endTime: endTime, cancellationToken: token);
                    foreach (var t in rawTicks.OrderBy(t => t.TradeTimeMs))
                    {
                        _tickQueue.Enqueue(t);
                    }

                    List<BinanceFuturesKlineItem> priorityKlines = await _aggTradeHelper.PriorityStreamingFetchTickKlinesAsync(
                        symbol, interval, startTime, endTime, onBackgroundChunkSynthesized, token);

                    List<BinanceFuturesKlineItem> sortedPriorityKlines = priorityKlines
                        .GroupBy(k => k.OpenTimeMs)
                        .Select(g => g.First())
                        .OrderBy(k => k.OpenTimeMs)
                        .ToList();

                    foreach (var kline in sortedPriorityKlines)
                    {
                        _klineQueue.Enqueue(kline);
                        Interlocked.Increment(ref _totalEnqueuedCount);
                    }

                    int priorityCount = sortedPriorityKlines.Count;
                    AppendLog($"[TICK 秒级加载 OK] 成功秒载 [{symbol}] 首批 {priorityCount} 条 Tick 拟合 K 线，图表即刻开始实时播放！", Color.Blue, true);
                }
                else
                {
                    // 2. 未勾选 Tick 模式：使用标准多线程 K 线 API 秒载数据
                    var progress = new Progress<FetchStatusReport>(report => { });

                    List<BinanceFuturesKlineItem> fetchedKlines = await _dataProvider.GetSymbolDataAsync(
                        symbol, interval, startTime, endTime, useCache: true, progress, token);

                    List<BinanceFuturesKlineItem> sortedKlines = fetchedKlines
                        .GroupBy(k => k.OpenTimeMs)
                        .Select(g => g.First())
                        .OrderBy(k => k.OpenTimeMs)
                        .ToList();

                    foreach (var kline in sortedKlines)
                    {
                        _klineQueue.Enqueue(kline);
                        Interlocked.Increment(ref _totalEnqueuedCount);
                    }

                    int fetchedCount = sortedKlines.Count;
                    AppendLog($"[KLINE FETCH OK] 成功载入 [{symbol}] {fetchedCount} 条标准 K 线并排队播放", Color.Blue, true);
                }
            }
            catch (OperationCanceledException)
            {
                AppendLog("[FETCH CANCEL] 取消获取历史数据", Color.Gray);
            }
            catch (Exception ex)
            {
                AppendLog($"[FETCH ERROR] {ex.Message}", Color.Red, true);
                MessageBox.Show($"获取历史数据失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnFetch.Enabled = true;
            }
        }

        /// <summary>
        /// 彩色日志追加输出（多空盈亏与系统状态不同颜色区分）
        /// </summary>
        private void AppendLog(string message, Color color, bool bold = false)
        {
            if (rtbLog.InvokeRequired)
            {
                rtbLog.BeginInvoke(new Action(() => AppendLog(message, color, bold)));
                return;
            }

            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.SelectionColor = color;
            rtbLog.SelectionFont = new System.Drawing.Font("Consolas", 8.5F, bold ? System.Drawing.FontStyle.Bold : System.Drawing.FontStyle.Regular);

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            rtbLog.AppendText($"[{timestamp}] {message}\r\n");
            rtbLog.SelectionColor = rtbLog.ForeColor;
            rtbLog.ScrollToCaret();
        }

        /// <summary>
        /// 重置/复位图表按钮事件 handler
        /// </summary>
        private void button1_Click(object sender, EventArgs e)
        {
            // 1. 取消在途的网络下载任务
            _fetchCts?.Cancel();

            // 2. 清空 ConcurrentQueue 数据队列与历史
            while (_klineQueue.TryDequeue(out _)) { }
            while (_tickQueue.TryDequeue(out _)) { }
            _historyTicks.Clear();
            _historyKlines.Clear();
            _strategyEngine.Reset();
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
            //StreamerVolume.Data.Clear();

            // 5. 复位视图与标题
            formsPlot1.Plot.Title("图表已重置");

            // 6. 恢复按钮与状态
            btnFetch.Enabled = true;
            AppendLog("图表与队列数据已重置完成，请重新选择币种后点击【获取币种历史数据】。", Color.DimGray);
            lblTrendState.Text = "策略状态: 未计算";
            lblTrendState.BackColor = Color.FromArgb(245, 245, 245);
            lblTrendState.ForeColor = Color.DimGray;

            // 7. 刷新界面
            formsPlot1.Refresh();
        }

        #region 单击步长回退数据处理逻辑

        private void btnRewind_Click(object? sender, EventArgs e)
        {
            PerformRewindStep();
        }

        private void PerformRewindStep()
        {
            if (_historyKlines.Count == 0) return;

            int speedIndex = cmbPlaySpeed.SelectedIndex >= 0 ? cmbPlaySpeed.SelectedIndex : 1;
            int rewindStep = speedIndex switch
            {
                0 => 1,                 
                1 => 1,                 
                2 => 1,                
                3 => 1,                
                4 => 1,                
                5 => 1, 
                _ => 1
            };

            rewindStep = Math.Min(rewindStep, _historyKlines.Count);

            // 从历史记录中弹出最后 N 个已播放的数据点
            int startIndex = _historyKlines.Count - rewindStep;
            var rewoundItems = _historyKlines.GetRange(startIndex, rewindStep);
            _historyKlines.RemoveRange(startIndex, rewindStep);

            // 将被回退的数据倒序重新压回队列最前端，以便继续正向顺序播放
            var remainingQueue = _klineQueue.ToList();
            while (_klineQueue.TryDequeue(out _)) { }
            while (_tickQueue.TryDequeue(out _)) { }
            _historyTicks.Clear();

            foreach (var item in rewoundItems)
            {
                _klineQueue.Enqueue(item);
            }
            foreach (var item in remainingQueue)
            {
                _klineQueue.Enqueue(item);
            }

            // 清空 ScottPlot 并全量重新灌入剩余回退后的历史数据点
            Streamer1.Data.Clear();
            //StreamerVolume.Data.Clear();
            _forceUpdatePivotOverlays = true;

            if (_historyKlines.Count > 0)
            {
                Streamer1.AddRange(_historyKlines.Select(k => (double)k.Close));
                //StreamerVolume.AddRange(_historyKlines.Select(k => (double)k.Volume));
                PerformTrendlineAnalysisAndEvaluation();
            }
            else
            {
                foreach (var item in _currentOverlayPlottables)
                {
                    formsPlot1.Plot.Remove(item);
                }
                _currentOverlayPlottables.Clear();
                lblTrendState.Text = "趋势状态: 观望盘整";
                lblTrendState.BackColor = Color.FromArgb(245, 245, 245);
                lblTrendState.ForeColor = Color.DimGray;
            }

            // 优先计算视口可见范围 Y 轴极限，再刷新图形呈现
            UpdateYAxisLimits();
            formsPlot1.Plot.Title($"[{_currentSymbol}] 已回退步长: {rewindStep} 点 | 剩余: {_historyKlines.Count:N0} 点 | 待播放队列: {_klineQueue.Count}");
            formsPlot1.Refresh();
        }

        #endregion

        #region 单击步长向前推进处理逻辑

        private void btnStepForward_Click(object? sender, EventArgs e)
        {
            PerformForwardStep();
        }

        private void PerformForwardStep()
        {
            if (_klineQueue.IsEmpty) return;

            int speedIndex = cmbPlaySpeed.SelectedIndex >= 0 ? cmbPlaySpeed.SelectedIndex : 1;
            int forwardStep = speedIndex switch
            {
                0 => 1,                 
                1 => 1,                 
                2 => 1,                
                3 => 1,                
                4 => 1,                
                5 => 1, 
                _ => 1
            };

            List<double> valuesToAdd = new();
            List<double> volumesToAdd = new();
            for (int i = 0; i < forwardStep && _klineQueue.TryDequeue(out var kline); i++)
            {
                valuesToAdd.Add((double)kline.Close);
                volumesToAdd.Add((double)kline.Volume);
                _historyKlines.Add(kline);
            }

            if (valuesToAdd.Count > 0)
            {
                Streamer1.AddRange(valuesToAdd);
                //StreamerVolume.AddRange(volumesToAdd);
                _forceUpdatePivotOverlays = true;
                PerformTrendlineAnalysisAndEvaluation();
                UpdateYAxisLimits();
                formsPlot1.Plot.Title($"[{_currentSymbol}] 已单步向前: {valuesToAdd.Count} 点 | 总计渲染: {_historyKlines.Count:N0} 点 | 队列剩余: {_klineQueue.Count}");
                formsPlot1.Refresh();
            }
        }

        /// <summary>
        /// 鼠标滚轮响应 handler：向上滚动 (Delta > 0) 触发单步向前推进；向下滚动 (Delta < 0) 触发单步回退
        /// </summary>
        private void OnControlMouseWheel(object? sender, MouseEventArgs e)
        {
            if (e.Delta > 0)
            {
                PerformForwardStep();
            }
            else if (e.Delta < 0)
            {
                PerformRewindStep();
            }
        }

        #endregion

        /// <summary>
        /// 动态设置 Y 轴可见范围为当前视口可见 K 线波幅 (含 8% 留白边距)
        /// </summary>
        private void UpdateYAxisLimits()
        {
            // 若用户取消勾选【自动更新 Y 轴范围】，则跳过坐标轴限制重置，保留用户自定义手动缩放与平移状态
            if (chkAutoFitY != null && !chkAutoFitY.Checked)
            {
                return;
            }

            double yMin = double.MaxValue;
            double yMax = double.MinValue;

            // 1. 精准取当前视口内可见的最多 1000 个 K 线数据点计算高低边界
            if (_historyKlines != null && _historyKlines.Count > 0)
            {
                int visibleCount = Math.Min(1000, _historyKlines.Count);
                int startIndex = _historyKlines.Count - visibleCount;

                for (int i = startIndex; i < _historyKlines.Count; i++)
                {
                    double close = (double)_historyKlines[i].Close;
                    if (close < yMin) yMin = close;
                    if (close > yMax) yMax = close;
                }
            }

            // 2. 如果历史数据为空，降级从 Streamer 原始数据数组计算
            if (yMin == double.MaxValue || yMax == double.MinValue)
            {
                double[] streamer1Data = Streamer1.Data.Data;
                if (streamer1Data != null && streamer1Data.Length > 0)
                {
                    for (int i = 0; i < streamer1Data.Length; i++)
                    {
                        double val = streamer1Data[i];
                        if (val != 0 && !double.IsNaN(val) && !double.IsInfinity(val))
                        {
                            if (val < yMin) yMin = val;
                            if (val > yMax) yMax = val;
                        }
                    }
                }
            }

            // 3. 动态更新 Y 轴可见极限 (按 8% 波幅留白，最小留白 10.0)
            if (yMin <= yMax && yMin != double.MaxValue)
            {
                double padding = Math.Max((yMax - yMin) * 0.08, 10.0);
                formsPlot1.Plot.Axes.SetLimitsY(yMin - padding, yMax + padding);
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

        /// <summary>
        /// 数据计算层：高低点 Pivot 计算、趋势线筛选算法与策略评测 (彻底与 UI 刷新解耦，在 AddNewDataTimer 数据 Tick 中完成)
        /// </summary>
        private void PerformTrendlineAnalysisAndEvaluation()
        {
            if (Streamer1.Data.CountTotal < 100) return;

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
            //PivotHelper.CalculatePeaksFast(
            //    _highsCache.AsSpan(0, length),
            //    _lowsCache.AsSpan(0, length),
            //    _peaksBuffer,
            //    _valleysBuffer,
            //    leftLen: 5,
            //    rightLen: 5
            //);

            var peaksBuffer = new List<PeakValleyResult>();
            var valleysBuffer = new List<PeakValleyResult>();

            PivotHelper.CalculatePeaksCombinedFast(
                _highsCache.AsSpan(0, length),
                _lowsCache.AsSpan(0, length),
                peaksBuffer,
                valleysBuffer,reversalBars:3, fractalArm:3
            );

            _peaksBuffer = peaksBuffer.Where(x=>x.IsFractalConfirmed).Select(x => x.Index).ToList();
            _valleysBuffer = valleysBuffer.Where(x => x.IsFractalConfirmed).Select(x => x.Index).ToList();

            // 2. 收集与计算高低点趋势线及策略评测
            ComputeAngleTrendLinesData(_peaksBuffer, _valleysBuffer, streamer1Data, nextIndex, length);

            // 3. 核心改进：计算与 Overlay 图层生成合并在同一次数据 Tick 中原子完成，彻底消除跨帧坐标偏移！
            RenderTrendlineOverlays();
        }

        /// <summary>
        /// 数据层算法：收集趋势线候选集、破位校验、碰撞加权、夹角保留与策略评测
        /// </summary>
        private void ComputeAngleTrendLinesData(List<int> peakIndices, List<int> valleyIndices, double[] rawData, int nextIndex, int length)
        {
            if (peakIndices == null || valleyIndices == null) return;

            var peakLines = new List<AngleTrendLineInfo>();
            var valleyLines = new List<AngleTrendLineInfo>();

            // 1. 每根 K 线均与前面的各个历史高点 (Peak) 进行连接匹配
            for (int i = 1; i < length; i++)
            {
                double x2 = i;
                double y2 = rawData[(nextIndex + i) % length];

                foreach (int p in peakIndices)
                {
                    if (p >= i) break; // 仅与前面的高点连接
                    double x1 = p;
                    double y1 = rawData[(nextIndex + p) % length];

                    if (Math.Abs(x2 - x1) < 2) continue;
                    double k = (y2 - y1) / (x2 - x1);
                    double normK = Math.Abs(k) / Math.Max(Math.Abs(y1), 1.0);

                    if (normK > 0.05) continue;
                    //if (k >= 0) continue; // 红色下压阻力线：角度/斜率为负 (k < 0)

                    peakLines.Add(new AngleTrendLineInfo
                    {
                        Pivot1Index = p,
                        Pivot2Index = i,
                        X1 = x1,
                        Y1 = y1,
                        X2 = x2,
                        Y2 = y2,
                        K = k,
                        NormK = normK,
                        IsPeak = true,
                        Line_Age = Math.Abs((length - 1) - x2)
                    });
                }
            }

            // 2. 每根 K 线均与前面的各个历史低点 (Valley) 进行连接匹配 (消除 k <= 0 过度筛选，提升绿色支撑线条数)
            for (int i = 1; i < length; i++)
            {
                double x2 = i;
                double y2 = rawData[(nextIndex + i) % length];

                foreach (int v in valleyIndices)
                {
                    if (v >= i) break; // 仅与前面的低点连接
                    double x1 = v;
                    double y1 = rawData[(nextIndex + v) % length];

                    if (Math.Abs(x2 - x1) < 2) continue;
                    double k = (y2 - y1) / (x2 - x1);
                    double normK = Math.Abs(k) / Math.Max(Math.Abs(y1), 1.0);

                    if (normK > 0.05) continue;

                    valleyLines.Add(new AngleTrendLineInfo
                    {
                        Pivot1Index = v,
                        Pivot2Index = i,
                        X1 = x1,
                        Y1 = y1,
                        X2 = x2,
                        Y2 = y2,
                        K = k,
                        NormK = normK,
                        IsPeak = false,
                        Line_Age = Math.Abs((length - 1) - x2)
                    });
                }
            }

            var allCandidates = peakLines.Concat(valleyLines).ToList();
            var allPivots = peakIndices.Select(p => (X: (double)p, Y: rawData[(nextIndex + p) % length]))
                .Concat(valleyIndices.Select(v => (X: (double)v, Y: rawData[(nextIndex + v) % length]))).ToList();

            // 3. 执行【穿透破位校验】：从 X1 锚点起，任何 K 线价格穿透趋势线均直接彻底销毁删除
            foreach (var line in allCandidates)
            {
                int startX = (int)Math.Max(0, line.X1);
                bool brokeOut = false;

                for (int x = startX + 1; x < length; x++)
                {
                    double price = rawData[(nextIndex + x) % length];
                    double lineY = line.GetY(x);

                    // 校验锚点之间或延伸过程中是否被 K 线实体/影线穿过
                    if (line.IsPeak && price > lineY)
                    {
                        line.IsBroken = true;
                        line.Keep = false;
                        line.Line_Extension_Range = Math.Max(0, x - line.X2);
                        brokeOut = true;
                        break;
                    }
                    else if (!line.IsPeak && price < lineY)
                    {
                        line.IsBroken = true;
                        line.Keep = false;
                        line.Line_Extension_Range = Math.Max(0, x - line.X2);
                        brokeOut = true;
                        break;
                    }
                }

                if (!brokeOut)
                {
                    line.IsBroken = false;
                    line.Line_Extension_Range = (length - 1) - line.X2;
                }

                // 一旦被穿过，立即彻底丢弃不予保留
                if (line.IsBroken)
                {
                    line.Keep = false;
                    continue;
                }

                // 统计 0.25% 允许波动偏差内的附加触碰点位 (0.0025)
                foreach (var pivot in allPivots)
                {
                    if (Math.Abs(pivot.X - line.X1) < 1e-3 || Math.Abs(pivot.X - line.X2) < 1e-3) continue;

                    double expectedY = line.GetY(pivot.X);
                    double relDiff = Math.Abs(pivot.Y - expectedY) / Math.Max(Math.Abs(pivot.Y), 1.0);

                    // 允许 0.25% 的上下波动偏差
                    if (relDiff <= 0.0025)
                    {
                        line.TouchCount++;
                    }
                }

                // 只要由 2 个或以上极值点构成且未破位即保留该趋势线
                if (line.TouchCount >= 2)
                {
                    line.Keep = true;
                }
            }

            // 彻底从集合中物理删除销毁被穿越/破位 (IsBroken || !Keep) 的趋势线
            peakLines.RemoveAll(d => d.IsBroken || !d.Keep);
            valleyLines.RemoveAll(u => u.IsBroken || !u.Keep);
            allCandidates.RemoveAll(c => c.IsBroken || !c.Keep);

            // 4. 有效趋势线统计与最新标识
            var validPeakLines = peakLines.Where(d => !d.IsBroken && d.Keep).ToList();
            var validValleyLines = valleyLines.Where(u => u.IsBroken && u.Keep).ToList();

            if (validPeakLines.Any())
            {
                validPeakLines.Last().IsLatest = true;
            }
            if (validValleyLines.Any())
            {
                validValleyLines.Last().IsLatest = true;
            }

            // 更新预计算出的有效线条缓存
            _cachedLinesToDraw.Clear();
            _cachedLinesToDraw.AddRange(allCandidates.Where(c => c.Keep && !c.IsBroken));

            // 5. 策略评测：精准计算【做空：红色正斜率 (k > 0) -> 绿色负斜率 (k < 0) 附近多条聚类支撑】与【做多】期望利润
            double currentPrice = rawData[(nextIndex + length - 1) % length];
            int latestPeakX = _peaksBuffer.Count > 0 ? _peaksBuffer[_peaksBuffer.Count - 1] : -1;
            int latestValleyX = _valleysBuffer.Count > 0 ? _valleysBuffer[_valleysBuffer.Count - 1] : -1;

            // 统计触碰/试探 红色趋势线 (Peak 高点线) 的去重真正线条数量
            int activeRedLinesCount = validPeakLines.Count(d => d.Keep && !d.IsBroken &&
                (Math.Abs(currentPrice - d.GetY(length - 1)) / Math.Max(currentPrice, 1.0) <= 0.0025 ||
                 (latestPeakX >= 0 && (d.Pivot1Index == latestPeakX || d.Pivot2Index == latestPeakX))));

            // 统计触碰/试探 绿色趋势线 (Valley 低点线) 的去重真正线条数量
            int activeGreenLinesCount = validValleyLines.Count(u => u.Keep && !u.IsBroken &&
                (Math.Abs(currentPrice - u.GetY(length - 1)) / Math.Max(currentPrice, 1.0) <= 0.0025 ||
                 (latestValleyX >= 0 && (u.Pivot1Index == latestValleyX || u.Pivot2Index == latestValleyX))));

            _cachedActiveRedCount = activeRedLinesCount;
            _cachedActiveGreenCount = activeGreenLinesCount;

            // --- 做空 SHORT 期望利润计算：红色正斜率 (k > 0) 延伸至绿色负斜率 (k < 0) 多条聚类支撑线 ---
            double shortChannelSpreadPct = 0;
            var activeRedPosLines = validPeakLines.Where(d => d.Keep && !d.IsBroken && d.K > 0).ToList();
            var greenNegLines = validValleyLines.Where(u => u.Keep && !u.IsBroken && u.K < 0).ToList();

            if (activeRedPosLines.Any() && greenNegLines.Any())
            {
                double entryY = activeRedPosLines.Max(d => d.GetY(length - 1));
                // 筛选位于开仓点下方的绿色负斜率趋势线
                var targetGreenLines = greenNegLines.Where(u => u.GetY(length - 1) < entryY).ToList();
                if (targetGreenLines.Any())
                {
                    // 若存在多条绿色趋势线，进行 0.5% 价格域聚类，并按综合权重 (CompositeWeight) 挑选最具代表性的目标支撑线
                    var topWeightedGreen = targetGreenLines.OrderByDescending(u => u.CompositeWeight).First();
                    double destY = topWeightedGreen.GetY(length - 1);
                    shortChannelSpreadPct = Math.Max(0, (entryY - destY) / Math.Max(currentPrice, 1.0) * 100.0);
                }
            }
            if (shortChannelSpreadPct <= 0)
            {
                double defaultPeakY = validPeakLines.Any() ? validPeakLines.Last().GetY(length - 1) : currentPrice * 1.03;
                double defaultValleyY = validValleyLines.Any() ? validValleyLines.Last().GetY(length - 1) : currentPrice * 0.97;
                shortChannelSpreadPct = Math.Abs(defaultPeakY - defaultValleyY) / Math.Max(currentPrice, 1.0) * 100.0;
            }

            // --- 做多 LONG 期望利润计算：绿色负斜率 (k < 0) 延伸至红色正斜率 (k > 0) 多条聚类阻力线 ---
            double longChannelSpreadPct = 0;
            var activeGreenNegLines = validValleyLines.Where(u => u.Keep && !u.IsBroken && u.K < 0).ToList();
            var redPosLines = validPeakLines.Where(d => d.Keep && !d.IsBroken && d.K > 0).ToList();

            if (activeGreenNegLines.Any() && redPosLines.Any())
            {
                double entryY = activeGreenNegLines.Min(u => u.GetY(length - 1));
                var targetRedLines = redPosLines.Where(d => d.GetY(length - 1) > entryY).ToList();
                if (targetRedLines.Any())
                {
                    var topWeightedRed = targetRedLines.OrderByDescending(d => d.CompositeWeight).First();
                    double destY = topWeightedRed.GetY(length - 1);
                    longChannelSpreadPct = Math.Max(0, (destY - entryY) / Math.Max(currentPrice, 1.0) * 100.0);
                }
            }
            if (longChannelSpreadPct <= 0)
            {
                longChannelSpreadPct = shortChannelSpreadPct;
            }

            int totalKlinesCount = _historyKlines.Count;
            int latestKlineIndex = totalKlinesCount - 1;

            if (chkEnableStrategy != null && chkEnableStrategy.Checked)
            {
                _strategyEngine.Evaluate(latestKlineIndex, currentPrice, activeRedLinesCount, activeGreenLinesCount, shortChannelSpreadPct, longChannelSpreadPct);
            }
        }

        /// <summary>
        /// UI 渲染层：轻量级读取 pre-calculated 预计算数据并渲染 UI 图层 (在 UpdatePlotTimer 中快速执行)
        /// </summary>
        private void RenderTrendlineOverlays()
        {
            string currentSignature = $"{_peaksBuffer.Count}_{(_peaksBuffer.Count > 0 ? _peaksBuffer[^1] : 0)}_{_valleysBuffer.Count}_{(_valleysBuffer.Count > 0 ? _valleysBuffer[^1] : 0)}";

            if (currentSignature == _lastPivotSignature && !_forceUpdatePivotOverlays)
            {
                return;
            }

            _lastPivotSignature = currentSignature;
            _forceUpdatePivotOverlays = false;

            double[] streamer1Data = Streamer1.Data.Data;
            int length = streamer1Data.Length;
            int nextIndex = Streamer1.Data.NextIndex;

            foreach (var item in _currentOverlayPlottables)
            {
                formsPlot1.Plot.Remove(item);
            }
            _currentOverlayPlottables.Clear();

            // 1. 渲染高点 Peak 标记 (红色圆圈)
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

            // 2. 渲染低点 Valley 标记 (绿色方块)
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

            // 3. 渲染每新增 K 线数据连接绘制的趋势延伸线 (延伸至碰撞第一根 K 线处 X2 + Line_Extension_Range)
            foreach (var lineData in _cachedLinesToDraw)
            {
                double xLeft = lineData.X1;
                double yLeft = lineData.Y1;
                double xRight = lineData.X2 + lineData.Line_Extension_Range;
                double yRight = lineData.GetY(xRight);

                var line = formsPlot1.Plot.Add.Line(xLeft, yLeft, xRight, yRight);
                float lineWidth = 1.0f;

                byte alpha = lineData.TouchCount switch
                {
                    >= 4 => (byte)255,
                    3 => (byte)210,
                    _ => (byte)160
                };

                if (lineData.IsPeak)
                {
                    line.LineStyle.Color = ScottPlot.Colors.Red.WithAlpha(alpha / 255.0f);
                }
                else
                {
                    line.LineStyle.Color = ScottPlot.Colors.Green.WithAlpha(alpha / 255.0f);
                }

                line.LineStyle.Width = lineWidth;
                line.LineStyle.Pattern = LinePattern.Solid;
                _currentOverlayPlottables.Add(line);
            }



            // 4. 渲染交易 Marker 标记与气泡文本
            double currentPrice = streamer1Data[(nextIndex + length - 1) % length];
            int totalKlinesCount = _historyKlines.Count;
            int latestKlineIndex = totalKlinesCount - 1;

            double yMinVal = double.MaxValue;
            double yMaxVal = double.MinValue;
            if (_historyKlines != null && _historyKlines.Count > 0)
            {
                int visCount = Math.Min(1000, _historyKlines.Count);
                int startIdx = _historyKlines.Count - visCount;
                for (int i = startIdx; i < _historyKlines.Count; i++)
                {
                    double closeVal = (double)_historyKlines[i].Close;
                    if (closeVal < yMinVal) yMinVal = closeVal;
                    if (closeVal > yMaxVal) yMaxVal = closeVal;
                }
            }
            if (yMinVal == double.MaxValue || yMaxVal == double.MinValue)
            {
                yMinVal = currentPrice - 100;
                yMaxVal = currentPrice + 100;
            }
            double priceRange = Math.Max(yMaxVal - yMinVal, 10.0);
            double verticalOffset = priceRange * 0.015;

            // 3.5 渲染大成交量异动标记 (当 K 线成交量超过当前视口均值 2.2 倍时，在 K 线上方标记 ⚡VOL 提示)
            if (_historyKlines != null && _historyKlines.Count >= 5)
            {
                int visCount = Math.Min(length, _historyKlines.Count);
                int startIdx = _historyKlines.Count - visCount;

                double sumVol = 0;
                for (int i = startIdx; i < _historyKlines.Count; i++)
                {
                    sumVol += (double)_historyKlines[i].Volume;
                }
                double avgVol = sumVol / Math.Max(1, visCount);
                double highVolThreshold = Math.Max(avgVol * 5, 10.0);

                for (int i = startIdx; i < _historyKlines.Count; i++)
                {
                    var kline = _historyKlines[i];
                    double vol = (double)kline.Volume;
                    if (vol >= highVolThreshold)
                    {
                        int barsAgo = _historyKlines.Count - 1 - i;
                        double x = (length - 1) - barsAgo;

                        if (x >= 0 && x < length)
                        {
                            double close = (double)kline.Close;
                            var spikeMarker = formsPlot1.Plot.Add.Marker(x, close);
                            spikeMarker.Shape = MarkerShape.FilledDiamond;
                            spikeMarker.Size = 9;
                            spikeMarker.Color = ScottPlot.Colors.OrangeRed;
                            _currentOverlayPlottables.Add(spikeMarker);

                            var spikeText = formsPlot1.Plot.Add.Text($"⚡{vol:N0}", x, close + verticalOffset * 2.2);
                            spikeText.LabelFontSize = 8.5f;
                            spikeText.LabelFontColor = ScottPlot.Colors.DarkRed;
                            spikeText.LabelBackgroundColor = ScottPlot.Colors.Yellow.WithAlpha(0.85f);
                            spikeText.LabelBorderColor = ScottPlot.Colors.OrangeRed;
                            spikeText.LabelBorderWidth = 1f;
                            spikeText.LabelAlignment = ScottPlot.Alignment.LowerCenter;
                            _currentOverlayPlottables.Add(spikeText);
                        }
                    }
                }
            }

            foreach (var trade in _strategyEngine.CompletedTrades)
            {
                int entryBarsAgo = latestKlineIndex - trade.EntryKlineIndex;
                double entryX = (length - 1) - entryBarsAgo;

                if (entryX >= 0 && entryX < length)
                {
                    var openMarker = formsPlot1.Plot.Add.Marker(entryX, trade.EntryPrice);
                    openMarker.Size = 10;
                    if (trade.Type == StrategyPositionType.Long)
                    {
                        openMarker.Shape = MarkerShape.FilledTriangleUp;
                        openMarker.Color = ScottPlot.Colors.Green;
                        double textY = trade.EntryPrice - verticalOffset * 1.5;
                        var openText = formsPlot1.Plot.Add.Text($"[BUY LONG] Open @ {trade.EntryPrice:F1}", entryX, textY);
                        openText.LabelFontSize = 9;
                        openText.LabelFontColor = ScottPlot.Colors.DarkGreen;
                        openText.LabelBackgroundColor = ScottPlot.Colors.White.WithAlpha(0.9);
                        openText.LabelBorderColor = ScottPlot.Colors.DarkGreen;
                        openText.LabelBorderWidth = 1f;
                        openText.LabelAlignment = ScottPlot.Alignment.UpperCenter;
                        _currentOverlayPlottables.Add(openText);
                    }
                    else
                    {
                        openMarker.Shape = MarkerShape.FilledTriangleDown;
                        openMarker.Color = ScottPlot.Colors.Red;
                        double textY = trade.EntryPrice + verticalOffset * 1.5;
                        var openText = formsPlot1.Plot.Add.Text($"[SELL SHORT] Open @ {trade.EntryPrice:F1}", entryX, textY);
                        openText.LabelFontSize = 9;
                        openText.LabelFontColor = ScottPlot.Colors.DarkRed;
                        openText.LabelBackgroundColor = ScottPlot.Colors.White.WithAlpha(0.9);
                        openText.LabelBorderColor = ScottPlot.Colors.DarkRed;
                        openText.LabelBorderWidth = 1f;
                        openText.LabelAlignment = ScottPlot.Alignment.LowerCenter;
                        _currentOverlayPlottables.Add(openText);
                    }
                    _currentOverlayPlottables.Add(openMarker);
                }

                int exitBarsAgo = latestKlineIndex - trade.ExitKlineIndex;
                double exitX = (length - 1) - exitBarsAgo;

                if (exitX >= 0 && exitX < length)
                {
                    var exitMarker = formsPlot1.Plot.Add.Marker(exitX, trade.ExitPrice);
                    exitMarker.Size = 10;
                    if (trade.IsProfit)
                    {
                        exitMarker.Shape = MarkerShape.FilledDiamond;
                        exitMarker.Color = ScottPlot.Colors.Gold;
                        double textY = trade.ExitPrice + verticalOffset * 2.8;
                        var exitText = formsPlot1.Plot.Add.Text($"{trade.ExitReason} @ {trade.ExitPrice:F1}", exitX, textY);
                        exitText.LabelFontSize = 9;
                        exitText.LabelFontColor = ScottPlot.Colors.DarkGoldenRod;
                        exitText.LabelBackgroundColor = ScottPlot.Colors.White.WithAlpha(0.9);
                        exitText.LabelBorderColor = ScottPlot.Colors.Gold;
                        exitText.LabelBorderWidth = 1f;
                        exitText.LabelAlignment = ScottPlot.Alignment.LowerCenter;
                        _currentOverlayPlottables.Add(exitText);
                    }
                    else
                    {
                        exitMarker.Shape = MarkerShape.Cross;
                        exitMarker.Color = ScottPlot.Colors.Purple;
                        double textY = trade.ExitPrice - verticalOffset * 2.8;
                        var exitText = formsPlot1.Plot.Add.Text($"{trade.ExitReason} @ {trade.ExitPrice:F1}", exitX, textY);
                        exitText.LabelFontSize = 9;
                        exitText.LabelFontColor = ScottPlot.Colors.Purple;
                        exitText.LabelBackgroundColor = ScottPlot.Colors.White.WithAlpha(0.9);
                        exitText.LabelBorderColor = ScottPlot.Colors.Purple;
                        exitText.LabelBorderWidth = 1f;
                        exitText.LabelAlignment = ScottPlot.Alignment.UpperCenter;
                        _currentOverlayPlottables.Add(exitText);
                    }
                    _currentOverlayPlottables.Add(exitMarker);
                }
            }

            if (_strategyEngine.CurrentPosition != StrategyPositionType.None)
            {
                int activeBarsAgo = latestKlineIndex - _strategyEngine.EntryKlineIndex;
                double activeX = (length - 1) - activeBarsAgo;

                if (activeX >= 0 && activeX < length)
                {
                    var activeOpenMarker = formsPlot1.Plot.Add.Marker(activeX, _strategyEngine.EntryPrice);
                    activeOpenMarker.Size = 12;

                    if (_strategyEngine.CurrentPosition == StrategyPositionType.Long)
                    {
                        activeOpenMarker.Shape = MarkerShape.FilledTriangleUp;
                        activeOpenMarker.Color = ScottPlot.Colors.Green;
                        double textY = _strategyEngine.EntryPrice - verticalOffset * 1.5;
                        var activeText = formsPlot1.Plot.Add.Text($"[HOLD LONG] Entry @ {_strategyEngine.EntryPrice:F1}", activeX, textY);
                        activeText.LabelFontSize = 10;
                        activeText.LabelFontColor = ScottPlot.Colors.White;
                        activeText.LabelBackgroundColor = ScottPlot.Colors.Green;
                        activeText.LabelBorderColor = ScottPlot.Colors.DarkGreen;
                        activeText.LabelBorderWidth = 1f;
                        activeText.LabelAlignment = ScottPlot.Alignment.UpperCenter;
                        _currentOverlayPlottables.Add(activeText);
                    }
                    else
                    {
                        activeOpenMarker.Shape = MarkerShape.FilledTriangleDown;
                        activeOpenMarker.Color = ScottPlot.Colors.Red;
                        double textY = _strategyEngine.EntryPrice + verticalOffset * 1.5;
                        var activeText = formsPlot1.Plot.Add.Text($"[HOLD SHORT] Entry @ {_strategyEngine.EntryPrice:F1}", activeX, textY);
                        activeText.LabelFontSize = 10;
                        activeText.LabelFontColor = ScottPlot.Colors.White;
                        activeText.LabelBackgroundColor = ScottPlot.Colors.Red;
                        activeText.LabelBorderColor = ScottPlot.Colors.DarkRed;
                        activeText.LabelBorderWidth = 1f;
                        activeText.LabelAlignment = ScottPlot.Alignment.LowerCenter;
                        _currentOverlayPlottables.Add(activeText);
                    }
                    _currentOverlayPlottables.Add(activeOpenMarker);
                }

                var tpLine = formsPlot1.Plot.Add.Line(-5000, _strategyEngine.TakeProfitPrice, length + 5000, _strategyEngine.TakeProfitPrice);
                tpLine.LineStyle.Color = ScottPlot.Colors.Gold;
                tpLine.LineStyle.Pattern = LinePattern.Dashed;
                _currentOverlayPlottables.Add(tpLine);

                var slLine = formsPlot1.Plot.Add.Line(-5000, _strategyEngine.StopLossPrice, length + 5000, _strategyEngine.StopLossPrice);
                slLine.LineStyle.Color = ScottPlot.Colors.Purple;
                slLine.LineStyle.Pattern = LinePattern.Dashed;
                _currentOverlayPlottables.Add(slLine);
            }

            // 5. 统计策略状态面板与战绩 (胜率与交易统计始终显示)
            int totalTrades = _strategyEngine.CompletedTrades.Count;
            int winCount = _strategyEngine.CompletedTrades.Count(t => t.IsProfit);
            int lossCount = totalTrades - winCount;
            double winRate = totalTrades > 0 ? (double)winCount / totalTrades * 100 : 0;
            string winRateStats = $"Trades: {totalTrades} (Win: {winCount} Loss: {lossCount} WinRate: {winRate:F0}%)";

            if (chkEnableStrategy != null && !chkEnableStrategy.Checked)
            {
                lblTrendState.Text = $"[STRATEGY: DISABLED]\n(Strategy Auto-Trade Paused)\n{winRateStats}";
                lblTrendState.BackColor = Color.FromArgb(245, 245, 245);
                lblTrendState.ForeColor = Color.Gray;
            }
            else if (_strategyEngine.CurrentPosition == StrategyPositionType.Long)
            {
                double rr = _strategyEngine.StopLossPct > 0 ? _strategyEngine.TakeProfitPct / _strategyEngine.StopLossPct : 0;
                lblTrendState.Text = $"[STRATEGY: LONG] Entry: {_strategyEngine.EntryPrice:F1}\nTP (+{_strategyEngine.TakeProfitPct:F1}%): {_strategyEngine.TakeProfitPrice:F1} | SL (-{_strategyEngine.StopLossPct:F1}%): {_strategyEngine.StopLossPrice:F1} (R:R {rr:F1})\n{winRateStats}";
                lblTrendState.BackColor = Color.FromArgb(230, 255, 230);
                lblTrendState.ForeColor = Color.DarkGreen;
            }
            else if (_strategyEngine.CurrentPosition == StrategyPositionType.Short)
            {
                double rr = _strategyEngine.StopLossPct > 0 ? _strategyEngine.TakeProfitPct / _strategyEngine.StopLossPct : 0;
                lblTrendState.Text = $"[STRATEGY: SHORT] Entry: {_strategyEngine.EntryPrice:F1}\nTP (+{_strategyEngine.TakeProfitPct:F1}%): {_strategyEngine.TakeProfitPrice:F1} | SL (-{_strategyEngine.StopLossPct:F1}%): {_strategyEngine.StopLossPrice:F1} (R:R {rr:F1})\n{winRateStats}";
                lblTrendState.BackColor = Color.FromArgb(255, 230, 230);
                lblTrendState.ForeColor = Color.DarkRed;
            }
            else
            {
                lblTrendState.Text = $"[STRATEGY: MONITORING]\nRed Lines: {_cachedActiveRedCount} | Green Lines: {_cachedActiveGreenCount}\n{winRateStats}";
                lblTrendState.BackColor = Color.FromArgb(245, 245, 245);
                lblTrendState.ForeColor = Color.DimGray;
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

    #region AppConfig Settings Persistence
    public class AppConfig
    {
        public string Symbol { get; set; } = "BTCUSDT";
        public List<string> SavedSymbols { get; set; } = new() { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "DOGEUSDT", "XRPUSDT", "ADAUSDT" };
        public string Interval { get; set; } = "15m";
        public int TimeRangeIndex { get; set; } = 2;
        public DateTime StartTime { get; set; } = DateTime.Now.AddDays(-1);
        public DateTime EndTime { get; set; } = DateTime.Now;
        public int PlaySpeedIndex { get; set; } = 0;
        public decimal TakeProfitPct { get; set; } = 1.0m;
        public decimal StopLossPct { get; set; } = 1.0m;
        public decimal ExpectedProfitPct { get; set; } = 3.0m;
        public bool EnableStrategy { get; set; } = true;
        public bool AutoFitY { get; set; } = true;
        public bool TickReplay { get; set; } = false;
    }

    public static class AppConfigManager
    {
        private static readonly string ConfigPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_settings.json");

        public static AppConfig LoadConfig()
        {
            try
            {
                if (System.IO.File.Exists(ConfigPath))
                {
                    string json = System.IO.File.ReadAllText(ConfigPath);
                    var config = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json);
                    if (config != null) return config;
                }
            }
            catch { }
            return new AppConfig();
        }

        public static void SaveConfig(AppConfig config)
        {
            try
            {
                string json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }
    }
    #endregion

}