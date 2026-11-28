using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LinkStudio.Core.Bus;

namespace LinkStudio.Core.Devices.Replay
{
    /// <summary>
    /// 日志回放中的单帧数据记录（含相对时间偏移、收发方向与原始字节载荷）。
    /// </summary>
    public sealed record ReplayFrame(
        int Index,
        TimeSpan Offset,
        PacketDirection Direction,
        byte[] Payload);

    /// <summary>
    /// 通用日志回放解析器：兼容 LinkStudio 录制的带时间戳串口日志（.log）以及普通 NMEA / JSON 文本日志。
    /// </summary>
    public static partial class LogReplayParser
    {
        // 匹配常见串口日志前缀，例如：
        // "2026-11-28 14:20:01.123 RX ..." / "[14:20:01.123] [RX] ..." / "14:20:01.123 RX ..."
        [GeneratedRegex(
            @"^\s*(?:\[?(?:\d{4}-\d{2}-\d{2}\s+)?(?<time>\d{2}:\d{2}:\d{2}(?:\.\d{1,3})?)\]?)\s+(?:\[?(?<dir>RX|TX|SYS)\]?\s+)?(?<payload>.*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase)]
        private static partial Regex TimestampedLineRegex();

        // 限制相邻两帧最大回放等待间隔为 2 秒，避免日志中曾暂停录制半小时导致回放卡住。
        private static readonly TimeSpan MaxFrameGap = TimeSpan.FromSeconds(2);

        public static async Task<IReadOnlyList<ReplayFrame>> LoadFileAsync(
            string filePath,
            int defaultStepMs = 100,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            var lines = await File.ReadAllLinesAsync(filePath, Encoding.UTF8, ct);
            return ParseLines(lines, defaultStepMs);
        }

        public static IReadOnlyList<ReplayFrame> ParseLines(
            IEnumerable<string> lines,
            int defaultStepMs = 100)
        {
            ArgumentNullException.ThrowIfNull(lines);
            var stepMs = Math.Clamp(defaultStepMs, 1, 10_000);
            var result = new List<ReplayFrame>();

            TimeSpan? prevRawTimestamp = null;
            var accumulatedOffset = TimeSpan.Zero;
            var index = 0;

            foreach (var rawLine in lines)
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                // 跳过日志文件的注释头（如 "# LinkStudio Serial Log..."）。
                if (rawLine.TrimStart().StartsWith('#'))
                {
                    continue;
                }

                var direction = PacketDirection.Rx;
                var payloadText = rawLine;
                TimeSpan? parsedTime = null;

                var match = TimestampedLineRegex().Match(rawLine);
                if (match.Success)
                {
                    var dirText = match.Groups["dir"].Value;
                    if (string.Equals(dirText, "SYS", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.Equals(dirText, "TX", StringComparison.OrdinalIgnoreCase))
                    {
                        direction = PacketDirection.Tx;
                    }

                    if (TimeSpan.TryParse(match.Groups["time"].Value, CultureInfo.InvariantCulture, out var ts))
                    {
                        parsedTime = ts;
                    }

                    payloadText = match.Groups["payload"].Value;
                }

                if (string.IsNullOrEmpty(payloadText))
                {
                    continue;
                }

                if (index > 0)
                {
                    if (parsedTime.HasValue && prevRawTimestamp.HasValue)
                    {
                        var delta = parsedTime.Value - prevRawTimestamp.Value;
                        if (delta < TimeSpan.Zero)
                        {
                            // 跨午夜时按默认步进补偿，避免负间隔。
                            delta = TimeSpan.FromMilliseconds(stepMs);
                        }
                        else if (delta > MaxFrameGap)
                        {
                            delta = MaxFrameGap;
                        }

                        accumulatedOffset += delta;
                    }
                    else
                    {
                        accumulatedOffset += TimeSpan.FromMilliseconds(stepMs);
                    }
                }

                if (parsedTime.HasValue)
                {
                    prevRawTimestamp = parsedTime.Value;
                }

                // 保证每帧末尾带 CRLF，确保 LineFramer 与协议解析管线能无损切帧。
                var normalized = payloadText.TrimEnd('\r', '\n') + "\r\n";
                var bytes = Encoding.UTF8.GetBytes(normalized);
                result.Add(new ReplayFrame(index, accumulatedOffset, direction, bytes));
                index++;
            }

            return result;
        }
    }
}
