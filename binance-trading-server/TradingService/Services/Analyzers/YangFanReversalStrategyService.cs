using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingTerminal.Hubs;
using TradingTerminal.Models;
using TradingTerminal.Utils;

namespace TradingTerminal.Services
{
    /// <summary>
    /// 🚀 扬帆多指标共振长短线交易策略
    /// 核心逻辑：完美复刻 PineScript 中的红蝴蝶(做多)与绿箭头(做空)共振模型
    /// </summary>
    public class YangFanReversalStrategyService : StrategyBase
    {
        private readonly IPositionManagementService _positionManager;
        private readonly ChartPublishService _chartPublishService;

        // 为多币种并发管理各自的历史 K 线缓冲 (以大周期 30m / 1h 驱动为主)
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, List<IKline>>> _tfBuffers = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastTradeTime = new();

        // 策略参数配置 (严格对齐 PineScript 默认配置)
        private const int PSY_PERIOD = 12;

        public YangFanReversalStrategyService(
            ILogger<YangFanReversalStrategyService> logger,
            IHubContext<MarketHub> hubContext,
            MarketEventBus eventBus,
            BinanceWebSocketService wsService,
            OrderChannel orderChannel,
            BinanceTradeWsService tradeWsService,
            IPositionManagementService positionManager,
            ChartPublishService chartPublishService)
            : base(logger, hubContext, eventBus, wsService, orderChannel, tradeWsService)
        {
            _positionManager = positionManager;
            _chartPublishService = chartPublishService;

            this.IsOrderEnabled = false; // 开启实盘执行
            this.IsStrategyEnabled = false;

            // 🌟 核心修改：由于该系统属于大周期共振波段策略，建议使用 30m 或 1h 周期判定
            this._timeframes = new[] { "30m", "1h" };
        }

        // ==========================================
        // 🌟 初始化预热：拉取大周期数据
        // ==========================================
        protected override async Task InitializeStrategyDataAsync(string symbol)
        {
            _logger.LogInformation($"[{GetType().Name}] 正在为 {symbol} 预热扬帆系统大周期历史底库...");

            var klines30m = await FetchHistoryAsync(symbol, "30m", 200);
            var klines1h = await FetchHistoryAsync(symbol, "1h", 200);

            var tfData = _tfBuffers.GetOrAdd(symbol, _ => new ConcurrentDictionary<string, List<IKline>>());
            if (klines30m.Any()) tfData["30m"] = klines30m;
            if (klines1h.Any()) tfData["1h"] = klines1h;
        }

