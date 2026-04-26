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

        public OrderController(BinanceTradeService tradeService)
        {
            _tradeService = tradeService;
        }
        // 定义前端传过来的数据结构
        public class PlaceOrderRequest
        {
            public string Symbol { get; set; }
            public string Side { get; set; }  // "BUY" 或 "SELL"
            public string Type { get; set; }  // "LIMIT" 或 "MARKET"
            public decimal Quantity { get; set; }
            public decimal? Price { get; set; }
        }

        [HttpPost("place-ws")]
        public async Task<IActionResult> PlaceOrderWs([FromBody] PlaceOrderRequest req)
        {
            if (req.Quantity <= 0) return BadRequest(new { message = "下单数量必须大于0" });

            try
            {
                // 🌟 走专线下单
                var result = await _wsApiService.PlaceOrderWsAsync(
                    req.Symbol, req.Side, req.Type, req.Quantity, req.Price);

                return Content(result, "application/json");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPost("place")]
        public async Task<IActionResult> PlaceOrder([FromBody] PlaceOrderRequest req)
        {
            // 简单的后端防御性风控
            if (req.Quantity <= 0) return BadRequest(new { message = "下单数量必须大于0" });
            if (req.Type == "LIMIT" && (req.Price == null || req.Price <= 0))
                return BadRequest(new { message = "限价单必须提供有效的价格" });

            try
            {
                var result = await _tradeService.PlaceOrderAsync(
                    req.Symbol, req.Side, req.Type, req.Quantity, req.Price);

                // 直接将币安的 JSON 返回给前端
                return Content(result, "application/json");
            }
            catch (Exception ex)
            {
                // 异常会被之前写的 ExceptionMiddleware 拦截，这里只需做基础返回
                return StatusCode(500, new { message = ex.Message });
            }
        }
    }
}