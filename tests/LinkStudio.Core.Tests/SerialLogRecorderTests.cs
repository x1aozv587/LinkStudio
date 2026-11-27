using System.Text;
using LinkStudio.Core.Bus;
using LinkStudio.Core.Services;
using Xunit;

namespace LinkStudio.Core.Tests
{
    public sealed class SerialLogRecorderTests : IDisposable
    {
        private sealed class SeqNoColumn : ISerialLogColumn
        {
            public string Name => "SeqNo";
            public string Format(RawDataPacket packet) => $"[#{packet.SeqNo}]";
        }

        private readonly string _tempDir = Path.Combine(
            Path.GetTempPath(),
            "LinkStudioLogTests",
            Guid.NewGuid().ToString("N"));

        [Fact]
        public async Task TextLog_RespectsEnabledColumnsAndSupportsExtraColumns()
        {
            var bus = new RawDataBus();
            var options = new SerialLogOptions(
                IsBinary: false,
                ShowTimestamp: true,
                ShowDirection: false,
                ShowPayload: true,
                FormatPayloadAsHex: false,
                ExtraColumns: [new SeqNoColumn()]);

            string filePath;
            await using (var recorder = new SerialLogRecorder(bus, "serial-com3", _tempDir, "COM3", () => options))
            {
                filePath = recorder.FilePath;
                var packet = new RawDataPacket(
                    ChannelId: "serial-com3",
                    ChannelKind: ChannelKind.Serial,
                    Direction: PacketDirection.Rx,
                    TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SeqNo: 42,
                    Payload: Encoding.UTF8.GetBytes("HELLO MCU"));

                await bus.PublishAsync(packet);
                // 先排空总线，再关闭记录器冲刷文件句柄。
                await bus.DisposeAsync();
            }

            var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            Assert.DoesNotContain("[RX]", content);
            Assert.Contains("[#42]", content);
            Assert.Contains("HELLO MCU", content);
        }

        [Fact]
        public async Task TextLog_FormatsPayloadAsHexWhenConfigured()
        {
            var bus = new RawDataBus();
            var options = new SerialLogOptions(
                IsBinary: false,
                ShowTimestamp: false,
                ShowDirection: true,
                ShowPayload: true,
                FormatPayloadAsHex: true);

            string filePath;
            await using (var recorder = new SerialLogRecorder(bus, "serial-com3", _tempDir, "COM3", () => options))
            {
                filePath = recorder.FilePath;
                var packet = new RawDataPacket(
                    ChannelId: "serial-com3",
                    ChannelKind: ChannelKind.Serial,
                    Direction: PacketDirection.Tx,
                    TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SeqNo: 1,
                    Payload: new byte[] { 0xAA, 0x55, 0x0D });

                await bus.PublishAsync(packet);
                await bus.DisposeAsync();
            }

            var content = (await File.ReadAllTextAsync(filePath, Encoding.UTF8)).Trim();
            Assert.Equal("[TX] AA 55 0D", content);
        }

        [Fact]
        public async Task BinaryLog_WritesExactRawBytes()
        {
            var bus = new RawDataBus();
            var options = new SerialLogOptions(
                IsBinary: true,
                ShowTimestamp: true,
                ShowDirection: true,
                ShowPayload: true,
                FormatPayloadAsHex: false);

            var payload1 = new byte[] { 0x01, 0x02, 0x03 };
            var payload2 = new byte[] { 0xFE, 0xFF };
            string filePath;

            await using (var recorder = new SerialLogRecorder(bus, "serial-com3", _tempDir, "COM3", () => options))
            {
                filePath = recorder.FilePath;
                await bus.PublishAsync(new RawDataPacket("serial-com3", ChannelKind.Serial, PacketDirection.Rx, 1000, 0, payload1));
                await bus.PublishAsync(new RawDataPacket("serial-com3", ChannelKind.Serial, PacketDirection.Rx, 1001, 1, payload2));
                await bus.DisposeAsync();
            }

            var actualBytes = await File.ReadAllBytesAsync(filePath);
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0xFE, 0xFF }, actualBytes);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
    }
}
