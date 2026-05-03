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
        public OrderExecutionConsumer(
            ILogger<OrderExecutionConsumer> logger,
            OrderChannel orderChannel,
            BinanceTradeWsService tradeWsService,
            RiskControlManager riskManager,
            SnapshotChannel snapshotChannel) // 👈 注入进来
        {
            _logger = logger;
            _orderChannel = orderChannel;
            _tradeWsService = tradeWsService;
            _riskManager = riskManager;
            _snapshotChannel = snapshotChannel;
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("⚙️ [订单消费者] 后台执行线程已启动，正在监听策略指令...");

            // 死循环监听管道，没有订单时会自动休眠，不消耗 CPU
            await foreach (var signal in _orderChannel.Reader.ReadAllAsync(stoppingToken))
            {
                // ⚠️ 极其关键：套上 try-catch！绝对不能因为一笔订单报错，导致消费者循环崩溃！
                try
                {
                    await ProcessOrderAsync(signal);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ [订单执行惨败] {signal.StrategyName} 策略下单异常: {ex.Message}");
                    // TODO: 蓝图第 3 阶段，这里将触发邮件/通知模块报错
                }
            }
            _logger.LogError($"退出订单消费者OrderExecutionConsumer");
        }

        private async Task ProcessOrderAsync(OrderSignal signal)
        {
            if (signal.Action == OrderAction.OpenMarket || signal.Action == OrderAction.OpenLimit)
            {
                if (!_riskManager.CanOpenPosition(signal.Symbol, out string blockReason))
                {
                    // 拦截！直接 Return，永远不发给币安
                    _logger.LogWarning($"⛔ [风控拦截] 策略 '{signal.StrategyName}' 试图开仓 {signal.Symbol} 被拒绝。原因: {blockReason}");
                    return;
                }
            }
            decimal finalQuantity = signal.Quantity;

            // 1. 计算出数量后，用网关的 FormatQuantity 剪裁精度！
            if (signal.IsUsdtMargin)
            {
                decimal rawQty = await _tradeWsService.ConvertUsdtToQuantityAsync(signal.Symbol, signal.UsdtAmount, signal.Leverage);

                // 🌟 使用动态精度格式化！彻底告别 -1111 和 -1013 错误！
                finalQuantity = _tradeWsService.FormatQuantity(signal.Symbol, rawQty);

                if (finalQuantity <= 0)
                    throw new Exception($"计算出的下单数量过小，已被 {signal.Symbol} 的精度规则截断为 0");
            }

            // 2. 将传入的价格也用 FormatPrice 裁剪一下，防止止损/限价单报错
            decimal? finalPrice = signal.Price.HasValue ? _tradeWsService.FormatPrice(signal.Symbol, signal.Price.Value) : null;
            decimal? finalStopPrice = signal.StopPrice.HasValue ? _tradeWsService.FormatPrice(signal.Symbol, signal.StopPrice.Value) : null;

            // 根据不同的动作类型，调用交易网关对应的 API
            switch (signal.Action)
            {
                case OrderAction.OpenMarket:
                   await _tradeWsService.OpenMarketPositionAsync(signal.Symbol, signal.Side, finalQuantity);
                    // 🌟 2. 蓝图落地：瞬间塞入开仓快照任务！
                    _snapshotChannel.TryWrite(new SnapshotTask
                    {
                        Symbol = signal.Symbol,
                        TradeType = OrderAction.OpenMarket.ToString(),
                        OrderSide = signal.Side,
                        StrategyName = signal.StrategyName
                    });
                    break;

                case OrderAction.OpenLimit:
                    if (!signal.Price.HasValue) throw new Exception("限价单必须提供 Price 参数");
                    await _tradeWsService.OpenLimitPositionAsync(signal.Symbol, signal.Side, finalQuantity, finalPrice.Value);
                    // 🌟 2. 蓝图落地：瞬间塞入开仓快照任务！
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
                    // 注意这里的 Side，传入的是你原本持仓的方向 (如 LONG)，网关底层会自动反转为 SELL 并带上 ReduceOnly
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
                    await _tradeWsService.CancelAllOpenOrdersWsAsync(signal.Symbol);
                    _logger.LogInformation($"🧹 [战场清理] 已成功撤销 {signal.Symbol} 的所有遗留条件单 (止盈/止损)！");
                    break;
                default:
                    _logger.LogWarning($"⚠️ 未知的订单指令类型: {signal.Action}");
                    break;
            }

            // TODO: 蓝图第 4 阶段，这里可以加入“快照模块”，记录开仓时的 K 线图
        }
    }
}