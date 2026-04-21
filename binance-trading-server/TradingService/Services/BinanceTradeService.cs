// Services/BinanceTradeService.cs
using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace TradingService.Services
{
    public class BinanceTradeService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<BinanceTradeService> _logger;
        private readonly string _apiSecret;

        public BinanceTradeService(HttpClient httpClient, IConfiguration config, ILogger<BinanceTradeService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _apiSecret = config["BinanceConfig:ApiSecret"];

            _httpClient.BaseAddress = new Uri(config["BinanceConfig:BaseUrl"]);
            _httpClient.DefaultRequestHeaders.Add("X-MBX-APIKEY", config["BinanceConfig:ApiKey"]);
        }

        public async Task<string> PlaceOrderAsync(string symbol, string side, string type, decimal quantity, decimal? price = null)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 拼接币安要求的必须参数
            var queryString = $"symbol={symbol}&side={side}&type={type}&quantity={quantity}&timestamp={timestamp}";

            // 如果是限价单，必须带上价格和 TimeInForce (GTC = 一直有效直到取消)
            if (type == "LIMIT" && price.HasValue)
            {
                queryString += $"&price={price.Value}&timeInForce=GTC";
            }

            // 生成签名
            var signature = GenerateSignature(queryString, _apiSecret);
            var requestUri = $"/fapi/v1/order?{queryString}&signature={signature}";

            _logger.LogInformation("🚀 正在发送订单: {Side} {Quantity} {Symbol} @ {Type}", side, quantity, symbol, type);

            // 发起 HTTP POST 请求
            var response = await _httpClient.PostAsync(requestUri, null);
            var resultJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("❌ 下单失败: {Error}", resultJson);
                throw new Exception($"币安接口返回错误: {resultJson}");
            }

            _logger.LogInformation("✅ 下单成功: {Result}", resultJson);
            return resultJson;
        }

        public async Task<string> ChangeLeverageAsync(string symbol, int leverage)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // 币安修改杠杆参数：symbol, leverage, timestamp
            var queryString = $"symbol={symbol}&leverage={leverage}&timestamp={timestamp}";
            var signature = GenerateSignature(queryString, _apiSecret);

            var requestUri = $"/fapi/v1/leverage?{queryString}&signature={signature}";

            var response = await _httpClient.PostAsync(requestUri, null);
            return await response.Content.ReadAsStringAsync();
        }

        private string GenerateSignature(string message, string secret)
        {
            var keyBytes = Encoding.UTF8.GetBytes(secret);
            using var hmac = new HMACSHA256(keyBytes);
            var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
        }

        // 在 BinanceTradeService.cs 中添加：

        /// <summary>
        /// 获取所有交易对的交易规则 (精度、最小数量等)
        /// </summary>
        public async Task<string> GetExchangeInfoAsync()
        {
            var response = await _httpClient.GetAsync("/fapi/v1/exchangeInfo");
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception("无法获取币安 ExchangeInfo");
            }
            return await response.Content.ReadAsStringAsync();
        }

        /// <summary>
        /// 获取币安服务器的当前时间
        /// </summary>
        public async Task<string> GetServerTimeAsync()
        {
            var response = await _httpClient.GetAsync("/fapi/v1/time");
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception("无法获取币安服务器时间");
            }
            return await response.Content.ReadAsStringAsync();
        }
    }
}