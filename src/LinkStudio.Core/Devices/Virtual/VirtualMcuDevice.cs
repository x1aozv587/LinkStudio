using LinkStudio.Core.Bus;
using System.Threading.Channels;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace LinkStudio.Core.Devices.Virtual
{
    /// <summary>
    /// 不依赖真实串口的虚拟 MCU。
    /// 这个主文件只保留对外 API、队列创建和资源释放；
    /// 命令处理与数据发布分别放在另外两个 partial 文件中。
    /// </summary>
    public sealed partial class VirtualMcuDevice : ICommunicationDevice
    {
        // 容量单位是“条命令”和“个数据包”，不是字节。
        private const int CommandCapacity = 64;
        private const int PublishBufferCapacity = 4096;

        private readonly IRawDataBus _dataBus;

        // 外部方法和定时器把 Open/Close/Send/Tick 写入这里，命令 Worker 是唯一读者。
        private readonly Channel<VirtualMcuCommand> _commands;

        // 命令 Worker 把已经生成的数据包写入这里，发布 Worker 是唯一读者。
        // 这个有界缓存用于吸收 RawDataBus 的短暂阻塞；缓存满后写入者会等待。
        private readonly Channel<PublishItem> _publishBuffer;

        // 保存 Worker 的 Task，释放设备时必须等待它们真正结束。
        private readonly Task _commandWorker;
        private readonly Task _publishWorker;

        // 防止多个调用方同时执行 DisposeAsync，导致重复关闭同一批资源。
        private readonly object _disposeGate = new();

        private Task? _disposeTask;

        // 0/1 状态配合 Volatile 读写，让其他 Task 能及时看到最新状态。
        private int _isOpen;
        private int _disposeStarted;

        public VirtualMcuDevice(IRawDataBus dataBus)
        {
            _dataBus = dataBus;

            // 命令可能由多个调用方和定时器同时写入，所以 SingleWriter 为 false；
            // 只有 CommandWorkerAsync 读取，所以 SingleReader 为 true。
            _commands = Channel.CreateBounded<VirtualMcuCommand>(new BoundedChannelOptions(CommandCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            // 发布缓存只有命令 Worker 写、发布 Worker 读，因此两项都可以设为 true。
            _publishBuffer = Channel.CreateBounded<PublishItem>(new BoundedChannelOptions(PublishBufferCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            // 构造设备时启动两个后台 Worker。没有数据时它们会异步等待，不会空转占用 CPU。
            _commandWorker = Task.Run(CommandWorkerAsync);
            _publishWorker = Task.Run(PublishWorkerAsync);
        }

        public string Id => "virtual-mcu-1";

        public string Name => "Virtual MCU";

        public ChannelKind Kind => ChannelKind.VirtualMcu;

        public bool IsOpen => Volatile.Read(ref _isOpen) == 1;

        public Task OpenAsync(CancellationToken ct = default)
        {
            // 已经开始释放的设备不能再接受新命令；已提前取消的请求也不必进入队列。
            ThrowIfDisposing();
            ct.ThrowIfCancellationRequested();

            // 同一张回执单一边交给 Worker 填写，一边交给调用方等待。
            var completion = CreateCompletion();
            return EnqueueAndWaitAsync(new OpenVirtualMcuCommand(completion, ct), completion, ct);
        }

        public Task CloseAsync()
        {
            ThrowIfDisposing();
            var completion = CreateCompletion();
            return EnqueueAndWaitAsync(
                new CloseVirtualMcuCommand(completion), completion, CancellationToken.None);
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            ThrowIfDisposing();
            ct.ThrowIfCancellationRequested();
            var completion = CreateCompletion();

            // 调用方可能复用原内存，所以入队前先复制一份。
            var command = new SendVirtualMcuCommand(data.ToArray(), completion, ct);
            return EnqueueAndWaitAsync(command, completion, ct);
        }

        // 测试时使用这个接口开启虚拟串口的发送
        public Task AutoSendAsync(bool enabled, UInt32 interval, CancellationToken ct = default )
        {
            ThrowIfDisposing();
            ct.ThrowIfCancellationRequested();
            var completion = CreateCompletion();

            // 调用方可能复用原内存，所以入队前先复制一份。
            var command = new AutoVirtualMcuCommand(enabled, interval, ct, completion);
            return EnqueueAndWaitAsync(command, completion, ct);
        }

        private async Task EnqueueAndWaitAsync(
            VirtualMcuCommand command,
            TaskCompletionSource completion,
            CancellationToken ct)
        {
            // 第一次 await 只保证命令已经进入队列，不代表命令执行完成。
            await _commands.Writer.WriteAsync(command, ct);

            // 第二次 await 等待 Worker 填写回执：成功、失败和取消都会传回调用方。
            await completion.Task.WaitAsync(ct);
        }

        private static TaskCompletionSource CreateCompletion()
        {
            // 回执完成后的后续代码异步调度，避免它直接挤在 Worker 当前调用栈里执行。
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private void ThrowIfDisposing()
        {
            // 释放开始后拒绝新命令，避免它们排在关闭命令之后而永远得不到处理。
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) == 1, this);
        }

        public ValueTask DisposeAsync()
        {
            lock (_disposeGate)
            {
                // 第一次调用创建释放任务，后续调用都等待同一个任务，保证释放只执行一次。
                _disposeTask ??= DisposeCoreAsync();
                return new ValueTask(_disposeTask);
            }
        }

        private async Task DisposeCoreAsync()
        {
            // 先禁止外部加入新命令，再用一条 Close 命令完成正常关闭和缓存冲刷。
            Volatile.Write(ref _disposeStarted, 1);
            Exception? failure = null;

            try
            {
                var completion = CreateCompletion();
                await _commands.Writer.WriteAsync(new CloseVirtualMcuCommand(completion));
                await completion.Task;
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                // 告诉命令 Worker：不会再有新命令，读完已有命令后可以退出。
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

            // 命令 Worker 已结束，不会再产生数据，可以让发布 Worker 排空后退出。
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
