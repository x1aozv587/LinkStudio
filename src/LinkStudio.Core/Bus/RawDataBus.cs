using Serilog;

namespace LinkStudio.Core.Bus
{
    public sealed class RawDataBus : IRawDataBus
    {
        private readonly object _subscriptionGate = new();
        private readonly RawDataMailbox _ingress;
        private readonly CancellationToken _abortToken;
        private readonly Task _dispatcher;
        private RawDataSubscription[] _subscriptions = [];
        private bool _closing;

        // 默认关闭会排空队列。宿主可以取消 abortToken 来放弃积压，并通知当前回调停止。
        public RawDataBus(RawDataBusOptions? options = null, CancellationToken abortToken = default)
        {
            options ??= new RawDataBusOptions();
            _abortToken = abortToken;
            _ingress = new RawDataMailbox(options.IngressCapacity, RawDataOverflowPolicy.Wait,
                singleWriter: false, "ingress");
            _dispatcher = DispatchAsync();
        }

        public RawDataQueueStatistics IngressStatistics => _ingress.Statistics;

        public ValueTask PublishAsync(RawDataPacket packet, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(packet.ChannelId);
            _abortToken.ThrowIfCancellationRequested();
            return _ingress.WriteAsync(packet, ct);
        }

        public IRawDataSubscription Subscribe(
            RawDataSubscriptionOptions options,
            Func<RawDataPacket, CancellationToken, ValueTask> handler)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(handler);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ChannelIdFilter);

            lock (_subscriptionGate)
            {
                ObjectDisposedException.ThrowIf(_closing, this);
                _abortToken.ThrowIfCancellationRequested();
                var subscription = new RawDataSubscription(options, handler, _abortToken, Remove);
                Volatile.Write(ref _subscriptions, [.. _subscriptions, subscription]);
                return subscription;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_subscriptionGate)
            {
                _closing = true;
                _ingress.Complete();
            }

            return new ValueTask(_dispatcher);
        }

        private void Remove(RawDataSubscription subscription)
        {
            lock (_subscriptionGate)
            {
                if (Array.IndexOf(_subscriptions, subscription) >= 0)
                {
                    Volatile.Write(ref _subscriptions, Array.FindAll(_subscriptions, item => item != subscription));
                }
            }
        }

        private async Task DispatchAsync()
        {
            try
            {
                await foreach (var packet in _ingress.ReadAllAsync(_abortToken).ConfigureAwait(false))
                {
                    if (_abortToken.IsCancellationRequested)
                    {
                        _ingress.RecordDiscardedPacket();
                        break;
                    }
                    foreach (var subscription in Volatile.Read(ref _subscriptions))
                    {
                        if (subscription.ChannelIdFilter == "*" || subscription.ChannelIdFilter == packet.ChannelId)
                        {
                            await subscription.EnqueueAsync(packet, _abortToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_abortToken.IsCancellationRequested)
            {
                // 宿主允许放弃尚未处理的包。
            }
            catch (Exception error)
            {
                _ingress.Complete(error);
                Log.Error(error, "原始数据总线调度 Worker 意外退出");
                throw;
            }
            finally
            {
                RawDataSubscription[] subscriptions;
                lock (_subscriptionGate)
                {
                    _closing = true;
                    _ingress.Complete();
                    subscriptions = _subscriptions;
                    Volatile.Write(ref _subscriptions, []);
                }

                _ingress.DiscardPending();
                foreach (var subscription in subscriptions)
                {
                    subscription.Complete();
                }

                await Task.WhenAll(subscriptions.Select(subscription => subscription.Completion)).ConfigureAwait(false);
            }
        }
    }
}
