using LinkStudio.Core.Bus;
using LinkStudio.Core.Channels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    }
}
