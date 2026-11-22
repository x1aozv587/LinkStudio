using LinkStudio.Core.Bus;
using LinkStudio.Core.Channels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace LinkStudio.Core.Tests
{
    public sealed class VirtualMcuChannelTest
    {
        [Fact]
        public async Task OpenAsync_PublishReadPacket()
        {
            await using var bus = new RawDataBus();
            await using var channel = new VirtualMcuChannel(bus);

            var received = new TaskCompletionSource<RawDataPacket>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            await using var subscription = bus.Subscribe(new(channel.Id), (packet, _) =>
            {
                received.TrySetResult(packet);
                return ValueTask.CompletedTask;
            });

            await channel.OpenAsync();

            var packet = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(channel.IsOpen);
            Assert.Equal(PacketDirection.Rx, packet.Direction);
            Assert.Equal("Virtual MCU Channel ready", Encoding.UTF8.GetString(packet.Payload.Span));
        }

        [Fact]
        public async Task OpenAsync_PublishesReadyThenTick()
        {
            await using var bus = new RawDataBus();
            await using var channel = new VirtualMcuChannel(bus);
            var packets = Channel.CreateBounded<RawDataPacket>(4);

            await using var subscription = bus.Subscribe(
                new(channel.Id),
                (packet, ct) => packets.Writer.WriteAsync(packet, ct));

            await channel.OpenAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var first = await packets.Reader.ReadAsync(timeout.Token);
            var second = await packets.Reader.ReadAsync(timeout.Token);

            await channel.CloseAsync();

            Assert.Equal("Virtual MCU Channel ready", Encoding.UTF8.GetString(first.Payload.Span));
            Assert.Equal("Virtual MCU tick", Encoding.UTF8.GetString(second.Payload.Span));
            Assert.Equal(first.SeqNo + 1u, second.SeqNo);
            Assert.False(channel.IsOpen);
        }

        [Fact]
        public async Task SendAsync_PublishesBytesAsTxThenRx()
        {
            await using var bus = new RawDataBus();
            await using var channel = new VirtualMcuChannel(bus);
            var received = new List<RawDataPacket>();
            var payload = new byte[] { 0x00, 0xFF };

            await using var subscription = bus.Subscribe(new(channel.Id), (packet, _) =>
            {
                if (packet.Payload.Span.SequenceEqual(payload))
                {
                    received.Add(packet);
                }

                return ValueTask.CompletedTask;
            });

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await channel.OpenAsync(timeout.Token);
            await channel.SendAsync(payload, timeout.Token);
            await channel.CloseAsync();

            // 排空总线后再读取列表，确保订阅 Worker 已处理两包。
            await bus.DisposeAsync();

            Assert.Collection(received,
                sent => Assert.Equal(PacketDirection.Tx, sent.Direction),
                echoed => Assert.Equal(PacketDirection.Rx, echoed.Direction));
            Assert.Equal(received[0].SeqNo + 1u, received[1].SeqNo);
        }

        [Fact]
        public async Task SendAsync_RejectsRequestsAfterClose()
        {
            await using var bus = new RawDataBus();
            await using var channel = new VirtualMcuChannel(bus);

            await channel.OpenAsync();
            await channel.CloseAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => channel.SendAsync(new byte[] { 0x01 }));
        }
    }
}
