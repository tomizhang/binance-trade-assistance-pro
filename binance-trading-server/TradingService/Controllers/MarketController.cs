using Microsoft.AspNetCore.Mvc;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using TradingService.Services;

namespace TradingService.Controllers
{
    [ApiController]
    [Route("api/[controller]")] // 基础路由为 /api/market
    public class MarketController : ControllerBase
    {
        private readonly BinanceTradeService _tradeService;
        private readonly IHttpClientFactory _httpClientFactory;
        public MarketController(BinanceTradeService tradeService, IHttpClientFactory httpClientFactory)
        {
            _tradeService = tradeService;
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// 获取币安合约交易规则和所有交易对信息 (无需签名)
        /// GET: /api/market/exchangeInfo
        /// </summary>
        [HttpGet("exchangeInfo")]
        public async Task<IActionResult> GetExchangeInfo()
        {
            try
            {
                // 调用 Service 获取币安原始 JSON 规则数据
                var result = await _tradeService.GetExchangeInfoAsync();

                // 直接将币安返回的庞大 JSON 透传给前端
                // 注意：这里必须指定 "application/json"，否则前端拿到的是纯文本
                return Content(result, "application/json");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"获取交易规则失败: {ex.Message}" });
            }
        }

        /// <summary>
        /// 获取服务器时间 (用于前端或后台校准时间差)
        /// GET: /api/market/time
        /// </summary>
        [HttpGet("time")]
        public async Task<IActionResult> GetServerTime()
        {
            try
            {
                // 可以顺手提供一个时间同步接口，辅助解决 -1021 时间戳越界问题
                var response = await _tradeService.GetServerTimeAsync();
                return Content(response, "application/json");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"获取服务器时间失败: {ex.Message}" });
            }
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