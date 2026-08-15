using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
#if WINDOWS
using System.Windows.Forms;
#endif

namespace WinFormsApp2
{
    internal static class Program
    {
        /// <summary>
        /// 应用程序主入口：自动检测平台与参数，Windows 上运行 GUI 界面，Linux 上运行 64-bit Headless 多币种交易引擎
        /// </summary>
        [STAThread]
        static async Task Main(string[] args)
        {
#if WINDOWS
            if (args.Length == 0 || !args[0].Equals("--console", StringComparison.OrdinalIgnoreCase))
            {
                ApplicationConfiguration.Initialize();
                Application.Run(new Form1());
                return;
            }
#endif

            // 读取本地 settings.conf 配置文件
            var settings = UserSettings.Load();

            // Linux 64-bit 无界面控制台 / 后台服务模式 (Headless Server Mode)
            Console.WriteLine("=================================================================");
            Console.WriteLine("  🚀 币安交易助手 Pro (Linux 64-bit Multi-Symbol Trading Server) ");
            Console.WriteLine("=================================================================");
            Console.WriteLine($"📄 已成功读取配置文件 [{UserSettings.SettingsConfPath}]");
            Console.WriteLine($"⚙ 交易模式: {(settings.IsLiveTrading ? "🟢 币安真实实盘下单 (Live)" : "🟡 本地模拟/回测挂单 (Simulated)")} | 杠杆: {settings.Leverage}x | 单笔资金: {settings.OrderQuantityUsdt} USDT");

            var engine = new TradingServerEngine();
            engine.ConfigureOrderEngine(settings.IsLiveTrading, settings.ApiKey, settings.ApiSecret, settings.Leverage, settings.OrderQuantityUsdt);

            engine.OnLog += msg => Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}");
            engine.OnTradeOpened += trade =>
            {
                string posStr = trade.Position == PositionType.Long ? "BUY LONG" : "SELL SHORT";
                Console.WriteLine($"🟢 [策略开仓信号] #{trade.Id} [{posStr}] @ {trade.EntryPrice} ({trade.EntryTime:yyyy-MM-dd HH:mm:ss})");
            };
            engine.OnTradeClosed += trade =>
            {
                string reasonStr = trade.ExitReason == TradeExitReason.TakeProfit ? "TAKE PROFIT" : "STOP LOSS";
                Console.WriteLine($"🔴 [策略平仓信号] #{trade.Id} [{reasonStr}] 收益: {trade.ProfitPct:+0.00;-0.00;0.00}% @ {trade.ExitPrice}");
            };

            List<string> symbolList = new List<string>();
            if (args.Length > 0)
            {
                foreach (var arg in args)
                {
                    if (arg.StartsWith("-")) continue;
                    var parts = arg.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    symbolList.AddRange(parts);
                }
            }

            if (symbolList.Count == 0)
            {
                symbolList = settings.SubscribedSymbols ?? new List<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT" };
            }

            string symbolsStr = string.Join(", ", symbolList);
            Console.WriteLine($"▶ 正在在线连接币安 WebSocket 实盘行情接口并发盯盘 [{symbolList.Count}] 个币种: [{symbolsStr}] [{UserSettings.FormatKlineInterval(settings.KlineInterval)}]...");

            try
            {
                await engine.StartMultiLiveStreamAsync(symbolList, settings.KlineInterval);

                Console.WriteLine("⚡ 多币种实盘盯盘与消费下单队列已常驻运行。按 Ctrl+C 安全退出...");
                var tcs = new TaskCompletionSource<bool>();
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    engine.Stop();
                    tcs.TrySetResult(true);
                };
                await tcs.Task;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 运行异常: {ex.Message}");
            }
        }
    }
}