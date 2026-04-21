// Infrastructure/ExceptionMiddleware.cs
using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace YourApp.Infrastructure
{
    public class ExceptionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ExceptionMiddleware> _logger;

        public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext httpContext)
        {
            try
            {
                // 放行请求到 Controller
                await _next(httpContext);
            }
            catch (Exception ex)
            {
                // 如果 Controller 抛出异常，这里统一拦截
                _logger.LogError(ex, "❌ [系统异常] 发生未处理的错误: {Message}", ex.Message);
                await HandleExceptionAsync(httpContext, ex);
            }
        }

        private static Task HandleExceptionAsync(HttpContext context, Exception exception)
        {
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

            // 统一返回给前端的标准 JSON 格式
            var response = new 
            {
                code = context.Response.StatusCode,
                message = "服务器内部错误，请稍后再试或联系管理员。",
                errorDetail = exception.Message // 生产环境中建议隐藏此字段
            };

            var json = JsonSerializer.Serialize(response);
            return context.Response.WriteAsync(json);
        }
    }
}