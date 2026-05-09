using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using TradingTerminal.Models;

namespace TradingTerminal.Services
{
    /// <summary>
    /// 订单消费者：无情执行引擎，永不阻塞策略分析主线程
    /// </summary>
    public class OrderExecutionConsumer : BackgroundService
    {
        private readonly ILogger<OrderExecutionConsumer> _logger;
        private readonly OrderChannel _orderChannel;
        private readonly BinanceTradeWsService _tradeWsService;
        private readonly RiskControlManager _riskManager;
        private readonly SnapshotChannel _snapshotChannel;
        private readonly PositionManagementService _positionManager;

        // 🌟 新增：注入私有数据服务，用于零延迟获取实时可用余额
        private readonly BinanceUserDataWsService _userDataWsService;

        public OrderExecutionConsumer(
            ILogger<OrderExecutionConsumer> logger,
            OrderChannel orderChannel,
            BinanceTradeWsService tradeWsService,
            RiskControlManager riskManager,
            SnapshotChannel snapshotChannel,
            PositionManagementService positionManager,
            BinanceUserDataWsService userDataWsService) // 👈 注入进来
        {
            _logger = logger;
            _orderChannel = orderChannel;
            _tradeWsService = tradeWsService;
            _riskManager = riskManager;
            _snapshotChannel = snapshotChannel;
            _positionManager = positionManager;
            _userDataWsService = userDataWsService; // 👈 赋值
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("⚙️ [订单消费者] 后台执行线程已启动，正在监听策略指令...");

            // 死循环监听管道，没有订单时会自动休眠，不消耗 CPU
            await foreach (var signal in _orderChannel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessOrderAsync(signal);
                    _logger.LogInformation("--------------------------------------------------------------");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ [订单执行惨败] {signal.StrategyName} 策略下单异常: {ex.Message}");
                }
            }
            _logger.LogError($"退出订单消费者 OrderExecutionConsumer");
        }

