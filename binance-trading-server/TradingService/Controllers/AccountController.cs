using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using TradingService.Services; // 替换成你实际的命名空间
using System;

namespace TradingService.Controllers
{
    [ApiController]
    // 路由映射为 /api/account
    [Route("api/[controller]")]
    public class AccountController : ControllerBase
    {
        // 我们之前把 ChangeLeverageAsync 写在了 BinanceTradeService 里
        private readonly BinanceTradeService _tradeService;

        public AccountController(BinanceTradeService tradeService)
        {
            _tradeService = tradeService;
        }

        // 定义前端传过来的参数结构
        public class ChangeLeverageRequest
        {
            public string Symbol { get; set; }
            public int Leverage { get; set; }
        }

        /// <summary>
        /// 修改全仓/逐仓杠杆倍数
        /// POST: /api/account/leverage
        /// </summary>
        [HttpPost("leverage")]
        public async Task<IActionResult> ChangeLeverage([FromBody] ChangeLeverageRequest req)
        {
            // 基础风控：币安 U 本位合约杠杆范围通常是 1 到 125
            if (req.Leverage < 1 || req.Leverage > 125)
            {
                return BadRequest(new { message = "杠杆倍数必须在 1 到 125 之间" });
            }

            try
            {
                // 调用后端 Service 发起带签名的请求
                var result = await _tradeService.ChangeLeverageAsync(req.Symbol, req.Leverage);

                // 将币安返回的 JSON（包含当前最大可开仓位等信息）原样返回给前端
                return Content(result, "application/json");
            }
            catch (Exception ex)
            {
                // 如果出现网络或签名错误，返回 500
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPost("listenKey")]
        public async Task<IActionResult> GetListenKey()
        {
            try
            {
                var result = await _tradeService.CreateListenKeyAsync();
                return Content(result, "application/json");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPut("listenKey")]
        public async Task<IActionResult> KeepAliveListenKey()
        {
            await _tradeService.KeepAliveListenKeyAsync();
            return Ok();
        }

        [HttpGet("trades")]
        public async Task<IActionResult> GetTrades([FromQuery] string symbol, [FromQuery] int limit = 50)
        {
            try
            {
                // 如果前端没传 symbol，我们要给个友好的报错或默认值
                if (string.IsNullOrEmpty(symbol))
                {
                    return BadRequest(new { message = "必须提供币种 symbol，例如 BTCUSDT" });
                }

                var result = await _tradeService.GetUserTradesAsync(symbol, limit);

                // 币安返回的本身就是 JSON 数组字符串，直接返回给前端
                return Content(result, "application/json");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpGet("positionRisk")]
        public async Task<IActionResult> GetPositionRisk([FromQuery] string symbol = null)
        {
            try
            {
                // 直接返回币安的原始 JSON 数组
                var result = await _tradeService.GetPositionRiskAsync(symbol);
                return Content(result, "application/json");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpGet("info")]
        public async Task<IActionResult> GetAccountInfo()
        {
            try
            {
                var result = await _tradeService.GetAccountInfoAsync();
                return Content(result, "application/json");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }
    }
}