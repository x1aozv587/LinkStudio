namespace LinkStudio.Core.Bus
{
    public enum RawDataOverflowPolicy : byte
    {
        Wait,
        DropOldest,
        DropIncoming,
        LatestOnly
    }

    public sealed record RawDataBusOptions(int IngressCapacity = 4096);

    public sealed record RawDataSubscriptionOptions(
        string ChannelIdFilter,
        int Capacity = 1024,
        RawDataOverflowPolicy OverflowPolicy = RawDataOverflowPolicy.Wait);

    // 高水位记录并发读写期间观测到的最大深度，遇到背压时记录为队列容量。
    public readonly record struct RawDataQueueStatistics(
        int Depth,
        int HighWaterMark,
        long DroppedPackets,
        long BackpressureEvents);

    public interface IRawDataSubscription : IAsyncDisposable
    {
        RawDataQueueStatistics Statistics { get; }
    }

    public interface IRawDataBus : IAsyncDisposable
    {
        RawDataQueueStatistics IngressStatistics { get; }

        // 成功返回表示进入总线。调用前应建立独立载荷，之后不得修改或复用底层缓冲区。
        ValueTask PublishAsync(RawDataPacket packet, CancellationToken ct = default);

        // "*" 订阅所有通道。回调须响应关闭令牌，且不能等待自身或总线退出，也不能向同一总线阻塞式回发。
        IRawDataSubscription Subscribe(
            RawDataSubscriptionOptions options,
            Func<RawDataPacket, CancellationToken, ValueTask> handler);
    }
}
