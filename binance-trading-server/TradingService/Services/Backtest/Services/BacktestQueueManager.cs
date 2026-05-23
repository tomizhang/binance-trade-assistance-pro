using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingTerminal.Services.Backtest.Models;

namespace TradingTerminal.Services.Backtest.Services
{
    public class BacktestQueueManager
    {
        private readonly ILogger<BacktestQueueManager> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly ConcurrentDictionary<string, BacktestTaskState> _tasks = new();
        private readonly SemaphoreSlim _concurrencySemaphore = new(2, 2); // 限制并发任务为 2 个

        public BacktestQueueManager(ILogger<BacktestQueueManager> logger, IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        public BacktestTaskState EnqueueTask(BacktestConfig config)
        {
            var taskState = new BacktestTaskState
            {
                TaskId = Guid.NewGuid().ToString("N").Substring(0, 8),
                Config = config,
                Progress = 0,
                Status = BacktestStatus.Queued,
                TradesCount = 0,
                CurrentBalance = config.InitialBalance,
                Cts = new CancellationTokenSource(),
                PauseEvent = new ManualResetEventSlim(true)
            };

            _tasks[taskState.TaskId] = taskState;

            // 后台异步启动任务，在 Semaphore 限制下运行
            _ = Task.Run(() => RunTaskWithSemaphoreAsync(taskState));

            _logger.LogInformation($"📥 [任务队列] 任务 {taskState.TaskId} ({config.Symbol} | {config.StrategyName}) 已加入队列。");
            return taskState;
        }

        private async Task RunTaskWithSemaphoreAsync(BacktestTaskState taskState)
        {
            try
            {
                await _concurrencySemaphore.WaitAsync(taskState.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                taskState.Status = BacktestStatus.Cancelled;
                _logger.LogInformation($"❌ [任务队列] 任务 {taskState.TaskId} 在等待队列时被取消。");
                return;
            }

            try
            {
                if (taskState.Cts.Token.IsCancellationRequested)
                {
                    taskState.Status = BacktestStatus.Cancelled;
                    return;
                }

                taskState.Status = BacktestStatus.Running;
                _logger.LogInformation($"▶️ [任务队列] 任务 {taskState.TaskId} 开始运行...");

                using (var scope = _serviceProvider.CreateScope())
                {
                    var runner = scope.ServiceProvider.GetRequiredService<BacktestRunner>();
                    await runner.RunBacktestAsync(taskState.Config, taskState, taskState.Cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                taskState.Status = BacktestStatus.Cancelled;
                _logger.LogWarning($"⏹️ [任务队列] 任务 {taskState.TaskId} 已被取消。");
            }
            catch (Exception ex)
            {
                taskState.Status = BacktestStatus.Failed;
                taskState.ErrorMessage = ex.Message;
                _logger.LogError(ex, $"💥 [任务队列] 任务 {taskState.TaskId} 运行失败: {ex.Message}");
            }
            finally
            {
                _concurrencySemaphore.Release();
            }
        }

        public bool PauseTask(string taskId)
        {
            if (_tasks.TryGetValue(taskId, out var task))
            {
                if (task.Status == BacktestStatus.Running)
                {
                    task.PauseEvent.Reset();
                    task.Status = BacktestStatus.Paused;
                    _logger.LogInformation($"⏸️ [任务队列] 任务 {taskId} 已暂停。");
                    return true;
                }
            }
            return false;
        }

        public bool ResumeTask(string taskId)
        {
            if (_tasks.TryGetValue(taskId, out var task))
            {
                if (task.Status == BacktestStatus.Paused)
                {
                    task.PauseEvent.Set();
                    task.Status = BacktestStatus.Running;
                    _logger.LogInformation($"▶️ [任务队列] 任务 {taskId} 已恢复运行。");
                    return true;
                }
            }
            return false;
        }

        public bool CancelTask(string taskId)
        {
            if (_tasks.TryGetValue(taskId, out var task))
            {
                if (task.Status == BacktestStatus.Running || task.Status == BacktestStatus.Paused || task.Status == BacktestStatus.Queued)
                {
                    task.Cts.Cancel();
                    task.PauseEvent.Set(); // 确保如果暂停则唤醒以进入 Cancelled 状态
                    task.Status = BacktestStatus.Cancelled;
                    _logger.LogInformation($"⏹️ [任务队列] 任务 {taskId} 发起取消。");
                    return true;
                }
            }
            return false;
        }

        public List<BacktestTaskState> GetTasks()
        {
            return _tasks.Values.OrderByDescending(t => t.CreatedAt).ToList();
        }

        public BacktestTaskState GetTaskState(string taskId)
        {
            _tasks.TryGetValue(taskId, out var state);
            return state;
        }
    }
}
