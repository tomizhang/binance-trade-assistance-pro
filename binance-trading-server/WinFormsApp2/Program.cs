using System;
using System.Threading.Tasks;
#if WINDOWS
using System.Windows.Forms;
#endif

namespace WinFormsApp2
{
    internal static class Program
    {
        /// <summary>
        /// 应用程序主入口：自动检测平台与参数，Windows 上运行 GUI 界面，Linux 上运行 64-bit Headless 交易引擎
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

            // Linux 64-bit 无界面控制台 / 后台服务模式 (Headless Server Mode)
            Console.WriteLine("=================================================================");
            Console.WriteLine("  🚀 币安交易助手 Pro (Linux 64-bit Headless Trading Server)     ");
            Console.WriteLine("=================================================================");

            var engine = new TradingServerEngine();
            engine.OnLog += msg => Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}");
            engine.OnTradeOpened += trade =>
            {
                string posStr = trade.Position == PositionType.Long ? "BUY LONG" : "SELL SHORT";
                Console.WriteLine($"🟢 [策略开仓] #{trade.Id} [{posStr}] @ {trade.EntryPrice} ({trade.EntryTime:yyyy-MM-dd HH:mm:ss})");
            };
            engine.OnTradeClosed += trade =>
            {
                string reasonStr = trade.ExitReason == TradeExitReason.TakeProfit ? "TAKE PROFIT" : "STOP LOSS";
                Console.WriteLine($"🔴 [策略平仓] #{trade.Id} [{reasonStr}] 收益: {trade.ProfitPct:+0.00;-0.00;0.00}% @ {trade.ExitPrice}");
            };

            string symbol = args.Length > 0 && !args[0].StartsWith("-") ? args[0].Trim().ToUpper() : "BTCUSDT";
            Console.WriteLine($"▶ 正在在线连接币安 WebSocket 实盘行情接口 [{symbol}] 启动盯盘策略推演...");

            try
            {
                await engine.StartLiveStreamAsync(symbol, Binance.Net.Enums.KlineInterval.OneMinute);

                Console.WriteLine("服务已启动。按下 Ctrl+C 安全退出...");
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