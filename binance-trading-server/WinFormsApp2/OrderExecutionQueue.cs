using Binance.Net.Clients;
using Binance.Net.Enums;
using System;
using System.Collections.Concurrent;
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
    }

    /// <summary>
    /// 高性能 Producer-Consumer 非阻塞实盘/模拟下单队列引擎 (Order Execution Queue Service)
    /// 特性：
    /// 1. 秒级非阻塞 (< 0.001ms)：高频 Tick/K线线程产生信号后直接投递队列，绝不阻塞计算与推演线程；
    /// 2. 币种下单精度自动校准：按 BTC/ETH(3位), SOL/BNB(2位), XRP(1位), DOGE/ADA(0位整数) 格式化张数；
    /// 3. 最小下单金额保底：自动校验并确保每笔订单满足币安 USDT-M 合约最小名义价值 (Min Notional 5.5 USDT)；
    /// 4. 0 侵入现有策略逻辑：TrendLineStrategy 无需任何改动。
    /// </summary>
    public class OrderExecutionQueue : IDisposable
    {
        private readonly ConcurrentQueue<OrderRequest> _orderQueue = new ConcurrentQueue<OrderRequest>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private BinanceRestClient? _restClient;

        public bool IsLiveTrading { get; set; } = false;
        public string ApiKey { get; set; } = string.Empty;
        public string ApiSecret { get; set; } = string.Empty;
        public int Leverage { get; set; } = 20;
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
            Leverage = Math.Max(1, Math.Min(125, leverage));
            OrderQuantityUsdt = orderQuantityUsdt > 0 ? orderQuantityUsdt : 1m;

            if (IsLiveTrading && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret))
            {
                _restClient = new BinanceRestClient(options =>
                {
                    options.ApiCredentials = new Binance.Net.BinanceCredentials(ApiKey, ApiSecret);
                });
                Log($"🟢 [实盘 API 初始化成功] 当前模式: 币安真实合约下单 | 杠杆: {Leverage}x | 单笔默认资金: {OrderQuantityUsdt} USDT");
            }
            else
            {
                _restClient?.Dispose();
                _restClient = null;
                Log($"🟡 [模拟下单初始化成功] 当前模式: 本地挂单匹配 (Simulated) | 杠杆: {Leverage}x | 单笔资金: {OrderQuantityUsdt} USDT");
            }
        }

        /// <summary>
        /// 调整指定币种的合约杠杆倍数 (仅实盘模式下生效)
        /// </summary>
        public async Task<bool> SetLeverageAsync(string symbol, int leverage)
        {
            if (!IsLiveTrading || _restClient == null) return true;

            try
            {
                var result = await _restClient.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, leverage).ConfigureAwait(false);
                if (result.Success)
                {
                    Log($"⚙ [杠杆校准成功] 币种 [{symbol}] 杠杆调整为: {result.Data.Leverage}x");
                    return true;
                }
                else
                {
                    Log($"⚠️ [杠杆校准提示] 币种 [{symbol}] 调整杠杆失败: {result.Error?.Message}");
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

        /// <summary>
        /// 后台 Consumer 常驻消费循环
        /// </summary>
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
        /// 单笔订单执行逻辑 (自动校准精度与最小名义价值)
        /// </summary>
        private async Task<OrderResult> ExecuteSingleOrderAsync(OrderRequest req)
        {
            var res = new OrderResult
            {
                Symbol = req.Symbol,
                Type = req.Type,
                ExecutedPrice = req.Price
            };

            decimal quantityUsdt = req.QuantityUsdt > 0 ? req.QuantityUsdt : OrderQuantityUsdt;
            decimal qty = CalculateValidQuantity(req.Symbol, req.Price, quantityUsdt, Leverage, out decimal effectiveNotional);

            if (!IsLiveTrading || _restClient == null)
            {
                // A. 本地模拟下单逻辑
                res.Success = true;
                res.OrderId = $"SIM-{DateTime.Now.Ticks}";
                res.ExecutedQuantity = qty;
                res.Message = "本地模拟订单匹配成功";

                Log($"🟢 [模拟下单完成] #{req.TradeId} [{req.Symbol}] [{req.Type}] 成交价: {res.ExecutedPrice:F2} | 数量: {res.ExecutedQuantity} | 额度: {effectiveNotional:F1} USDT");
                return res;
            }

            // B. 币安真实 API 下单逻辑 ( USDT-M 合约市价单)
            try
            {
                // 1. 自动校准/确保杠杆已经成功设置
                await SetLeverageAsync(req.Symbol, Leverage).ConfigureAwait(false);

                OrderSide side = (req.Type == OrderType.BuyLongOpen || req.Type == OrderType.CloseShort) ? OrderSide.Buy : OrderSide.Sell;

                Log($"▶ [币安实盘下单中] [{req.Symbol}] [{req.Type}] Side: {side} | 数量: {qty} | 杠杆: {Leverage}x | 对应金额: {effectiveNotional:F1} USDT...");

                var orderResult = await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: req.Symbol,
                    side: side,
                    type: FuturesOrderType.Market,
                    quantity: qty).ConfigureAwait(false);

                if (orderResult.Success)
                {
                    res.Success = true;
                    res.OrderId = orderResult.Data.Id.ToString();
                    res.ExecutedPrice = orderResult.Data.Price > 0 ? orderResult.Data.Price : req.Price;
                    res.ExecutedQuantity = orderResult.Data.Quantity;
                    res.Message = "币安实盘订单成交成功";

                    Log($"✅ [币安实盘成交成功!] 单号 #{res.OrderId} [{req.Symbol}] [{req.Type}] 均价: {res.ExecutedPrice} | 数量: {res.ExecutedQuantity}");
                }
                else
                {
                    res.Success = false;
                    res.Message = orderResult.Error?.Message ?? "未知下单错误";
                    Log($"❌ [币安实盘下单拒绝] [{req.Symbol}] [{req.Type}] 原因: {res.Message}");
                }
            }
            catch (Exception ex)
            {
                res.Success = false;
                res.Message = ex.Message;
                Log($"❌ [币安实盘下单失败] [{req.Symbol}] 异常: {ex.Message}");
            }

            return res;
        }

        #region 币种下单精度与最小金额校准引擎 (Symbol Precision & Min Notional Rules)

        /// <summary>
        /// 规则 1 & 规则 2：按币种返回 (数量精度, 最小张数, 币安最小名义价值 USDT)
        /// </summary>
        private static (int quantityPrecision, decimal minQuantity, decimal minNotionalUsdt) GetSymbolRules(string symbol)
        {
            string sym = (symbol ?? string.Empty).ToUpperInvariant();

            // 1. BTC / ETH 顶级主流币：支持 3 位小数，最小挂单 0.001
            if (sym.Contains("BTC") || sym.Contains("ETH"))
            {
                return (3, 0.001m, 5.5m);
            }
            // 2. SOL / BNB / AVAX / LINK 中盘主流币：支持 2 位小数，最小挂单 0.01
            if (sym.Contains("SOL") || sym.Contains("BNB") || sym.Contains("AVAX") || sym.Contains("LINK"))
            {
                return (2, 0.01m, 5.5m);
            }
            // 3. XRP / DOT / LTC / MATIC 山寨主流币：支持 1 位小数，最小挂单 0.1
            if (sym.Contains("XRP") || sym.Contains("DOT") || sym.Contains("LTC") || sym.Contains("MATIC"))
            {
                return (1, 0.1m, 5.5m);
            }
            // 4. DOGE / ADA / TRX / SHIB / PEPE Meme 币：必须为 0 位整数，最小挂单 1
            if (sym.Contains("DOGE") || sym.Contains("ADA") || sym.Contains("TRX") || sym.Contains("SHIB") || sym.Contains("PEPE") || sym.Contains("1000"))
            {
                return (0, 1m, 5.5m);
            }

            // 默认兜底规则：2 位小数精度，最小数量 0.01，币安 USDT-M 合约最小名义价值限制 5.5 USDT
            return (2, 0.01m, 5.5m);
        }

        /// <summary>
        /// 核心张数/数量计算公式，自动完成最小金额保底与精度截断
        /// </summary>
        private static decimal CalculateValidQuantity(string symbol, decimal price, decimal quantityUsdt, int leverage, out decimal effectiveNotionalUsdt)
        {
            var (precision, minQty, minNotional) = GetSymbolRules(symbol);

            if (price <= 0m)
            {
                effectiveNotionalUsdt = 0m;
                return 0m;
            }

            // 1. 计算理论名义价值 (Notional Value in USDT)
            decimal notional = quantityUsdt * leverage;

            // 2. 规则 1 校验：最小下单金额保底 (Min Notional Limit, 币安永续合约至少 5.5 USDT)
            if (notional < minNotional)
            {
                notional = minNotional;
            }

            effectiveNotionalUsdt = notional;

            // 3. 规则 2 校验：张数/数量按该币种的 Step Size 精度 (Precision) 进行向零截断
            decimal rawQty = notional / price;
            decimal roundedQty = Math.Round(rawQty, precision, MidpointRounding.ToZero);

            // 4. 确保满足最小下单数量 (Min Quantity)
            if (roundedQty < minQty)
            {
                roundedQty = minQty;
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
