using System.Threading.Channels;
using Serilog;

namespace LinkStudio.Core.Bus
{
    internal sealed class RawDataSubscription : IRawDataSubscription
    {
        private readonly RawDataMailbox _mailbox;
        private readonly Action<RawDataSubscription> _remove;

        public RawDataSubscription(
            RawDataSubscriptionOptions options,
            Func<RawDataPacket, CancellationToken, ValueTask> handler,
            CancellationToken abortToken,
            Action<RawDataSubscription> remove)
        {
            ChannelIdFilter = options.ChannelIdFilter;
            _remove = remove;
            _mailbox = new RawDataMailbox(options.Capacity, options.OverflowPolicy, singleWriter: true,
                $"subscription:{ChannelIdFilter}");
            Completion = ConsumeAsync(handler, abortToken);
        }

        public string ChannelIdFilter { get; }

        public RawDataQueueStatistics Statistics => _mailbox.Statistics;

        public Task Completion { get; }

        public void Complete() => _mailbox.Complete();

        public async ValueTask EnqueueAsync(RawDataPacket packet, CancellationToken ct)
        {
            try
            {
                await _mailbox.WriteAsync(packet, ct).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // 路由可能正使用注销前的快照。已关闭的订阅可以直接跳过。
            }
        }

        public async ValueTask DisposeAsync()
        {
            Complete();
            await Completion.ConfigureAwait(false);
            _remove(this);
        }

        private async Task ConsumeAsync(
            Func<RawDataPacket, CancellationToken, ValueTask> handler,
            CancellationToken abortToken)
        {
            try
            {
                await foreach (var packet in _mailbox.ReadAllAsync(abortToken).ConfigureAwait(false))
                {
                    if (abortToken.IsCancellationRequested)
                    {
                        _mailbox.RecordDiscardedPacket();
                        break;
                    }

                    await InvokeHandlerAsync(packet, handler, abortToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (abortToken.IsCancellationRequested)
            {
                // 强制关闭可以中断空闲等待或当前回调。
            }
            catch (Exception error)
            {
                Log.Error(error, "原始数据订阅 {ChannelFilter} 的 Worker 意外退出", ChannelIdFilter);
                throw;
            }
            finally
            {
                Complete();
                _mailbox.DiscardPending();
            }
        }

        private async ValueTask InvokeHandlerAsync(
            RawDataPacket packet,
            Func<RawDataPacket, CancellationToken, ValueTask> handler,
            CancellationToken abortToken)
        {
            try
            {
                await handler(packet, abortToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (abortToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                Log.Error(error, "原始数据订阅 {ChannelFilter} 处理通道 {ChannelId} 包 {Sequence} 失败",
                    ChannelIdFilter, packet.ChannelId, packet.SeqNo);
            }
        }
    }
}
