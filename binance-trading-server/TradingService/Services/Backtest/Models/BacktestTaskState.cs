using System;
using System.Text.Json.Serialization;
using System.Threading;

namespace TradingTerminal.Services.Backtest.Models
{
    public enum BacktestStatus
    {
        Queued,
        Running,
        Paused,
        Completed,
        Cancelled,
        Failed
    }

    public class BacktestTaskState
    {
        public string TaskId { get; set; }
        public BacktestConfig Config { get; set; }
        public double Progress { get; set; }
        public BacktestStatus Status { get; set; }
        public int TradesCount { get; set; }
        public decimal CurrentBalance { get; set; }
        public string ErrorMessage { get; set; }
        public BacktestReport Report { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [JsonIgnore]
        public CancellationTokenSource Cts { get; set; } = new();

        [JsonIgnore]
        public ManualResetEventSlim PauseEvent { get; set; } = new(true);
    }
}
