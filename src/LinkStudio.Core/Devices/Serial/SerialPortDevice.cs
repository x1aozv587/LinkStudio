using LinkStudio.Core.Bus;
using LinkStudio.Core.Models;
using System.IO.Ports;
using System.Threading.Channels;

namespace LinkStudio.Core.Devices.Serial
{
    /// <summary>
    /// 通过串口收发原始数据，并按实际 I/O 顺序将数据发布到 RawDataBus。
    /// </summary>
    public sealed partial class SerialPortDevice : ICommunicationDevice
    {
        // 两级队列分别以命令条数和发布项个数计容量。
        private const int CommandCapacity = 64;
        private const int PublishBufferCapacity = 4096;

        private readonly IRawDataBus _dataBus;
        private readonly SerialPortConfig _config;

        // 对外 API 和串口事件都可以投递命令，只有命令 Worker 消费。
        private readonly Channel<SerialCommand> _commands;

        // 命令 Worker 写入，发布 Worker 按顺序消费。缓存满时，命令 Worker 等待空位。
        private readonly Channel<PublishItem> _publishBuffer;
        private readonly Task _commandWorker;
        private readonly Task _publishWorker;
        private readonly object _disposeGate = new();

        // 底层端口由命令 Worker 管理；其他线程通过 Volatile 读写的状态字段获取状态。
        private SerialPort? _port;
        private Task? _disposeTask;
        private int _isOpen;
        private int _disposeStarted;

        public SerialPortDevice(IRawDataBus dataBus, SerialPortConfig config)
        {
            ArgumentNullException.ThrowIfNull(dataBus);
            ArgumentNullException.ThrowIfNull(config);

            _dataBus = dataBus;
            // 保存独立的配置快照，后续打开端口和生成通道 ID 都使用这一份配置。
            _config = new SerialPortConfig
            {
                PortName = config.PortName,
                BaudRate = config.BaudRate,
                DataBits = config.DataBits,
                StopBits = config.StopBits,
                Parity = config.Parity,
                FlowControl = config.FlowControl
            };

            // 两个队列均禁用同步延续，避免唤醒的消费者在写入者的调用栈上直接执行。
            _commands = Channel.CreateBounded<SerialCommand>(new BoundedChannelOptions(CommandCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });

            _publishBuffer = Channel.CreateBounded<PublishItem>(new BoundedChannelOptions(PublishBufferCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });

            // 从后台启动，避免串口的同步 Open、Read 和 Close 占用创建者的 UI 线程。
            _commandWorker = Task.Run(CommandWorkerAsync);
            _publishWorker = Task.Run(PublishWorkerAsync);
        }

        public string Id => $"serial-{_config.PortName.ToLowerInvariant()}";

        public string Name => _config.PortName;

        public ChannelKind Kind => ChannelKind.Serial;

        public bool IsOpen => Volatile.Read(ref _isOpen) == 1;

        /// <summary>
        /// 命令 Worker 在故障后关闭端口并更新状态，然后在后台触发此事件。
        /// UI 订阅方需要自行切回 UI 线程。
        /// </summary>
        public event EventHandler<Exception>? Faulted;

        /// <summary>枚举系统当前报告的串口名称。</summary>
        public static string[] GetAvailablePorts() => SerialPort.GetPortNames();

        /// <summary>按构造时的配置打开串口。底层同步 Open 开始后，取消只能停止调用方的等待。</summary>
        public Task OpenAsync(CancellationToken ct = default)
        {
            ThrowIfDisposing();
            ct.ThrowIfCancellationRequested();

            var completion = CreateCompletion();
            return EnqueueAndWaitAsync(new OpenSerialCommand(completion, ct), completion, ct);
        }

        /// <summary>关闭端口，并等待此前已入队的收发包写入 RawDataBus。</summary>
        public Task CloseAsync()
        {
            ThrowIfDisposing();

            var completion = CreateCompletion();
            return EnqueueAndWaitAsync(
                new CloseSerialCommand(completion), completion, CancellationToken.None);
        }

        /// <summary>
        /// 写入串口并等待 TX 包进入 RawDataBus。调用方取消等待时，底层写入可能已经发生。
        /// </summary>
        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            ThrowIfDisposing();
            ct.ThrowIfCancellationRequested();

            var completion = CreateCompletion();
            // 调用方可以复用原缓冲区，命令持有自己的数据副本。
            var command = new SendSerialCommand(data.ToArray(), completion, ct);
            return EnqueueAndWaitAsync(command, completion, ct);
        }

        private async Task EnqueueAndWaitAsync(
            SerialCommand command,
            TaskCompletionSource completion,
            CancellationToken ct)
        {
            try
            {
                // 队列满时等待空位，入队成功仅表示 Worker 已接收该请求。
                await _commands.Writer.WriteAsync(command, ct);
            }
            catch (ChannelClosedException) when (Volatile.Read(ref _disposeStarted) == 1)
            {
                throw new ObjectDisposedException(nameof(SerialPortDevice));
            }

            // 取消可以结束调用方的等待；Worker 仍负责结清这条命令的回执。
            await completion.Task.WaitAsync(ct);
        }

        // 回执的后续逻辑异步调度，避免调用方代码占用 Worker 的调用栈。
        private static TaskCompletionSource CreateCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void ThrowIfDisposing()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) == 1, this);
        }

        public ValueTask DisposeAsync()
        {
            lock (_disposeGate)
            {
                // 并发或重复释放都等待同一个任务。
                _disposeTask ??= DisposeCoreAsync();
                return new ValueTask(_disposeTask);
            }
        }

        private async Task DisposeCoreAsync()
        {
            // 标记释放开始，再投递关闭命令以关闭端口、冲刷已产生的数据。
            Volatile.Write(ref _disposeStarted, 1);
            Exception? failure = null;

            try
            {
                var completion = CreateCompletion();
                await _commands.Writer.WriteAsync(new CloseSerialCommand(completion));
                await completion.Task;
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                // 关闭失败时也结束命令输入，让命令 Worker 能退出。
                _commands.Writer.TryComplete();
            }

            try
            {
                await _commandWorker;
            }
            catch (Exception error)
            {
                failure ??= error;
            }

            // 命令 Worker 已退出，不会再产生发布项，通知发布 Worker 排空队列后结束。
            _publishBuffer.Writer.TryComplete();
            try
            {
                await _publishWorker;
            }
            catch (Exception error)
            {
                failure ??= error;
            }

            GC.SuppressFinalize(this);
            if (failure is not null)
            {
                throw failure;
            }
        }
    }
}
