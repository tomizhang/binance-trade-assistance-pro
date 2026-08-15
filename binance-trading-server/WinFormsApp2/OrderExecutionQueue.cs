using Binance.Net.Clients;
using Binance.Net.Enums;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
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
        public long TradeId { get; set; }
        public string Symbol { get; set; } = "BTCUSDT";
        public OrderType Type { get; set; }
        public decimal Price { get; set; }
        public decimal QuantityUsdt { get; set; } = 100m;
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Comment { get; set; } = string.Empty;
    }

    public class OrderResult
    {
        public bool Success { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public OrderType Type { get; set; }
        public decimal ExecutedPrice { get; set; }
        public decimal ExecutedQuantity { get; set; }
        public string OrderId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// 高性能异步无阻塞下单队列处理器 (Producer-Consumer Order Pipeline)
    /// 核心规则：
    /// 1. 策略推演产生信号时，秒级非阻塞入队 (< 0.0001ms)，绝不阻塞高频 Tick 与 K线回调线程；
    /// 2. 后台单线程/消费者服务按序安全处理每一个下单请求；
    /// 3. 支持 IsLiveTrading 参数自由切换“本地模拟模式”与“币安实盘 API 模式”；
    /// 4. 内置杠杆倍数修改与边际保证金类型调整 API。
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
            // 启动后台消费者下单线程
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
                Log($"🟢 [实盘 API 初始化成功] 当前模式: 币安真实下单 | 杠杆: {Leverage}x");
            }
            else
            {
                _restClient?.Dispose();
                _restClient = null;
                Log($"🟡 [本地模拟模式] 当前模式: 本地模拟/回测挂单 (0 真实 API 开销)");
            }
        }

        /// <summary>
        /// 调整杠杆倍率 API (支持在交易前或动态调用)
        /// </summary>
        public async Task<bool> SetLeverageAsync(string symbol, int leverage)
        {
            symbol = symbol.Trim().ToUpper();
            leverage = Math.Max(1, Math.Min(125, leverage));
            Leverage = leverage;

            if (!IsLiveTrading || _restClient == null)
            {
                Log($"[模拟杠杆设置] [{symbol}] 杠杆调整为: {leverage}x (本地模拟)");
                return true;
            }

            try
            {
                Log($"▶ [币安 API 杠杆调整] 正在提交 [{symbol}] 杠杆修改为 {leverage}x...");
                var result = await _restClient.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, leverage).ConfigureAwait(false);

                if (result.Success)
                {
                    Log($"✅ [杠杆设置成功] [{symbol}] 杠杆已成功调整为 {result.Data.Leverage}x");
                    return true;
                }
                else
                {
                    Log($"❌ [杠杆设置失败] [{symbol}] {result.Error?.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"❌ [杠杆调整异常] {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 调整持仓模式 / 保证金类型 API (逐仓/全仓)
        /// </summary>
        public async Task<bool> SetMarginTypeAsync(string symbol, FuturesMarginType marginType)
        {
            symbol = symbol.Trim().ToUpper();
            if (!IsLiveTrading || _restClient == null)
            {
                Log($"[模拟保证金模式] [{symbol}] 模式调整为: {marginType}");
                return true;
            }

            try
            {
                var result = await _restClient.UsdFuturesApi.Account.ChangeMarginTypeAsync(symbol, marginType).ConfigureAwait(false);
                if (result.Success)
                {
                    Log($"✅ [保证金模式成功] [{symbol}] 成功调整为 {marginType}");
                    return true;
                }
                else
                {
                    Log($"⚠️ [保证金模式提示] [{symbol}] {result.Error?.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"❌ [保证金模式异常] {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 生产者入队 API：0 延迟非阻塞入队 (< 0.001ms)，主线程秒级返回
        /// </summary>
        public void EnqueueOrder(OrderRequest request)
        {
            if (request == null) return;
            _orderQueue.Enqueue(request);
            _signal.Release();
            Log($"📥 [下单请求入队] #{request.TradeId} [{request.Symbol}] [{request.Type}] @ 预设价格 {request.Price}");
        }

        private void Log(string msg)
        {
            OnLog?.Invoke(msg);
        }

        /// <summary>
        /// 消费者后台单线程下单循环
        /// </summary>
        private async Task ProcessOrdersConsumerLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(token).ConfigureAwait(false);

                    if (_orderQueue.TryDequeue(out var req))
                    {
                        var result = await ExecuteSingleOrderAsync(req).ConfigureAwait(false);
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
        /// 执行单个下单任务 (区分实盘 API 下单与本地模拟下单)
        /// </summary>
        private async Task<OrderResult> ExecuteSingleOrderAsync(OrderRequest req)
        {
            OrderResult res = new OrderResult
            {
                Symbol = req.Symbol,
                Type = req.Type,
                ExecutedPrice = req.Price
            };

            if (!IsLiveTrading || _restClient == null)
            {
                // A. 本地模拟下单逻辑
                res.Success = true;
                res.OrderId = $"SIM-{DateTime.Now.Ticks}";
                res.ExecutedQuantity = req.Price > 0 ? req.QuantityUsdt / req.Price : 0m;
                res.Message = "本地模拟订单匹配成功";

                Log($"🟢 [模拟下单完成] #{req.TradeId} [{req.Symbol}] [{req.Type}] 成交价: {res.ExecutedPrice:F2} | 成交量: {res.ExecutedQuantity:F4}");
                return res;
            }

            // B. 币安真实 API 下单逻辑 (以 USDT-M 合约市价单为例)
            try
            {
                // 1. 自动校准/确保杠杆已经成功设置
                await SetLeverageAsync(req.Symbol, Leverage).ConfigureAwait(false);

                // 2. 计算下单张数 / 数量 (Quantity)
                decimal qty = req.Price > 0 ? Math.Round(req.QuantityUsdt * Leverage / req.Price, 3) : 0m;
                if (qty <= 0) qty = 0.001m;

                OrderSide side = (req.Type == OrderType.BuyLongOpen || req.Type == OrderType.CloseShort) ? OrderSide.Buy : OrderSide.Sell;

                Log($"▶ [币安实盘下单中] [{req.Symbol}] [{req.Type}] Side: {side} | 数量: {qty} | 杠杆: {Leverage}x...");

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

        public void Dispose()
        {
            _cts.Cancel();
            _signal.Dispose();
            _restClient?.Dispose();
            _cts.Dispose();
        }
    }
}
