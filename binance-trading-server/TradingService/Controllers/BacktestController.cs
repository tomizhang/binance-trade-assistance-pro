using Microsoft.AspNetCore.Mvc;
using System;
using System.Linq;
using System.Threading.Tasks;
using TradingTerminal.Services;
using TradingTerminal.Services.Backtest.Models;
using TradingTerminal.Services.Backtest.Services;

namespace TradingService.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class BacktestController : ControllerBase
    {
        private readonly BacktestQueueManager _queueManager;
        private readonly BinanceDataDownloadService _downloadService;

        public BacktestController(BacktestQueueManager queueManager, BinanceDataDownloadService downloadService)
        {
            _queueManager = queueManager;
            _downloadService = downloadService;
        }

        /// <summary>
        /// 获取所有可用的回测策略
        /// GET: /api/backtest/strategies
        /// </summary>
        [HttpGet("strategies")]
        public IActionResult GetStrategies()
        {
            try
            {
                var baseType = typeof(StrategyBase);
                var types = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(s => s.GetTypes())
                    .Where(p => baseType.IsAssignableFrom(p) && p.IsClass && !p.IsAbstract)
                    .Select(t => new
                    {
                        Name = t.Name,
                        DisplayName = GetStrategyDisplayName(t)
                    })
                    .ToList();

                return Ok(types);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"获取策略列表失败: {ex.Message}" });
            }
        }

        private static string GetStrategyDisplayName(Type type)
        {
            var displayNameAttr = (System.ComponentModel.DisplayNameAttribute)Attribute.GetCustomAttribute(type, typeof(System.ComponentModel.DisplayNameAttribute));
            if (displayNameAttr != null)
            {
                return displayNameAttr.DisplayName;
            }

            return type.Name switch
            {
                "VReversalStrategyService" => "VReversal 反转策略 (1m入场+15m判定+1d周期)",
                "VolumeExhaustionReversalStrategyService" => "成交量衰竭反转策略",
                "HighVolStructureStrategyService" => "爆量超跌反弹策略",
                _ => type.Name
            };
        }

        /// <summary>
        /// 提交一个新的回测任务
        /// POST: /api/backtest/run
        /// </summary>
        [HttpPost("run")]
        public IActionResult RunBacktest([FromBody] BacktestConfig config)
        {
            try
            {
                if (config == null)
                {
                    return BadRequest(new { message = "回测配置不能为空" });
                }

                if (string.IsNullOrWhiteSpace(config.Symbol))
                {
                    return BadRequest(new { message = "交易对不能为空，如 BTCUSDT" });
                }

                if (string.IsNullOrWhiteSpace(config.StrategyName))
                {
                    return BadRequest(new { message = "策略名称不能为空" });
                }

                if (config.StartTime >= config.EndTime)
                {
                    return BadRequest(new { message = "开始时间必须早于结束时间" });
                }

                var taskState = _queueManager.EnqueueTask(config);
                return Ok(taskState);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"提交回测任务失败: {ex.Message}" });
            }
        }

        /// <summary>
        /// 暂停指定的回测任务
        /// POST: /api/backtest/pause/{taskId}
        /// </summary>
        [HttpPost("pause/{taskId}")]
        public IActionResult PauseBacktest(string taskId)
        {
            bool success = _queueManager.PauseTask(taskId);
            if (success)
            {
                return Ok(new { success = true, message = $"任务 {taskId} 已成功暂停" });
            }
            return BadRequest(new { success = false, message = $"无法暂停任务 {taskId}，任务可能不存在或未在运行中" });
        }

        /// <summary>
        /// 恢复指定的已暂停回测任务
        /// POST: /api/backtest/resume/{taskId}
        /// </summary>
        [HttpPost("resume/{taskId}")]
        public IActionResult ResumeBacktest(string taskId)
        {
            bool success = _queueManager.ResumeTask(taskId);
            if (success)
            {
                return Ok(new { success = true, message = $"任务 {taskId} 已成功恢复" });
            }
            return BadRequest(new { success = false, message = $"无法恢复任务 {taskId}，任务可能不存在或未处于暂停状态" });
        }

        /// <summary>
        /// 取消指定的回测任务
        /// POST: /api/backtest/cancel/{taskId}
        /// </summary>
        [HttpPost("cancel/{taskId}")]
        public IActionResult CancelBacktest(string taskId)
        {
            bool success = _queueManager.CancelTask(taskId);
            if (success)
            {
                return Ok(new { success = true, message = $"任务 {taskId} 已成功发出取消信号" });
            }
            return BadRequest(new { success = false, message = $"无法取消任务 {taskId}，任务可能已结束或不存在" });
        }

        /// <summary>
        /// 获取所有回测任务的状态列表
        /// GET: /api/backtest/tasks
        /// </summary>
        [HttpGet("tasks")]
        public IActionResult GetTasks()
        {
            var tasks = _queueManager.GetTasks();
            return Ok(tasks);
        }

        /// <summary>
        /// 获取特定回测任务的状态与报告详情
        /// GET: /api/backtest/report/{taskId}
        /// </summary>
        [HttpGet("report/{taskId}")]
        public IActionResult GetReport(string taskId)
        {
            var state = _queueManager.GetTaskState(taskId);
            if (state == null)
            {
                return NotFound(new { message = $"未找到任务 {taskId}" });
            }
            return Ok(state);
        }

        /// <summary>
        /// 预下载/手动缓存历史行情数据
        /// POST: /api/backtest/download-data
        /// </summary>
        [HttpPost("download-data")]
        public async Task<IActionResult> DownloadData([FromQuery] string symbol, [FromQuery] string interval, [FromQuery] long startTimeMs, [FromQuery] long endTimeMs)
        {
            try
            {
                DateTime start = DateTimeOffset.FromUnixTimeMilliseconds(startTimeMs).UtcDateTime;
                DateTime end = DateTimeOffset.FromUnixTimeMilliseconds(endTimeMs).UtcDateTime;

                var data = await _downloadService.GetKlinesAsync(symbol, interval, start, end);
                return Ok(new { success = true, count = data.Count, message = $"成功下载/加载了 {data.Count} 根 K 线" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = $"下载数据失败: {ex.Message}" });
            }
        }
    }
}
