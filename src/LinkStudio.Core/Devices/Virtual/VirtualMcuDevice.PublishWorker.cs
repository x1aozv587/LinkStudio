using LinkStudio.Core.Bus;

namespace LinkStudio.Core.Devices.Virtual
{
    /// <summary>
    /// VirtualMcuDevice 的数据发布部分。
    /// 命令 Worker 只把数据放入缓存，发布 Worker 才负责等待 RawDataBus。
    /// </summary>
    public sealed partial class VirtualMcuDevice
    {
        // 同一设备内，每产生一个数据包就递增一次，用于检查收发顺序和是否丢包。
        private uint _seq;

        /// <summary>
        /// 发布缓存中的一项。Packet 为 null 时表示“冲刷标记”，不是真的数据包；
        /// Completion 不为 null 时，发布 Worker 处理到这一项后要填写这张回执单。
        /// </summary>
        private sealed record PublishItem(
            RawDataPacket? Packet,
            TaskCompletionSource? Completion,
            CancellationToken CancellationToken);

        private async Task QueuePacketAsync(
            PacketDirection direction,
            ReadOnlyMemory<byte> payload,
            TaskCompletionSource? completion,
            CancellationToken ct)
        {
            var packet = new RawDataPacket(
                ChannelId: Id,
                ChannelKind: Kind,
                Direction: direction,
                TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SeqNo: _seq++,
                Payload: payload);

            // 这里只进入设备自己的发布缓存；真正等待 RawDataBus 的是发布 Worker。
            await _publishBuffer.Writer.WriteAsync(new PublishItem(packet, completion, ct), ct);
        }

        private async Task PublishWorkerAsync()
        {
            // 这个 Worker 是发布缓存的唯一读者，保证数据包按进入缓存的顺序发布。
            Exception? failure = null;
            try
            {
                await foreach (var item in _publishBuffer.Reader.ReadAllAsync())
                {
                    try
                    {
                        item.CancellationToken.ThrowIfCancellationRequested();
                        if (item.Packet is RawDataPacket packet)
                        {
                            await _dataBus.PublishAsync(packet, item.CancellationToken);
                        }

                        // Packet 为 null 时是冲刷标记；走到这里也说明它前面的数据已经全部发布。
                        item.Completion?.TrySetResult();
                    }
                    catch (OperationCanceledException) when (item.CancellationToken.IsCancellationRequested)
                    {
                        item.Completion?.TrySetCanceled(item.CancellationToken);
                    }
                    catch (Exception error)
                    {
                        item.Completion?.TrySetException(error);
                        failure = error;
                        throw;
                    }
                }
            }
            finally
            {
                if (failure is not null)
                {
                    // 发布 Worker 已经失效，禁止命令 Worker 继续向无人消费的缓存写入。
                    _publishBuffer.Writer.TryComplete(failure);
                }

                while (_publishBuffer.Reader.TryRead(out var remaining))
                {
                    if (failure is null)
                    {
                        remaining.Completion?.TrySetCanceled();
                    }
                    else
                    {
                        remaining.Completion?.TrySetException(failure);
                    }
                }
            }
        }
    }
}
