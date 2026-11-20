using LinkStudio.Core.Models;
using Serilog;
using System.IO.Ports;
using System.Text;
using System.Threading.Channels;

namespace LinkStudio.Core.Devices
{
    public class SerialPortDevice : ISerialPortDevice
    {
        private SerialPort? _port;

        private readonly Channel<SerialCommand> _commandChannel;
        private readonly Task _workerTask;

        public bool IsOpen => _port?.IsOpen ?? false;

        /// <summary>
        /// 串口收到数据时触发，载荷为本次读到的原始字节。
        /// 在 Worker 线程触发，订阅方若要更新 UI，需自己切回 UI 线程（Dispatcher）。
        /// </summary>
        public event EventHandler<byte[]>? DataReceived;

        /// <summary>
        /// 串口发送数据时触发，载荷为本次发送的原始字节。
        /// 在 Worker 线程触发，订阅方若要更新 UI，需自己切回 UI 线程（Dispatcher）。
        /// </summary>
        public event EventHandler<byte[]>? DataSent;

        /// <summary>
        /// 串口发送/接收时发生 I/O 故障（设备被拔、驱动崩溃、句柄失效等）时触发。
        /// 在 Worker 线程触发，订阅方若要更新 UI，需自己切回 UI 线程（Dispatcher）。
        /// </summary>
        public event EventHandler? Faulted;

        public SerialPortDevice()
        {
            // 这里暂时先使用无限制的队列
            _commandChannel = Channel.CreateUnbounded<SerialCommand>();

            // Task.Run 建立后台执行边界：否则循环内 await 会捕获 UI SynchronizationContext，
            // 串口 Open/Read/Write/Close 会跑回 UI 线程，数据密集时卡界面。
            _workerTask = Task.Run(WorkerLoopAsync);

            Log.Debug("SerialPortDevice 已创建，Worker 线程已启动");
        }

