using LinkStudio.Core.Bus;
using System.Text;

namespace LinkStudio.Core.Devices.Virtual
{
    /// <summary>
    /// VirtualMcuDevice 的命令处理部分。
    /// partial 表示它会和其他文件中的 VirtualMcuDevice 合并成同一个类。
    /// </summary>
    public sealed partial class VirtualMcuDevice
    {
        private CancellationTokenSource? _timerCancellation;
        private Task? _timerTask;

        private async Task CommandWorkerAsync()
        {
            // 这个 Worker 是命令队列的唯一读者，因此所有设备操作天然按顺序执行。
            Exception? failure = null;
            try
            {
                await foreach (var command in _commands.Reader.ReadAllAsync())
                {
                    try
                    {
                        switch (command)
                        {
                            case OpenVirtualMcuCommand open:
                                await HandleOpenAsync(open);
                                break;
                            case CloseVirtualMcuCommand close:
                                await HandleCloseAsync(close);
                                break;
                            case SendVirtualMcuCommand send:
                                await HandleSendAsync(send);
                                break;

                            case TickVirtualMcuCommand:
                            {
                                await HandleTickAsync();
                            } break;

                            case AutoVirtualMcuCommand autoSend:
                                await HandleAutoSend(autoSend);
                                break;
                        }
                    }
                    catch (OperationCanceledException) when (GetToken(command).IsCancellationRequested)
                    {
                        // when 确认异常确实来自当前命令的取消信号，避免把程序错误误判为正常取消。
                        CompleteAsCanceled(command, GetToken(command));
                    }
                    catch (Exception error)
                    {
                        // 单条命令失败只填写这条命令的回执，不让整个命令 Worker 退出。
                        CompleteWithException(command, error);
                    }
                }
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }
            finally
            {
                // Worker 整体退出时，设备已无法继续工作，必须停止定时器并更新状态。
                await StopTimerAsync();
                Volatile.Write(ref _isOpen, 0);
                DrainPendingCommands(failure);
            }
        }

        private async Task HandleAutoSend(AutoVirtualMcuCommand autoSend)
        {
            autoSend.CancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposeStarted) == 1)
            {
                autoSend.Completion.TrySetException(new ObjectDisposedException(nameof(VirtualMcuDevice)));
                return;
            }

            if (IsOpen == false)
            {
                autoSend.Completion.TrySetException(new InvalidOperationException("请先打开虚拟设备。"));
                return;
            }

            if (autoSend.enabled)
            {
                if (autoSend.intervalMS == 0)
                {
                    autoSend.Completion.TrySetException(
                        new ArgumentOutOfRangeException(nameof(autoSend.intervalMS), "定时器周期必须大于 0 ms。"));
                    return;
                }

                // 先关再开，避免旧定时器泄漏，同时支持运行中直接修改周期。
                await StopTimerAsync();

                StartTimer(autoSend.intervalMS);
            }
            else
            {
                await StopTimerAsync();
            }

