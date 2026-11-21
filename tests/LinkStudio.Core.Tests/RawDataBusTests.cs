using System.Threading.Channels;
using LinkStudio.Core.Bus;
using LinkStudio.Core.Bus.Metadata;
using Xunit;

namespace LinkStudio.Core.Tests
{
    public sealed class RawDataBusTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task ConcurrentProducersPreserveTheirOrderAndCallbacksRunSerially()
        {
            var bus = new RawDataBus(new RawDataBusOptions(4));
            var all = new List<(string Channel, uint Sequence)>();
            var serial = new List<uint>();
            var concurrentCallbacks = 0;
            var activeCallbacks = 0;
            bus.Subscribe(new("*", 4), async (packet, _) =>
            {
                if (Interlocked.Increment(ref activeCallbacks) > 1)
                {
                    Interlocked.Increment(ref concurrentCallbacks);
                }

                await Task.Yield();
                all.Add((packet.ChannelId, packet.SeqNo));
                Interlocked.Decrement(ref activeCallbacks);
            });
            bus.Subscribe(new("serial", 4), (packet, _) =>
            {
                serial.Add(packet.SeqNo);
                return ValueTask.CompletedTask;
            });

            await Task.WhenAll(Publish("serial"), Publish("virtual")).WaitAsync(Timeout);
            await Task.WhenAll(bus.DisposeAsync().AsTask(), bus.DisposeAsync().AsTask()).WaitAsync(Timeout);

            var expected = Enumerable.Range(0, 50).Select(sequence => (uint)sequence).ToArray();
            Assert.Equal(0, concurrentCallbacks);
            Assert.Equal(100, all.Count);
            Assert.Equal(expected, all.Where(item => item.Channel == "serial").Select(item => item.Sequence));
            Assert.Equal(expected, all.Where(item => item.Channel == "virtual").Select(item => item.Sequence));
            Assert.Equal(expected, serial);

            async Task Publish(string channel)
            {
                for (uint sequence = 0; sequence < 50; sequence++)
                {
                    await bus.PublishAsync(Packet(sequence, channel));
                }
            }
        }

        [Theory]
        [InlineData(RawDataOverflowPolicy.DropOldest, 2, 3)]
        [InlineData(RawDataOverflowPolicy.DropIncoming, 1, 2)]
        [InlineData(RawDataOverflowPolicy.LatestOnly, 3, 3)]
        public async Task OverflowPreservesExpectedPacketsAndCountsDrops(
            RawDataOverflowPolicy policy, int firstQueued, int lastQueued)
        {
            var bus = new RawDataBus();
            var release = NewSignal();
            var started = NewSignal();
            var routed = NewSignal();
            var received = new List<uint>();
            var subscription = bus.Subscribe(new("*", 2, policy), async (packet, _) =>
            {
                if (packet.SeqNo == 0)
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(Timeout);
                }

                received.Add(packet.SeqNo);
            });
            bus.Subscribe(new("*"), (packet, _) =>
            {
                if (packet.SeqNo == 3)
                {
                    routed.TrySetResult();
                }

                return ValueTask.CompletedTask;
            });

            await bus.PublishAsync(Packet(0));
            await started.Task.WaitAsync(Timeout);
            for (uint sequence = 1; sequence <= 3; sequence++)
            {
                await bus.PublishAsync(Packet(sequence));
            }

            await routed.Task.WaitAsync(Timeout);
            var full = subscription.Statistics;
            release.TrySetResult();
            await bus.DisposeAsync().AsTask().WaitAsync(Timeout);

            int capacity = policy == RawDataOverflowPolicy.LatestOnly ? 1 : 2;
            Assert.Equal(capacity, full.Depth);
            Assert.Equal(capacity, full.HighWaterMark);
            Assert.Equal(3 - capacity, full.DroppedPackets);
            Assert.Equal(0, subscription.Statistics.Depth);
            var expected = Enumerable.Range(firstQueued, lastQueued - firstQueued + 1).Select(item => (uint)item);
            Assert.Equal(expected.Prepend(0u), received);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task WaitBackpressuresPublishersAndSupportsCancellationAndShutdown(bool abort)
        {
            using var shutdown = new CancellationTokenSource();
            using var publishCancellation = new CancellationTokenSource();
            var bus = new RawDataBus(new RawDataBusOptions(1), shutdown.Token);
            var release = NewSignal();
            var started = NewSignal();
            var received = new List<uint>();
            var subscription = bus.Subscribe(new("*", 1), async (packet, ct) =>
            {
                if (packet.SeqNo == 0)
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(Timeout, ct);
                }

                received.Add(packet.SeqNo);
            });

