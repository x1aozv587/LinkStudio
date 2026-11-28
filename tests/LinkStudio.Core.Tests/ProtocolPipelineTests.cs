using System.Text;
using LinkStudio.Core.Bus;
using LinkStudio.Core.Protocols;
using Xunit;

namespace LinkStudio.Core.Tests
{
    public sealed class ProtocolPipelineTests
    {
        [Fact]
        public void LineFramer_ReassemblesSplitAndMergedPackets()
        {
            var framer = new LineFramer();
            var output = new List<RawFrame>();

            // 模拟串口半包 + 粘包 + \r\r\n
            framer.Feed(MakeRxPacket("{\"te"), output);
            Assert.Empty(output);

            framer.Feed(MakeRxPacket("mp\":24.6}\r\r\n{\"vbat\":4.18}\r\n"), output);

            Assert.Equal(2, output.Count);
            Assert.Equal("{\"temp\":24.6}", Encoding.UTF8.GetString(output[0].Payload.Span));
            Assert.Equal("{\"vbat\":4.18}", Encoding.UTF8.GetString(output[1].Payload.Span));
        }

        [Fact]
        public void LineFramer_ClearsBufferWhenExceedingMaxFrameBytes()
        {
            var framer = new LineFramer(maxFrameBytes: 8);
            var output = new List<RawFrame>();

            framer.Feed(MakeRxPacket("1234567890"), output);
            framer.Feed(MakeRxPacket("OK\r\n"), output);

            Assert.Single(output);
            Assert.Equal("90OK", Encoding.UTF8.GetString(output[0].Payload.Span));
        }

        [Fact]
        public void JsonProtocolParser_FlattensNestedObjectAndHandlesLogPrefix()
        {
            var parser = new JsonProtocolParser();
            var raw = MakeRawFrame("[11:20:01] [MCU] {\"temp\":24.6,\"imu\":{\"ax\":0.12,\"ok\":true},\"mode\":\"sleep\"}");

            var matched = parser.TryParse(raw, out var parsed);

            Assert.True(matched);
            Assert.NotNull(parsed);
            Assert.True(parsed!.IsValid);
            Assert.Equal("JSON", parsed.ProtocolName);

            var map = parsed.Fields.ToDictionary(f => f.Key);
            Assert.Equal(24.6, map["temp"].NumberValue);
            Assert.Equal(0.12, map["imu.ax"].NumberValue);
            Assert.True(map["imu.ok"].BoolValue);
            Assert.Equal("sleep", map["mode"].TextValue);
        }

        [Fact]
        public void JsonProtocolParser_IgnoresPlainLogLines()
        {
            var parser = new JsonProtocolParser();

            Assert.False(parser.TryParse(MakeRawFrame("hsm_gps_state_power_on_idle<==ENTRY"), out _));
            Assert.False(parser.TryParse(MakeRawFrame("[usb log] bad {not json}"), out _));
        }

        [Fact]
        public async Task ProtocolPipeline_EndToEndBusToJsonParsedFrame()
        {
            var bus = new RawDataBus();
            var received = new List<ParsedFrame>();

            await using (var pipeline = new ProtocolPipeline(
                bus,
                channelId: "serial-com1",
                framer: new LineFramer(),
                parsers: [new JsonProtocolParser()],
                onParsed: frame =>
                {
                    received.Add(frame);
                    return ValueTask.CompletedTask;
                }))
            {
                await bus.PublishAsync(MakeRxPacket("ordinary log line\r\n"));
                await bus.PublishAsync(MakeRxPacket("{\"cpu\":"));
                await bus.PublishAsync(MakeRxPacket("37.5,\"online\":true}\r\n"));

                // 先排空总线再释放管线，确保尾帧全部解析完成。
                await bus.DisposeAsync();
            }

            Assert.Single(received);
            Assert.Equal("JSON", received[0].ProtocolName);
            Assert.Equal(37.5, received[0].Fields.Single(f => f.Key == "cpu").NumberValue);
            Assert.True(received[0].Fields.Single(f => f.Key == "online").BoolValue);
        }

        private static RawDataPacket MakeRxPacket(string text) => new(
            ChannelId: "serial-com1",
            ChannelKind: ChannelKind.Serial,
            Direction: PacketDirection.Rx,
            TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SeqNo: 0,
            Payload: Encoding.UTF8.GetBytes(text));

        private static RawFrame MakeRawFrame(string text) => new(
            ChannelId: "serial-com1",
            Direction: PacketDirection.Rx,
            TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload: Encoding.UTF8.GetBytes(text));
    }
}
