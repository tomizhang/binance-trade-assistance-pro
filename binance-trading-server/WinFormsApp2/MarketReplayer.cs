using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WinFormsApp2
{
    public enum ReplayState
    {
        Stopped,
        Playing,
        Paused
    }

    /// <summary>
    /// K线行情回放与可选 Tick 细粒度推送引擎 (支持单步向前/向后，纯后台解耦，0 UI 阻塞)
    /// </summary>
    public class MarketReplayer
    {
        private Kline[] _klines = Array.Empty<Kline>();
        private Tick[] _ticks = Array.Empty<Tick>();
        private int _currentIndex = 0;
        private CancellationTokenSource? _cts;

        public ReplayState State { get; private set; } = ReplayState.Stopped;
        public bool EnableTickPush { get; set; } = false;
        public int IntervalMs { get; set; } = 500;

        public event Action<Kline, int, int>? OnKlinePushed;
        public event Action<Kline[], int, int>? OnStepBackward;
        public event Action<Tick>? OnTickPushed;
        public event Action? OnPlaybackCompleted;
        public event Action<string>? OnLog;

        /// <summary>
        /// 启动/开始回放
        /// </summary>
        public void StartPlayback(Kline[] klines, Tick[] ticks, bool enableTickPush, int intervalMs)
        {
            StopPlayback();

            if (klines == null || klines.Length == 0)
            {
                OnLog?.Invoke("没有可供回放的 K线数据。");
                return;
            }

            _klines = klines.OrderBy(k => k.OpenTime).ToArray();
            _ticks = ticks != null ? ticks.OrderBy(t => t.Time).ToArray() : Array.Empty<Tick>();
            EnableTickPush = enableTickPush;
            IntervalMs = Math.Max(50, intervalMs);
            _currentIndex = 0;
            State = ReplayState.Playing;

            _cts = new CancellationTokenSource();
            Task.Run(() => ReplayLoopAsync(_cts.Token));
        }

        /// <summary>
        /// 暂停回放
        /// </summary>
        public void PausePlayback()
        {
            if (State == ReplayState.Playing)
            {
                State = ReplayState.Paused;
                OnLog?.Invoke("回放已暂停。");
            }
        }

        /// <summary>
        /// 恢复播放
        /// </summary>
        public void ResumePlayback()
        {
            if (State == ReplayState.Paused)
            {
                State = ReplayState.Playing;
                OnLog?.Invoke("恢复回放中...");
            }
        }

        /// <summary>
        /// 停止回放
        /// </summary>
        public void StopPlayback()
        {
            if (State != ReplayState.Stopped)
            {
                _cts?.Cancel();
                State = ReplayState.Stopped;
                _currentIndex = 0;
                OnLog?.Invoke("回放已停止。");
            }
        }

        /// <summary>
        /// 单步向前 / 下一帧 (Step Forward)
        /// </summary>
        public void StepForward()
        {
            if (_klines == null || _klines.Length == 0) return;

            if (State == ReplayState.Playing)
            {
                PausePlayback();
            }
            else if (State == ReplayState.Stopped)
            {
                State = ReplayState.Paused;
            }

            if (_currentIndex < _klines.Length)
            {
                Kline kline = _klines[_currentIndex];

                if (EnableTickPush && _ticks.Length > 0)
                {
                    DateTime klineStart = kline.OpenTime;
                    DateTime klineEnd = kline.CloseTime > klineStart ? kline.CloseTime : klineStart.AddMinutes(1);
                    var matchingTicks = _ticks.Where(t => t.Time >= klineStart && t.Time <= klineEnd).ToArray();
                    foreach (var tick in matchingTicks)
                    {
                        OnTickPushed?.Invoke(tick);
                    }
                }

                _currentIndex++;
                OnKlinePushed?.Invoke(kline, _currentIndex, _klines.Length);
            }
            else
            {
                OnLog?.Invoke("已经处于最后一帧，无法继续单步向前。");
            }
        }

        /// <summary>
        /// 单步向后 / 上一帧 (Step Backward)
        /// </summary>
        public void StepBackward()
        {
            if (_klines == null || _klines.Length == 0) return;

            if (State == ReplayState.Playing)
            {
                PausePlayback();
            }
            else if (State == ReplayState.Stopped)
            {
                State = ReplayState.Paused;
            }

            if (_currentIndex > 1)
            {
                _currentIndex--;
                var subKlines = _klines.Take(_currentIndex).ToArray();
                OnStepBackward?.Invoke(subKlines, _currentIndex, _klines.Length);
            }
            else
            {
                OnLog?.Invoke("已经处于第一帧，无法继续单步向后。");
            }
        }

        private async Task ReplayLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _currentIndex < _klines.Length)
            {
                if (State == ReplayState.Paused)
                {
                    try
                    {
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    continue;
                }

                if (State == ReplayState.Playing)
                {
                    Kline kline = _klines[_currentIndex];

                    // 1. 如果开启了 Tick 细粒度推送，匹配并推送属于当前 K 线时间窗口内的 Tick 成交
                    if (EnableTickPush && _ticks.Length > 0)
                    {
                        DateTime klineStart = kline.OpenTime;
                        DateTime klineEnd = kline.CloseTime > klineStart ? kline.CloseTime : klineStart.AddMinutes(1);

                        var matchingTicks = _ticks.Where(t => t.Time >= klineStart && t.Time <= klineEnd).ToArray();
                        
                        foreach (var tick in matchingTicks)
                        {
                            if (token.IsCancellationRequested || State == ReplayState.Stopped) break;
                            OnTickPushed?.Invoke(tick);
                        }
                    }

                    // 2. 推送当前 K 线数据帧
                    OnKlinePushed?.Invoke(kline, _currentIndex + 1, _klines.Length);

                    _currentIndex++;

                    if (_currentIndex >= _klines.Length)
                    {
                        State = ReplayState.Stopped;
                        OnPlaybackCompleted?.Invoke();
                        break;
                    }

                    try
                    {
                        await Task.Delay(IntervalMs, token).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }
            }
        }
    }
}
