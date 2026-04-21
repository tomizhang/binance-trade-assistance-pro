// Services/BinanceAccountService.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public class BinanceAccountService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _apiSecret;

    public BinanceAccountService(HttpClient httpClient, IConfiguration config)
    {
        _httpClient = httpClient;
        _apiKey = config["BinanceConfig:ApiKey"];
        _apiSecret = config["BinanceConfig:ApiSecret"];
        _httpClient.BaseAddress = new Uri(config["BinanceConfig:BaseUrl"]);
        _httpClient.DefaultRequestHeaders.Add("X-MBX-APIKEY", _apiKey);
    }

    // 🌟 获取账户余额 (初始同步用)
    public async Task<string> GetAccountBalanceAsync()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var query = $"timestamp={timestamp}";
        var signature = GenerateSignature(query);
        return await _httpClient.GetStringAsync($"/fapi/v2/account?{query}&signature={signature}");
    }

    // 🌟 申请 ListenKey (开启 WebSocket 门票)
    public async Task<string> CreateListenKeyAsync()
    {
        var response = await _httpClient.PostAsync("/fapi/v1/listenKey", null);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("listenKey").GetString();
    }

    // 🌟 延长 ListenKey 有效期 (续命)
    public async Task KeepAliveListenKeyAsync()
    {
        await _httpClient.PutAsync("/fapi/v1/listenKey", null);
    }

    private string GenerateSignature(string message)
    {
        var keyBytes = Encoding.UTF8.GetBytes(_apiSecret);
        using var hmac = new HMACSHA256(keyBytes);
        return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))).Replace("-", "").ToLower();
    }
}