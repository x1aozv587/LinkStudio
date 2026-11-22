using LinkStudio.Core.Bus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        private bool _disposed;

        public bool IsOpen => _sendTask is { IsCompleted: false };

        private readonly IRawDataBus _dataBus;

        private uint _seq;

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

                var cancellation = new CancellationTokenSource();
                _sendCancellation = cancellation;
                _sendTask = Task.Run(() => SendLoopAsync(cancellation.Token));
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

        private async Task SendLoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

            try
            {
                await PublishPacketAsync("Virtual MCU Channel ready", ct);

                while (await timer.WaitForNextTickAsync(ct))
                {
                    await PublishPacketAsync("Virtual MCU tick", ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 正常停止时，取消令牌会打断定时等待或总线入队等待。
            }
        }

        /// 发送方法
        /// 
        private async Task PublishPacketAsync(string message, CancellationToken ct)
        {
            var packet = new RawDataPacket(
                ChannelId: Id,
                ChannelKind: Kind,
                Direction: PacketDirection.Rx,
                TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SeqNo: _seq,
                Payload: Encoding.UTF8.GetBytes(message));

            await _dataBus.PublishAsync(packet, ct);
            _seq++;
        }

    }
}
