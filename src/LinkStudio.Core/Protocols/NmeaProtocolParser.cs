using System.Globalization;
using System.Text;

namespace LinkStudio.Core.Protocols
{
    /// <summary>
    /// 标准 NMEA-0183 协议解析器（兼容多星座 GP/GN/BD/GL/GA 与中科微 PCAS 等私有语句）。
    /// 支持行内自动提取 $...*XX（即使前面混有固件日志前缀），执行 XOR 校验和验证并容忍未定位时的空字段。
    /// </summary>
    public sealed class NmeaProtocolParser : IProtocolParser
    {
        private const double KnotsToKmh = 1.852;

        public string Name => "NMEA-0183";

        public bool TryParse(RawFrame frame, out ParsedFrame? parsed)
        {
            parsed = null;
            if (frame.Payload.IsEmpty)
            {
                return false;
            }

            var rawLine = Encoding.UTF8.GetString(frame.Payload.Span);
            if (!TryExtractSentence(rawLine, out var sentence, out var body, out var expectedChecksum))
            {
                return false;
            }

            var parts = body.Split(',');
            if (parts.Length == 0 || parts[0].Length < 3)
            {
                return false;
            }

            var header = parts[0].ToUpperInvariant();
            var isProprietary = header.StartsWith('P') && header.Length >= 4;
            var talker = isProprietary ? "P" : header[..2];
            var sentenceType = isProprietary ? header : header[2..];

            var actualChecksum = ComputeXorChecksum(body);
            if (actualChecksum != expectedChecksum)
            {
                parsed = new ParsedFrame(
                    ChannelId: frame.ChannelId,
                    ProtocolName: Name,
                    FrameType: sentenceType,
                    TimestampMs: frame.TimestampMs,
                    IsValid: false,
                    RawText: sentence,
                    Fields: [],
                    ErrorMessage: $"校验和错误（报文 {expectedChecksum:X2}，计算 {actualChecksum:X2}）");
                return true;
            }

            var fields = new List<ParsedField>();
            fields.Add(ParsedField.Text("nmea.talker", talker));

            if (isProprietary)
            {
                ParseProprietary(header, parts, fields);
            }
            else
            {
                switch (sentenceType)
                {
                    case "GGA":
                        ParseGga(parts, fields);
                        break;
                    case "RMC":
                        ParseRmc(parts, fields);
                        break;
                    case "GSA":
                        ParseGsa(parts, fields);
                        break;
                    case "GSV":
                        ParseGsv(talker, parts, fields);
                        break;
                    case "GLL":
                        ParseGll(parts, fields);
                        break;
                    case "VTG":
                        ParseVtg(parts, fields);
                        break;
                    case "ZDA":
                        ParseZda(parts, fields);
                        break;
                    case "TXT":
                        ParseTxt(parts, fields);
                        break;
                    default:
                        if (parts.Length > 1)
                        {
                            fields.Add(ParsedField.Text("nmea.payload", string.Join(',', parts.Skip(1))));
                        }
                        break;
                }
            }

            parsed = new ParsedFrame(
                ChannelId: frame.ChannelId,
                ProtocolName: Name,
                FrameType: sentenceType,
                TimestampMs: frame.TimestampMs,
                IsValid: true,
                RawText: sentence,
                Fields: fields);
            return true;
        }

        private static bool TryExtractSentence(
            string line,
            out string sentence,
            out string body,
            out byte expectedChecksum)
        {
            sentence = string.Empty;
            body = string.Empty;
            expectedChecksum = 0;

            var dollarIndex = line.IndexOf('$');
            if (dollarIndex < 0)
            {
                return false;
            }

            var starIndex = line.IndexOf('*', dollarIndex + 1);
            if (starIndex <= dollarIndex + 1 || starIndex + 2 >= line.Length)
            {
                return false;
            }

            var checksumHex = line.Substring(starIndex + 1, 2);
            if (!byte.TryParse(checksumHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out expectedChecksum))
            {
                return false;
            }

            body = line.Substring(dollarIndex + 1, starIndex - dollarIndex - 1);
            sentence = line.Substring(dollarIndex, starIndex - dollarIndex + 3);
            return true;
        }

