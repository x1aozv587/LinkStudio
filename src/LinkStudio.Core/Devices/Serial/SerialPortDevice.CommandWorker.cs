using LinkStudio.Core.Bus;
using Serilog;
using System.IO.Ports;
using System.Text;

namespace LinkStudio.Core.Devices.Serial
{
    /// <summary>
    /// 串口命令处理。打开、关闭、读写都由同一个 Worker 串行执行。
    /// </summary>
    public sealed partial class SerialPortDevice
    {
        private const int MaxReadChunkSize = 4096;

        private async Task CommandWorkerAsync()
        {
            Exception? failure = null;
            try
            {
                await foreach (var command in _commands.Reader.ReadAllAsync())
                {
                    try
                    {
                        switch (command)
                        {
                            case OpenSerialCommand open:
                                HandleOpen(open);
                                break;
                            case CloseSerialCommand close:
                                await HandleCloseAsync(close);
                                break;
                            case SendSerialCommand send:
                                await HandleSendAsync(send);
                                break;
                            case ReadAvailableSerialCommand:
                                await HandleReadAvailableAsync();
                                break;
                        }
                    }
                    catch (OperationCanceledException) when (GetToken(command).IsCancellationRequested)
                    {
                        // 只有命令自己的令牌已取消时，才将这次异常作为取消返回。
                        CompleteAsCanceled(command, GetToken(command));
                    }
                    catch (Exception error)
                    {
                        // 单条命令的失败通过回执返回，Worker 继续处理后续命令。
                        Log.Error(error, "处理串口命令 {Command} 失败", command.GetType().Name);
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
                // 退出前释放端口，并结清无法再执行的命令。
                CloseInternal();
                Volatile.Write(ref _isOpen, 0);
                DrainPendingCommands(failure);
            }
        }

        private void HandleOpen(OpenSerialCommand command)
        {
            command.CancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposeStarted) == 1)
            {
                command.Completion.TrySetException(
                    new ObjectDisposedException(nameof(SerialPortDevice)));
                return;
            }

            // 重复打开时先释放旧实例，只有新端口打开成功后才恢复打开状态。
            CloseInternal();
            Volatile.Write(ref _isOpen, 0);

            var port = new SerialPort
            {
                PortName = _config.PortName,
                BaudRate = _config.BaudRate,
                DataBits = _config.DataBits,
                StopBits = _config.StopBits,
                Parity = _config.Parity,
                Handshake = _config.FlowControl
            };

            // 提前订阅，端口刚打开时发生的可读事件也能投递到命令队列。
            port.DataReceived += HandleDataReceived;
            try
            {
                command.CancellationToken.ThrowIfCancellationRequested();
                port.Open();
                _port = port;
                Volatile.Write(ref _isOpen, 1);
                command.Completion.TrySetResult();
                Log.Information("串口 {PortName} 打开成功", _config.PortName);
            }
            catch
            {
                // 打开失败时清理尚未交给设备管理的端口实例。
                port.DataReceived -= HandleDataReceived;
                port.Dispose();
                throw;
            }
        }

        private async Task HandleCloseAsync(CloseSerialCommand command)
        {
            CloseInternal();
            Volatile.Write(ref _isOpen, 0);

            // 用空包作为冲刷标记，发布 Worker 处理到这里时，前面的包已进入总线。
            await _publishBuffer.Writer.WriteAsync(
                new PublishItem(null, command.Completion, CancellationToken.None));
            Log.Information("串口 {PortName} 已关闭", _config.PortName);
        }

        private async Task HandleSendAsync(SendSerialCommand command)
        {
            command.CancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposeStarted) == 1)
            {
                command.Completion.TrySetException(
                    new ObjectDisposedException(nameof(SerialPortDevice)));
                return;
            }

            var port = _port;
            if (port is null || !port.IsOpen || !IsOpen)
            {
                command.Completion.TrySetException(
                    new InvalidOperationException("串口不存在或尚未打开。"));
                return;
            }

