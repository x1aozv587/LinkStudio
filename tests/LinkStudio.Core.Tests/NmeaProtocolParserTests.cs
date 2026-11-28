using System.Text;
using LinkStudio.Core.Bus;
using LinkStudio.Core.Protocols;
using Xunit;

namespace LinkStudio.Core.Tests
{
    public sealed class NmeaProtocolParserTests
    {
        [Fact]
        public void TryParse_ParsesRealAt6558rLogWithFirmwarePrefix()
        {
            var parser = new NmeaProtocolParser();

            // 来自真实 AT6558R 串口日志：带固件前缀 + 未定位空字段
            var ggaLine = "18622\t2026-11-28 11:19:35.774\t08:07:18.962\t[407-NotDefined]\t$GNGGA,,,,,,0,00,25.5,,,,,,*64";
            Assert.True(parser.TryParse(MakeFrame(ggaLine), out var gga));
            Assert.True(gga!.IsValid);
            Assert.Equal("GGA", gga.FrameType);
            var ggaMap = gga.Fields.ToDictionary(f => f.Key);
            Assert.Equal(0, ggaMap["gga.fixQuality"].NumberValue);
            Assert.Equal(0, ggaMap["gga.satellites"].NumberValue);
            Assert.Equal(25.5, ggaMap["gga.hdop"].NumberValue);

            // 真实多星 GSV 载噪比提取（仰角/方位角为空，但 SNR 有值）
            var gsvLine = "18696\t2026-11-28 11:19:39.824\t08:07:22.013\t[407-NotDefined]\t$GPGSV,1,1,02,26,,,40,28,,,34,0*6A";
            Assert.True(parser.TryParse(MakeFrame(gsvLine), out var gsv));
            Assert.True(gsv!.IsValid);
            Assert.Equal("GSV", gsv.FrameType);
            var gsvMap = gsv.Fields.ToDictionary(f => f.Key);
            Assert.Equal(2, gsvMap["gsv.GP.inView"].NumberValue);
            Assert.Equal(40, gsvMap["gsv.GP.26.snr"].NumberValue);
            Assert.Equal(34, gsvMap["gsv.GP.28.snr"].NumberValue);

            // 真实 TXT 天线开路告警
            var txtLine = "18678\t2026-11-28 11:19:37.826\t08:07:20.012\t[405-NotDefined]\t$GPTXT,01,01,01,ANTENNA OPEN*25";
            Assert.True(parser.TryParse(MakeFrame(txtLine), out var txt));
            Assert.True(txt!.IsValid);
            Assert.Equal("TXT", txt.FrameType);
            Assert.Equal("ANTENNA OPEN", txt.Fields.Single(f => f.Key == "txt.message").TextValue);

            // 真实中科微私有语句 PCAS04
            var pcasLine = "18621\t2026-11-28 11:19:35.185\t08:07:18.391\t[404-NotDefined]\t$PCAS04,7*1E";
            Assert.True(parser.TryParse(MakeFrame(pcasLine), out var pcas));
            Assert.True(pcas!.IsValid);
            Assert.Equal("PCAS04", pcas.FrameType);
            Assert.Equal("7", pcas.Fields.Single(f => f.Key == "proprietary.args").TextValue);
        }

        [Fact]
        public void TryParse_ConvertsFixedGgaAndRmcCoordinatesToDecimalDegrees()
        {
            var parser = new NmeaProtocolParser();

            // 标准定位成功的 GGA 语句（纬度 31°13.8240'N，经度 121°28.4220'E）
            var ggaBody = "GNGGA,092725.00,3113.8240,N,12128.4220,E,1,12,0.8,18.5,M,-5.2,M,,";
            var ggaSentence = BuildSentence(ggaBody);

            Assert.True(parser.TryParse(MakeFrame(ggaSentence), out var gga));
            Assert.True(gga!.IsValid);
            var map = gga.Fields.ToDictionary(f => f.Key);
            Assert.InRange(map["gga.lat"].NumberValue!.Value, 31.2303, 31.2305);
            Assert.InRange(map["gga.lon"].NumberValue!.Value, 121.4736, 121.4738);
            Assert.Equal(12, map["gga.satellites"].NumberValue);
            Assert.Equal(18.5, map["gga.altitude"].NumberValue);

            // 南纬西经应转为负数，并计算 km/h
            var rmcBody = "GNRMC,123519,A,3351.4080,S,15112.9180,W,10.0,084.4,281126,003.1,W";
            Assert.True(parser.TryParse(MakeFrame(BuildSentence(rmcBody)), out var rmc));
            Assert.True(rmc!.IsValid);
            var rmcMap = rmc.Fields.ToDictionary(f => f.Key);
            Assert.True(rmcMap["rmc.lat"].NumberValue < 0);
            Assert.True(rmcMap["rmc.lon"].NumberValue < 0);
            Assert.Equal(18.52, rmcMap["rmc.speedKmh"].NumberValue);
        }

        [Fact]
        public void TryParse_ReportsInvalidFrameWhenChecksumMismatch()
        {
            var parser = new NmeaProtocolParser();

            // 故意改错末尾校验和（正确应为 25）
            var badLine = "$GPTXT,01,01,01,ANTENNA OPEN*FF";
            Assert.True(parser.TryParse(MakeFrame(badLine), out var parsed));
            Assert.NotNull(parsed);
            Assert.False(parsed!.IsValid);
            Assert.Contains("校验和错误", parsed.ErrorMessage);
        }

        [Fact]
        public void TryParse_IgnoresOrdinaryMcuLogLines()
        {
            var parser = new NmeaProtocolParser();

            Assert.False(parser.TryParse(MakeFrame("18633\t[802-NotDefined]\thsm_gps_state_power_on_writeparam==>EXIT"), out _));
            Assert.False(parser.TryParse(MakeFrame("agps url: http://agps.loctube.com:10009/agps/url"), out _));
        }

        private static string BuildSentence(string body)
        {
            byte cs = 0;
            foreach (var ch in body)
            {
                cs ^= (byte)ch;
            }

            return $"${body}*{cs:X2}";
        }

        private static RawFrame MakeFrame(string text) => new(
            ChannelId: "serial-com17",
            Direction: PacketDirection.Rx,
            TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload: Encoding.UTF8.GetBytes(text));
    }
}
