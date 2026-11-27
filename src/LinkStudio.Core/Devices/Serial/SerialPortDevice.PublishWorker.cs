using LinkStudio.Core.Bus;

namespace LinkStudio.Core.Devices.Serial
{
    /// <summary>
    /// 串口数据发布。命令 Worker 负责生成数据包，发布 Worker 负责等待 RawDataBus 接收。
    /// </summary>
    public sealed partial class SerialPortDevice
    {
        // 由命令 Worker 分配，同一个设备实例的 RX 和 TX 共用序号。
        private uint _seq;

        /// <summary>
        /// Packet 为空表示关闭时的冲刷标记。Completion 用于通知发送或关闭操作的等待方。
        /// </summary>
        private sealed record PublishItem(
            RawDataPacket? Packet,
            TaskCompletionSource? Completion,
            CancellationToken CancellationToken);

        private async Task QueuePacketAsync(
            PacketDirection direction,
            ReadOnlyMemory<byte> payload,
            TaskCompletionSource? completion,
            CancellationToken enqueueCt,
            CancellationToken? publishCt = null)
        {
            var packet = new RawDataPacket(
                ChannelId: Id,
                ChannelKind: Kind,
                Direction: direction,
                TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SeqNo: _seq++,
                Payload: payload);

            // 这里只等待进入设备缓存，写入总线后的回执由发布 Worker 填写。
            await _publishBuffer.Writer.WriteAsync(
                new PublishItem(packet, completion, publishCt ?? enqueueCt),
                enqueueCt);
        }

        private async Task PublishWorkerAsync()
        {
            // 单一消费者逐项发布，保持 RX、TX 和冲刷标记的入队顺序。
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

                        // 完成表示包已进入总线，订阅者随后由总线调度。
                        // 对于冲刷标记，表示排在它前面的包均已进入总线。
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
                    // 发布失败时关闭写端，使等待空位的命令也能收到异常。
                    _publishBuffer.Writer.TryComplete(failure);
                }

                // 结清剩余的发送及关闭回执，避免调用方一直等待已退出的 Worker。
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
