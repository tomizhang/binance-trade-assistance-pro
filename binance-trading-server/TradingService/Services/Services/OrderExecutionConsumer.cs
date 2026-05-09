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

        // 🌟 新增：注入仓位管理中心，用于查询实时仓位状态
        private readonly PositionManagementService _positionManager;

        public OrderExecutionConsumer(
            ILogger<OrderExecutionConsumer> logger,
            OrderChannel orderChannel,
            BinanceTradeWsService tradeWsService,
            RiskControlManager riskManager,
            SnapshotChannel snapshotChannel,
            PositionManagementService positionManager) // 👈 注入进来
        {
            _logger = logger;
            _orderChannel = orderChannel;
            _tradeWsService = tradeWsService;
            _riskManager = riskManager;
            _snapshotChannel = snapshotChannel;
            _positionManager = positionManager; // 👈 赋值
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
                    _logger.LogInformation("--------------------------------------------------------------");
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
            // ==========================================
            // 🛡️ 1. 风控与重复开仓拦截门
            // ==========================================
            if (signal.Action == OrderAction.OpenMarket || signal.Action == OrderAction.OpenLimit)
            {
                // 🌟 第一道防线：防重复开仓拦截
                if (_positionManager.HasActivePosition(signal.Symbol))
                {
                    _logger.LogWarning($"⛔ [防重拦截] 策略 '{signal.StrategyName}' 试图开仓 {signal.Symbol} 被拒绝。原因: 已持有该币种的活动仓位，禁止重复开仓！");
                    return; // 拦截！直接 Return
                }

                // 第二道防线：黑名单与全局风控拦截
                if (!_riskManager.CanOpenPosition(signal.Symbol, out string blockReason))
                {
                    // 拦截！直接 Return，永远不发给币安
                    _logger.LogWarning($"⛔ [风控拦截] 策略 '{signal.StrategyName}' 试图开仓 {signal.Symbol} 被拒绝。原因: {blockReason}");
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

                // 🌟 使用动态精度格式化！彻底告别 -1111 和 -1013 错误！
                finalQuantity = _tradeWsService.FormatQuantity(signal.Symbol, rawQty);

                if (finalQuantity <= 0)
                    throw new Exception($"计算出的下单数量过小，已被 {signal.Symbol} 的精度规则截断为 0");
            }

            // 将传入的基础价格也用 FormatPrice 裁剪一下，主要防止独立触发的止损/限价单报错
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

                    // 🌟 瞬间塞入开仓快照任务！
                    _snapshotChannel.TryWrite(new SnapshotTask
                    {
                        Symbol = signal.Symbol,
                        TradeType = OrderAction.OpenMarket.ToString(),
                        OrderSide = signal.Side,
                        StrategyName = signal.StrategyName
                    });

                    // 【阶段二：连招触发 (如果主开仓报错，这里绝对不会被执行！)】
                    string positionSide = signal.Side == "BUY" ? "LONG" : "SHORT";
                    // 🌟 计算反向平仓动作：如果你是 BUY 开仓，那平仓动作就是 SELL
                    string closeSide = signal.Side == "BUY" ? "SELL" : "BUY";

                    if (signal.StopLossPrice.HasValue || signal.TakeProfitPrice.HasValue)
                    {
                        // 稍微等待 10 毫秒，确保币安撮合引擎已生成仓位
                        await Task.Delay(10);
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
                            // 🚨 极速插针导致价格已经跌破止损价！不要挂单了，直接跑！
                            _logger.LogCritical($"🚨 [极速插针断臂] {signal.Symbol} 价格已穿透止损线！立即执行应急市价平仓！{ex.Message}");
                            await _tradeWsService.PlaceOrderWsAsync(signal.Symbol, closeSide, "MARKET", finalQuantity, reduceOnly: true);
                            return; // 既然已经平仓止损了，下面的止盈就直接跳过不挂了
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
                            // 💰 短时间内暴涨穿透了止盈线！这是天降横财，直接市价砸盘落袋为安！
                            _logger.LogCritical($"🚀 [极速暴涨落袋] {signal.Symbol} 价格已穿透止盈线！立即执行市价平仓收割利润！");
                            await _tradeWsService.PlaceOrderWsAsync(signal.Symbol, closeSide, "MARKET", finalQuantity, reduceOnly: true);
                            return;
                        }
                    }
                    break;
                case OrderAction.OpenLimit:
                    if (!signal.Price.HasValue) throw new Exception("限价单必须提供 Price 参数");
                    await _tradeWsService.OpenLimitPositionAsync(signal.Symbol, signal.Side, finalQuantity, finalPrice.Value);

                    // 🌟 瞬间塞入开仓快照任务！
                    _snapshotChannel.TryWrite(new SnapshotTask
                    {
                        Symbol = signal.Symbol,
                        TradeType = OrderAction.OpenLimit.ToString(),
                        OrderSide = signal.Side,
                        StrategyName = signal.StrategyName
                    });
                    break;

                case OrderAction.StopLossMarket:
                    // 独立触发的止损单
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
                    // 独立触发的止盈单
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