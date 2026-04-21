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
    }
}