using LinkStudio.Core.Bus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace LinkStudio.Core.Channels
{
    public class VirtualMcuChannel : IChannels
    {
        public string Id => "ID Virtual MCU Channel";

        public string Name => "Name Virtual MCU Channel";

        public ChannelKind Kind => ChannelKind.VirtualMcu;

        private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
        private CancellationTokenSource? _sendCancellation;
        private volatile Task? _sendTask;
        private volatile Channel<SendRequest>? _sendRequests;
        private bool _disposed;

        public bool IsOpen => _sendTask is { IsCompleted: false };

        private readonly IRawDataBus _dataBus;

        private uint _seq;

        private sealed record SendRequest(
            byte[] Payload, CancellationToken CancellationToken, TaskCompletionSource Completion);

        public VirtualMcuChannel(IRawDataBus bus)
        {
            _dataBus = bus;
        }

        public async Task OpenAsync(CancellationToken ct = default)
        {
            await _lifecycleGate.WaitAsync(ct);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (IsOpen)
                {
                    return;
                }

                // 如果上一次循环已退出，先结清它的任务与取消资源。
                await StopSendingAsync();
                ct.ThrowIfCancellationRequested();

                var requests = Channel.CreateBounded<SendRequest>(new BoundedChannelOptions(16)
                {
                    SingleReader = true,
                    FullMode = BoundedChannelFullMode.Wait
                });

                var cancellation = new CancellationTokenSource();
                _sendCancellation = cancellation;
                _sendRequests = requests;
                _sendTask = Task.Run(() => SendLoopAsync(requests, cancellation.Token));
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public async Task CloseAsync()
        {
            await _lifecycleGate.WaitAsync();
            try
            {
                await StopSendingAsync();
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        // 请求入队与发送完成是两个阶段。回执由唯一的发包 Worker 完成。
        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var requests = _sendRequests
                ?? throw new InvalidOperationException("请先打开虚拟设备。");

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = new SendRequest(data.ToArray(), ct, completion);
            using var cancellation = ct.Register(() => completion.TrySetCanceled(ct));

            await requests.Writer.WriteAsync(request, ct);
            await completion.Task;
        }

        public async ValueTask DisposeAsync()
        {
            await _lifecycleGate.WaitAsync();
            try
            {
                _disposed = true;
                await StopSendingAsync();
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        private async Task StopSendingAsync()
        {
            var cancellation = _sendCancellation;
            var sendTask = _sendTask;

            if (cancellation is null || sendTask is null)
            {
                return;
            }

            try
            {
                _sendRequests = null;
                cancellation.Cancel();
                await sendTask;
            }
            finally
            {
                cancellation.Dispose();
                _sendCancellation = null;
                _sendTask = null;
            }
        }

        private async Task SendLoopAsync(Channel<SendRequest> requests, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            Exception? failure = null;

            try
            {
                await PublishPacketAsync(
                    PacketDirection.Rx, Encoding.UTF8.GetBytes("Virtual MCU Channel ready"), ct);

                // 定时器和队列各保留一个等待，只有消费了结果才创建下一次等待。
                var nextTick = timer.WaitForNextTickAsync(ct).AsTask();
                var requestReady = requests.Reader.WaitToReadAsync(ct).AsTask();

                while (true)
                {
                    await Task.WhenAny(nextTick, requestReady);
                    ct.ThrowIfCancellationRequested();

                    if (nextTick.IsCompleted)
                    {
                        if (!await nextTick)
                        {
                            break;
                        }

                        await PublishPacketAsync(
                            PacketDirection.Rx, Encoding.UTF8.GetBytes("Virtual MCU tick"), ct);
                        nextTick = timer.WaitForNextTickAsync(ct).AsTask();
                    }

                    if (requestReady.IsCompleted)
                    {
                        if (!await requestReady)
                        {
                            break;
                        }

                        if (requests.Reader.TryRead(out var request))
                        {
                            await EchoAsync(request, ct);
                        }

                        requestReady = requests.Reader.WaitToReadAsync(ct).AsTask();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 停止时会打断定时等待、命令等待或总线入队等待。
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }
            finally
            {
                // 先结束入队等待，再结清已入队但尚未处理的回执。
                requests.Writer.TryComplete();
                while (requests.Reader.TryRead(out var remaining))
                {
                    if (failure is null)
                    {
                        remaining.Completion.TrySetCanceled(ct);
                    }
                    else
                    {
                        remaining.Completion.TrySetException(failure);
                    }
                }
            }
        }

        private async Task EchoAsync(SendRequest request, CancellationToken workerToken)
        {
            if (request.CancellationToken.IsCancellationRequested)
            {
                request.Completion.TrySetCanceled(request.CancellationToken);
                return;
            }

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                workerToken, request.CancellationToken);

            try
            {
                await PublishPacketAsync(PacketDirection.Tx, request.Payload, cancellation.Token);
                await PublishPacketAsync(PacketDirection.Rx, request.Payload, cancellation.Token);
                request.Completion.TrySetResult();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                var canceledToken = workerToken.IsCancellationRequested
                    ? workerToken
                    : request.CancellationToken;
                request.Completion.TrySetCanceled(canceledToken);
            }
            catch (Exception error)
            {
                request.Completion.TrySetException(error);
                throw;
            }
        }

        private async Task PublishPacketAsync(
            PacketDirection direction, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            var packet = new RawDataPacket(
                ChannelId: Id,
                ChannelKind: Kind,
                Direction: direction,
                TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SeqNo: _seq,
                Payload: payload);

            await _dataBus.PublishAsync(packet, ct);
            _seq++;
        }

    }
}
