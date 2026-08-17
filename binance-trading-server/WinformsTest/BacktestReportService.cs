using Binance.Net.Enums;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WinFormsApp2
{
    /// <summary>
    /// 回测绩效计算与标准化 HTML 报告生成服务引擎
    /// </summary>
    public static class BacktestReportService
    {
        public class BacktestContext
        {
            public string Symbol { get; set; } = "BTCUSDT";
            public KlineInterval KlineInterval { get; set; } = KlineInterval.OneMinute;
            public DateTime StartDate { get; set; }
            public DateTime EndDate { get; set; }
            public StrategyParameters StrategyParams { get; set; } = new StrategyParameters();
            public bool EnableTickPush { get; set; } = true;
            public bool EnableWarmup { get; set; } = true;
            public List<TradeRecord> Trades { get; set; } = new List<TradeRecord>();
            public decimal InitialCapital { get; set; } = 10000m; // 默认初始基准资金 (USDT)
        }

        public class BacktestMetrics
        {
            public int TotalTrades { get; set; }
            public int LongTrades { get; set; }
            public int ShortTrades { get; set; }
            public int WinTrades { get; set; }
            public int LossTrades { get; set; }
            public decimal WinRatePct { get; set; }
            public decimal TotalProfitPct { get; set; }
            public decimal CompoundReturnPct { get; set; }
            public decimal FinalCapital { get; set; }
            public decimal MaxDrawdownPct { get; set; }
            public decimal MaxDrawdownAmount { get; set; }
            public decimal ProfitFactor { get; set; }
            public decimal AvgProfitPct { get; set; }
            public decimal AvgWinPct { get; set; }
            public decimal AvgLossPct { get; set; }
            public decimal WinLossRatio { get; set; }
            public int MaxConsecutiveWins { get; set; }
            public int MaxConsecutiveLosses { get; set; }
            public decimal LargestWinPct { get; set; }
            public decimal LargestLossPct { get; set; }
            public TimeSpan AvgHoldingDuration { get; set; }
            public int TakeProfitCount { get; set; }
            public int StopLossCount { get; set; }
        }

        public class EquityPoint
        {
            public int TradeId { get; set; }
            public string TimeStr { get; set; } = "";
            public decimal TradeProfitPct { get; set; }
            public decimal CumProfitPct { get; set; }
            public decimal CompoundEquity { get; set; }
            public decimal PeakEquity { get; set; }
            public decimal DrawdownPct { get; set; }
        }

        /// <summary>
        /// 计算完整的量化风控与交易绩效指标
        /// </summary>
        public static (BacktestMetrics Metrics, List<EquityPoint> EquityCurve) CalculateMetrics(BacktestContext ctx)
        {
            var m = new BacktestMetrics();
            var curve = new List<EquityPoint>();

            var trades = ctx.Trades ?? new List<TradeRecord>();
            m.TotalTrades = trades.Count;

            // 初始资金曲线基准点
            decimal initialCap = ctx.InitialCapital > 0 ? ctx.InitialCapital : 10000m;
            decimal currentEquity = initialCap;
            decimal peakEquity = initialCap;
            decimal maxDrawdownPct = 0m;
            decimal maxDrawdownAmount = 0m;

            curve.Add(new EquityPoint
            {
                TradeId = 0,
                TimeStr = ctx.StartDate.ToString("yyyy-MM-dd HH:mm"),
                TradeProfitPct = 0m,
                CumProfitPct = 0m,
                CompoundEquity = currentEquity,
                PeakEquity = peakEquity,
                DrawdownPct = 0m
            });

            if (trades.Count == 0)
            {
                m.FinalCapital = currentEquity;
                return (m, curve);
            }

            decimal totalCumPct = 0m;
            decimal grossProfitPct = 0m;
            decimal grossLossPct = 0m;
            int curConsecWins = 0;
            int maxConsecWins = 0;
            int curConsecLoss = 0;
            int maxConsecLoss = 0;
            TimeSpan totalHoldTime = TimeSpan.Zero;
            decimal largestWin = 0m;
            decimal largestLoss = 0m;

            for (int i = 0; i < trades.Count; i++)
            {
                var t = trades[i];
                if (t.Position == PositionType.Long) m.LongTrades++;
                else if (t.Position == PositionType.Short) m.ShortTrades++;

                if (t.ExitReason == TradeExitReason.TakeProfit) m.TakeProfitCount++;
                else if (t.ExitReason == TradeExitReason.StopLoss) m.StopLossCount++;

                totalCumPct += t.ProfitPct;

                // 复利净值迭代
                currentEquity *= (1m + t.ProfitPct / 100m);
                if (currentEquity > peakEquity) peakEquity = currentEquity;

                decimal drawdownAmount = peakEquity - currentEquity;
                decimal drawdownPct = peakEquity > 0 ? (drawdownAmount / peakEquity) * 100m : 0m;

                if (drawdownPct > maxDrawdownPct) maxDrawdownPct = drawdownPct;
                if (drawdownAmount > maxDrawdownAmount) maxDrawdownAmount = drawdownAmount;

                // 统计盈亏
                if (t.ProfitPct > 0)
                {
                    m.WinTrades++;
                    grossProfitPct += t.ProfitPct;
                    curConsecWins++;
                    curConsecLoss = 0;
                    if (curConsecWins > maxConsecWins) maxConsecWins = curConsecWins;
                    if (t.ProfitPct > largestWin) largestWin = t.ProfitPct;
                }
                else if (t.ProfitPct < 0)
                {
                    m.LossTrades++;
                    grossLossPct += Math.Abs(t.ProfitPct);
                    curConsecLoss++;
                    curConsecWins = 0;
                    if (curConsecLoss > maxConsecLoss) maxConsecLoss = curConsecLoss;
                    if (t.ProfitPct < largestLoss) largestLoss = t.ProfitPct;
                }
                else
                {
                    curConsecWins = 0;
                    curConsecLoss = 0;
                }

                if (t.ExitTime > t.EntryTime)
                {
                    totalHoldTime += (t.ExitTime - t.EntryTime);
                }

                curve.Add(new EquityPoint
                {
                    TradeId = t.Id,
                    TimeStr = t.ExitTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    TradeProfitPct = t.ProfitPct,
                    CumProfitPct = totalCumPct,
                    CompoundEquity = Math.Round(currentEquity, 2),
                    PeakEquity = Math.Round(peakEquity, 2),
                    DrawdownPct = Math.Round(drawdownPct, 2)
                });
            }

            m.WinRatePct = m.TotalTrades > 0 ? (decimal)m.WinTrades / m.TotalTrades * 100m : 0m;
            m.TotalProfitPct = totalCumPct;
            m.CompoundReturnPct = initialCap > 0 ? ((currentEquity - initialCap) / initialCap) * 100m : 0m;
            m.FinalCapital = Math.Round(currentEquity, 2);
            m.MaxDrawdownPct = Math.Round(maxDrawdownPct, 2);
            m.MaxDrawdownAmount = Math.Round(maxDrawdownAmount, 2);

            m.ProfitFactor = grossLossPct > 0 ? Math.Round(grossProfitPct / grossLossPct, 2) : (grossProfitPct > 0 ? 99.99m : 0m);
            m.AvgProfitPct = m.TotalTrades > 0 ? Math.Round(totalCumPct / m.TotalTrades, 2) : 0m;
            m.AvgWinPct = m.WinTrades > 0 ? Math.Round(grossProfitPct / m.WinTrades, 2) : 0m;
            m.AvgLossPct = m.LossTrades > 0 ? Math.Round(grossLossPct / m.LossTrades, 2) : 0m;
            m.WinLossRatio = m.AvgLossPct > 0 ? Math.Round(m.AvgWinPct / m.AvgLossPct, 2) : (m.AvgWinPct > 0 ? 99.99m : 0m);

            m.MaxConsecutiveWins = maxConsecWins;
            m.MaxConsecutiveLosses = maxConsecLoss;
            m.LargestWinPct = largestWin;
            m.LargestLossPct = largestLoss;
            m.AvgHoldingDuration = m.TotalTrades > 0 ? TimeSpan.FromTicks(totalHoldTime.Ticks / m.TotalTrades) : TimeSpan.Zero;

            return (m, curve);
        }

        /// <summary>
        /// 生成 HTML 回测报告并保存至 config 目录下
        /// </summary>
        public static string GenerateAndSaveHtmlReport(BacktestContext ctx)
        {
            var (metrics, curve) = CalculateMetrics(ctx);
            string htmlContent = BuildHtml(ctx, metrics, curve);

            string configDir = Config.GetConfigPath();
            string reportsDir = Config.GetReportsPath();

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"Backtest_Report_{ctx.Symbol}_{ctx.KlineInterval}_{timestamp}.html";

            // 1. 统一写入 config 目录
            string configFilePath = Path.Combine(configDir, fileName);
            File.WriteAllText(configFilePath, htmlContent, Encoding.UTF8);

            // 2. 同时在 config/reports 目录下保存并更新 latest 快捷副本
            try
            {
                string reportsFilePath = Path.Combine(reportsDir, fileName);
                File.WriteAllText(reportsFilePath, htmlContent, Encoding.UTF8);

                string latestInConfig = Path.Combine(configDir, "Backtest_Report_Latest.html");
                File.WriteAllText(latestInConfig, htmlContent, Encoding.UTF8);

                string latestInReports = Path.Combine(reportsDir, "Backtest_Report_Latest.html");
                File.WriteAllText(latestInReports, htmlContent, Encoding.UTF8);
            }
            catch
            {
                // 忽略非关键辅助文件覆盖异常
            }

            return configFilePath;
        }

        /// <summary>
        /// 在系统默认浏览器中打开 HTML 报告
        /// </summary>
        public static void OpenReportInBrowser(string filePath)
        {
            if (!File.Exists(filePath)) return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"[回测报告] 无法自动调用浏览器打开报告: {ex.Message}");
            }
        }

        /// <summary>
        /// 构建美观自适应现代科技风 HTML 模板
        /// </summary>
        private static string BuildHtml(BacktestContext ctx, BacktestMetrics m, List<EquityPoint> curve)
        {
            var sb = new StringBuilder();
            var trades = ctx.Trades ?? new List<TradeRecord>();

            // 准备图表序列 JSON
            var chartLabels = curve.Select(p => p.TradeId == 0 ? "起点" : $"#{p.TradeId}").ToArray();
            var chartCumProfits = curve.Select(p => p.CumProfitPct).ToArray();
            var chartCompoundEquities = curve.Select(p => p.CompoundEquity).ToArray();
            var chartDrawdowns = curve.Select(p => -p.DrawdownPct).ToArray(); // 负值画水下回撤
            var chartTradeProfits = curve.Where(p => p.TradeId > 0).Select(p => p.TradeProfitPct).ToArray();
            var chartTradeIds = curve.Where(p => p.TradeId > 0).Select(p => $"#{p.TradeId}").ToArray();

            string jsonLabels = JsonSerializer.Serialize(chartLabels);
            string jsonCumProfits = JsonSerializer.Serialize(chartCumProfits);
            string jsonCompoundEquities = JsonSerializer.Serialize(chartCompoundEquities);
            string jsonDrawdowns = JsonSerializer.Serialize(chartDrawdowns);
            string jsonTradeProfits = JsonSerializer.Serialize(chartTradeProfits);
            string jsonTradeIds = JsonSerializer.Serialize(chartTradeIds);

            string profitColor = m.TotalProfitPct >= 0 ? "#10b981" : "#ef4444";
            string profitSign = m.TotalProfitPct >= 0 ? "+" : "";

            sb.Append($@"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>量化回测报告 - {ctx.Symbol} ({ctx.KlineInterval})</title>
    <!-- ECharts CDN & Fallback -->
    <script src=""https://cdn.jsdelivr.net/npm/echarts@5.5.0/dist/echarts.min.js""></script>
    <style>
        :root {{
            --bg-primary: #0f172a;
            --bg-card: #1e293b;
            --bg-card-hover: #273549;
            --border-color: #334155;
            --text-main: #f8fafc;
            --text-muted: #94a3b8;
            --color-primary: #38bdf8;
            --color-success: #10b981;
            --color-danger: #ef4444;
            --color-warning: #f59e0b;
            --color-long: #06b6d4;
            --color-short: #ec4899;
        }}

        * {{
            box-sizing: border-box;
            margin: 0;
            padding: 0;
        }}

        body {{
            font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, ""PingFang SC"", ""Microsoft YaHei"", sans-serif;
            background-color: var(--bg-primary);
            color: var(--text-main);
            line-height: 1.5;
            padding: 24px;
        }}

        .container {{
            max-width: 1400px;
            margin: 0 auto;
        }}

        /* Header */
        .header {{
            display: flex;
            justify-content: space-between;
            align-items: center;
            padding: 20px 24px;
            background: linear-gradient(135deg, #1e293b 0%, #0f172a 100%);
            border: 1px solid var(--border-color);
            border-radius: 12px;
            margin-bottom: 24px;
            box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.3);
        }}

        .header-title h1 {{
            font-size: 24px;
            font-weight: 700;
            color: #fff;
            display: flex;
            align-items: center;
            gap: 12px;
        }}

        .header-title p {{
            font-size: 13px;
            color: var(--text-muted);
            margin-top: 4px;
        }}

        .header-badges {{
            display: flex;
            gap: 10px;
            align-items: center;
            flex-wrap: wrap;
        }}

        .badge {{
            padding: 6px 12px;
            border-radius: 6px;
            font-size: 13px;
            font-weight: 600;
            border: 1px solid transparent;
        }}

        .badge-symbol {{
            background: rgba(56, 189, 248, 0.15);
            color: var(--color-primary);
            border-color: rgba(56, 189, 248, 0.3);
        }}

        .badge-interval {{
            background: rgba(245, 158, 11, 0.15);
            color: var(--color-warning);
            border-color: rgba(245, 158, 11, 0.3);
        }}

        .badge-time {{
            background: rgba(148, 163, 184, 0.15);
            color: var(--text-muted);
            border-color: rgba(148, 163, 184, 0.3);
        }}

        /* Grid Layout */
        .metrics-grid {{
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
            gap: 16px;
            margin-bottom: 24px;
        }}

        .metric-card {{
            background: var(--bg-card);
            border: 1px solid var(--border-color);
            border-radius: 10px;
            padding: 16px;
            transition: all 0.2s ease;
        }}

        .metric-card:hover {{
            background: var(--bg-card-hover);
            transform: translateY(-2px);
        }}

        .metric-label {{
            font-size: 13px;
            color: var(--text-muted);
            margin-bottom: 6px;
            display: flex;
            justify-content: space-between;
            align-items: center;
        }}

        .metric-value {{
            font-size: 24px;
            font-weight: 700;
            letter-spacing: -0.5px;
        }}

        .metric-subtext {{
            font-size: 12px;
            color: var(--text-muted);
            margin-top: 4px;
        }}

        .val-success {{ color: var(--color-success); }}
        .val-danger {{ color: var(--color-danger); }}
        .val-primary {{ color: var(--color-primary); }}
        .val-warning {{ color: var(--color-warning); }}

        /* Section Card */
        .section-card {{
            background: var(--bg-card);
            border: 1px solid var(--border-color);
            border-radius: 12px;
            padding: 20px;
            margin-bottom: 24px;
            box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.2);
        }}

        .section-header {{
            display: flex;
            justify-content: space-between;
            align-items: center;
            padding-bottom: 12px;
            margin-bottom: 16px;
            border-bottom: 1px solid var(--border-color);
        }}

        .section-title {{
            font-size: 16px;
            font-weight: 600;
            color: #fff;
            display: flex;
            align-items: center;
            gap: 8px;
        }}

        /* Parameters Table */
        .params-grid {{
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
            gap: 12px;
        }}

        .param-item {{
            display: flex;
            justify-content: space-between;
            align-items: center;
            background: rgba(15, 23, 42, 0.6);
            padding: 10px 14px;
            border-radius: 6px;
            border: 1px solid rgba(51, 65, 85, 0.6);
            font-size: 13px;
        }}

        .param-name {{
            color: var(--text-muted);
        }}

        .param-val {{
            font-weight: 600;
            color: var(--text-main);
        }}

        /* Chart Containers */
        .chart-box {{
            width: 100%;
            height: 380px;
            margin-bottom: 16px;
        }}

        .chart-sub-box {{
            width: 100%;
            height: 200px;
        }}

        /* Trades Table Toolbar & Filters */
        .table-toolbar {{
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 16px;
            flex-wrap: wrap;
            gap: 12px;
        }}

        .filter-buttons {{
            display: flex;
            gap: 8px;
        }}

        .filter-btn {{
            background: rgba(15, 23, 42, 0.8);
            border: 1px solid var(--border-color);
            color: var(--text-muted);
            padding: 6px 14px;
            border-radius: 6px;
            font-size: 13px;
            cursor: pointer;
            transition: all 0.2s ease;
        }}

        .filter-btn:hover, .filter-btn.active {{
            background: var(--color-primary);
            color: #0f172a;
            font-weight: 600;
            border-color: var(--color-primary);
        }}

        .search-input {{
            background: rgba(15, 23, 42, 0.8);
            border: 1px solid var(--border-color);
            color: var(--text-main);
            padding: 6px 14px;
            border-radius: 6px;
            font-size: 13px;
            min-width: 240px;
            outline: none;
        }}

        .search-input:focus {{
            border-color: var(--color-primary);
        }}

        /* Table */
        .table-wrapper {{
            overflow-x: auto;
            border-radius: 8px;
            border: 1px solid var(--border-color);
        }}

        table {{
            width: 100%;
            border-collapse: collapse;
            font-size: 13px;
            text-align: left;
        }}

        th {{
            background: #0f172a;
            color: var(--text-muted);
            font-weight: 600;
            padding: 12px 14px;
            border-bottom: 1px solid var(--border-color);
            white-space: nowrap;
        }}

        td {{
            padding: 10px 14px;
            border-bottom: 1px solid rgba(51, 65, 85, 0.4);
            white-space: nowrap;
        }}

        tr:hover td {{
            background: rgba(56, 189, 248, 0.05);
        }}

        .tag {{
            display: inline-block;
            padding: 3px 8px;
            border-radius: 4px;
            font-size: 12px;
            font-weight: 600;
        }}

        .tag-long {{ background: rgba(6, 182, 212, 0.15); color: var(--color-long); border: 1px solid rgba(6, 182, 212, 0.3); }}
        .tag-short {{ background: rgba(236, 72, 153, 0.15); color: var(--color-short); border: 1px solid rgba(236, 72, 153, 0.3); }}
        .tag-win {{ background: rgba(16, 185, 129, 0.15); color: var(--color-success); border: 1px solid rgba(16, 185, 129, 0.3); }}
        .tag-loss {{ background: rgba(239, 68, 68, 0.15); color: var(--color-danger); border: 1px solid rgba(239, 68, 68, 0.3); }}
        .tag-tp {{ background: rgba(16, 185, 129, 0.2); color: #34d399; }}
        .tag-sl {{ background: rgba(239, 68, 68, 0.2); color: #f87171; }}

        .btn-export {{
            background: rgba(16, 185, 129, 0.2);
            border: 1px solid rgba(16, 185, 129, 0.4);
            color: var(--color-success);
            padding: 6px 14px;
            border-radius: 6px;
            font-size: 13px;
            cursor: pointer;
            font-weight: 600;
            transition: all 0.2s;
        }}

        .btn-export:hover {{
            background: var(--color-success);
            color: #0f172a;
        }}

        /* Footer */
        .footer {{
            text-align: center;
            font-size: 12px;
            color: var(--text-muted);
            margin-top: 30px;
            padding-bottom: 20px;
        }}
    </style>
</head>
<body>
<div class=""container"">

    <!-- Header -->
    <div class=""header"">
        <div class=""header-title"">
            <h1>⚡ Binance 趋势线突破回调策略 - 回测报告</h1>
            <p>生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss} | 存储目录: {Config.GetConfigPath()}</p>
        </div>
        <div class=""header-badges"">
            <span class=""badge badge-symbol"">标的: {ctx.Symbol}</span>
            <span class=""badge badge-interval"">周期: {ctx.KlineInterval}</span>
            <span class=""badge badge-time"">回测区间: {ctx.StartDate:yyyy-MM-dd} ~ {ctx.EndDate:yyyy-MM-dd}</span>
        </div>
    </div>

    <!-- Key Metrics Grid -->
    <div class=""metrics-grid"">
        <div class=""metric-card"">
            <div class=""metric-label"">
                <span>累计收益率 (Total Return)</span>
                <span>📈</span>
            </div>
            <div class=""metric-value"" style=""color: {profitColor};"">{profitSign}{m.TotalProfitPct:F2}%</div>
            <div class=""metric-subtext"">复利收益: {m.CompoundReturnPct:+0.00;-0.00;0.00}%</div>
        </div>

        <div class=""metric-card"">
            <div class=""metric-label"">
                <span>最大回撤率 (Max Drawdown)</span>
                <span>⚠️</span>
            </div>
            <div class=""metric-value val-danger"">{m.MaxDrawdownPct:F2}%</div>
            <div class=""metric-subtext"">基准回撤额: -${m.MaxDrawdownAmount:F2}</div>
        </div>

        <div class=""metric-card"">
            <div class=""metric-label"">
                <span>策略胜率 (Win Rate)</span>
                <span>🎯</span>
            </div>
            <div class=""metric-value val-primary"">{m.WinRatePct:F1}%</div>
            <div class=""metric-subtext"">盈利: {m.WinTrades} 笔 | 亏损: {m.LossTrades} 笔</div>
        </div>

        <div class=""metric-card"">
            <div class=""metric-label"">
                <span>盈亏比 (Profit Factor)</span>
                <span>⚖️</span>
            </div>
            <div class=""metric-value val-warning"">{m.ProfitFactor:F2}</div>
            <div class=""metric-subtext"">均盈/均亏: {m.WinLossRatio:F2}</div>
        </div>

        <div class=""metric-card"">
            <div class=""metric-label"">
                <span>总交易笔数 (Total Trades)</span>
                <span>📊</span>
            </div>
            <div class=""metric-value"">{m.TotalTrades}</div>
            <div class=""metric-subtext"">多: {m.LongTrades} | 空: {m.ShortTrades}</div>
        </div>

        <div class=""metric-card"">
            <div class=""metric-label"">
                <span>单笔平均收益 (Avg PnL)</span>
                <span>💰</span>
            </div>
            <div class=""metric-value"" style=""color: {(m.AvgProfitPct >= 0 ? "var(--color-success)" : "var(--color-danger)")};"">{m.AvgProfitPct:+0.00;-0.00;0.00}%</div>
            <div class=""metric-subtext"">均持: {(int)m.AvgHoldingDuration.TotalMinutes} 分钟</div>
        </div>
    </div>

    <!-- Section 1: Parameters & Configuration -->
    <div class=""section-card"">
        <div class=""section-header"">
            <div class=""section-title"">⚙️ 回测时间周期与策略风控参数 (Parameters & Settings)</div>
        </div>
        <div class=""params-grid"">
            <div class=""param-item"">
                <span class=""param-name"">交易标的 (Symbol):</span>
                <span class=""param-val"">{ctx.Symbol}</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">K线周期 (Kline Interval):</span>
                <span class=""param-val"">{ctx.KlineInterval}</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">回测开始时间 (Start Date):</span>
                <span class=""param-val"">{ctx.StartDate:yyyy-MM-dd}</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">回测结束时间 (End Date):</span>
                <span class=""param-val"">{ctx.EndDate:yyyy-MM-dd}</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">策略状态 (Strategy Enabled):</span>
                <span class=""param-val"">{(ctx.StrategyParams.Enabled ? "🟢 开启" : "🔴 关闭")}</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">最小趋势线跨度 (line_x1_x2):</span>
                <span class=""param-val"">{ctx.StrategyParams.MinLineX1X2} 根K线</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">最小趋势线年龄 (open_age):</span>
                <span class=""param-val"">{ctx.StrategyParams.MinLineAge} 根K线</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">固定止盈比例 (Take Profit):</span>
                <span class=""param-val val-success"">+{ctx.StrategyParams.TakeProfitPct:F2}%</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">固定止损比例 (Stop Loss):</span>
                <span class=""param-val val-danger"">-{ctx.StrategyParams.StopLossPct:F2}%</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">开仓频率冷却 (Cooldown):</span>
                <span class=""param-val"">{(ctx.StrategyParams.EnableCooldown ? "✅ 开启 (同分钟/同K线限1次)" : "❌ 关闭")}</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">Tick 细粒度推送 (Tick Stream):</span>
                <span class=""param-val"">{(ctx.EnableTickPush ? "✅ 开启 (高精度)" : "❌ 关闭")}</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">API 预热 1000 根 (Warmup):</span>
                <span class=""param-val"">{(ctx.EnableWarmup ? "✅ 已启用" : "❌ 未启用")}</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">初始基准本金 (Initial Capital):</span>
                <span class=""param-val"">${ctx.InitialCapital:N0} USDT</span>
            </div>
        </div>
    </div>

    <!-- Section 2: Interactive Equity & Drawdown Charts -->
    <div class=""section-card"">
        <div class=""section-header"">
            <div class=""section-title"">📈 资金权益曲线 & 水下回撤图 (Equity & Drawdown Curve)</div>
        </div>
        <div id=""chartEquity"" class=""chart-box""></div>
        <div id=""chartDrawdown"" class=""chart-sub-box""></div>
    </div>

    <!-- Section 3: Detailed Risk & Performance Metrics -->
    <div class=""section-card"">
        <div class=""section-header"">
            <div class=""section-title"">📋 深度绩效与风控统计指标 (Detailed Statistics)</div>
        </div>
        <div class=""params-grid"">
            <div class=""param-item"">
                <span class=""param-name"">初始基准本金:</span>
                <span class=""param-val"">${ctx.InitialCapital:N2}</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">期末模拟净值:</span>
                <span class=""param-val"" style=""color: {profitColor};"">${m.FinalCapital:N2}</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">累计单利收益:</span>
                <span class=""param-val"" style=""color: {profitColor};"">{m.TotalProfitPct:+0.00;-0.00;0.00}%</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">复利净值收益:</span>
                <span class=""param-val"" style=""color: {profitColor};"">{m.CompoundReturnPct:+0.00;-0.00;0.00}%</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">最大回撤幅度 (MDD):</span>
                <span class=""param-val val-danger"">{m.MaxDrawdownPct:F2}%</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">盈亏比 (Profit Factor):</span>
                <span class=""param-val val-warning"">{m.ProfitFactor:F2}</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">平均单笔盈利:</span>
                <span class=""param-val val-success"">+{m.AvgWinPct:F2}%</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">平均单笔亏损:</span>
                <span class=""param-val val-danger"">-{m.AvgLossPct:F2}%</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">单笔最大盈利:</span>
                <span class=""param-val val-success"">+{m.LargestWinPct:F2}%</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">单笔最大亏损:</span>
                <span class=""param-val val-danger"">{m.LargestLossPct:F2}%</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">最大连续盈利次数:</span>
                <span class=""param-val val-success"">{m.MaxConsecutiveWins} 连胜</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">最大连续亏损次数:</span>
                <span class=""param-val val-danger"">{m.MaxConsecutiveLosses} 连亏</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">止盈平仓触发笔数:</span>
                <span class=""param-val val-success"">{m.TakeProfitCount} 笔</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">止损平仓触发笔数:</span>
                <span class=""param-val val-danger"">{m.StopLossCount} 笔</span>
            </div>
            <div class=""param-item"">
                <span class=""param-name"">平均持仓时长:</span>
                <span class=""param-val"">{(int)m.AvgHoldingDuration.TotalMinutes} 分 {m.AvgHoldingDuration.Seconds} 秒</span>
            </div>
        </div>
    </div>

    <!-- Section 4: Trade Records Table -->
    <div class=""section-card"">
        <div class=""section-header"">
            <div class=""section-title"">📑 订单明细记录 (Trade Records) - 共 {trades.Count} 笔交易</div>
            <button class=""btn-export"" onclick=""exportTableToCSV('Backtest_Trades_{ctx.Symbol}.csv')"">📥 导出订单 CSV</button>
        </div>

        <div class=""table-toolbar"">
            <div class=""filter-buttons"">
                <button class=""filter-btn active"" onclick=""filterTrades('all', this)"">全部 ({trades.Count})</button>
                <button class=""filter-btn"" onclick=""filterTrades('win', this)"">盈利单 ({m.WinTrades})</button>
                <button class=""filter-btn"" onclick=""filterTrades('loss', this)"">亏损单 ({m.LossTrades})</button>
                <button class=""filter-btn"" onclick=""filterTrades('long', this)"">做多单 ({m.LongTrades})</button>
                <button class=""filter-btn"" onclick=""filterTrades('short', this)"">做空单 ({m.ShortTrades})</button>
            </div>
            <input type=""text"" id=""searchInput"" class=""search-input"" placeholder=""🔍 搜索订单 (ID/时间/价格/原因)..."" onkeyup=""searchTrades()"">
        </div>

        <div class=""table-wrapper"">
            <table id=""tradesTable"">
                <thead>
                    <tr>
                        <th>订单#</th>
                        <th>方向</th>
                        <th>开仓时间</th>
                        <th>开仓价格</th>
                        <th>趋势线基准价</th>
                        <th>平仓时间</th>
                        <th>平仓价格</th>
                        <th>平仓原因</th>
                        <th>收益率 (%)</th>
                        <th>持仓时长</th>
                        <th>结果</th>
                    </tr>
                </thead>
                <tbody>");

            decimal runningCumProfit = 0m;
            for (int i = 0; i < trades.Count; i++)
            {
                var t = trades[i];
                runningCumProfit += t.ProfitPct;
                string posClass = t.Position == PositionType.Long ? "tag-long" : "tag-short";
                string posText = t.Position == PositionType.Long ? "BUY 多" : "SELL 空";
                string resultClass = t.IsWin ? "tag-win" : "tag-loss";
                string resultText = t.IsWin ? "盈利 WIN" : "亏损 LOSS";
                string reasonClass = t.ExitReason == TradeExitReason.TakeProfit ? "tag-tp" : "tag-sl";
                string reasonText = t.ExitReason == TradeExitReason.TakeProfit ? "止盈 TP" : "止损 SL";
                string profitColorClass = t.ProfitPct >= 0 ? "val-success" : "val-danger";

                TimeSpan dur = t.ExitTime > t.EntryTime ? (t.ExitTime - t.EntryTime) : TimeSpan.Zero;
                string durStr = dur.TotalHours >= 1 ? $"{(int)dur.TotalHours}h {dur.Minutes}m" : $"{(int)dur.TotalMinutes}m {dur.Seconds}s";

                string filterPos = t.Position == PositionType.Long ? "long" : "short";
                string filterWin = t.IsWin ? "win" : "loss";

                sb.Append($@"
                    <tr data-pos=""{filterPos}"" data-win=""{filterWin}"">
                        <td><strong>#{t.Id}</strong></td>
                        <td><span class=""tag {posClass}"">{posText}</span></td>
                        <td>{t.EntryTime:yyyy-MM-dd HH:mm:ss}</td>
                        <td>{t.EntryPrice:F2}</td>
                        <td>{t.EntryTrendLinePrice:F2}</td>
                        <td>{t.ExitTime:yyyy-MM-dd HH:mm:ss}</td>
                        <td>{t.ExitPrice:F2}</td>
                        <td><span class=""tag {reasonClass}"">{reasonText}</span></td>
                        <td class=""{profitColorClass}"" style=""font-weight:600;"">{t.ProfitPct:+0.00;-0.00;0.00}%</td>
                        <td>{durStr}</td>
                        <td><span class=""tag {resultClass}"">{resultText}</span></td>
                    </tr>");
            }

            sb.Append($@"
                </tbody>
            </table>
        </div>
    </div>

    <!-- Footer -->
    <div class=""footer"">
        <p>Trading Assistance Pro - 量化交易策略回测引擎 | 报告文件统一保存在: {Config.GetConfigPath()}</p>
    </div>

</div>

<!-- Scripts for ECharts & Interactive Filters -->
<script>
    // 1. 初始化 ECharts 资金曲线与水下回撤图
    const chartData = {{
        labels: {jsonLabels},
        cumProfits: {jsonCumProfits},
        compoundEquities: {jsonCompoundEquities},
        drawdowns: {jsonDrawdowns},
        tradeProfits: {jsonTradeProfits},
        tradeIds: {jsonTradeIds}
    }};

    function initCharts() {{
        if (typeof echarts === 'undefined') {{
            document.getElementById('chartEquity').innerHTML = '<div style=""color:#94a3b8;padding:40px;text-align:center;"">ECharts 离线加载中，请查看下表详细资金数据。</div>';
            return;
        }}

        // A. 资金权益曲线
        const equityChart = echarts.init(document.getElementById('chartEquity'));
        const equityOption = {{
            backgroundColor: 'transparent',
            tooltip: {{
                trigger: 'axis',
                backgroundColor: 'rgba(30, 41, 59, 0.95)',
                borderColor: '#334155',
                textStyle: {{ color: '#f8fafc' }},
                formatter: function(params) {{
                    let res = '<strong>交易点: ' + params[0].axisValue + '</strong><br/>';
                    params.forEach(item => {{
                        let valStr = item.seriesName.indexOf('收益率') >= 0 ? (item.data >= 0 ? '+' : '') + item.data.toFixed(2) + '%' : '$' + item.data.toLocaleString();
                        res += '<span style=""display:inline-block;margin-right:4px;border-radius:10px;width:10px;height:10px;background-color:' + item.color + ';""></span>' + item.seriesName + ': <strong>' + valStr + '</strong><br/>';
                    }});
                    return res;
                }}
            }},
            legend: {{
                data: ['累计收益率 (%)', '复利资金规模 (USDT)'],
                textStyle: {{ color: '#94a3b8' }},
                top: 0
            }},
            grid: {{
                left: '3%',
                right: '4%',
                bottom: '8%',
                top: '12%',
                containLabel: true
            }},
            xAxis: {{
                type: 'category',
                boundaryGap: false,
                data: chartData.labels,
                axisLine: {{ lineStyle: {{ color: '#334155' }} }},
                axisLabel: {{ color: '#94a3b8' }}
            }},
            yAxis: [
                {{
                    type: 'value',
                    name: '累计收益率 (%)',
                    position: 'left',
                    axisLine: {{ lineStyle: {{ color: '#38bdf8' }} }},
                    splitLine: {{ lineStyle: {{ color: 'rgba(51, 65, 85, 0.3)' }} }},
                    axisLabel: {{ color: '#94a3b8', formatter: '{{value}}%' }}
                }},
                {{
                    type: 'value',
                    name: '资金净值 (USDT)',
                    position: 'right',
                    axisLine: {{ lineStyle: {{ color: '#10b981' }} }},
                    splitLine: {{ show: false }},
                    axisLabel: {{ color: '#94a3b8', formatter: '${{value}}' }}
                }}
            ],
            series: [
                {{
                    name: '累计收益率 (%)',
                    type: 'line',
                    smooth: true,
                    showSymbol: chartData.labels.length < 50,
                    symbolSize: 6,
                    lineStyle: {{ width: 3, color: '#38bdf8' }},
                    itemStyle: {{ color: '#38bdf8' }},
                    areaStyle: {{
                        color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
                            {{ offset: 0, color: 'rgba(56, 189, 248, 0.35)' }},
                            {{ offset: 1, color: 'rgba(56, 189, 248, 0.0)' }}
                        ])
                    }},
                    data: chartData.cumProfits
                }},
                {{
                    name: '复利资金规模 (USDT)',
                    type: 'line',
                    yAxisIndex: 1,
                    smooth: true,
                    showSymbol: false,
                    lineStyle: {{ width: 2, color: '#10b981', type: 'dashed' }},
                    itemStyle: {{ color: '#10b981' }},
                    data: chartData.compoundEquities
                }}
            ]
        }};
        equityChart.setOption(equityOption);

        // B. 水下回撤图
        const ddChart = echarts.init(document.getElementById('chartDrawdown'));
        const ddOption = {{
            backgroundColor: 'transparent',
            tooltip: {{
                trigger: 'axis',
                backgroundColor: 'rgba(30, 41, 59, 0.95)',
                borderColor: '#334155',
                textStyle: {{ color: '#f8fafc' }},
                formatter: function(params) {{
                    return '<strong>交易点: ' + params[0].axisValue + '</strong><br/>' +
                           '当前回撤率: <strong style=""color:#ef4444;"">' + params[0].data.toFixed(2) + '%</strong>';
                }}
            }},
            grid: {{
                left: '3%',
                right: '4%',
                bottom: '10%',
                top: '15%',
                containLabel: true
            }},
            xAxis: {{
                type: 'category',
                boundaryGap: false,
                data: chartData.labels,
                axisLine: {{ lineStyle: {{ color: '#334155' }} }},
                axisLabel: {{ color: '#94a3b8' }}
            }},
            yAxis: {{
                type: 'value',
                name: '水下回撤 (Drawdown %)',
                max: 0,
                axisLine: {{ lineStyle: {{ color: '#ef4444' }} }},
                splitLine: {{ lineStyle: {{ color: 'rgba(51, 65, 85, 0.3)' }} }},
                axisLabel: {{ color: '#94a3b8', formatter: '{{value}}%' }}
            }},
            series: [
                {{
                    name: '回撤率',
                    type: 'line',
                    smooth: true,
                    showSymbol: false,
                    lineStyle: {{ width: 2, color: '#ef4444' }},
                    itemStyle: {{ color: '#ef4444' }},
                    areaStyle: {{
                        color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
                            {{ offset: 0, color: 'rgba(239, 68, 68, 0.1)' }},
                            {{ offset: 1, color: 'rgba(239, 68, 68, 0.45)' }}
                        ])
                    }},
                    data: chartData.drawdowns
                }}
            ]
        }};
        ddChart.setOption(ddOption);

        window.addEventListener('resize', () => {{
            equityChart.resize();
            ddChart.resize();
        }});
    }}

    document.addEventListener('DOMContentLoaded', initCharts);

    // 2. 订单表格筛选与搜索
    let currentFilter = 'all';

    function filterTrades(type, btn) {{
        currentFilter = type;
        document.querySelectorAll('.filter-btn').forEach(b => b.classList.remove('active'));
        if (btn) btn.classList.add('active');
        applyTableFilter();
    }}

    function searchTrades() {{
        applyTableFilter();
    }}

    function applyTableFilter() {{
        const search = document.getElementById('searchInput').value.toLowerCase();
        const rows = document.querySelectorAll('#tradesTable tbody tr');

        rows.forEach(row => {{
            const pos = row.getAttribute('data-pos');
            const win = row.getAttribute('data-win');
            const text = row.innerText.toLowerCase();

            let matchesFilter = true;
            if (currentFilter === 'win' && win !== 'win') matchesFilter = false;
            else if (currentFilter === 'loss' && win !== 'loss') matchesFilter = false;
            else if (currentFilter === 'long' && pos !== 'long') matchesFilter = false;
            else if (currentFilter === 'short' && pos !== 'short') matchesFilter = false;

            let matchesSearch = text.indexOf(search) >= 0;

            if (matchesFilter && matchesSearch) {{
                row.style.display = '';
            }} else {{
                row.style.display = 'none';
            }}
        }});
    }}

    // 3. 导出 CSV
    function exportTableToCSV(filename) {{
        const rows = document.querySelectorAll('#tradesTable tr');
        let csv = [];
        for (let i = 0; i < rows.length; i++) {{
            let row = [], cols = rows[i].querySelectorAll('td, th');
            for (let j = 0; j < cols.length; j++) {{
                let data = cols[j].innerText.replace(/(\r\n|\n|\r)/gm, '').replace(/,/g, ';');
                row.push('""' + data + '""');
            }}
            csv.push(row.join(','));
        }}
        const csvFile = new Blob([""\uFEFF"" + csv.join('\n')], {{ type: 'text/csv;charset=utf-8;' }});
        const downloadLink = document.createElement('a');
        downloadLink.download = filename;
        downloadLink.href = window.URL.createObjectURL(csvFile);
        downloadLink.style.display = 'none';
        document.body.appendChild(downloadLink);
        downloadLink.click();
        document.body.removeChild(downloadLink);
    }}
</script>
</body>
</html>");

            return sb.ToString();
        }
    }
}