        private static byte ComputeXorChecksum(string body)
        {
            byte checksum = 0;
            for (var i = 0; i < body.Length; i++)
            {
                checksum ^= (byte)body[i];
            }

            return checksum;
        }

        private static void ParseGga(string[] parts, List<ParsedField> fields)
        {
            AddIfNotEmpty(fields, "gga.utc", GetPart(parts, 1));

            if (TryParseCoordinate(GetPart(parts, 2), GetPart(parts, 3), degreeDigits: 2, out var lat))
            {
                fields.Add(ParsedField.Number("gga.lat", Math.Round(lat, 7), "°"));
            }

            if (TryParseCoordinate(GetPart(parts, 4), GetPart(parts, 5), degreeDigits: 3, out var lon))
            {
                fields.Add(ParsedField.Number("gga.lon", Math.Round(lon, 7), "°"));
            }

            AddDoubleIfPresent(fields, "gga.fixQuality", GetPart(parts, 6));
            AddDoubleIfPresent(fields, "gga.satellites", GetPart(parts, 7));
            AddDoubleIfPresent(fields, "gga.hdop", GetPart(parts, 8));
            AddDoubleIfPresent(fields, "gga.altitude", GetPart(parts, 9), "m");
        }

        private static void ParseRmc(string[] parts, List<ParsedField> fields)
        {
            AddIfNotEmpty(fields, "rmc.utc", GetPart(parts, 1));
            AddIfNotEmpty(fields, "rmc.status", GetPart(parts, 2));

            if (TryParseCoordinate(GetPart(parts, 3), GetPart(parts, 4), degreeDigits: 2, out var lat))
            {
                fields.Add(ParsedField.Number("rmc.lat", Math.Round(lat, 7), "°"));
            }

            if (TryParseCoordinate(GetPart(parts, 5), GetPart(parts, 6), degreeDigits: 3, out var lon))
            {
                fields.Add(ParsedField.Number("rmc.lon", Math.Round(lon, 7), "°"));
            }

            if (TryParseDouble(GetPart(parts, 7), out var knots))
            {
                fields.Add(ParsedField.Number("rmc.speedKnots", knots, "kn"));
                fields.Add(ParsedField.Number("rmc.speedKmh", Math.Round(knots * KnotsToKmh, 2), "km/h"));
            }

            AddDoubleIfPresent(fields, "rmc.course", GetPart(parts, 8), "°");
            AddIfNotEmpty(fields, "rmc.date", GetPart(parts, 9));
        }

        private static void ParseGsa(string[] parts, List<ParsedField> fields)
        {
            AddIfNotEmpty(fields, "gsa.mode", GetPart(parts, 1));
            AddDoubleIfPresent(fields, "gsa.fixType", GetPart(parts, 2));
            AddDoubleIfPresent(fields, "gsa.pdop", GetPart(parts, 15));
            AddDoubleIfPresent(fields, "gsa.hdop", GetPart(parts, 16));
            AddDoubleIfPresent(fields, "gsa.vdop", GetPart(parts, 17));

            // NMEA 4.11 在末尾附带星座 System ID（1=GPS, 2=GLONASS, 3=Galileo, 4=BDS）。
            var sysId = GetPart(parts, 18);
            if (!string.IsNullOrEmpty(sysId))
            {
                fields.Add(ParsedField.Text("gsa.systemId", sysId));
            }
        }

        private static void ParseGsv(string talker, string[] parts, List<ParsedField> fields)
        {
            AddDoubleIfPresent(fields, $"gsv.{talker}.inView", GetPart(parts, 3));

            // 从第 4 个字段起每 4 个为一组（PRN、仰角、方位角、SNR），末尾可能带 NMEA 4.11 的 signalId。
            for (var i = 4; i + 3 < parts.Length; i += 4)
            {
                var prn = parts[i];
                if (string.IsNullOrEmpty(prn))
                {
                    continue;
                }

                AddDoubleIfPresent(fields, $"gsv.{talker}.{prn}.elev", parts[i + 1], "°");
                AddDoubleIfPresent(fields, $"gsv.{talker}.{prn}.azim", parts[i + 2], "°");
                AddDoubleIfPresent(fields, $"gsv.{talker}.{prn}.snr", parts[i + 3], "dB");
            }
        }

