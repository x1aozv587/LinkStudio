using System.Threading.Channels;
using Serilog;

namespace LinkStudio.Core.Bus
{
    internal sealed class RawDataMailbox
    {
        private readonly Channel<RawDataPacket> _channel;
        private readonly int _capacity;
        private readonly string _name;
        private int _highWaterMark;
        private long _droppedPackets;
        private long _backpressureEvents;

        public RawDataMailbox(int capacity, RawDataOverflowPolicy policy, bool singleWriter, string name)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
            _capacity = policy == RawDataOverflowPolicy.LatestOnly ? 1 : capacity;
            _name = name;
            var mode = policy switch
            {
                RawDataOverflowPolicy.Wait => BoundedChannelFullMode.Wait,
                RawDataOverflowPolicy.DropOldest or RawDataOverflowPolicy.LatestOnly => BoundedChannelFullMode.DropOldest,
                RawDataOverflowPolicy.DropIncoming => BoundedChannelFullMode.DropWrite,
                _ => throw new ArgumentOutOfRangeException(nameof(policy))
            };
            _channel = Channel.CreateBounded<RawDataPacket>(new BoundedChannelOptions(_capacity)
            {
                FullMode = mode,
                SingleReader = true,
                SingleWriter = singleWriter
            }, _ =>
            {
                ObserveDepth(_capacity);
                Interlocked.Increment(ref _droppedPackets);
            });
        }

        public RawDataQueueStatistics Statistics => new(
            _channel.Reader.Count,
            Volatile.Read(ref _highWaterMark),
            Interlocked.Read(ref _droppedPackets),
            Interlocked.Read(ref _backpressureEvents));

        public async ValueTask WriteAsync(RawDataPacket packet, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!_channel.Writer.TryWrite(packet))
            {
                var write = _channel.Writer.WriteAsync(packet, ct);
                if (!write.IsCompleted)
                {
                    ObserveDepth(_capacity);
                    if (Interlocked.Increment(ref _backpressureEvents) == 1)
                    {
                        Log.Warning("原始数据队列 {QueueName} 已满，开始等待，容量 {Capacity}", _name, _capacity);
                    }
                }

                await write.ConfigureAwait(false);
            }

            ObserveDepth(_channel.Reader.Count);
        }

        public IAsyncEnumerable<RawDataPacket> ReadAllAsync(CancellationToken ct) =>
            _channel.Reader.ReadAllAsync(ct);

        public void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);

        public void RecordDiscardedPacket() => Interlocked.Increment(ref _droppedPackets);

        // 仅由消费 Worker 在关闭写入后释放未处理的载荷。
        public void DiscardPending()
        {
            while (_channel.Reader.TryRead(out _))
            {
                RecordDiscardedPacket();
            }
        }

        private void ObserveDepth(int depth)
        {
            int previous;
            do
            {
                previous = Volatile.Read(ref _highWaterMark);
                if (depth <= previous)
                {
                    return;
                }
            } while (Interlocked.CompareExchange(ref _highWaterMark, depth, previous) != previous);
        }
    }
}