        private async Task ProcessOrderAsync(OrderSignal signal)
        {
            // ==========================================
            // 🛡️ 1. 风控、资金与重复开仓拦截门
            // ==========================================
            if (signal.Action == OrderAction.OpenMarket || signal.Action == OrderAction.OpenLimit)
            {
                // 🌟 【第一道防线：资金余额不足拦截】
                // 只有 U本位合约需要校验 USDT 余额
                if (signal.IsUsdtMargin)
                {
                    // 获取零延迟的纯净钱包余额（不含未实现盈亏）
                    decimal currentBalance = _userDataWsService.CachedPureWalletBalance;

                    if (currentBalance < signal.UsdtAmount)
                    {
                        _logger.LogWarning($"⛔ [资金不足拦截] 策略 '{signal.StrategyName}' 企图开仓 {signal.Symbol} 被拒绝。本次开仓需要 {signal.UsdtAmount} USDT，当前钱包纯净余额仅剩 {currentBalance:F2} USDT！");
                        return; // 拦截！直接 Return
                    }
                }

                // 【第二道防线：防重复开仓拦截】
                if (_positionManager.HasActivePosition(signal.Symbol))
                {
                    _logger.LogWarning($"⛔ [防重拦截] 策略 '{signal.StrategyName}' 企图开仓 {signal.Symbol} 被拒绝。原因: 已持有该币种的活动仓位，禁止重复开仓！");
                    return; // 拦截！直接 Return
                }

                // 【第三道防线：黑名单与全局风控拦截】
                if (!_riskManager.CanOpenPosition(signal.Symbol, out string blockReason))
                {
                    _logger.LogWarning($"⛔ [风控拦截] 策略 '{signal.StrategyName}' 企图开仓 {signal.Symbol} 被拒绝。原因: {blockReason}");
                    return;
                }
            }

            decimal finalQuantity = signal.Quantity;

            // ==========================================
            // 📐 2. 数量与价格精度裁剪
            // ==========================================
            // 计算出数量后，用网关的 FormatQuantity 剪裁精度！
            if (signal.IsUsdtMargin)
            {
                decimal rawQty = await _tradeWsService.ConvertUsdtToQuantityAsync(signal.Symbol, signal.UsdtAmount, signal.Leverage);

                // 使用动态精度格式化
                finalQuantity = _tradeWsService.FormatQuantity(signal.Symbol, rawQty);

                if (finalQuantity <= 0)
                    throw new Exception($"计算出的下单数量过小，已被 {signal.Symbol} 的精度规则截断为 0");
            }

            // 将传入的基础价格也用 FormatPrice 裁剪一下
            decimal? finalPrice = signal.Price.HasValue ? _tradeWsService.FormatPrice(signal.Symbol, signal.Price.Value) : null;
            decimal? finalStopPrice = signal.StopPrice.HasValue ? _tradeWsService.FormatPrice(signal.Symbol, signal.StopPrice.Value) : null;

            // ==========================================
            // 🚀 3. 核心路由与原子级连招
            // ==========================================
            switch (signal.Action)
            {
                case OrderAction.OpenMarket:
                    // 【阶段一：执行主开仓】
                    await _tradeWsService.OpenMarketPositionAsync(signal.Symbol, signal.Side, finalQuantity);
                    _logger.LogInformation($"✅ 开仓成功！附言: {signal.Message}");

                    _snapshotChannel.TryWrite(new SnapshotTask
                    {
                        Symbol = signal.Symbol,
                        TradeType = OrderAction.OpenMarket.ToString(),
                        OrderSide = signal.Side,
                        StrategyName = signal.StrategyName
                    });

                    // 【阶段二：连招触发】
                    string positionSide = signal.Side == "BUY" ? "LONG" : "SHORT";
                    string closeSide = signal.Side == "BUY" ? "SELL" : "BUY";

                    if (signal.StopLossPrice.HasValue || signal.TakeProfitPrice.HasValue)
                    {
                        await Task.Delay(10); // 稍微等待 10 毫秒，确保币安撮合引擎已生成仓位
                    }

                    // 🛡️ 挂载止损
                    if (signal.StopLossPrice.HasValue)
                    {
                        decimal formattedSl = _tradeWsService.FormatPrice(signal.Symbol, signal.StopLossPrice.Value);
                        try
                        {
                            await _tradeWsService.SetStopLossMarketAsync(signal.Symbol, positionSide, finalQuantity, formattedSl);
                            _logger.LogInformation($"🛡️ [{signal.Symbol}] 保护性止损成功挂载: {formattedSl}");
                        }
                        catch (Exception ex) when (ex.Message.Contains("-2021"))
                        {
                            _logger.LogCritical($"🚨 [极速插针断臂] {signal.Symbol} 价格已穿透止损线！立即执行应急市价平仓！{ex.Message}");
                            await _tradeWsService.PlaceOrderWsAsync(signal.Symbol, closeSide, "MARKET", finalQuantity, reduceOnly: true);
                            return;
                        }
                    }

                    // 💰 挂载止盈
                    if (signal.TakeProfitPrice.HasValue)
                    {
                        decimal formattedTp = _tradeWsService.FormatPrice(signal.Symbol, signal.TakeProfitPrice.Value);
                        try
                        {
                            await _tradeWsService.SetTakeProfitMarketAsync(signal.Symbol, positionSide, finalQuantity, formattedTp);
                            _logger.LogInformation($"💰 [{signal.Symbol}] 目标性止盈成功挂载: {formattedTp}");
                        }
                        catch (Exception ex) when (ex.Message.Contains("-2021"))
                        {
                            _logger.LogCritical($"🚀 [极速暴涨落袋] {signal.Symbol} 价格已穿透止盈线！立即执行市价平仓收割利润！");
                            await _tradeWsService.PlaceOrderWsAsync(signal.Symbol, closeSide, "MARKET", finalQuantity, reduceOnly: true);
                            return;
                        }
                    }
                    break;

                case OrderAction.OpenLimit:
                    if (!signal.Price.HasValue) throw new Exception("限价单必须提供 Price 参数");
                    await _tradeWsService.OpenLimitPositionAsync(signal.Symbol, signal.Side, finalQuantity, finalPrice.Value);

                    _snapshotChannel.TryWrite(new SnapshotTask
                    {
                        Symbol = signal.Symbol,
                        TradeType = OrderAction.OpenLimit.ToString(),
                        OrderSide = signal.Side,
                        StrategyName = signal.StrategyName
                    });
                    break;

                case OrderAction.StopLossMarket:
                    if (!signal.StopPrice.HasValue) throw new Exception("止损单必须提供 StopPrice 参数");
                    await _tradeWsService.SetStopLossMarketAsync(signal.Symbol, signal.Side, finalQuantity, finalStopPrice.Value);

                    _snapshotChannel.TryWrite(new SnapshotTask
                    {
                        Symbol = signal.Symbol,
                        TradeType = OrderAction.StopLossMarket.ToString(),
                        OrderSide = signal.Side,
                        StrategyName = signal.StrategyName
                    });
                    break;

                case OrderAction.TakeProfitMarket:
                    if (!signal.StopPrice.HasValue) throw new Exception("止盈单必须提供 StopPrice 参数");
                    await _tradeWsService.SetTakeProfitMarketAsync(signal.Symbol, signal.Side, finalQuantity, finalStopPrice.Value);

                    _snapshotChannel.TryWrite(new SnapshotTask
                    {
                        Symbol = signal.Symbol,
                        TradeType = OrderAction.TakeProfitMarket.ToString(),
                        OrderSide = signal.Side,
                        StrategyName = signal.StrategyName
                    });
                    break;

                case OrderAction.CancelAll:
                    await _tradeWsService.CancelAllOpenOrdersAsync(signal.Symbol);
                    _logger.LogInformation($"🧹 [战场清理] 已对 {signal.Symbol} 的双引擎 (普通池/条件池) 进行了深度撤单扫荡！");
                    break;

                default:
                    _logger.LogWarning($"⚠️ 未知的订单指令类型: {signal.Action}");
                    break;
            }
        }
    }
}