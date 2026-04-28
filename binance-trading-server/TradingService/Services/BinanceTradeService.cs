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

        // 🌟 终极版下单引擎：自动识别参数名变更 (stopPrice -> triggerPrice)
        // 🌟 标准版下单引擎：老老实实走 fapi/v1/order，把丢失的 stopPrice 补上！
        // 🌟 1. 终极发单引擎：自动切换 algoType 与 triggerPrice
        public async Task<string> PlaceOrderAsync(string symbol, string side, string type, decimal quantity, decimal? price = null, decimal? stopPrice = null, bool? reduceOnly = null)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 判断是否为条件单
            bool isAlgo = type == "STOP_MARKET" || type == "TAKE_PROFIT_MARKET" || type == "STOP" || type == "TAKE_PROFIT" || type == "TRAILING_STOP_MARKET";

            var queryParams = new List<string>();
            queryParams.Add($"symbol={symbol.ToUpper()}");
            queryParams.Add($"side={side}");
            queryParams.Add($"type={type}");
            queryParams.Add($"quantity={quantity}");

            if (price.HasValue) queryParams.Add($"price={price.Value}");
            if (reduceOnly.HasValue && reduceOnly.Value) queryParams.Add("reduceOnly=true");

            if (isAlgo)
            {
                // 条件单专有硬性规定
                queryParams.Add("algoType=CONDITIONAL");
                if (stopPrice.HasValue) queryParams.Add($"triggerPrice={stopPrice.Value}");
            }
            else
            {
                // 普通单专有规定
                if (stopPrice.HasValue) queryParams.Add($"stopPrice={stopPrice.Value}");
                if (type == "LIMIT") queryParams.Add("timeInForce=GTC");
            }

            queryParams.Add($"timestamp={timestamp}");

            var queryString = string.Join("&", queryParams);
            var signature = GenerateSignature(queryString, _apiSecret);

            // 路由分发
            var endpoint = isAlgo ? "/fapi/v1/algoOrder" : "/fapi/v1/order";
            var requestUri = $"{endpoint}?{queryString}&signature={signature}";

            _logger.LogInformation("🚀 发送订单 [{Endpoint}]: {Side} {Quantity} {Symbol} @ {Type}", endpoint, side, quantity, symbol, type);

            var response = await _httpClient.PostAsync(requestUri, null);
            var resultJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"币安接口拒绝: {resultJson}");

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

        // 申请 ListenKey
        public async Task<string> CreateListenKeyAsync()
        {
            var response = await _httpClient.PostAsync("/fapi/v1/listenKey", null);
            // 注意这里需要带上 X-MBX-APIKEY 请求头，但不需要 Signature 签名
            var result = await response.Content.ReadAsStringAsync();
            return result; // 返回 {"listenKey": "pqia91ma19a5s61cv6a81va65sdf19v8a65a1a5s61cv6a81v"}
        }

        // 延长 ListenKey 有效期 (保活)
        public async Task KeepAliveListenKeyAsync()
        {
            await _httpClient.PutAsync("/fapi/v1/listenKey", null);
        }

        // 获取历史成交记录
        public async Task<string> GetUserTradesAsync(string symbol, int limit = 50)
        {
            // 币安要求强制带有时间戳
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 拼接请求参数
            var queryString = $"symbol={symbol}&limit={limit}&timestamp={timestamp}";

            // 生成签名 (假设你已经有了签名方法，和下单接口一样)
            var signature = GenerateSignature(queryString, _apiSecret);

            // 组合最终 URL
            var requestUrl = $"/fapi/v1/userTrades?{queryString}&signature={signature}";

            // 发起 GET 请求 (HttpClient 应该已经配置了 X-MBX-APIKEY 请求头)
            var response = await _httpClient.GetAsync(requestUrl);
            var result = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"获取历史记录失败: {result}");
            }

            return result;
        }

        // 获取用户持仓风险（包含杠杆和强平价）
        public async Task<string> GetPositionRiskAsync(string symbol = null)
        {
            // 1. 准备基础参数
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var queryString = $"timestamp={timestamp}";

            // 如果传了 symbol，则只查特定币种，否则查全量
            if (!string.IsNullOrEmpty(symbol))
            {
                queryString = $"symbol={symbol.ToUpper()}&{queryString}";
            }

            // 2. 生成签名 (使用你现有的 CreateSignature 方法)
            var signature = GenerateSignature(queryString, _apiSecret);

            // 3. 组合 URL
            var requestUrl = $"/fapi/v2/positionRisk?{queryString}&signature={signature}";

            // 4. 发送请求 (HttpClient 需配置好 X-MBX-APIKEY 请求头)
            var response = await _httpClient.GetAsync(requestUrl);
            var result = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"[币安API错误] 获取风险配置失败: {result}");
            }

            return result;
        }

        // 获取账户全面信息（包含余额）
        public async Task<string> GetAccountInfoAsync()
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var queryString = $"timestamp={timestamp}";
            var signature = GenerateSignature(queryString, _apiSecret);
            var requestUrl = $"/fapi/v2/account?{queryString}&signature={signature}";

            var response = await _httpClient.GetAsync(requestUrl);
            var result = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"获取账户信息失败: {result}");
            }
            return result;
        }

        // 🌟 2. 终极查单引擎：双通道并发拉取并合并
        public async Task<string> GetOpenOrdersAsync(string symbol = null)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var queryParams = $"timestamp={timestamp}";
            if (!string.IsNullOrEmpty(symbol)) queryParams = $"symbol={symbol.ToUpper()}&{queryParams}";

            // 并发请求两个接口
            var sig1 = GenerateSignature(queryParams, _apiSecret);
            var task1 = _httpClient.GetAsync($"/fapi/v1/openOrders?{queryParams}&signature={sig1}");

            var sig2 = GenerateSignature(queryParams, _apiSecret);
            var task2 = _httpClient.GetAsync($"/fapi/v1/openAlgoOrders?{queryParams}&signature={sig2}");

            await Task.WhenAll(task1, task2);

            var res1 = await task1.Result.Content.ReadAsStringAsync();
            var res2 = await task2.Result.Content.ReadAsStringAsync();

            bool hasNormal = task1.Result.IsSuccessStatusCode && res1.Trim().Length > 2;
            bool hasAlgo = task2.Result.IsSuccessStatusCode && res2.Trim().Length > 2;

            if (hasNormal && hasAlgo)
                return "[" + res1.Trim().Trim('[', ']') + "," + res2.Trim().Trim('[', ']') + "]";
            else if (hasNormal)
                return res1;
            else if (hasAlgo)
                return res2;

            return "[]";
        }

        // 🌟 3. 终极智能撤单：静默双重尝试
        public async Task<string> CancelOrderAsync(string symbol, string orderId)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 尝试 1：当作算法单撤销 (algoId)
            var algoQuery = $"symbol={symbol.ToUpper()}&algoId={orderId}&timestamp={timestamp}";
            var algoSig = GenerateSignature(algoQuery, _apiSecret);
            var algoRes = await _httpClient.DeleteAsync($"/fapi/v1/algoOrder?{algoQuery}&signature={algoSig}");
            if (algoRes.IsSuccessStatusCode) return await algoRes.Content.ReadAsStringAsync();

            // 尝试 2：当作普通单撤销 (orderId)
            var normalQuery = $"symbol={symbol.ToUpper()}&orderId={orderId}&timestamp={timestamp}";
            var normalSig = GenerateSignature(normalQuery, _apiSecret);
            var normalRes = await _httpClient.DeleteAsync($"/fapi/v1/order?{normalQuery}&signature={normalSig}");
            var normalJson = await normalRes.Content.ReadAsStringAsync();

            if (normalRes.IsSuccessStatusCode) return normalJson;

            throw new Exception($"撤单失败。正常单响应: {normalJson}");
        }

    }
}