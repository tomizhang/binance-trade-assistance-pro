using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using System;
using TradingService.BackgroundWorkers;
using TradingService.Services;
using TradingTerminal.Hubs;
using TradingTerminal.Services;
using YourApp.Infrastructure; // 记得替换为你的实际命名空间

// ==========================================
// 1. 初始化 Serilog 日志记录器
// ==========================================
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning) // 过滤掉微软底层的一堆废话日志
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("Logs/trading_log_.txt",
        rollingInterval: RollingInterval.Day, // 每天自动生成一个新的日志文件
        retainedFileCountLimit: 30)           // 最多保留 30 天的日志
    .CreateLogger();

try
{
    Log.Information("🚀 交易系统后端服务正在启动...");

    var builder = WebApplication.CreateBuilder(args);

    // 🌟 将 Serilog 接管为系统默认日志
    builder.Host.UseSerilog();

    // ==========================================
    // 2. 注册依赖注入服务 (DI)
    // ==========================================
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();


    builder.Services.AddSignalR();
    builder.Services.AddHttpClient<BinanceAccountService>();
    builder.Services.AddHostedService<BinanceUserDataWorker>();
    builder.Services.AddHttpClient<BinanceTradeService>();
    // 🌟 1. 先注册为单例，让 Controller 可以注入它
    //builder.Services.AddSingleton<BinanceWsApiService>();

    // 🌟 2. 再将其作为后台宿主服务启动，触发 ExecuteAsync
    //builder.Services.AddHostedService(provider => provider.GetRequiredService<BinanceWsApiService>());

    // 🌟 步骤 1：先把引擎注册为单例，这样 MarketHub 才能在构造函数里拿到它！
    builder.Services.AddSingleton<TradingTerminal.Services.BinanceWebSocketService>();

    // 🌟 步骤 2：再把这个单例引擎作为后台任务跑起来
    builder.Services.AddHostedService(provider =>
        provider.GetRequiredService<TradingTerminal.Services.BinanceWebSocketService>());
    // 2. 注册后台 WebSocket 转发服务
    //builder.Services.AddHostedService<BinanceDataForwarderService>();
    // 注册跨域策略 (开发阶段允许前端 Vue 请求)
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowVueFrontend",
                policy => policy
                    // 🌟 核心修改：允许任意来源，但使用安全的 SetIsOriginAllowed 替代 AllowAnyOrigin
                    .SetIsOriginAllowed(origin => true)
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    // 🌟 核心修改：SignalR 跨域协商必须加上这一句！
                    .AllowCredentials());
    });

    // 预留位置：后续在这里注册 HttpClient 和 SignalR
    // builder.Services.AddHttpClient<BinanceTradeService>();

    var app = builder.Build();

    // ==========================================
    // 3. 配置 HTTP 请求管道 (Middleware)
    // ==========================================

    // 🌟 注册我们自己写的全局异常拦截器 (放在最前面，保护后续所有流程)
    app.UseMiddleware<ExceptionMiddleware>();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.MapHub<AccountHub>("/hubs/account");
    // 3. 映射 Hub 路由
    app.MapHub<MarketHub>("/hubs/market");

    app.UseCors("AllowVueFrontend");
    app.UseAuthorization();
    app.MapControllers();

    // 启动应用
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "💀 系统启动失败，发生致命错误！");
}
finally
{
    // 确保在程序退出前，将内存中的日志全部刷入硬盘
    Log.CloseAndFlush();
}