using LinkStudio.Core.Bus;
using LinkStudio.Core.Devices.Virtual;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace LinkStudio.Core.Tests
{
    public sealed class VirtualMcuDeviceTest
    {
        [Fact]
        public async Task OpenAsync_PublishReadPacket()
        {
            await using var bus = new RawDataBus();
            await using var device = new VirtualMcuDevice(bus);

            var received = new TaskCompletionSource<RawDataPacket>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            await using var subscription = bus.Subscribe(new(device.Id), (packet, _) =>
            {
                received.TrySetResult(packet);
                return ValueTask.CompletedTask;
            });

            await device.OpenAsync();

            var packet = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(device.IsOpen);
            Assert.Equal(PacketDirection.Rx, packet.Direction);
            Assert.Equal("Virtual MCU ready", Encoding.UTF8.GetString(packet.Payload.Span));
        }

        [Fact]
        public async Task AutoSendAsync_PublishesReadyThenTick()
        {
            await using var bus = new RawDataBus();
            await using var device = new VirtualMcuDevice(bus);
            var packets = Channel.CreateBounded<RawDataPacket>(4);

            await using var subscription = bus.Subscribe(
                new(device.Id),
                (packet, ct) => packets.Writer.WriteAsync(packet, ct));

            await device.OpenAsync();
            await device.AutoSendAsync(enabled: true, interval: 50);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var first = await packets.Reader.ReadAsync(timeout.Token);
            var second = await packets.Reader.ReadAsync(timeout.Token);

            await device.CloseAsync();

            Assert.Equal("Virtual MCU ready", Encoding.UTF8.GetString(first.Payload.Span));
            Assert.Equal("Virtual MCU tick", Encoding.UTF8.GetString(second.Payload.Span));
            Assert.Equal(first.SeqNo + 1u, second.SeqNo);
            Assert.False(device.IsOpen);
        }

        [Fact]
        public async Task SendAsync_PublishesBytesAsTxThenRx()
        {
            await using var bus = new RawDataBus();
            await using var device = new VirtualMcuDevice(bus);
            var received = new List<RawDataPacket>();
            var payload = new byte[] { 0x00, 0xFF };

            await using var subscription = bus.Subscribe(new(device.Id), (packet, _) =>
            {
                if (packet.Payload.Span.SequenceEqual(payload))
                {
                    received.Add(packet);
                }

                return ValueTask.CompletedTask;
            });

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await device.OpenAsync(timeout.Token);
            await device.SendAsync(payload, timeout.Token);
            await device.CloseAsync();

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
            await using var device = new VirtualMcuDevice(bus);

            await device.OpenAsync();
            await device.CloseAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.SendAsync(new byte[] { 0x01 }));
        }

        [Fact]
        public async Task AutoSendAsync_ValidatesZeroIntervalAndAllowsDisableWithZero()
        {
            await using var bus = new RawDataBus();
            await using var device = new VirtualMcuDevice(bus);

            await device.OpenAsync();

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => device.AutoSendAsync(enabled: true, interval: 0));

            await device.AutoSendAsync(enabled: true, interval: 50);
            // 关闭定时器时允许传入 0，不应误报周期无效。
            await device.AutoSendAsync(enabled: false, interval: 0);
        }

        [Fact]
        public async Task DisposeAsync_ConcurrentWithSendAsync_DoesNotLeakChannelClosedException()
        {
            await using var bus = new RawDataBus();
            var device = new VirtualMcuDevice(bus);
            await device.OpenAsync();

            var payload = new byte[] { 0x41 };
            var sendTasks = Enumerable.Range(0, 32)
                .Select(_ => Task.Run(() => device.SendAsync(payload)))
                .ToArray();

            var disposeTask = device.DisposeAsync().AsTask();
            await disposeTask;

            foreach (var sendTask in sendTasks)
            {
                try
                {
                    await sendTask;
                }
                catch (ObjectDisposedException)
                {
                    // 并发释放时允许抛出标准的 ObjectDisposedException。
                }
                catch (InvalidOperationException)
                {
                    // 并发释放前如果刚好排在内部 Close 之后，可能抛出未打开异常。
                }
                catch (OperationCanceledException)
                {
                    // Worker 退出排空队列时可能取消未执行的命令。
                }
            }

            await Assert.ThrowsAsync<ObjectDisposedException>(() => device.OpenAsync());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => device.SendAsync(payload));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => device.AutoSendAsync(true, 100));
        }
    }
}
