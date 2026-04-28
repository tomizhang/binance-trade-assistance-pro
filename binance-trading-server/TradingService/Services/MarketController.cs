using Microsoft.AspNetCore.Mvc;

namespace TradingTerminal.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MarketController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public MarketController(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        // 🌟 GET: api/market/klines
        [HttpGet("klines")]
        public async Task<IActionResult> GetHistoricalKlines([FromQuery] string symbol, [FromQuery] string interval, [FromQuery] int limit = 1000, [FromQuery] long? endTime = null)
        {
            var client = _httpClientFactory.CreateClient();
            
            var url = $"https://fapi.binance.com/fapi/v1/klines?symbol={symbol}&interval={interval}&limit={limit}";

            if (endTime.HasValue)
            {
                url += $"&endTime={endTime.Value}";
            }

            try
            {
                // 作为中继，直接请求币安并原样返回给前端
                var response = await client.GetAsync(url);
                var content = await response.Content.ReadAsStringAsync();

                // 直接透传币安的 JSON 数组结构
                return Content(content, "application/json");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { msg = "中继请求币安K线失败", error = ex.Message });
            }
        }
    }
}