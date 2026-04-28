// Controllers/OrderController.cs
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using TradingService.Services;
using System;
using TradingTerminal.Services;

namespace TradingService.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class OrderController : ControllerBase
    {
        private readonly BinanceTradeService _tradeService;
        // 在 OrderController 中注入新服务
        private readonly BinanceWebSocketService _wsApiService;

        public OrderController(BinanceTradeService tradeService, BinanceWebSocketService wsApiService)
        {
            _tradeService = tradeService;
            _wsApiService = wsApiService;
        }
        // 定义前端传过来的数据结构
  

        //[HttpPost("place-ws")]
        //public async Task<IActionResult> PlaceOrderWs([FromBody] PlaceOrderRequest req)
        //{
        //    if (req.Quantity <= 0) return BadRequest(new { message = "下单数量必须大于0" });

        //    try
        //    {
        //        // 🌟 走专线下单
        //        var result = await _wsApiService.PlaceOrderWsAsync(
        //            req.Symbol, req.Side, req.Type, req.Quantity, req.Price);

        //        return Content(result, "application/json");
        //    }
        //    catch (Exception ex)
        //    {
        //        return StatusCode(500, new { message = ex.Message });
        //    }
        //}

        // 1. 确保你的 Request 模型包含了这两个新字段
        public class PlaceOrderRequest
        {
            public string Symbol { get; set; }
            public string Side { get; set; }
            public string Type { get; set; }
            public decimal Quantity { get; set; }
            public decimal? Price { get; set; }

            // 🌟 新增字段
            public decimal? StopPrice { get; set; }
            public bool? ReduceOnly { get; set; }
        }

        // 2. 确保在接口调用时，把参数传给服务层
        [HttpPost("place-ws")]
        public async Task<IActionResult> PlaceOrder([FromBody] PlaceOrderRequest req)
        {
            try
            {
                // 🌟 把 StopPrice 和 ReduceOnly 传进去
                var resultJson = await _tradeService.PlaceOrderAsync(
                    req.Symbol,
                    req.Side,
                    req.Type,
                    req.Quantity,
                    req.Price,
                    req.StopPrice,
                    req.ReduceOnly
                );

                return Content(resultJson, "application/json");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { msg = "下单执行异常", error = ex.Message });
            }
        }

        // 🌟 新增：暴露给前端的获取挂单接口
        // GET: api/order/openOrders?symbol=BTCUSDT
        [HttpGet("openOrders")]
        public async Task<IActionResult> GetOpenOrders([FromQuery] string symbol = null)
        {
            try
            {
                var resultJson = await _tradeService.GetOpenOrdersAsync(symbol);
                return Content(resultJson, "application/json");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { msg = "获取挂单数据异常", error = ex.Message });
            }
        }

        [HttpPost("cancel-ws")]
        public async Task<IActionResult> CancelOrder([FromBody] CancelOrderRequest req)
        {
            if (string.IsNullOrEmpty(req.Symbol) || string.IsNullOrEmpty(req.OrderId))
            {
                return BadRequest(new { msg = "参数错误：symbol 和 orderId 不能为空" });
            }

            try
            {
                // 🌟 调用刚刚补齐的服务层方法
                var resultJson = await _tradeService.CancelOrderAsync(req.Symbol, req.OrderId);

                // 返回币安的原始响应，其中包含 status: "CANCELED"
                return Content(resultJson, "application/json");
            }
            catch (Exception ex)
            {
                //_logger.LogError(ex, "撤单过程发生异常");
                return StatusCode(500, new { msg = "撤单执行异常", error = ex.Message });
            }
        }
    }

    public class CancelOrderRequest
    {
        public string Symbol { get; set; }
        public string OrderId { get; set; }

    }
}