        private async Task WorkerLoopAsync()
        {
            Log.Debug("Worker 线程开始运行");

            try
            {
                await foreach (var command in _commandChannel.Reader.ReadAllAsync())
                {
                    Log.Debug("Worker 收到命令：{Command}", command.GetType().Name);
                    try
                    {
                        switch (command)
                        {
                            case OpenCommand open: { HandleOpen(open); } break;
                            case CloseCommand close: { HandleClose(close); } break;
                            case WriteCommand write: { await HandleWriteAsync(write); } break;
                            case ReadAvailableCommand: { HandleReadAvailable(); } break;
                        }
                    }
                    catch (Exception ex)
                    {
                        // 单条命令失败不能让 Worker 死掉，也不能让调用方永久等待：把异常塞回回执单。
                        Log.Error(ex, "处理命令 {Command} 失败", command.GetType().Name);
                        CompleteCommandWithException(command, ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Serial Worker 异常退出");
            }
            finally
            {
                // Worker 即将退出，队列里剩下的命令永远不会被执行：先结清它们的回执，
                // 再关端口，避免调用方永久卡在 completion.Task 上。
                DrainPendingCommands();

                // Worker 退出前确保端口被关闭释放；Dispose 不再同步等待 Worker。
                CloseInternal();
            }

            Log.Debug("Worker 线程已退出");
        }

        /// <summary>
        /// 把异常塞回命令的回执单（若还没被填）。ReadAvailableCommand 没有回执单，跳过。
        /// </summary>
        private static void CompleteCommandWithException(SerialCommand command, Exception ex)
        {
            switch (command)
            {
                case OpenCommand open: open.Completion.TrySetException(ex); break;
                case CloseCommand close: close.Completion.TrySetException(ex); break;
                case WriteCommand write: write.Completion.TrySetException(ex); break;
            }
        }

        /// <summary>
        /// 把命令的回执填成"已取消"（若还没被填）。ReadAvailableCommand 没有回执单，跳过。
        /// </summary>
        /// <remarks>
        /// 用在"设备正在关闭、这条命令不会被执行"的场景：调用方 await 的就是这张回执单，
        /// 不结清它就会永久等待。
        /// </remarks>
        private static void CompleteCommandAsCanceled(SerialCommand command)
        {
            switch (command)
            {
                case OpenCommand open: open.Completion.TrySetCanceled(); break;
                case CloseCommand close: close.Completion.TrySetCanceled(); break;
                case WriteCommand write: write.Completion.TrySetCanceled(); break;
            }
        }

        /// <summary>
        /// 把队列里剩余命令的回执全部结清（不再执行它们）。
        /// </summary>
        /// <remarks>
        /// 只在 Worker 退出路径上调用：正常退出时队列已被 ReadAllAsync 读空，
        /// 这里主要兜住"Worker 异常退出"的情况——否则那些命令的回执单永远填不上。
        /// </remarks>
        private void DrainPendingCommands()
        {
            var dropped = 0;
            while (_commandChannel.Reader.TryRead(out var pending))
            {
                CompleteCommandAsCanceled(pending);
                dropped++;
            }

            if (dropped > 0)
            {
                Log.Debug("设备关闭，{Count} 条尚未执行的命令已作废", dropped);
            }
        }

        /// <summary>
        /// cancel 现在还没用到 先做占位
        /// </summary>
        /// <param name="config">串口配置</param>
        /// <param name="cancellationToken">暂时没用到</param>
        /// <returns></returns>
        public async Task OpenAsync(SerialPortConfig config, CancellationToken cancellationToken = default)
        {
            // 1. 给这条命令发一张"回执单"
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // 2. 把命令（配置 + 回执单）放进队列
            await _commandChannel.Writer.WriteAsync(new OpenCommand(config, completion));

            // 3. 等 Worker 把回执单填好（成功 SetResult / 失败 SetException）
            await completion.Task;
        }

        /// <summary>
        /// cancel 现在还没用到 先做占位
        /// </summary>
        /// <param name="cancellationToken">暂时没用到</param>
        /// <returns></returns>
        public async Task CloseAsync(CancellationToken cancellationToken = default)
        {
            // 1. 给这条命令发一张"回执单"
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // 2. 把命令（配置 + 回执单）放进队列
            await _commandChannel.Writer.WriteAsync(new CloseCommand(completion));

            // 3. 等 Worker 把回执单填好（成功 SetResult / 失败 SetException）
            await completion.Task;
        }

        /// <summary>
        /// 把数据写进串口。
        /// </summary>
        /// <param name="data">要发送的原始字节。</param>
        /// <param name="cancellationToken">
        /// 取消令牌。取消后：尚未开始的写会被跳过、调用方立即返回；
        /// 已经开始的那一次写无法中途打断，会自行写完（串口的物理限制）。
        /// </param>
        /// <remarks>
        /// 取消时抛 <see cref="OperationCanceledException"/>。此时"数据到底发出去没有"是不确定的：
        /// 可能一个字节都没写，也可能已经写出去了——调用方不能把取消当作"没发"来处理。
        /// </remarks>
        public async Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            // 还没入队就已经被取消：直接失败，不占用队列。
            cancellationToken.ThrowIfCancellationRequested();

            // 1. 给这条命令发一张"回执单"
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // 2. 把命令（数据 + 回执单 + 取消令牌）放进队列
            await _commandChannel.Writer.WriteAsync(
                new WriteCommand(data, completion, cancellationToken),
                cancellationToken);

            // 3. 等 Worker 把回执单填好（成功 SetResult / 失败 SetException）
            //    WaitAsync 让"取消"能立刻返回，不必干等 Worker 处理到这条命令：
            //    Worker 之后取到它时会看到令牌已取消，直接跳过不写。
            await completion.Task.WaitAsync(cancellationToken);
        }

        private void HandleOpen(OpenCommand command)
        {
            try
            {
                // 无论怎样先关闭
                CloseInternal();

                var config = command.Config;

                Log.Debug("开始打开串口 {PortName}，波特率 {BaudRate}", config.PortName, config.BaudRate);

                var port = new SerialPort
                {
                    PortName = config.PortName,
                    BaudRate = config.BaudRate,
                    DataBits = config.DataBits,
                    StopBits = config.StopBits,
                    Parity = config.Parity,
                    Handshake = config.FlowControl,
                };

                // 订阅"有数据到达"：这里只发信号，真正的读取交给 Worker
                port.DataReceived += HandleDataReceived;

                try
                {
                    // 开启串口
                    port.Open();
                }
                catch
                {
                    // 打开失败：退订并释放，避免留下"半成品"串口
                    port.DataReceived -= HandleDataReceived;
                    port.Dispose();
                    throw;
                }

                _port = port;

                // 成功：把回执单填成"成功"
                command.Completion.SetResult();

                Log.Information("串口 {PortName} 打开成功", config.PortName);
            }
            catch (Exception ex)
            {
                // 失败：把异常塞进回执单，OpenAsync 那边的 await 会把它抛出来
                Log.Error(ex, "打开串口 {PortName} 失败", command.Config.PortName);
                command.Completion.SetException(ex);
            }
        }

        /// <summary>
        /// SerialPort.DataReceived 的回调（在系统的后台线程触发）。
        /// 只投递一个"可读"信号，不在这里 Read——真正的读取由 Worker 统一做，
        /// 这样 Open/Close/Read/Write 就全在同一个 Worker 里串行执行了。
        /// </summary>
        private void HandleDataReceived(object? sender, SerialDataReceivedEventArgs e)
        {
            Log.Verbose("DataReceived 触发（串口有数据到达）");
            _commandChannel.Writer.TryWrite(new ReadAvailableCommand());
        }

        private void HandleClose(CloseCommand close)
        {
            Log.Debug("收到关闭命令");

            // 无论怎样先关闭
            CloseInternal();
            close.Completion.SetResult();

            Log.Information("串口已关闭");
        }

        private async Task HandleWriteAsync(WriteCommand write)
        {
            bool success = false;

            // 排队期间被取消：跳过这次写。
            // 这是"停止循环发送"能立刻生效的关键：否则已经入队、还没轮到的数据会继续发出去。
            if (write.CancellationToken.IsCancellationRequested)
            {
                LogWriteSkipped(write);
                write.Completion.TrySetCanceled(write.CancellationToken);
                return;
            }

            try
            {
                // 非空 且 开启了串口
                if (_port == null || _port.IsOpen == false)
                {
                    // 这里算应用没开端口
                    // 返回异常给到执行者就行
                    write.Completion.TrySetException(new InvalidOperationException("串口不存在 or 串口没打开"));
                    return;
                }

                Log.Debug("写入 {Count} 字节", write.Data.Length);

                // 令牌传下去是有效的：Stream.WriteAsync 在真正动手写之前会先检查令牌，
                // 已取消就直接返回一个"已取消"的 Task，一个字节都不会写出去。
                // 但它的作用范围仅限"写之前"：写动作一旦真的开始，串口流无法中途收回已发出的字节。
                await _port.BaseStream.WriteAsync(write.Data, write.CancellationToken);

                // 成功则给出回执。
                // 统一用 TrySet*：取消路径可能已经先把回执填成"已取消"，非 Try 版本会抛 InvalidOperationException。
                write.Completion.TrySetResult();

                // 发送成功
                success = true;
            }
            catch (OperationCanceledException) when (write.CancellationToken.IsCancellationRequested)
            {
                // 取消落在上面那次"写之前"的令牌检查上：WriteAsync 直接返回已取消的 Task，
                // 本次一个字节都没发出去。这里把回执填成"已取消"，
                // 关键是不能走下面的 HandleFault——用户主动停止不该被当成设备故障去关端口。
                Log.Debug("写命令在开始写入前被取消，未发送：{Count} 字节", write.Data.Length);
                write.Completion.TrySetCanceled(write.CancellationToken);
            }
            catch (Exception ex)
            {
                // 失败：把异常塞进回执单，WriteAsync 那边的 await 会把它抛出来
                Log.Error(ex, "写入串口失败");
                write.Completion.TrySetException(ex);
                HandleFault(ex);
            }

            // 避免 发送回执给出去了
            // 但是 事件通知出现异常
            // 导致 数据正常但记录失败
            // 所以 发送完成后单独记录错误
            if ((success == true) && (write.Data.Length > 0))
            {
                RaiseDataSent(write.Data);
            }
        }

        /// <summary>
        /// 记录"因取消而被跳过"的写命令，便于确认"停止"之后确实没有再往外发数据。
        /// 这里比 RX 日志（Debug 只打 64 字节预览）多打一些内容：跳过是低频事件（只在停止时发生），
        /// 而排查"到底哪几条被拦下了"必须看清内容，所以直接在 Debug 级别打出 HEX 与文本。
        /// </summary>
        private static void LogWriteSkipped(WriteCommand write)
        {
            // 只防"超大包把一行日志撑爆"，正常预设长度都会完整打出来。
            const int MaxLoggedBytes = 256;

            var data = write.Data;
            var loggedLength = Math.Min(data.Length, MaxLoggedBytes);
            var truncated = data.Length > loggedLength ? $"（仅前 {loggedLength} 字节）" : string.Empty;

            var hex = loggedLength > 0
                ? BitConverter.ToString(data, 0, loggedLength).Replace("-", " ")
                : "(空)";

            Log.Debug(
                "写命令在排队期间被取消，跳过发送：{Count} 字节{Truncated} | HEX: {Hex} | Text: {Text}",
                data.Length,
                truncated,
                hex,
                loggedLength > 0 ? Encoding.UTF8.GetString(data, 0, loggedLength) : string.Empty);
        }

        private void RaiseDataSent(byte[] data)
        {
            try
            {
                DataSent?.Invoke(this, data);
            }
            catch (Exception ex)
            {
                // 订阅方（ReceiveViewModel）回调出错只记日志，不影响串口写本身
                Log.Warning(ex, "DataSent 订阅方异常");
            }
        }

        /// <summary>
        /// 在 Worker 线程里真正读取数据，再通过 DataReceived 事件送出去。
        /// </summary>
        private void HandleReadAvailable()
        {
            byte[]? data = null;
            int read = 0;

            try
            {
                if (_port == null || _port.IsOpen == false)
                {
                    return;
                }

                int count = _port.BytesToRead;
                if (count <= 0)
                {
                    return;
                }

                byte[] buffer = new byte[count];
                read = _port.Read(buffer, 0, count);

                if (read > 0)
                {
                    data = new byte[read];
                    Array.Copy(buffer, data, read);
                }
            }
            catch (Exception ex)
            {
                // 读取失败（如设备被拔）：记日志并触发故障处理
                Log.Error(ex, "读取串口失败");
                HandleFault(ex);
            }

            if ((data != null) && (data.Length > 0))
            {
                RecvDataSaveToLog(data, read);
                RaiseDataRecv(data);
            }
        }

        private void RecvDataSaveToLog(byte[] data, int count)
        {
            // Debug 只记"长度 + 前 64 字节 HEX 预览"：完整 HEX/全文的字符串拼接成本很高，
            // 而且发生在 Worker 线程上，会拖慢后续 Read/Open/Close/Write 命令的处理。
            // 完整内容本就在表格的 HEX/ASCII 两列里，日志不再重复拼一遍。
            const int MaxPreviewBytes = 64;
            int previewLength = Math.Min(data.Length, MaxPreviewBytes);
            string previewHex = BitConverter.ToString(data, 0, previewLength).Replace("-", " ");

            Log.Debug("RX {Count} bytes | HEX 前 {Preview} 字节: {Hex}", count, previewLength, previewHex);

            // 只有 Verbose 级别才拼完整 HEX/全文（默认关）。
            // 先判断级别再拼，否则即使 Verbose 关闭，下面两行也会被提前求值、白拼一遍。
            if (Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
            {
                Log.Verbose(
                    "RX {Count} bytes | HEX: {Hex} | String: {Text}",
                    count,
                    BitConverter.ToString(data).Replace("-", " "),
                    Encoding.UTF8.GetString(data));
            }
        }

        private void RaiseDataRecv(byte[] data)
        {
            try
            {
                DataReceived?.Invoke(this, data);
            }
            catch (Exception ex)
            {
                // 订阅方（ReceiveViewModel）回调出错只记日志，不影响读取与 Worker 本身
                Log.Warning(ex, "DataReceived 订阅方异常");
            }
        }

        private void HandleFault(Exception ex)
        {
            // I/O 故障：先关端口，再广播；订阅方异常不能影响关闭
            Log.Warning(ex, "串口发生 I/O 故障，关闭连接");
            CloseInternal();
            RaiseFaulted();
        }

        private void RaiseFaulted()
        {
            try
            {
                Faulted?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Faulted 订阅方异常");
            }
        }

        /// <summary>
        /// 关闭并释放当前端口；没打开时调用是安全的。
        /// Open（重复打开前）和 Close 都要用它，所以抽成单独方法避免重复。
        /// </summary>
        private void CloseInternal()
        {
            // 先把引用摘到局部变量并清空字段：这样即使下面 Close/Dispose 抛异常，
            // _port 也不会停留在"已经失效的端口对象"上（否则 IsOpen 和写路径的行为不确定）。
            var port = _port;
            _port = null;

            // 没打开过，什么都不用做
            if (port is null)
            {
                return;
            }

            // 先退订事件，避免关闭后还收到回调
            port.DataReceived -= HandleDataReceived;

            try
            {
                if (port.IsOpen)
                {
                    port.Close();
                }
            }
            catch (Exception ex)
            {
                // 设备可能已被拔出，连 Close 都会失败；记录日志即可，不影响后续清理
                Log.Warning(ex, "关闭串口失败");
            }
            finally
            {
                // 无论如何都要释放底层句柄；字段已在开头清空，这里再抛异常也不会留下失效引用
                port.Dispose();
            }
        }

        /// <summary>
        /// 释放资源：通知队列不再接收新命令，不阻塞调用线程。
        /// </summary>
        /// <remarks>
        /// 关闭策略是"优雅排空"：已排队的命令照常执行完，Worker 处理完剩余命令后自行退出，
        /// 并在退出前关闭端口。用户在关闭应用前按下的发送已经入队，不该被静默丢弃。
        /// （循环发送的排队写不靠这里兜：停止循环时它们已经被各自的取消令牌跳过。）
        /// 本方法不等待 Worker——确定性等待属于异步关闭链的职责。重复调用是安全的。
        /// </remarks>
        public void Dispose()
        {
            Log.Debug("开始释放 SerialPortDevice");

            //  通知队列"不再有新命令"（幂等：已关闭时 TryComplete 返回 false，无副作用）
            _commandChannel.Writer.TryComplete();

            GC.SuppressFinalize(this);

            Log.Debug("SerialPortDevice 已释放");
        }
    }
}
