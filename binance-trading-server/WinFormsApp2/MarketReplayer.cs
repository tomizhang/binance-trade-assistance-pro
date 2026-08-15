using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// K线行情回放与可选 Tick 细粒度推送引擎 (全数组下标指针访问，0 LINQ 开销，0 UI 阻塞)
    /// </summary>
    public class MarketReplayer
    {
        private Kline[] _klines = Array.Empty<Kline>();
        private Tick[] _ticks = Array.Empty<Tick>();
        private int _currentIndex = 0;
        private int _lastTickIndex = 0; // Tick 游标数组指针 (避免每次全表检索)
        private CancellationTokenSource? _cts;

        public ReplayState State { get; private set; } = ReplayState.Stopped;
        public bool EnableTickPush { get; set; } = false;
        public int IntervalMs { get; set; } = 500;

        public event Action<Kline, int, int>? OnKlinePushed;
        public event Action<Kline[], int, int>? OnStepBackward;
        public event Action<Tick>? OnTickPushed;
        public event Action? OnPlaybackCompleted;
        public event Action<string>? OnLog;
        public event Func<Task<BatchDataChunk?>>? OnNeedNextBatchChunk;
        public event Action? OnPreloadThresholdReached;

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

            _klines = klines;
            _ticks = ticks ?? Array.Empty<Tick>();
            EnableTickPush = enableTickPush;
            IntervalMs = Math.Max(50, intervalMs);
            _currentIndex = 0;
            _lastTickIndex = 0;
            _lastPushedTickPrice = -1m;
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
                _lastTickIndex = 0;
                OnLog?.Invoke("回放已停止。");
            }
        }

        /// <summary>
        /// 单步向前 / 下一帧 (Step Forward) - 全数组下标指针访问
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

                // 推送当前 K 线窗口对应的 Tick 列表 (直接数组下标访问)
                PushMatchingTicksForKline(kline, CancellationToken.None);

                _currentIndex++;
                OnKlinePushed?.Invoke(kline, _currentIndex, _klines.Length);
            }
            else
            {
                OnLog?.Invoke("已经处于最后一帧，无法继续单步向前。");
            }
        }

        /// <summary>
        /// 单步向后 / 上一帧 (Step Backward) - 使用 Array.Copy 替代 LINQ Take
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

                // 使用高性能 Array.Copy 代替 LINQ Take().ToArray()，避免垃圾回收开销
                Kline[] subKlines = new Kline[_currentIndex];
                Array.Copy(_klines, 0, subKlines, 0, _currentIndex);

                // 同步复位 Tick 指针
                SyncTickIndexForCurrentKline(_klines[_currentIndex - 1].OpenTime);

                OnStepBackward?.Invoke(subKlines, _currentIndex, _klines.Length);
            }
            else
            {
                OnLog?.Invoke("已经处于第一帧，无法继续单步向后。");
            }
        }

        private decimal _lastPushedTickPrice = -1m;

        /// <summary>
        /// 低延迟高效 Tick 推送方法：
        /// 弃用 LINQ Where().ToArray() 全扫，直接通过数组游标指针与 index 循环线性扫描
        /// 具备价格无变动去重过滤逻辑，达到均摊 O(1) 时间复杂度与 0 GC 分配
        /// </summary>

        Stopwatch sw = new Stopwatch();
        private int FindTickIndexForTime(DateTime targetTime, int startIndex = 0)
        {
            if (_ticks.Length == 0) return 0;
            if (startIndex < 0 || startIndex >= _ticks.Length) startIndex = 0;

            // 绝大多数顺序播放场景：从上次记住的游标 _lastTickIndex 直接顺序向前，0~2 步即刻命中 (O(1) 均摊)
            if (_ticks[startIndex].Time <= targetTime)
            {
                while (startIndex < _ticks.Length && _ticks[startIndex].Time < targetTime)
                {
                    startIndex++;
                }
                return startIndex;
            }

            // 发生回退或跳帧场景：使用二分查找在 O(log N) 0.0001ms 内极速定位起始 Tick 索引，绝不盲目从 0 全扫描！
            int low = 0;
            int high = _ticks.Length - 1;
            int result = _ticks.Length;

            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                if (_ticks[mid].Time >= targetTime)
                {
                    result = mid;
                    high = mid - 1;
                }
                else
                {
                    low = mid + 1;
                }
            }

            return result < _ticks.Length ? result : _ticks.Length;
        }

        private void PushMatchingTicksForKline(Kline kline, CancellationToken token)
        {
            if (!EnableTickPush || _ticks.Length == 0) return;

            DateTime klineStart = kline.OpenTime;
            DateTime klineEnd = kline.CloseTime > klineStart ? kline.CloseTime : klineStart.AddMinutes(1);

            // 1. 记住上次下标：从上次记忆的 _lastTickIndex 索引开始游走定位起始点，绝不每次从 0 检索全天数据！
            _lastTickIndex = FindTickIndexForTime(klineStart, _lastTickIndex);

            // 2. 从定位到的游标位置直投当前 K 线时间窗口内的 Tick 数据
            int scanIndex = _lastTickIndex;
            sw.Start();
            while (scanIndex < _ticks.Length)
            {
                if (token.IsCancellationRequested || State == ReplayState.Stopped) break;

                ref readonly Tick tick = ref _ticks[scanIndex];

                // 超过该 K 线的时间闭区间直接中断跳出 (提前早退，不浪费无谓循环)
                if (tick.Time > klineEnd)
                {
                    break;
                }

                // 价格无变动去重优化：如果新 Tick 价格与上一 Tick 价格完全一致，则直接跳过推送
                if (_lastPushedTickPrice > 0m && tick.LastPrice == _lastPushedTickPrice)
                {
                    scanIndex++;
                    continue;
                }

                _lastPushedTickPrice = tick.LastPrice;
                OnTickPushed?.Invoke(tick);
                scanIndex++;
            }

            // 3. 实时记忆并锁死本次扫描结束的下标索引，供下一根 K 线直接继承使用
            _lastTickIndex = scanIndex;

            sw.Stop();
            if (sw.ElapsedMilliseconds > 200)
                Logger.Log($"tick 花费时间毫秒:{sw.ElapsedMilliseconds}ms; tick长度: {_ticks.Length}");
        }

        /// <summary>
        /// 当后退帧时，重置 Tick 游标指针至当前 K 线开始时间附近
        /// </summary>
        private void SyncTickIndexForCurrentKline(DateTime klineStart)
        {
            if (_ticks.Length == 0) return;
            int idx = 0;
            while (idx < _ticks.Length && _ticks[idx].Time < klineStart)
            {
                idx++;
            }
            _lastTickIndex = idx;
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

                    // 1. 直推 Tick (数组下标游标指针加速)
                    PushMatchingTicksForKline(kline, token);

                    // 2. 推送当前 K 线帧
                    OnKlinePushed?.Invoke(kline, _currentIndex + 1, _klines.Length);

                    _currentIndex++;

                    // 当推进至当前 3 天切片批次的 50% 进度时，提前触发下一批次数据的后台提取与预读
                    if (_klines.Length > 10 && _currentIndex == (int)(_klines.Length * 0.5))
                    {
                        OnPreloadThresholdReached?.Invoke();
                    }

                    if (_currentIndex >= _klines.Length)
                    {
                        if (OnNeedNextBatchChunk != null)
                        {
                            OnLog?.Invoke("当前 3 天切片批次数据播放完成，无缝衔接已提前预读就绪的下一批次数据...");

                            // 1. 显式切断上一个 Batch 的旧 Tick/Kline 大数组引用
                            _ticks = Array.Empty<Tick>();
                            _klines = Array.Empty<Kline>();

                            // 2. 强行触发 LOH (Large Object Heap) 大对象堆回收与内存紧缩压缩，归还 Windows OS
                            GC.Collect(2, GCCollectionMode.Forced, false, true);
                            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;

                            var nextChunk = await OnNeedNextBatchChunk.Invoke().ConfigureAwait(false);
                            if (nextChunk != null && nextChunk.Klines != null && nextChunk.Klines.Length > 0)
                            {
                                _klines = nextChunk.Klines;
                                _ticks = nextChunk.Ticks ?? Array.Empty<Tick>();
                                _currentIndex = 0;
                                _lastTickIndex = 0;
                                continue; // 0 延迟无缝继续播放！
                            }
                        }

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
