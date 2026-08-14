using Binance.Net.Enums;
using System;
using System.Data;
using System.Linq;
using System.Windows.Forms;

namespace WinFormsApp2
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
            InitControls();
            InitializePlot();
            AppendLog("系统初始化完成。准备就绪。");
        }

        private void InitControls()
        {
            // 初始化 K 线时间周期下拉框
            cmbKlineInterval.Items.Clear();
            cmbKlineInterval.Items.Add(new { Text = "1天 (OneDay)", Value = KlineInterval.OneDay });
            cmbKlineInterval.Items.Add(new { Text = "1小时 (OneHour)", Value = KlineInterval.OneHour });
            cmbKlineInterval.Items.Add(new { Text = "15分钟 (FifteenMinutes)", Value = KlineInterval.FifteenMinutes });
            cmbKlineInterval.Items.Add(new { Text = "1分钟 (OneMinute)", Value = KlineInterval.OneMinute });
            cmbKlineInterval.DisplayMember = "Text";
            cmbKlineInterval.ValueMember = "Value";
            cmbKlineInterval.SelectedIndex = 0; // 默认 1天

            // 初始化日期选择器（最小时间单位：天）
            dtpStartDate.Value = DateTime.Today.AddDays(-3);
            dtpEndDate.Value = DateTime.Today;
        }

        /// <summary>
        /// 初始化 ScottPlot 5 图表
        /// </summary>
        private void InitializePlot()
        {
            // 生成 50 个示例数据点
            double[] ys = ScottPlot.Generate.Sin(50);

            formsPlot1.Plot.Add.Signal(ys);
            formsPlot1.Plot.Title("实时行情 / 数据分析 (ScottPlot 5)");
            formsPlot1.Plot.XLabel("时间 (Tick)");
            formsPlot1.Plot.YLabel("数值 (Price / Value)");
            formsPlot1.Refresh();
        }

        /// <summary>
        /// 线程安全的日志输出方法
        /// </summary>
        /// <param name="message">日志文本内容</param>
        public void AppendLog(string message)
        {
            if (rtbLog.InvokeRequired)
            {
                rtbLog.Invoke(new Action(() => AppendLog(message)));
                return;
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            rtbLog.AppendText($"[{timestamp}] {message}{Environment.NewLine}");
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.ScrollToCaret();
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            string symbol = txtSymbol.Text.Trim();
            if (string.IsNullOrEmpty(symbol))
            {
                MessageBox.Show("请输入交易对名称 (如 BTCUSDT)", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            dynamic selectedIntervalObj = cmbKlineInterval.SelectedItem;
            KlineInterval interval = (KlineInterval)selectedIntervalObj.Value;

            DateTime startDate = dtpStartDate.Value.Date;
            DateTime endDate = dtpEndDate.Value.Date;

            AppendLog($"开始获取 [{symbol}] [{interval}] 自定义日期范围 [{startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}] 数据...");

            try
            {
                // 调用多线程下载帮助类并发抓取/加载 K线数据
                Kline[] klines = await MultiThreadDownloader.DownloadKlinesParallelAsync(
                    symbol,
                    interval,
                    startDate,
                    endDate,
                    maxDegreeOfParallelism: 4,
                    logger: AppendLog);

                // 调用片段化多线程下载帮助类并发抓取/加载 1-小时切片全量 Tick 逐笔/归集成交数据 (解决文件过大与1000条不完整问题)
                Tick[] ticks = await MultiThreadDownloader.DownloadTicksInSlicesParallelAsync(
                    symbol,
                    startDate,
                    endDate,
                    chunkHours: 1,
                    maxDegreeOfParallelism: 4,
                    logger: AppendLog);

                if (ticks.Length > 0)
                {
                    var lastTick = ticks.Last();
                    AppendLog($"[Tick 逐笔全量统计] {symbol} 共加载/翻页下载 {ticks.Length} 条 Tick 逐笔成交数据。最新成交时间: {lastTick.Time:yyyy-MM-dd HH:mm:ss.fff}, 最新成交价: {lastTick.LastPrice}, 最新成交量: {lastTick.Volume}");
                }

                // 将收盘价数据渲染到 ScottPlot 图表中
                if (klines.Length > 0)
                {
                    double[] prices = klines.Select(k => (double)k.ClosePrice).ToArray();
                    formsPlot1.Plot.Clear();
                    formsPlot1.Plot.Add.Signal(prices);
                    formsPlot1.Plot.Title($"{symbol} ({interval}) [{startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}] 共 {klines.Length} 条数据");
                    formsPlot1.Refresh();
                }
                else
                {
                    AppendLog("未获取到指定时间范围内的 K线数据。");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"数据处理异常: {ex.Message}");
            }
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            AppendLog("已接收停止请求，任务终止。");
        }

        private void btnClearLog_Click(object sender, EventArgs e)
        {
            rtbLog.Clear();
            AppendLog("日志已清空。");
        }
    }
}