        private async Task<List<IKline>> FetchHistoryAsync(string symbol, string interval, int limit)
        {
            try
            {
                string json = await _wsService.GetHistoricalKlinesAsync(symbol, interval, limit);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var historyList = new List<IKline>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    historyList.Add(new KlineMessage
                    {
                        Symbol = symbol,
                        Interval = interval,
                        IsClosed = true,
                        OpenTime = item[0].GetInt64(),
                        Open = decimal.Parse(item[1].GetString()),
                        High = decimal.Parse(item[2].GetString()),
                        Low = decimal.Parse(item[3].GetString()),
                        Close = decimal.Parse(item[4].GetString()),
                        Volume = decimal.Parse(item[5].GetString()),
                        TradeCount = item[8].GetInt32(),
                        TakerBuyBaseVolume = decimal.Parse(item[9].GetString())
                    });
                }
                return historyList;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"拉取 {symbol} {interval} 历史数据失败");
                return new List<IKline>();
            }
        }

        // ==========================================
        // 🌟 流式实时行情接收中枢
        // ==========================================
        protected override void OnKlineReceived(IKline msg)
        {
            if (!msg.IsClosed) return; // 严格等大周期收盘再计算，防止盘中信号闪烁 (Signal Flashing)

            string symbol = msg.Symbol;
            string interval = msg.Interval;

            var symbolData = _tfBuffers.GetOrAdd(symbol, _ => new ConcurrentDictionary<string, List<IKline>>());
            var buffer = symbolData.GetOrAdd(interval, _ => new List<IKline>());

            lock (buffer)
            {
                var lastKline = buffer.LastOrDefault();
                if (lastKline != null && lastKline.OpenTime == msg.OpenTime)
                {
                    buffer[buffer.Count - 1] = msg;
                }
                else
                {
                    buffer.Add(msg);
                }
                if (buffer.Count > 200) buffer.RemoveAt(0);
            }

            // 这里以你选择的主运算周期为主（假设以 30m 大周期驱动信号）
            if (interval == "30m" && buffer.Count >= 80)
            {
                CalculateYangFanSignals(symbol, buffer);
            }
        }

        // ==========================================
        // 🌟 核心算法：复刻 PineScript 指标流
        // ==========================================
        private void CalculateYangFanSignals(string symbol, List<IKline> klines)
        {
            if (_positionManager.HasActivePosition(symbol)) return;
            if (_lastTradeTime.TryGetValue(symbol, out var lastTime) && (DateTime.Now - lastTime).TotalMinutes < 30) return;

            int count = klines.Count;
            var closeList = klines.Select(k => k.Close).ToList();
            var highList = klines.Select(k => k.High).ToList();
            var lowList = klines.Select(k => k.Low).ToList();

            // ---- 1. 计算核心 PSY (心理线) ----
            int psyWin = 0;
            for (int i = count - PSY_PERIOD; i < count; i++)
            {
                if (closeList[i] > closeList[i - 1]) psyWin++;
            }
            decimal psy = ((decimal)psyWin / PSY_PERIOD) * 100m;

            // ---- 2. 蝶形指标前置计算：A23 -> A24 -> A25 ----
            var a23List = new List<decimal>();
            for (int i = 0; i < count; i++)
            {
                a23List.Add((closeList[i] + lowList[i] + highList[i]) / 3m);
            }
            var a24List = CalculateEMA(a23List, 6);
            var a25List = CalculateEMA(a24List, 5);

            // 判断金叉条件
            bool isA24CrossoverA25 = (a24List[count - 2] <= a25List[count - 2]) && (a24List[count - 1] > a25List[count - 1]);

            // ---- 3. 筹码动能核心计算: car_yf 与 hexie_1 ----
            var var1xList = new List<decimal>();
            for (int i = 0; i < count; i++)
            {
                var1xList.Add((2m * closeList[i] + highList[i] + lowList[i]) / 4m);
            }

            var carYfRaw = new List<decimal>();
            for (int i = 34; i < count; i++)
            {
                decimal lowest34 = lowList.GetRange(i - 33, 34).Min();
                decimal highest34 = highList.GetRange(i - 33, 34).Max();

                decimal diff = highest34 - lowest34;
                decimal val = diff == 0 ? 0 : (var1xList[i] - lowest34) / diff * 100m;
                carYfRaw.Add(val);
            }

            // 补齐前面的空缺，保证索引对齐
            var carYfList = new List<decimal>(new decimal[34]);
            carYfList.AddRange(CalculateEMA(carYfRaw, 13));

            // 计算 hexie_1 (复刻通达信独有的 0.667*nz[1] + 0.333*current 的 2周期 SMA)
            var hexie1List = new List<decimal>(new decimal[carYfList.Count]);
            for (int i = 1; i < carYfList.Count; i++)
            {
                hexie1List[i] = (0.667m * carYfList[i - 1]) + (0.333m * carYfList[i]);
            }

            // 判断做空交叉点 (hexie_1 金叉 car_yf)
            bool isHexieCrossoverCarYf = (hexie1List[count - 2] <= carYfList[count - 2]) && (hexie1List[count - 1] > carYfList[count - 1]);

            // =====================================================================
            // 🌟 信号判断与买卖狙击
            // =====================================================================
            bool isButterflyBuy = isA24CrossoverA25 && (psy < 55m);
            bool isGreenArrowSell = isHexieCrossoverCarYf && (psy > 67m);

            if (isButterflyBuy) // 【做多信号触发】
            {
                ExecuteOrder(symbol, true, klines.Last().Close, "YF_Butterfly_Long");
            }
            else if (isGreenArrowSell) // 【做空信号触发】
            {
                ExecuteOrder(symbol, false, klines.Last().Close, "YF_GreenArrow_Short");
            }
        }

        private void ExecuteOrder(string symbol, bool isLong, decimal entryPrice, string strategyName)
        {
            _lastTradeTime[symbol] = DateTime.Now;

            // 宽基大周期防守型止损止盈参数 (可根据回测动态对调调整)
            decimal leverage = 10.0m;
            decimal requiredRoeTp = 0.015m * leverage; // 1.5% 实际波动率目标
            decimal requiredRoeSl = 0.010m * leverage; // 1.0% 防御止损限制

            _logger.LogWarning($"🎯 [{symbol}] 扬帆共振引擎扣动扳机！方向: {(isLong ? "做多" : "做空")}, 价格: {entryPrice:F4}");

            _ = Task.Run(async () =>
            {
                await PlaceOrderWithLeverageRiskAsync(
                    symbol, isLong, entryPrice, 5.0m, leverage, requiredRoeTp, requiredRoeSl, strategyName
                );
            });
        }

        // ==========================================
        // 🛠️ 纯数学工具函数：复刻 Pine 内部技术算法
        // ==========================================
        private List<decimal> CalculateEMA(List<decimal> src, int period)
        {
            var ema = new List<decimal>(new decimal[src.Count]);
            if (src.Count == 0) return ema;

            decimal alpha = 2m / (period + 1m);
            ema[0] = src[0];

            for (int i = 1; i < src.Count; i++)
            {
                ema[i] = alpha * src[i] + (1m - alpha) * ema[i - 1];
            }
            return ema;
        }

        /// <summary>
        /// 复刻通达信/PineScript 归一化移动平均过滤
        /// </summary>
        private List<decimal> CalculateTdxSma(List<decimal> src, int n, int m)
        {
            var sma = new List<decimal>(new decimal[src.Count]);
            if (src.Count == 0) return sma;

            sma[0] = src[0];
            for (int i = 1; i < src.Count; i++)
            {
                sma[i] = (m * src[i] + (n - m) * sma[i - 1]) / n;
            }
            return sma;
        }
    }
}