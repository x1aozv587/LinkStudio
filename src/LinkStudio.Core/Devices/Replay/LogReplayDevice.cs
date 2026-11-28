using LinkStudio.Core.Bus;

namespace LinkStudio.Core.Devices.Replay
{
    /// <summary>
    /// 基于历史日志文件的只读回放数据源（实现 ICommunicationDevice）。
    /// 回放帧直接发布到 RawDataBus，无缝复用与真实串口完全一致的协议解析管线与可视化画板。
    /// </summary>
    public sealed class LogReplayDevice : ICommunicationDevice
    {
        private readonly IRawDataBus _dataBus;
        private readonly IReadOnlyList<ReplayFrame> _frames;
        private readonly object _stateGate = new();
        private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;
        private int _isOpen;
        private int _disposed;
        private bool _isPlaying;
        private bool _loop;
        private double _speed = 1.0;
        private int _currentIndex;
        private uint _seqNo;

        public LogReplayDevice(IRawDataBus dataBus, string displayName, IReadOnlyList<ReplayFrame> frames)
        {
            _dataBus = dataBus ?? throw new ArgumentNullException(nameof(dataBus));
            Name = string.IsNullOrWhiteSpace(displayName) ? "日志回放" : displayName.Trim();
            _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        }

        public string Id => "log-replay-1";

        public string Name { get; }

        public ChannelKind Kind => ChannelKind.LogReplay;

        public bool IsOpen => Volatile.Read(ref _isOpen) == 1;

        public int TotalFrames => _frames.Count;

        public int CurrentIndex
        {
            get { lock (_stateGate) { return _currentIndex; } }
        }

        public bool IsPlaying
        {
            get { lock (_stateGate) { return _isPlaying; } }
        }

        public double Speed
        {
            get { lock (_stateGate) { return _speed; } }
        }

        public bool Loop
        {
            get { lock (_stateGate) { return _loop; } }
            set { lock (_stateGate) { _loop = value; } }
        }

        public event Action? ProgressChanged;

        public Task OpenAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            ct.ThrowIfCancellationRequested();

            if (Interlocked.CompareExchange(ref _isOpen, 1, 0) == 0)
            {
                _loopCts = new CancellationTokenSource();
                _loopTask = Task.Run(() => PlaybackLoopAsync(_loopCts.Token));
            }

            return Task.CompletedTask;
        }

        public async Task CloseAsync()
        {
            if (Interlocked.Exchange(ref _isOpen, 0) == 0)
            {
                return;
            }

            lock (_stateGate)
            {
                _isPlaying = false;
            }

            var cts = _loopCts;
            var task = _loopTask;
            _loopCts = null;
            _loopTask = null;

            if (cts is not null)
            {
                cts.Cancel();
                SignalLoop();
            }

            if (task is not null)
            {
                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                {
                    // 关闭回放取消循环属于预期行为。
                }
            }

            cts?.Dispose();
            ProgressChanged?.Invoke();
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            throw new NotSupportedException("日志回放为只读数据源，不支持发送数据。");
        }

        public void Play()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (!IsOpen || _frames.Count == 0)
            {
                return;
            }

            lock (_stateGate)
            {
                // 如果已经播到末尾，再次点击播放时自动从头开始。
                if (_currentIndex >= _frames.Count)
                {
                    _currentIndex = 0;
                }

                _isPlaying = true;
            }

            SignalLoop();
            ProgressChanged?.Invoke();
        }

        public void Pause()
        {
            lock (_stateGate)
            {
                _isPlaying = false;
            }

            SignalLoop();
            ProgressChanged?.Invoke();
        }

        public void Seek(int frameIndex)
        {
            lock (_stateGate)
            {
                _currentIndex = _frames.Count == 0
                    ? 0
                    : Math.Clamp(frameIndex, 0, _frames.Count - 1);
            }

            SignalLoop();
            ProgressChanged?.Invoke();
        }

        public void SetSpeed(double speed)
        {
            // 0 表示全速瞬间回放；正数表示倍速（0.1x ~ 50x）。
            var normalized = speed <= 0 ? 0.0 : Math.Clamp(speed, 0.1, 50.0);
            lock (_stateGate)
            {
                _speed = normalized;
            }

            SignalLoop();
            ProgressChanged?.Invoke();
        }

        private void SignalLoop()
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _signal.Release();
            }
        }

        private async Task PlaybackLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                ReplayFrame? frame = null;
                TimeSpan delay = TimeSpan.Zero;
                var shouldPauseWait = false;

                lock (_stateGate)
                {
                    if (!_isPlaying || _frames.Count == 0)
                    {
                        shouldPauseWait = true;
                    }
                    else if (_currentIndex >= _frames.Count)
                    {
                        if (_loop)
                        {
                            _currentIndex = 0;
                        }
                        else
                        {
                            _isPlaying = false;
                            shouldPauseWait = true;
                        }
                    }

                    if (!shouldPauseWait)
                    {
                        var idx = _currentIndex;
                        frame = _frames[idx];
                        if (idx > 0 && _speed > 0)
                        {
                            var rawGap = frame.Offset - _frames[idx - 1].Offset;
                            if (rawGap > TimeSpan.Zero)
                            {
                                delay = TimeSpan.FromMilliseconds(rawGap.TotalMilliseconds / _speed);
                            }
                        }

                        _currentIndex = idx + 1;
                    }
                }

                if (shouldPauseWait)
                {
                    ProgressChanged?.Invoke();
                    await _signal.WaitAsync(ct);
                    continue;
                }

                if (delay > TimeSpan.Zero)
                {
                    // 用 SemaphoreSlim 计时等待替代纯 Task.Delay，这样暂停/调速/拖动进度条能毫秒级响应。
                    await _signal.WaitAsync(delay, ct);
                    lock (_stateGate)
                    {
                        if (!_isPlaying)
                        {
                            // 刚在等待期间被暂停，把序号回退一格避免丢帧。
                            _currentIndex = Math.Max(0, _currentIndex - 1);
                            continue;
                        }
                    }
                }

                if (frame is not null)
                {
                    var packet = new RawDataPacket(
                        ChannelId: Id,
                        ChannelKind: Kind,
                        Direction: frame.Direction,
                        TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        SeqNo: ++_seqNo,
                        Payload: frame.Payload);

                    await _dataBus.PublishAsync(packet, ct);
                    ProgressChanged?.Invoke();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await CloseAsync();
            _signal.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