            try
            {
                await port.BaseStream.WriteAsync(command.Payload, command.CancellationToken);

                // 物理写入成功后，TX 记录不能再被外部取消，否则会出现已发送但未记录。
                await QueuePacketAsync(
                    PacketDirection.Tx,
                    command.Payload,
                    command.Completion,
                    CancellationToken.None);
            }
            catch (OperationCanceledException) when (command.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                HandleFault(error);
                throw;
            }
        }

        // 系统事件线程只尝试投递可读信号，避免在回调里读取串口或等待队列空位。
        private void HandleDataReceived(object? sender, SerialDataReceivedEventArgs e)
        {
            _commands.Writer.TryWrite(new ReadAvailableSerialCommand());
        }

        private async Task HandleReadAvailableAsync()
        {
            try
            {
                // 可读信号可能合并，也可能在命令队列满时未能入队。
                // 每次处理时循环读取当前可用数据，并限制单块大小，控制突发流量下的单次分配。
                while (_port is { IsOpen: true } port && port.BytesToRead > 0)
                {
                    var requested = Math.Min(port.BytesToRead, MaxReadChunkSize);
                    var buffer = new byte[requested];
                    var read = port.Read(buffer, 0, requested);
                    if (read <= 0)
                    {
                        break;
                    }

                    // 短读时只发布实际读到的字节，去掉未写入的缓冲区尾部。
                    var data = read == buffer.Length ? buffer : buffer[..read];
                    LogReceivedData(data);
                    await QueuePacketAsync(
                        PacketDirection.Rx,
                        data,
                        null,
                        CancellationToken.None);
                }
            }
            catch (Exception error)
            {
                Log.Error(error, "读取串口 {PortName} 失败", _config.PortName);
                HandleFault(error);
            }
        }

        private static void LogReceivedData(byte[] data)
        {
            if (Log.IsEnabled(Serilog.Events.LogEventLevel.Debug))
            {
                // Debug 只记录短预览，完整转换仅在 Verbose 开启时执行。
                const int MaxPreviewBytes = 64;
                var previewLength = Math.Min(data.Length, MaxPreviewBytes);
                var previewHex = BitConverter.ToString(data, 0, previewLength).Replace("-", " ");

                Log.Debug(
                    "RX {Count} bytes | HEX 前 {Preview} 字节: {Hex}",
                    data.Length,
                    previewLength,
                    previewHex);
            }

            if (Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
            {
                Log.Verbose(
                    "RX {Count} bytes | HEX: {Hex} | String: {Text}",
                    data.Length,
                    BitConverter.ToString(data).Replace("-", " "),
                    Encoding.UTF8.GetString(data));
            }
        }

        private void HandleFault(Exception error)
        {
            // 先更新设备状态，保证订阅方收到事件时读到的是已关闭状态。
            Log.Warning(error, "串口 {PortName} 发生 I/O 故障，关闭连接", _config.PortName);
            CloseInternal();
            Volatile.Write(ref _isOpen, 0);

            try
            {
                Faulted?.Invoke(this, error);
            }
            catch (Exception subscriberError)
            {
                Log.Warning(subscriberError, "Faulted 订阅方异常");
            }
        }

        private void CloseInternal()
        {
            // 先摘除引用，再尝试关闭和释放，避免故障端口继续被后续命令使用。
            var port = _port;
            _port = null;
            if (port is null)
            {
                return;
            }

            port.DataReceived -= HandleDataReceived;
            try
            {
                if (port.IsOpen)
                {
                    port.Close();
                }
            }
            catch (Exception error)
            {
                Log.Warning(error, "关闭串口 {PortName} 失败", _config.PortName);
            }

            try
            {
                port.Dispose();
            }
            catch (Exception error)
            {
                Log.Warning(error, "释放串口 {PortName} 失败", _config.PortName);
            }
        }

        private static CancellationToken GetToken(SerialCommand command) => command switch
        {
            OpenSerialCommand open => open.CancellationToken,
            SendSerialCommand send => send.CancellationToken,
            _ => CancellationToken.None
        };

        private static void CompleteWithException(SerialCommand command, Exception error)
        {
            switch (command)
            {
                case OpenSerialCommand open:
                    open.Completion.TrySetException(error);
                    break;
                case CloseSerialCommand close:
                    close.Completion.TrySetException(error);
                    break;
                case SendSerialCommand send:
                    send.Completion.TrySetException(error);
                    break;
            }
        }

        private static void CompleteAsCanceled(
            SerialCommand command,
            CancellationToken ct = default)
        {
            switch (command)
            {
                case OpenSerialCommand open:
                    open.Completion.TrySetCanceled(ct);
                    break;
                case CloseSerialCommand close:
                    close.Completion.TrySetCanceled(ct);
                    break;
                case SendSerialCommand send:
                    send.Completion.TrySetCanceled(ct);
                    break;
            }
        }

        private void DrainPendingCommands(Exception? failure)
        {
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