            await bus.PublishAsync(Packet(0));
            await started.Task.WaitAsync(Timeout);
            await bus.PublishAsync(Packet(1));
            await bus.PublishAsync(Packet(2));
            await WaitUntilAsync(() => subscription.Statistics.BackpressureEvents > 0);
            await bus.PublishAsync(Packet(3));
            var canceledPublish = bus.PublishAsync(Packet(4), publishCancellation.Token).AsTask();
            Assert.False(canceledPublish.IsCompleted);
            publishCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledPublish.WaitAsync(Timeout));

            var blockedPublish = bus.PublishAsync(Packet(5)).AsTask();
            var stopping = bus.DisposeAsync().AsTask();
            await Assert.ThrowsAsync<ChannelClosedException>(() => blockedPublish.WaitAsync(Timeout));
            Assert.True(bus.IngressStatistics.BackpressureEvents >= 2);
            Assert.Equal(1, bus.IngressStatistics.HighWaterMark);
            Assert.False(stopping.IsCompleted);

            if (abort)
            {
                shutdown.Cancel();
            }
            else
            {
                release.TrySetResult();
            }

            await stopping.WaitAsync(Timeout);
            Assert.Equal(0, bus.IngressStatistics.Depth);
            Assert.Equal(0, subscription.Statistics.Depth);
            Assert.Equal(abort ? Array.Empty<uint>() : [0u, 1u, 2u, 3u], received);
        }

        [Fact]
        public async Task AHandlerExceptionDoesNotStopAnySubscription()
        {
            var bus = new RawDataBus();
            var first = new List<uint>();
            var second = new List<uint>();
            bus.Subscribe(new("*"), (packet, _) =>
            {
                if (packet.SeqNo == 0)
                {
                    throw new InvalidOperationException("Test handler failure");
                }

                first.Add(packet.SeqNo);
                return ValueTask.CompletedTask;
            });
            bus.Subscribe(new("*"), (packet, _) =>
            {
                second.Add(packet.SeqNo);
                return ValueTask.CompletedTask;
            });

            await bus.PublishAsync(Packet(0));
            await bus.PublishAsync(Packet(1));
            await bus.DisposeAsync().AsTask().WaitAsync(Timeout);

            Assert.Equal(new uint[] { 1 }, first);
            Assert.Equal(new uint[] { 0, 1 }, second);
        }

        [Fact]
        public async Task UnsubscribingUnblocksTheDispatcherAndDrainsThatSubscription()
        {
            var bus = new RawDataBus();
            var release = NewSignal();
            var started = NewSignal();
            var routed = NewSignal();
            var received = new List<uint>();
            var subscription = bus.Subscribe(new("*", 1), async (packet, _) =>
            {
                if (packet.SeqNo == 0)
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(Timeout);
                }

                received.Add(packet.SeqNo);
            });
            bus.Subscribe(new("*"), (packet, _) =>
            {
                if (packet.SeqNo == 2)
                {
                    routed.TrySetResult();
                }

                return ValueTask.CompletedTask;
            });

            await bus.PublishAsync(Packet(0));
            await started.Task.WaitAsync(Timeout);
            await bus.PublishAsync(Packet(1));
            await bus.PublishAsync(Packet(2));
            await WaitUntilAsync(() => subscription.Statistics.BackpressureEvents > 0);

            var disposing = subscription.DisposeAsync().AsTask();
            var repeated = subscription.DisposeAsync().AsTask();
            await routed.Task.WaitAsync(Timeout);
            var stopping = bus.DisposeAsync().AsTask();
            Assert.False(stopping.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(disposing, repeated, stopping).WaitAsync(Timeout);
            Assert.Equal(new uint[] { 0, 1 }, received);
        }

        [Fact]
        public async Task DispatchPreservesProtocolMetadataAndPayload()
        {
            var bus = new RawDataBus();
            var received = new List<RawDataPacket>();
            bus.Subscribe(new("*"), (packet, _) =>
            {
                received.Add(packet);
                return ValueTask.CompletedTask;
            });

            var endpoint = new RemoteEndpoint("192.0.2.10", 8080);
            var packets = new[]
            {
                Packet(0),
                new RawDataPacket("http", ChannelKind.HttpServer, PacketDirection.Rx, 0, 0,
                    new byte[] { 1, 2 }, new HttpPacketMeta("POST", "/status", endpoint)),
                new RawDataPacket("mqtt", ChannelKind.MqttBroker, PacketDirection.Rx, 0, 0,
                    new byte[] { 3 }, new MqttPacketMeta("device/status", 1, "device-1")),
                new RawDataPacket("tcp", ChannelKind.TcpServer, PacketDirection.Rx, 0, 0,
                    new byte[] { 4 }, new NetworkPacketMeta(endpoint))
            };

            foreach (var packet in packets)
            {
                await bus.PublishAsync(packet);
            }

            await bus.DisposeAsync().AsTask().WaitAsync(Timeout);

            Assert.Equal(packets, received);
            Assert.Null(received[0].Meta);
            Assert.Equal(endpoint, Assert.IsType<HttpPacketMeta>(received[1].Meta).RemoteEndpoint);
            Assert.Equal("device/status", Assert.IsType<MqttPacketMeta>(received[2].Meta).MqttTopic);
            Assert.Equal(endpoint, Assert.IsType<NetworkPacketMeta>(received[3].Meta).RemoteEndpoint);
            Assert.Equal(new byte[] { 1, 2 }, received[1].Payload.ToArray());
        }

        private static RawDataPacket Packet(uint sequence, string channel = "serial") =>
            new(channel, ChannelKind.Serial, PacketDirection.Rx, 0, sequence, ReadOnlyMemory<byte>.Empty);

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(Timeout);
            while (!condition())
            {
                await Task.Delay(5, timeout.Token);
            }
        }
    }
}
