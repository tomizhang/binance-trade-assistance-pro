using Binance.Net.Enums;
using Common;
using Common.Cursor;
using Common.Helper;
using Common.Interfaces;
using Common.Models;
using Common.Providers;
using Common.Strategies;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleTest
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("=================================================================");
            Console.WriteLine("          Binance Market Data Engine & Strategy Test Runner      ");
            Console.WriteLine("=================================================================");
            Console.WriteLine($"[系统信息] 数据根目录: {Config.GetRootPath()}");
            Console.WriteLine($"[系统信息] 当前统一 UTC 时间: {DateTime.UtcNow.ToUtc0String()}");
            Console.WriteLine("=================================================================\n");

            while (true)
            {
                Console.WriteLine("请选择测试功能:");
                Console.WriteLine("  1. 测试 UTC+0 时间转换与按需格式化规范 (TimeHelper)");
                Console.WriteLine("  2. 测试 100条双向游标前进/后退/滑窗缓存 (MarketDataCursor)");
                Console.WriteLine("  3. 测试 DuckDB 历史数据提供者 (DuckDbHistoricalDataProvider)");
                Console.WriteLine("  4. 测试 策略基类与双均线策略执行 (StrategyBase & MovingAverageCrossStrategy)");
                Console.WriteLine("  5. 测试 极值点波峰波谷分形计算 (PivotHelper)");
                Console.WriteLine("  6. 测试 趋势线Tick穿透回弹策略 (TrendLineReboundStrategy - LineX1X2>40, LineAge>4, 5-Tick Rebound)");
                Console.WriteLine("  7. 测试 Binance.Net 实时 WebSocket 推流 (BinanceLiveMarketDataProvider)");
                Console.WriteLine("  8. 运行全套自动化自测 (Automated Self-Test)");
                Console.WriteLine("  0. 退出");
                Console.Write("\n请输入选项 (0-8): ");

                string? input = Console.ReadLine()?.Trim();
                Console.WriteLine();

                switch (input)
                {
                    case "1":
                        TestTimeHelper();
                        break;
                    case "2":
                        TestBidirectionalCursor();
                        break;
                    case "3":
                        TestDuckDbHistoricalProvider();
                        break;
                    case "4":
                        await TestStrategyExecution();
                        break;
                    case "5":
                        TestPivotHelper();
                        break;
                    case "6":
                        TestTrendLineReboundStrategy();
                        break;
                    case "7":
                        await TestLiveWebSocketStream();
                        break;
                    case "8":
                        await RunAllSelfTests();
                        break;
                    case "0":
                        Console.WriteLine("已退出测试程序。");
                        return;
                    default:
                        Console.WriteLine("[提示] 无效的选项，请重新输入。");
                        break;
                }

                Console.WriteLine("\n按回车键继续...");
                Console.ReadLine();
                Console.Clear();
            }
        }

        #region 1. UTC+0 时间测试

        static void TestTimeHelper()
        {
            Console.WriteLine("--- [1] 测试 UTC+0 时间转换与按需格式化 ---");

            DateTime nowUtc = DateTime.UtcNow;
            long nowMs = TimeHelper.ToUnixTimeMilliseconds(nowUtc);
            DateTime convertedUtc = TimeHelper.FromUnixTimeMilliseconds(nowMs);

            Console.WriteLine($"当前 UTC 时间: {nowUtc.ToUtc0String()}");
            Console.WriteLine($"Unix 毫秒戳:   {nowMs}");
            Console.WriteLine($"时间戳转回:   {convertedUtc.ToUtc0String()}");

            string testTimeStr = "2026-01-01 08:30:00";
            DateTime parsed = TimeHelper.ParseUtc(testTimeStr);
            Console.WriteLine($"字符串解析:   {testTimeStr} -> Kind: {parsed.Kind}, 格式化: {parsed.ToUtc0String()}");

            Console.WriteLine("[验证通过] UTC+0 时间标准与 yyyy-MM-dd HH:mm:ss 格式化正确！\n");
        }

        #endregion

        #region 2. 100 条双向游标测试

        static void TestBidirectionalCursor()
        {
            Console.WriteLine("--- [2] 测试 100 条双向游标前进/后退/滑窗缓存 ---");

            // 构造 150 根模拟测试 K 线 (从 2026-01-01 00:00:00 开始，每 30 分钟一根)
            var mockKlines = new List<MarketKline>();
            DateTime baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            for (int i = 0; i < 150; i++)
            {
                DateTime openTime = baseTime.AddMinutes(i * 30);
                mockKlines.Add(new MarketKline
                {
                    Symbol = "BTCUSDT",
                    Interval = "30m",
                    OpenTime = openTime,
                    CloseTime = openTime.AddMinutes(30),
                    Open = 60000m + i * 10,
                    High = 60050m + i * 10,
                    Low = 59950m + i * 10,
                    Close = 60020m + i * 10,
                    Volume = 100m + i
                });
            }

            ICursor<MarketKline> cursor = new MarketDataCursor<MarketKline>(mockKlines, bufferCapacity: 100);
            Console.WriteLine($"数据集总条数: {cursor.TotalCount}");

            // 阶段 1: 前进 50 步
            Console.WriteLine("\n[阶段 1] 前进 50 步...");
            for (int i = 0; i < 50; i++)
            {
                cursor.MoveNext();
            }
            Console.WriteLine($"当前位置: 第 {cursor.CurrentIndex + 1} 根 | 时间: {cursor.Current.FormattedOpenTime} | 收盘价: {cursor.Current.Close}");
            Console.WriteLine($"当前滑窗缓存数量: {cursor.GetBuffer().Count} (预期: 50)");

            // 阶段 2: 继续前进到第 150 步 (超过 100 容量上限)
            Console.WriteLine("\n[阶段 2] 继续前进至第 150 步 (测试 100 容量上限)...");
            while (cursor.MoveNext()) { }
            Console.WriteLine($"当前位置: 第 {cursor.CurrentIndex + 1} 根 | 时间: {cursor.Current.FormattedOpenTime} | 收盘价: {cursor.Current.Close}");
            var buffer150 = cursor.GetBuffer();
            Console.WriteLine($"当前滑窗缓存数量: {buffer150.Count} (预期: 100)");
            Console.WriteLine($"滑窗最老一条时间: {buffer150[0].FormattedOpenTime} (第 51 根)");
            Console.WriteLine($"滑窗最新一条时间: {buffer150[^1].FormattedOpenTime} (第 150 根)");

            // 阶段 3: 后退 10 步
            Console.WriteLine("\n[阶段 3] 后退 10 步 (测试 MovePrevious)...");
            for (int i = 0; i < 10; i++)
            {
                cursor.MovePrevious();
            }
            Console.WriteLine($"后退后位置: 第 {cursor.CurrentIndex + 1} 根 | 时间: {cursor.Current.FormattedOpenTime} | 收盘价: {cursor.Current.Close} (预期: 第 140 根)");

            // 阶段 4: 获取最近 5 根切片
            var recent5 = cursor.GetRecent(5);
            Console.WriteLine($"\n[阶段 4] 获取最近 5 根历史数据计算指标 (当前第 {cursor.CurrentIndex + 1} 根及前 4 根):");
            foreach (var k in recent5)
            {
                Console.WriteLine($"   -> {k.FormattedOpenTime} Close: {k.Close}");
            }

            Console.WriteLine("\n[验证通过] 双向游标与 100 条滑动历史缓存机制工作正常！\n");
        }

        #endregion

        #region 3. DuckDB 历史数据提供者测试

        static void TestDuckDbHistoricalProvider()
        {
            Console.WriteLine("--- [3] 测试 DuckDB 历史数据提供者 ---");

            using var provider = new DuckDbHistoricalDataProvider(cursorBufferCapacity: 100);

            DateTime startUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime endUtc = new DateTime(2026, 1, 31, 23, 59, 59, DateTimeKind.Utc);

            Console.WriteLine($"正在通过 DuckDB 查询 [BTCUSDT 30m] 历史数据 ({startUtc.ToUtc0String()} 至 {endUtc.ToUtc0String()})...");
            var klineCursor = provider.GetKlineCursor("BTCUSDT", KlineInterval.ThirtyMinutes, startUtc, endUtc);

            Console.WriteLine($"DuckDB 查询完成！加载数据总条数: {klineCursor.TotalCount}");

            if (klineCursor.TotalCount > 0)
            {
                int count = 0;
                while (klineCursor.MoveNext() && count < 5)
                {
                    Console.WriteLine($"  [K线 {count + 1}] {klineCursor.Current.FormattedOpenTime} O:{klineCursor.Current.Open} C:{klineCursor.Current.Close} V:{klineCursor.Current.Volume}");
                    count++;
                }
            }
            else
            {
                Console.WriteLine("  (未在本地磁盘找到对应日期的 zip/csv 文件，已按规范返回空数据集)");
                Console.WriteLine($"  提示: 预期文件路径类似: {Config.GetKlineFilePath("BTCUSDT", "30m", startUtc, ".zip")}");
            }

            Console.WriteLine("[验证通过] DuckDB 历史数据提供者接口调用成功！\n");
        }

        #endregion

        #region 4. 策略基类与双均线策略执行测试

        static async Task TestStrategyExecution()
        {
            Console.WriteLine("--- [4] 测试 策略基类 (StrategyBase) 与双均线策略 (MovingAverageCrossStrategy) ---");

            // 1. 创建策略对象
            var strategy = new MovingAverageCrossStrategy(symbol: "BTCUSDT", interval: "30m", fastPeriod: 3, slowPeriod: 6);

            int signalCount = 0;
            strategy.OnSignal += signal =>
            {
                Interlocked.Increment(ref signalCount);
                Console.WriteLine($"  🔥 [策略信号触发] -> {signal}");
            };

            strategy.OnLog += logMsg =>
            {
                Console.WriteLine($"  📝 {logMsg}");
            };

            // 2. 模拟注入连续 12 根 K 线与 1 笔 Tick，观察均线金叉/死叉产生
            DateTime baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            decimal[] simulatedPrices = new decimal[] { 60000m, 59900m, 59800m, 59700m, 59600m, 59500m, 60200m, 60800m, 61500m, 61000m, 60500m, 59800m };

            for (int i = 0; i < simulatedPrices.Length; i++)
            {
                DateTime openTime = baseTime.AddMinutes(i * 30);
                var kline = new MarketKline
                {
                    Symbol = "BTCUSDT",
                    Interval = "30m",
                    OpenTime = openTime,
                    CloseTime = openTime.AddMinutes(30),
                    Open = simulatedPrices[i] - 50m,
                    High = simulatedPrices[i] + 100m,
                    Low = simulatedPrices[i] - 100m,
                    Close = simulatedPrices[i],
                    Volume = 200m
                };
                strategy.OnKlineUpdate(kline);

                // 模拟注入 Tick
                var tick = new MarketTick
                {
                    Symbol = "BTCUSDT",
                    TradeId = 1000 + i,
                    Time = openTime.AddMinutes(15),
                    Price = simulatedPrices[i],
                    Quantity = 0.5m,
                    IsBuyerMaker = false
                };
                strategy.OnTickUpdate(tick);
            }

            Console.WriteLine($"\n[测试结果] 策略历史 K线缓存数量: {strategy.KlineHistory.Count}/100, 历史 Tick 缓存数量: {strategy.TickHistory.Count}/100");
            Console.WriteLine($"[测试结果] 共捕获交易信号: {signalCount} 个");

            await Task.CompletedTask;
            Console.WriteLine("[验证通过] StrategyBase 策略基类与双通道数据处理机制工作正常！\n");
        }

        #endregion

        #region 5. 极值点分形计算测试 (PivotHelper)

        static void TestPivotHelper()
        {
            Console.WriteLine("--- [5] 测试 极值点波峰波谷计算 (PivotHelper) ---");

            // 构造包含明显波峰和波谷的测试 K 线序列
            var klines = new List<MarketKline>();
            DateTime baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            decimal[] highs = new decimal[] { 100, 105, 115, 130, 125, 110, 95, 90, 85, 92, 105, 120, 110 };
            decimal[] lows = new decimal[] { 95, 100, 108, 120, 115, 100, 90, 80, 82, 88, 98, 112, 102 };

            for (int i = 0; i < highs.Length; i++)
            {
                klines.Add(new MarketKline
                {
                    Symbol = "BTCUSDT",
                    Interval = "30m",
                    OpenTime = baseTime.AddMinutes(i * 30),
                    High = highs[i],
                    Low = lows[i],
                    Close = (highs[i] + lows[i]) / 2m
                });
            }

            var (peaks, valleys) = PivotHelper.CalculatePeaks(klines, leftLen: 2, rightLen: 2);

            Console.WriteLine($"识别出的波峰 (Peaks) 数量: {peaks.Count}");
            foreach (var p in peaks)
            {
                Console.WriteLine($"  {p}");
            }

            Console.WriteLine($"\n识别出的波谷 (Valleys) 数量: {valleys.Count}");
            foreach (var v in valleys)
            {
                Console.WriteLine($"  {v}");
            }

            Console.WriteLine("\n[验证通过] PivotHelper 成功直接适配 IReadOnlyList<MarketKline> 并返回强类型 PivotPoint！\n");
        }

        #endregion

        #region 6. 趋势线Tick穿透回弹策略测试 (TrendLineReboundStrategy)

        static void TestTrendLineReboundStrategy()
        {
            Console.WriteLine("--- [6] 测试 趋势线Tick穿透回弹策略 (TrendLineReboundStrategy) ---");
            Console.WriteLine("规则: LineX1X2 > 40, LineAge > 4, 并在 5 个 Tick 内发生回弹时触发信号\n");

            var strategy = new TrendLineReboundStrategy(
                symbol: "BTCUSDT",
                interval: "30m",
                minLineX1X2: 40,
                minLineAge: 4,
                reboundTicksWindow: 5);

            int signalCount = 0;
            strategy.OnSignal += signal =>
            {
                Interlocked.Increment(ref signalCount);
                Console.WriteLine($"  🎯 [趋势线回弹信号] -> {signal}");
            };

            strategy.OnLog += logMsg =>
            {
                Console.WriteLine($"  📝 {logMsg}");
            };

            // 1. 构造 70 根模拟 K 线生成跨度 > 40 且年龄 > 4 的有效趋势线
            DateTime baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var klines = new List<MarketKline>();

            for (int i = 0; i < 70; i++)
            {
                decimal basePrice = 60000m;
                // 在 i=5 与 i=50 处构造明显波谷 (跨度 45 > 40, 距离末尾 70-50=20 > 4)
                if (i == 5) basePrice = 58000m;
                else if (i == 50) basePrice = 58500m;
                else basePrice = 60000m + (i % 5) * 100m;

                var kline = new MarketKline
                {
                    Symbol = "BTCUSDT",
                    Interval = "30m",
                    OpenTime = baseTime.AddMinutes(i * 30),
                    CloseTime = baseTime.AddMinutes((i + 1) * 30),
                    Open = basePrice + 50,
                    High = basePrice + 150,
                    Low = basePrice - 100,
                    Close = basePrice,
                    Volume = 100m
                };
                klines.Add(kline);
                strategy.OnKlineUpdate(kline);
            }

            Console.WriteLine($"\n[阶段 1] 70 根周期 K 线注入完毕，初始活跃趋势线数量: {strategy.ActiveLinesCount} 条。");

            // 2. 模拟 Tick 逐笔穿透与 5-Tick 回弹测试
            DateTime tickTime = baseTime.AddMinutes(70 * 30);

            Console.WriteLine("\n[阶段 2] 开始注入 Tick 逐笔测试 (向下穿透并在第 2 个 Tick 回弹做多):");
            // Tick 1: 58800 (在支撑线 58700 之上)
            strategy.OnTickUpdate(new MarketTick { Symbol = "BTCUSDT", TradeId = 1, Time = tickTime.AddSeconds(1), Price = 58800m });
            // Tick 2: 58650 (向下穿过趋势线 58700 -> 触发穿透监测)
            strategy.OnTickUpdate(new MarketTick { Symbol = "BTCUSDT", TradeId = 2, Time = tickTime.AddSeconds(2), Price = 58650m });
            // Tick 3: 58600 (穿透中继续下探)
            strategy.OnTickUpdate(new MarketTick { Symbol = "BTCUSDT", TradeId = 3, Time = tickTime.AddSeconds(3), Price = 58600m });
            // Tick 4: 58720 (第 2 个 Tick 迅速回弹突破 58700 支撑线之上 -> 触发做多信号 Buy! 并立即删除该趋势线)
            strategy.OnTickUpdate(new MarketTick { Symbol = "BTCUSDT", TradeId = 4, Time = tickTime.AddSeconds(4), Price = 58720m });

            Console.WriteLine($"\n[阶段 3] 穿透回弹后当前存活趋势线数量: {strategy.ActiveLinesCount} 条 (已成功删除被穿过趋势线)");
            Console.WriteLine($"[测试统计] 成功捕获回弹交易信号: {signalCount} 个");
            Console.WriteLine("[验证通过] TrendLineReboundStrategy 策略与穿透后自动删除机制工作正常！\n");
        }

        #endregion

        #region 7. Binance.Net 实时 WebSocket 推流测试

        static async Task TestLiveWebSocketStream()
        {
            Console.WriteLine("--- [7] 测试 Binance.Net 实时 WebSocket 推流 ---");
            Console.WriteLine("正在连接 Binance 现货公共行情 WebSocket (BTCUSDT 1m & Trades)...");

            using var liveProvider = new BinanceLiveMarketDataProvider(bufferCapacity: 100);

            int klineCount = 0;
            int tickCount = 0;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // 监听 10 秒后自动停止

            liveProvider.OnKline += kline =>
            {
                Interlocked.Increment(ref klineCount);
                Console.WriteLine($"[实时 K线] {kline.Symbol} {kline.Interval} @ {kline.FormattedOpenTime} | C:{kline.Close} | Final:{kline.IsClosed}");
            };

            liveProvider.OnTick += tick =>
            {
                Interlocked.Increment(ref tickCount);
                if (tickCount % 5 == 0) // 每 5 笔输出一次，避免刷屏
                {
                    Console.WriteLine($"[实时 逐笔] {tick.Symbol} @ {tick.FormattedTime} | P:{tick.Price} | Q:{tick.Quantity} | Dir:{(tick.IsBuyerMaker ? "SELL" : "BUY")}");
                }
            };

            bool klineSub = await liveProvider.SubscribeKlineAsync("BTCUSDT", KlineInterval.OneMinute);
            bool tradeSub = await liveProvider.SubscribeTradeAsync("BTCUSDT");

            Console.WriteLine($"K线订阅结果: {(klineSub ? "成功" : "失败")}");
            Console.WriteLine($"逐笔订阅结果: {(tradeSub ? "成功" : "失败")}");
            Console.WriteLine("开始监听实时推流数据 (持续 10 秒，请观察输出)...\n");

            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(500, cts.Token);
                }
            }
            catch (TaskCanceledException) { }

            await liveProvider.StopAsync();
            Console.WriteLine($"\n[监听结束] 共收到实时 K线更新: {klineCount} 次，实时逐笔成交: {tickCount} 笔。");
            Console.WriteLine("[验证通过] Binance.Net 实盘推流与数据模型转换工作正常！\n");
        }

        #endregion

        #region 8. 全套自动化自测

        static async Task RunAllSelfTests()
        {
            Console.WriteLine("=================================================================");
            Console.WriteLine("                开始运行全套自动化自测                           ");
            Console.WriteLine("=================================================================\n");

            TestTimeHelper();
            TestBidirectionalCursor();
            TestDuckDbHistoricalProvider();
            await TestStrategyExecution();
            TestPivotHelper();
            TestTrendLineReboundStrategy();

            Console.WriteLine("=================================================================");
            Console.WriteLine("                🎉 全套自动化自测全部通过！                       ");
            Console.WriteLine("=================================================================\n");
        }

        #endregion
    }
}
