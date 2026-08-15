using Binance.Net.Clients;
using Binance.Net.Enums;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WinFormsApp2
{
    public enum OrderType
    {
        BuyLongOpen,    // 开多买入
        SellShortOpen,  // 开空卖出
        CloseLong,      // 平多离场
        CloseShort      // 平空离场
    }

    public class OrderRequest
    {
        public int TradeId { get; set; }
        public string Symbol { get; set; } = "BTCUSDT";
        public OrderType Type { get; set; }
        public decimal Price { get; set; }
        public decimal QuantityUsdt { get; set; } = 1m;
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Comment { get; set; } = string.Empty;

        // 止盈止损计算位置与百分比
        public decimal TakeProfitPrice { get; set; }
        public decimal StopLossPrice { get; set; }
        public decimal TakeProfitPct { get; set; } = 1.5m;
        public decimal StopLossPct { get; set; } = 0.8m;
    }

    public class OrderResult
    {
        public bool Success { get; set; }
        public string OrderId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public OrderType Type { get; set; }
        public decimal ExecutedPrice { get; set; }
        public decimal ExecutedQuantity { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;

        // 止盈止损与耗时跟踪
        public decimal TakeProfitPrice { get; set; }
        public decimal StopLossPrice { get; set; }
        public long ElapsedMs { get; set; }
    }

    public class SymbolRuleInfo
    {
        public int Precision { get; set; } = 0;
        public decimal StepSize { get; set; } = 1m;
        public decimal MinQty { get; set; } = 1m;
        public decimal MinNotional { get; set; } = 5.5m;
    }

    /// <summary>
    /// 高性能 Producer-Consumer 非阻塞实盘/模拟下单队列引擎 (Order Execution Queue Service)
    /// 核心优化：
    /// 1. 对接币安 /fapi/v2/account 接口同步账户真实杠杆，设置成功后无需在每次下单时重复设置；
    /// 2. 对接币安 /fapi/v1/leverageBracket 接口：杠杆设置为 -1 时自动使用支持的最大杠杆；
    /// 3. 动态对齐 ExchangeInfo 规则：自动获取各币种的官方 StepSize / QuantityPrecision / MinNotional；
    /// 4. 币种名称合法化清洗：自动剥离非标准字符，防止非法的 API 参数请求。
    /// </summary>
    public class OrderExecutionQueue : IDisposable
    {
        private readonly ConcurrentQueue<OrderRequest> _orderQueue = new ConcurrentQueue<OrderRequest>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private static readonly ConcurrentDictionary<string, SymbolRuleInfo> _symbolRulesCache = new ConcurrentDictionary<string, SymbolRuleInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, int> _maxLeverageCache = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, int> _exchangeCurrentLeverageCache = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, bool> _setLeverageDoneSymbols = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private BinanceRestClient? _restClient;

        public bool IsLiveTrading { get; set; } = false;
        public string ApiKey { get; set; } = string.Empty;
        public string ApiSecret { get; set; } = string.Empty;
        public int Leverage { get; set; } = 20; // 若为 -1，则自动匹配币安最大支持杠杆
        public decimal OrderQuantityUsdt { get; set; } = 1m;

        public event Action<OrderResult>? OnOrderExecuted;
        public event Action<string>? OnLog;

        public OrderExecutionQueue()
        {
            // 启动后台独立 Consumer 下单处理线程
            Task.Run(() => ProcessOrdersConsumerLoopAsync(_cts.Token));
        }

        public void ConfigureApi(bool isLiveTrading, string apiKey, string apiSecret, int leverage, decimal orderQuantityUsdt = 1m)
        {
            IsLiveTrading = isLiveTrading;
            ApiKey = apiKey ?? string.Empty;
            ApiSecret = apiSecret ?? string.Empty;
            Leverage = leverage; // 支持 -1
            OrderQuantityUsdt = orderQuantityUsdt > 0 ? orderQuantityUsdt : 1m;

            if (IsLiveTrading && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret))
            {
                _restClient = new BinanceRestClient(options =>
                {
                    options.ApiCredentials = new Binance.Net.BinanceCredentials(ApiKey, ApiSecret);
                });
                Log($"🟢 [实盘 API 初始化成功] 当前模式: 币安真实合约下单 | 杠杆配置: {(Leverage <= 0 ? "-1 (自动使用币安该币种最大杠杆)" : Leverage + "x")} | 单笔默认资金: {OrderQuantityUsdt} USDT");

                // 后台异步同步 ExchangeInfo 精度库、/fapi/v1/leverageBracket 最大杠杆表 与 /fapi/v2/account 账号杠杆信息
                _ = FetchExchangeInfoRulesAsync();
                _ = FetchLeverageBracketsAsync();
                _ = FetchAccountLeveragesAsync();
            }
            else
            {
                _restClient?.Dispose();
                _restClient = null;
                Log($"🟡 [模拟下单初始化成功] 当前模式: 本地挂单匹配 (Simulated) | 杠杆配置: {(Leverage <= 0 ? "-1 (默认使用最大杠杆)" : Leverage + "x")} | 单笔资金: {OrderQuantityUsdt} USDT");
            }
        }

        /// <summary>
        /// 异步从币安接口 /fapi/v2/account 获取用户账户仓位与杠杆数据，初始化本地杠杆缓存
        /// </summary>
        private async Task FetchAccountLeveragesAsync()
        {
            if (_restClient == null) return;

            try
            {
                var accountResult = await _restClient.UsdFuturesApi.Account.GetAccountInfoV2Async().ConfigureAwait(false);
                if (accountResult.Success && accountResult.Data != null && accountResult.Data.Positions != null)
                {
                    int count = 0;
                    foreach (var pos in accountResult.Data.Positions)
                    {
                        if (!string.IsNullOrEmpty(pos.Symbol))
                        {
                            _exchangeCurrentLeverageCache[pos.Symbol] = pos.Leverage;
                            count++;
                        }
                    }
                    Log($"⚙ [账户杠杆同步成功] 成功从 /fapi/v2/account 拉取 {count} 个合约的当前真实杠杆状态！");
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ [账户杠杆同步提示] {ex.Message}");
            }
        }

        /// <summary>
        /// 异步从币安官方接口 /fapi/v1/leverageBracket 拉取并缓存各合约最大支持杠杆
        /// </summary>
        private async Task FetchLeverageBracketsAsync()
        {
            if (_restClient == null) return;

            try
            {
                var bracketsResult = await _restClient.UsdFuturesApi.Account.GetBracketsAsync().ConfigureAwait(false);
                if (bracketsResult.Success && bracketsResult.Data != null)
                {
                    int count = 0;
                    foreach (var symbolBracket in bracketsResult.Data)
                    {
                        if (symbolBracket.Brackets != null && symbolBracket.Brackets.Any())
                        {
                            int maxLev = symbolBracket.Brackets.Max(b => b.InitialLeverage);
                            _maxLeverageCache[symbolBracket.Symbol] = maxLev;
                            count++;
                        }
                    }
                    Log($"⚙ [杠杆阶梯同步成功] 已成功从 /fapi/v1/leverageBracket 拉取 {count} 个合约的官方最大支持杠杆库！");
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ [杠杆阶梯同步提示] 无法拉取官方杠杆阶梯 ({ex.Message})，自动使用安全智能阶梯。");
            }
        }

        /// <summary>
        /// 异步从币安官方获取全量合约的精确精度、StepSize 与最小名义价值规则库
        /// </summary>
        private async Task FetchExchangeInfoRulesAsync()
        {
            if (_restClient == null) return;

            try
            {
                var infoResult = await _restClient.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync().ConfigureAwait(false);
                if (infoResult.Success && infoResult.Data != null)
                {
                    int count = 0;
                    foreach (var symbol in infoResult.Data.Symbols)
                    {
                        var info = new SymbolRuleInfo
                        {
                            Precision = symbol.QuantityPrecision,
                            StepSize = symbol.LotSizeFilter?.StepSize ?? 1m,
                            MinQty = symbol.LotSizeFilter?.MinQuantity ?? 1m,
                            MinNotional = symbol.MinNotionalFilter?.MinNotional ?? 5.5m
                        };
                        _symbolRulesCache[symbol.Name] = info;
                        count++;
                    }
                    Log($"✅ [币安精度库同步成功] 成功同步 {count} 个合约的官方 StepSize / 数量精度规则库！");
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ [币安精度库同步提示] 无法拉取官方精度库 ({ex.Message})，自动使用智能价格阶梯机制进行精度保底。");
            }
        }

        /// <summary>
        /// 获取币种在币安上支持的最大杠杆倍数
        /// </summary>
        public static int GetMaxSupportedLeverage(string symbol)
        {
            symbol = SanitizeSymbol(symbol);
            if (_maxLeverageCache.TryGetValue(symbol, out int maxLev) && maxLev > 0)
            {
                return maxLev;
            }

            string sym = symbol.ToUpperInvariant();
            if (sym.Contains("BTC")) return 125;
            if (sym.Contains("ETH")) return 100;
            if (sym.Contains("SOL") || sym.Contains("BNB")) return 50;

            // Meme 币或普通山寨币通常支持 20x
            return 20;
        }

        /// <summary>
        /// 币种名称合法化清洗 (全面支持标准英文与币安中文 Meme 币种，如“龙虾USDT”、“1000000龙虾USDT”、“我踏马来USDT”，剥离空格与逗号)
        /// </summary>
        public static string SanitizeSymbol(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            // 保留中文字符 (\u4e00-\u9fa5)、英文字母 (A-Za-z)、数字 (0-9) 以及破折号/下划线 (_ -)
            string clean = Regex.Replace(input.Trim(), @"[^\u4e00-\u9fa5A-Za-z0-9_\-]", "");
            return clean;
        }

        /// <summary>
        /// 调整指定币种的合约杠杆倍数 (已设置过或与交易所侧一致时自动跳过，无需每次下单重复设置)
        /// </summary>
        public async Task<bool> SetLeverageAsync(string symbol, int configuredLeverage)
        {
            symbol = SanitizeSymbol(symbol);
            if (string.IsNullOrEmpty(symbol) || !IsLiveTrading || _restClient == null) return true;

            int maxSupported = GetMaxSupportedLeverage(symbol);
            int targetLeverage = configuredLeverage <= 0 ? maxSupported : Math.Min(configuredLeverage, maxSupported);

            // 关键性能优化：若本地已设置完成且交易所侧杠杆符合目标杠杆，直接跳过重复 API 设置！
            if (_exchangeCurrentLeverageCache.TryGetValue(symbol, out int currentLev) && currentLev == targetLeverage)
            {
                return true;
            }

            if (_setLeverageDoneSymbols.TryGetValue(symbol, out bool isDone) && isDone)
            {
                return true;
            }

            try
            {
                var result = await _restClient.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, targetLeverage).ConfigureAwait(false);
                if (result.Success)
                {
                    _exchangeCurrentLeverageCache[symbol] = result.Data.Leverage;
                    _setLeverageDoneSymbols[symbol] = true;
                    Log($"⚙ [杠杆一次性校准成功] 币种 [{symbol}] 杠杆调整为: {result.Data.Leverage}x (官方最大支持: {maxSupported}x，后续下单免重复调用)");
                    return true;
                }
                else
                {
                    Log($"⚠️ [杠杆校准提示] 币种 [{symbol}] 调整杠杆至 {targetLeverage}x 提示: {result.Error?.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ [杠杆校准异常] 币种 [{symbol}] {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 秒级非阻塞入队 (Producer) - 高频 Tick 线程调用耗时 < 0.001ms
        /// </summary>
        public void EnqueueOrder(OrderRequest request)
        {
            if (request == null) return;
            _orderQueue.Enqueue(request);
            _signal.Release();
        }

        private async Task ProcessOrdersConsumerLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(ct).ConfigureAwait(false);

                    if (_orderQueue.TryDequeue(out var request))
                    {
                        var result = await ExecuteSingleOrderAsync(request).ConfigureAwait(false);
                        OnOrderExecuted?.Invoke(result);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"❌ [下单队列消费异常] {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 单笔订单执行逻辑 (自动校准精度与最小名义价值，记录止盈止损位置与毫秒级耗时)
        /// </summary>
        private async Task<OrderResult> ExecuteSingleOrderAsync(OrderRequest req)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            string cleanSymbol = SanitizeSymbol(req.Symbol);
            if (string.IsNullOrEmpty(cleanSymbol))
            {
                sw.Stop();
                return new OrderResult
                {
                    Symbol = req.Symbol,
                    Success = false,
                    Message = "非法交易对名称",
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }

            var res = new OrderResult
            {
                Symbol = cleanSymbol,
                Type = req.Type,
                ExecutedPrice = req.Price,
                TakeProfitPrice = req.TakeProfitPrice,
                StopLossPrice = req.StopLossPrice
            };

            int targetLeverage = Leverage;
            int effectiveLeverage = targetLeverage <= 0 ? GetMaxSupportedLeverage(cleanSymbol) : Math.Min(targetLeverage, GetMaxSupportedLeverage(cleanSymbol));

            decimal quantityUsdt = req.QuantityUsdt > 0 ? req.QuantityUsdt : OrderQuantityUsdt;
            decimal qty = CalculateValidQuantity(cleanSymbol, req.Price, quantityUsdt, effectiveLeverage, out decimal effectiveNotional);

            string tpSlLog = (req.TakeProfitPrice > 0 || req.StopLossPrice > 0)
                ? $" | 🎯 止盈: {req.TakeProfitPrice} | 🛡 止损: {req.StopLossPrice}"
                : "";

            if (!IsLiveTrading || _restClient == null)
            {
                // A. 本地模拟下单逻辑
                sw.Stop();
                res.ElapsedMs = sw.ElapsedMilliseconds;
                res.Success = true;
                res.OrderId = $"SIM-{DateTime.Now.Ticks}";
                res.ExecutedQuantity = qty;
                res.Message = "本地模拟订单匹配成功";

                Log($"🟢 [模拟下单完成] #{req.TradeId} [{cleanSymbol}] [{req.Type}] 成交价: {res.ExecutedPrice} | 数量: {res.ExecutedQuantity} | 杠杆: {effectiveLeverage}x | 额度: {effectiveNotional:F1} USDT{tpSlLog} | ⚡ 耗时: {res.ElapsedMs}ms");
                return res;
            }

            // B. 币安真实 API 下单逻辑 ( USDT-M 合约市价单)
            try
            {
                // 1. 一次性校准杠杆 (若已校准或一致自动 0 延迟跳过)
                await SetLeverageAsync(cleanSymbol, Leverage).ConfigureAwait(false);

                OrderSide side = (req.Type == OrderType.BuyLongOpen || req.Type == OrderType.CloseShort) ? OrderSide.Buy : OrderSide.Sell;

                Log($"▶ [币安实盘下单中] [{cleanSymbol}] [{req.Type}] Side: {side} | 数量: {qty} | 杠杆: {effectiveLeverage}x | 对应金额: {effectiveNotional:F1} USDT{tpSlLog}...");

                var orderResult = await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: cleanSymbol,
                    side: side,
                    type: FuturesOrderType.Market,
                    quantity: qty).ConfigureAwait(false);

                sw.Stop();
                res.ElapsedMs = sw.ElapsedMilliseconds;

                if (orderResult.Success)
                {
                    res.Success = true;
                    res.OrderId = orderResult.Data.Id.ToString();
                    res.ExecutedPrice = orderResult.Data.Price > 0 ? orderResult.Data.Price : req.Price;
                    res.ExecutedQuantity = orderResult.Data.Quantity;
                    res.Message = "币安实盘订单成交成功";

                    Log($"✅ [币安实盘成交成功!] 单号 #{res.OrderId} [{cleanSymbol}] [{req.Type}] 均价: {res.ExecutedPrice} | 数量: {res.ExecutedQuantity}{tpSlLog} | ⚡ 耗时: {res.ElapsedMs}ms");
                }
                else
                {
                    res.Success = false;
                    res.Message = orderResult.Error?.Message ?? "未知下单错误";
                    Log($"❌ [币安实盘下单拒绝] [{cleanSymbol}] [{req.Type}] 原因: {res.Message} | ⚡ 耗时: {res.ElapsedMs}ms");
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                res.ElapsedMs = sw.ElapsedMilliseconds;
                res.Success = false;
                res.Message = ex.Message;
                Log($"❌ [币安实盘下单失败] [{cleanSymbol}] 异常: {ex.Message} | ⚡ 耗时: {res.ElapsedMs}ms");
            }

            return res;
        }

        #region 币种下单精度与最小金额校准引擎 (Symbol Precision & Step-Size Rules)

        /// <summary>
        /// 规则检索：优先从币安官方 ExchangeInfo 缓存中拿精度；若没有则自动基于价格阶梯做智能保底
        /// </summary>
        private static SymbolRuleInfo GetSymbolRules(string symbol, decimal currentPrice)
        {
            if (_symbolRulesCache.TryGetValue(symbol, out var cachedRule))
            {
                return cachedRule;
            }

            string sym = symbol.ToUpperInvariant();

            // 规则 A: 特别标杆币种
            if (sym.Contains("BTC") || sym.Contains("ETH"))
            {
                return new SymbolRuleInfo { Precision = 3, StepSize = 0.001m, MinQty = 0.001m, MinNotional = 5.5m };
            }
            if (sym.Contains("SOL") || sym.Contains("BNB") || sym.Contains("AVAX") || sym.Contains("LINK"))
            {
                return new SymbolRuleInfo { Precision = 2, StepSize = 0.01m, MinQty = 0.01m, MinNotional = 5.5m };
            }
            if (sym.StartsWith("1000"))
            {
                return new SymbolRuleInfo { Precision = 0, StepSize = 1m, MinQty = 1m, MinNotional = 5.5m };
            }

            // 规则 B: 智能价格阶梯机制 (应对 PNUT, MEME, BOME, FET, HEMI, SYRUP, BARD 等山寨/Meme 币)
            if (currentPrice < 1.0m)
            {
                return new SymbolRuleInfo { Precision = 0, StepSize = 1m, MinQty = 1m, MinNotional = 5.5m };
            }
            if (currentPrice < 100.0m)
            {
                return new SymbolRuleInfo { Precision = 1, StepSize = 0.1m, MinQty = 0.1m, MinNotional = 5.5m };
            }

            return new SymbolRuleInfo { Precision = 0, StepSize = 1m, MinQty = 1m, MinNotional = 5.5m };
        }

        /// <summary>
        /// 核心张数/数量计算公式，自动完成最小金额保底与 StepSize 步进对齐
        /// </summary>
        private static decimal CalculateValidQuantity(string symbol, decimal price, decimal quantityUsdt, int leverage, out decimal effectiveNotionalUsdt)
        {
            var rule = GetSymbolRules(symbol, price);

            if (price <= 0m)
            {
                effectiveNotionalUsdt = 0m;
                return 0m;
            }

            // 1. 计算理论名义价值 (Notional Value in USDT)
            decimal notional = quantityUsdt * leverage;

            // 2. 规则 1 校验：最小下单金额保底 (Min Notional Limit, 币安永续合约至少 5.5 USDT)
            if (notional < rule.MinNotional)
            {
                notional = rule.MinNotional;
            }

            effectiveNotionalUsdt = notional;
            decimal rawQty = notional / price;

            // 3. 规则 2 校验：按照币安官方 StepSize 或 Precision 进行下取整
            decimal roundedQty = 0m;
            if (rule.StepSize > 0m)
            {
                decimal steps = Math.Floor(rawQty / rule.StepSize);
                roundedQty = steps * rule.StepSize;
            }
            else
            {
                roundedQty = Math.Round(rawQty, rule.Precision, MidpointRounding.ToZero);
            }

            // 4. 确保满足最小下单张数
            if (roundedQty < rule.MinQty)
            {
                roundedQty = rule.MinQty;
            }

            return roundedQty;
        }

        #endregion

        private void Log(string msg)
        {
            OnLog?.Invoke(msg);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _signal.Dispose();
            _restClient?.Dispose();
            _cts.Dispose();
        }
    }
}
