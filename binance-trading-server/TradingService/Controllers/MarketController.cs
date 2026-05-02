using Microsoft.AspNetCore.Mvc;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using TradingService.Services;
using TradingTerminal.Services; // 🌟 引入自定义 K 线聚合器所在的命名空间 (请根据实际情况调整)

namespace TradingService.Controllers
{
    [ApiController]
    [Route("api/[controller]")] // 基础路由为 /api/market
    public class MarketController : ControllerBase
    {
        private readonly BinanceTradeService _tradeService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<BinanceTradeService> _logger;
        public MarketController(BinanceTradeService tradeService, IHttpClientFactory httpClientFactory, ILogger<BinanceTradeService> logger )
        {
            _tradeService = tradeService;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
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

            // 🌟 实例化我们的聚合引擎帮助类
            var aggregator = new CustomKlineAggregator();

            // 🌟 1. 偷梁换柱：向币安隐瞒真实意图。如果是 2m，这里会替换成 1m，并计算出安全的 neededLimit
            var (baseInterval, neededLimit) = aggregator.GetBaseHistoryRequestParams(interval, limit);

            // 2. 正常去币安拉取数据（使用替换后的底层参数）
            var url = $"https://fapi.binance.com/fapi/v1/klines?symbol={symbol}&interval={baseInterval}&limit={neededLimit}";

            if (endTime.HasValue)
            {
                url += $"&endTime={endTime.Value}";
            }

            try
            {
                // 作为中继，请求币安数据
                var response = await client.GetAsync(url);
                var rawContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return StatusCode((int)response.StatusCode, new { msg = "币安接口返回错误", error = rawContent });
                }

                // 🌟 3. 瞒天过海：加工并伪装数据。如果是自定义周期，这一步会将 1m 的 rawContent 揉捏成 2m/4m 等周期的 JSON
                string finalJson = aggregator.AggregateHistoricalJson(rawContent, interval);

                // 直接透传处理后的 JSON 数组结构给前端
                return Content(finalJson, "application/json");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { msg = "中继请求币安K线失败", error = ex.Message });
            }
        }
    }
}