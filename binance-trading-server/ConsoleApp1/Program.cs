// See https://aka.ms/new-console-template for more information
using Binance.Net.Clients;
using Binance.Net.Enums;
using ConsoleApp1;
using CryptoExchange.Net.Interfaces.Clients;
using System.Collections;
using System.Drawing;
using System.Linq.Expressions;
using System.Net;
using System.Runtime.InteropServices;

var words = new List<string>()
{
"wheel"
,"when"
,"where"
,"whip"
,"whisper"
,"wide"
,"width"
,"wife"
,"wild"
,"will"
,"win"
//,"window"
};

for(int i = 0; i < 100; i++)
{
    function();
}


void function()
{
    var list = Bip39ChecksumCalculator.LoadBip39WordList();

    var newList = new List<string>();
    for (int i = 0; i < 11; i++)
    {
        newList.Add(list[new Random().Next(0, list.Length)]);
    }
    var result = Bip39ChecksumCalculator.GetValid12thWords(newList.ToArray());
    newList.Add(result.FirstOrDefault());
    File.AppendAllLines("result.txt",new string[] { string.Join(' ', newList) });
}
return;
var client = new BinanceSocketClient();

client.ClientOptions.Proxy = new CryptoExchange.Net.Objects.ApiProxy("http://127.0.0.1", 10808);

//await client.UsdFuturesApi.ExchangeData.SubscribeToKlineUpdatesAsync("BTCUSDT", KlineInterval.OneMinute, data =>
//{
//    var kline = data.Data;
//    Console.WriteLine($"最新收盘价:{kline.Data.OpenTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")} | {kline.Data.CloseTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")} {kline.Data.ClosePrice}");
//});

var prices = new List<decimal>();
var subscribeResult = await client.UsdFuturesApi.ExchangeData.SubscribeToAggregatedTradeUpdatesAsync("BIRBUSDT", data =>
{
    var tick = data.Data; // 拿到强类型的 Tick 数据

    // 提取核心数据
    decimal price = tick.Price;
    decimal quantity = tick.Quantity;
    DateTime time = tick.TradeTime;

    // 判断买卖方向：BuyerIsMaker 为 true 意味着买方是挂单方，说明这是主动砸盘卖出
    string side = tick.BuyerIsMaker ? "🔴主动卖出" : "🟢主动买入";

    Console.WriteLine($"[{time:HH:mm:ss.fff}] {side} | 价格: {price} | 数量: {quantity}");
    prices.Add(price);
    if (prices.Count % 100 == 0)
    {
        var myHighs = new List<decimal>();
        var myLows = new List<decimal>();
        var peaksBuffer = new List<int>(1024);   // 预分配容量，避免初期扩容
        var valleysBuffer = new List<int>(1024);
        PivotHelper.CalculatePeaksFast(
        CollectionsMarshal.AsSpan(myHighs), // 如果是 .NET 5+，使用 AsSpan() 零拷贝转换
        CollectionsMarshal.AsSpan(myLows),
        peaksBuffer,
        valleysBuffer,
        5, 5
        );
        DrawFunction(prices.Select((x, i) => new PointF(i, (float)x)).ToList(), new List<PointF>());
    }
});

await Task.Delay(-1);


void DrawFunction(List<PointF> dataPoints, List<PointF> hlPoints)
{

    int width = 800;
    int height = 600;
    int padding = 50; // 留出边缘空白，防止线条贴边

    using (Bitmap bitmap = new Bitmap(width, height))
    {
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // 1. 找出数据的最大最小值
            float minX = dataPoints.Min(p => p.X);
            float maxX = dataPoints.Max(p => p.X);
            float minY = dataPoints.Min(p => p.Y);
            float maxY = dataPoints.Max(p => p.Y);

            // 防止除以0的异常（当所有X或Y值相同时）
            float rangeX = Math.Max(maxX - minX, 0.000001f);
            float rangeY = Math.Max(maxY - minY, 0.000001f);

            // 2. 计算可绘制区域的物理大小
            float drawWidth = width - padding * 2;
            float drawHeight = height - padding * 2;

            // 3. 将业务数据点转换为屏幕像素点
            var pixelPoints = new List<PointF>();
            foreach (var pt in dataPoints)
            {
                // X轴映射：(当前值 - 最小值) / 范围 * 屏幕宽度 + 左边距
                float pixelX = padding + ((pt.X - minX) / rangeX) * drawWidth;

                // Y轴映射：注意这里用 height 减去，是为了翻转 Y 轴，让数值大的在上方
                float pixelY = height - padding - ((pt.Y - minY) / rangeY) * drawHeight;

                pixelPoints.Add(new PointF(pixelX, pixelY));
            }

            // 4. 绘制线条
            using (Pen pen = new Pen(Color.Blue, 2))
            {
                g.DrawLines(pen, pixelPoints.ToArray());
            }

            // 5. 在每个数据点上绘制一个小圆点标记
            //foreach (var pt in pixelPoints)
            //{
            //    float radius = 3f;
            //    // DrawEllipse/FillEllipse 的坐标是左上角，所以要偏移半径
            //    g.FillEllipse(Brushes.Red, pt.X - radius, pt.Y - radius, radius * 2, radius * 2);
            //}
        }

        // 保存图片
        bitmap.Save($"lineChart_scaled{Guid.NewGuid()}.png", System.Drawing.Imaging.ImageFormat.Png);
    }
}