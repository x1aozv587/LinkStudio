using System.Text;
using System.Threading.Channels;
using LinkStudio.Core.Bus;
using LinkStudio.Core.Devices.Replay;
using LinkStudio.Core.Processing;
using LinkStudio.Core.Protocols;
using Xunit;

namespace LinkStudio.Core.Tests
{
    public sealed class LogReplayAndSignalTests
    {
        [Fact]
        public void LogReplayParser_ParsesTimestampedAndPlainLinesAndSkipsSysAndComments()
        {
            string[] lines =
            [
                "# LinkStudio Serial Log",
                "2026-11-28 14:20:01.000 RX {\"imu\":{\"ax\":1.2}}",
                "2026-11-28 14:20:01.250 SYS 串口已打开",
                "2026-11-28 14:20:01.500 TX AT+VER",
                "$GNGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47"
            ];

            var frames = LogReplayParser.ParseLines(lines, defaultStepMs: 100);

            Assert.Equal(3, frames.Count);
            Assert.Equal(PacketDirection.Rx, frames[0].Direction);
            Assert.Equal(TimeSpan.Zero, frames[0].Offset);
            Assert.Equal("{\"imu\":{\"ax\":1.2}}\r\n", Encoding.UTF8.GetString(frames[0].Payload));

            Assert.Equal(PacketDirection.Tx, frames[1].Direction);
            Assert.Equal(TimeSpan.FromMilliseconds(500), frames[1].Offset);

            Assert.Equal(PacketDirection.Rx, frames[2].Direction);
            Assert.Equal(TimeSpan.FromMilliseconds(600), frames[2].Offset);
        }

        [Fact]
        public async Task LogReplayDevice_FullSpeedReplayFeedsProtocolPipelineEndToEnd()
        {
            await using var bus = new RawDataBus();
            var frames = LogReplayParser.ParseLines(
            [
                "14:20:01.000 RX {\"imu\":{\"ax\":1.5},\"temp\":36.5}",
                "14:20:01.100 RX {\"imu\":{\"ax\":2.5},\"temp\":37.0}"
            ]);

            await using var replay = new LogReplayDevice(bus, "test.log", frames);
            var parsedChannel = Channel.CreateUnbounded<ParsedFrame>();

            await using var pipeline = new ProtocolPipeline(
                bus,
                replay.Id,
                new LineFramer(),
                [new JsonProtocolParser()],
                parsed =>
                {
                    parsedChannel.Writer.TryWrite(parsed);
                    return ValueTask.CompletedTask;
                });

            replay.SetSpeed(0); // 全速回放
            await replay.OpenAsync();
            replay.Play();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var first = await parsedChannel.Reader.ReadAsync(timeout.Token);
            var second = await parsedChannel.Reader.ReadAsync(timeout.Token);

            Assert.True(first.IsValid);
            Assert.True(second.IsValid);
            Assert.Equal(1.5, first.Fields.First(f => f.Key == "imu.ax").NumberValue);
            Assert.Equal(2.5, second.Fields.First(f => f.Key == "imu.ax").NumberValue);
            Assert.Equal(ChannelKind.LogReplay, replay.Kind);
        }

        [Fact]
        public async Task LogReplayDevice_SeekAndReplayRepeatsSameDataDeterministically()
        {
            await using var bus = new RawDataBus();
            var frames = LogReplayParser.ParseLines(
            [
                "{\"v\":10}",
                "{\"v\":20}",
                "{\"v\":30}"
            ]);

            await using var replay = new LogReplayDevice(bus, "repeat.jsonl", frames);
            var packets = Channel.CreateUnbounded<RawDataPacket>();
            await using var sub = bus.Subscribe(new(replay.Id), (pkt, ct) => packets.Writer.WriteAsync(pkt, ct));

            replay.SetSpeed(0);
            await replay.OpenAsync();
            replay.Seek(1);
            replay.Play();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var p1 = await packets.Reader.ReadAsync(timeout.Token);
            var p2 = await packets.Reader.ReadAsync(timeout.Token);

            Assert.Equal("{\"v\":20}\r\n", Encoding.UTF8.GetString(p1.Payload.Span));
            Assert.Equal("{\"v\":30}\r\n", Encoding.UTF8.GetString(p2.Payload.Span));
        }

        [Fact]
        public void SignalProcessor_MovingAverageLowPassAndDeltaProduceExpectedValues()
        {
            double[] input = [10, 20, 30, 40, 50];

            var ma = SignalProcessor.Apply(input, SignalFilterKind.MovingAverage, windowSize: 3);
            Assert.Equal(10, ma[0], 6);
            Assert.Equal(15, ma[1], 6);
            Assert.Equal(20, ma[2], 6);
            Assert.Equal(30, ma[3], 6);
            Assert.Equal(40, ma[4], 6);

            var lp = SignalProcessor.Apply(input, SignalFilterKind.LowPass, alpha: 0.5);
            Assert.Equal(10, lp[0], 6);
            Assert.Equal(15, lp[1], 6);
            Assert.Equal(22.5, lp[2], 6);

            var delta = SignalProcessor.Apply(input, SignalFilterKind.Delta);
            Assert.Equal([0, 10, 10, 10, 10], delta);
        }
    }
}