        private static void ParseGll(string[] parts, List<ParsedField> fields)
        {
            if (TryParseCoordinate(GetPart(parts, 1), GetPart(parts, 2), degreeDigits: 2, out var lat))
            {
                fields.Add(ParsedField.Number("gll.lat", Math.Round(lat, 7), "°"));
            }

            if (TryParseCoordinate(GetPart(parts, 3), GetPart(parts, 4), degreeDigits: 3, out var lon))
            {
                fields.Add(ParsedField.Number("gll.lon", Math.Round(lon, 7), "°"));
            }

            AddIfNotEmpty(fields, "gll.utc", GetPart(parts, 5));
            AddIfNotEmpty(fields, "gll.status", GetPart(parts, 6));
        }

        private static void ParseVtg(string[] parts, List<ParsedField> fields)
        {
            AddDoubleIfPresent(fields, "vtg.courseTrue", GetPart(parts, 1), "°");
            AddDoubleIfPresent(fields, "vtg.speedKnots", GetPart(parts, 5), "kn");
            AddDoubleIfPresent(fields, "vtg.speedKmh", GetPart(parts, 7), "km/h");
        }

        private static void ParseZda(string[] parts, List<ParsedField> fields)
        {
            AddIfNotEmpty(fields, "zda.utc", GetPart(parts, 1));
            var day = GetPart(parts, 2);
            var month = GetPart(parts, 3);
            var year = GetPart(parts, 4);
            if (!string.IsNullOrEmpty(year) && !string.IsNullOrEmpty(month) && !string.IsNullOrEmpty(day))
            {
                fields.Add(ParsedField.Text("zda.date", $"{year}-{month}-{day}"));
            }
        }

        private static void ParseTxt(string[] parts, List<ParsedField> fields)
        {
            AddIfNotEmpty(fields, "txt.level", GetPart(parts, 3));
            if (parts.Length > 4)
            {
                var message = string.Join(',', parts.Skip(4));
                if (!string.IsNullOrEmpty(message))
                {
                    fields.Add(ParsedField.Text("txt.message", message));
                }
            }
        }

        private static void ParseProprietary(string command, string[] parts, List<ParsedField> fields)
        {
            fields.Add(ParsedField.Text("proprietary.command", command));
            if (parts.Length > 1)
            {
                fields.Add(ParsedField.Text("proprietary.args", string.Join(',', parts.Skip(1))));
            }
        }

        private static bool TryParseCoordinate(string raw, string hemisphere, int degreeDigits, out double decimalDegrees)
        {
            decimalDegrees = 0;
            if (string.IsNullOrEmpty(raw) || raw.Length <= degreeDigits)
            {
                return false;
            }

            var degPart = raw[..degreeDigits];
            var minPart = raw[degreeDigits..];
            if (!double.TryParse(degPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var degrees)
                || !double.TryParse(minPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
            {
                return false;
            }

            decimalDegrees = degrees + minutes / 60.0;
            if (string.Equals(hemisphere, "S", StringComparison.OrdinalIgnoreCase)
                || string.Equals(hemisphere, "W", StringComparison.OrdinalIgnoreCase))
            {
                decimalDegrees = -decimalDegrees;
            }

            return true;
        }

        private static string GetPart(string[] parts, int index) =>
            index >= 0 && index < parts.Length ? parts[index] : string.Empty;

        private static bool TryParseDouble(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private static void AddDoubleIfPresent(List<ParsedField> fields, string key, string raw, string? unit = null)
        {
            if (TryParseDouble(raw, out var value))
            {
                fields.Add(ParsedField.Number(key, value, unit));
            }
        }

        private static void AddIfNotEmpty(List<ParsedField> fields, string key, string raw)
        {
            if (!string.IsNullOrEmpty(raw))
            {
                fields.Add(ParsedField.Text(key, raw));
            }
        }
    }
}