            autoSend.Completion.TrySetResult();
        }

        private async Task HandleOpenAsync(OpenVirtualMcuCommand command)
        {
            command.CancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposeStarted) == 1)
            {
                command.Completion.TrySetException(new ObjectDisposedException(nameof(VirtualMcuDevice)));
                return;
            }

            if (IsOpen)
            {
                command.Completion.TrySetResult();
                return;
            }

            // 先把 ready 包写入发布缓存（且发布阶段不接受外部取消），确认入队后再置 IsOpen = 1，
            // 避免入队中途取消导致“OpenAsync 失败但 IsOpen 却为 true”的状态撕裂。
            await QueuePacketAsync(
                PacketDirection.Rx,
                Encoding.UTF8.GetBytes("Virtual MCU ready"),
                command.Completion,
                command.CancellationToken,
                publishCt: CancellationToken.None);

            Volatile.Write(ref _isOpen, 1);
        }

        private async Task HandleCloseAsync(CloseVirtualMcuCommand command)
        {
            await StopTimerAsync();
            Volatile.Write(ref _isOpen, 0);

            // 空包是冲刷标记：发布 Worker 读到这里时，前面的包已全部进入 Bus。
            await _publishBuffer.Writer.WriteAsync(
                new PublishItem(null, command.Completion, CancellationToken.None));
        }

        private async Task HandleSendAsync(SendVirtualMcuCommand command)
        {
            command.CancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposeStarted) == 1)
            {
                command.Completion.TrySetException(new ObjectDisposedException(nameof(VirtualMcuDevice)));
                return;
            }

            if (!IsOpen)
            {
                command.Completion.TrySetException(
                    new InvalidOperationException("请先打开虚拟设备。"));
                return;
            }

            await QueuePacketAsync(
                PacketDirection.Tx, command.Payload, null, command.CancellationToken);

            // Rx 回显携带回执，它进入 Bus 后 SendAsync 才完成。
            await QueuePacketAsync(
                PacketDirection.Rx, command.Payload, command.Completion, command.CancellationToken);
        }

        private async Task HandleTickAsync()
        {
            if (IsOpen)
            {
                await QueuePacketAsync(
                    PacketDirection.Rx,
                    Encoding.UTF8.GetBytes("Virtual MCU tick"),
                    null,
                    CancellationToken.None);
            }
        }

        private void StartTimer(UInt32 intervalMS )
        {
            // CancellationTokenSource 相当于定时器的“停止开关”，Close 时会调用 Cancel。
            _timerCancellation = new CancellationTokenSource();
            _timerTask = TimerLoopAsync(intervalMS , _timerCancellation.Token);
        }

        private async Task TimerLoopAsync(UInt32 intervalMS, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMS));
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    // 命令队列忙时跳过一次 Tick，避免定时消息不断积压。
                    _commands.Writer.TryWrite(new TickVirtualMcuCommand());
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // CloseAsync 取消定时器属于正常停止。
            }
        }

        private async Task StopTimerAsync()
        {
            var cancellation = _timerCancellation;
            var timerTask = _timerTask;
            _timerCancellation = null;
            _timerTask = null;

            if (cancellation is null || timerTask is null)
            {
                return;
            }

            cancellation.Cancel();
            try
            {
                await timerTask;
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        private static CancellationToken GetToken(VirtualMcuCommand command)
        {
            // Open、Send 和 AutoSend 允许调用方取消；Close 和 Tick 返回永不取消的 None。
            return command switch
            {
                OpenVirtualMcuCommand open => open.CancellationToken,
                SendVirtualMcuCommand send => send.CancellationToken,
                AutoVirtualMcuCommand auto => auto.CancellationToken,
                _ => CancellationToken.None
            };
        }

        private static void CompleteWithException(VirtualMcuCommand command, Exception error)
        {
            switch (command)
            {
                case OpenVirtualMcuCommand open:
                    open.Completion.TrySetException(error);
                    break;
                case CloseVirtualMcuCommand close:
                    close.Completion.TrySetException(error);
                    break;
                case SendVirtualMcuCommand send:
                    send.Completion.TrySetException(error);
                    break;

                case AutoVirtualMcuCommand autoVirtualMcuCommand:
                    autoVirtualMcuCommand.Completion.TrySetException(error);
                    break;
            }
        }

        private static void CompleteAsCanceled(VirtualMcuCommand command, CancellationToken ct = default)
        {
            switch (command)
            {
                case OpenVirtualMcuCommand open:
                    open.Completion.TrySetCanceled(ct);
                    break;
                case CloseVirtualMcuCommand close:
                    close.Completion.TrySetCanceled(ct);
                    break;
                case SendVirtualMcuCommand send:
                    send.Completion.TrySetCanceled(ct);
                    break;

                case AutoVirtualMcuCommand autoVirtualMcuCommand:
                    autoVirtualMcuCommand.Completion.TrySetCanceled(ct);
                    break;
            }
        }

        private void DrainPendingCommands(Exception? failure)
        {
            // Worker 已退出，队列里剩余命令不会再执行，必须结清回执避免调用方永久等待。
            while (_commands.Reader.TryRead(out var command))
            {
                if (failure is null)
                {
                    CompleteAsCanceled(command);
                }
                else
                {
                    CompleteWithException(command, failure);
                }
            }
        }
    }
}
