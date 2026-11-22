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
    }
}
