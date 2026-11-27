using System.Text;
using LinkStudio.Core.Bus;
using Serilog;

namespace LinkStudio.Core.Services
{
    /// <summary>
    /// 串口文本日志的可扩展列定义。
    /// 未来需要增加新列（例如序号、协议解析字段、来源设备名）时，只需实现本接口并注入即可。
    /// </summary>
    public interface ISerialLogColumn
    {
        string Name { get; }

        /// <summary>
        /// 返回当前数据包在这一列的文本；返回 null 或空串时该列会被省略。
        /// </summary>
        string? Format(RawDataPacket packet);
    }

    /// <summary>
    /// 串口日志记录选项：决定写入二进制还是文本，以及文本模式下输出哪些列。
    /// </summary>
    public sealed record SerialLogOptions(
        bool IsBinary,
        bool ShowTimestamp,
        bool ShowDirection,
        bool ShowPayload,
        bool FormatPayloadAsHex,
        IReadOnlyList<ISerialLogColumn>? ExtraColumns = null);

    /// <summary>
    /// 串口收发数据日志记录器（RawDataBus 的独立订阅者）。
    /// 依赖总线为每个订阅者提供的串行后台 Worker 异步写盘，不阻塞高速串口收发。
    /// </summary>
    public sealed class SerialLogRecorder : IAsyncDisposable
    {
        private readonly FileStream _stream;
        private readonly IAsyncDisposable _subscription;
        private readonly Func<SerialLogOptions> _optionsProvider;
        private readonly bool _isBinary;
        private int _disposed;

        public string FilePath { get; }

        public SerialLogRecorder(
            IRawDataBus bus,
            string channelId,
            string directory,
            string sourceLabel,
            Func<SerialLogOptions> optionsProvider)
        {
            ArgumentNullException.ThrowIfNull(bus);
            ArgumentNullException.ThrowIfNull(optionsProvider);

            if (string.IsNullOrWhiteSpace(channelId))
            {
                throw new ArgumentException("通道 ID 不能为空。", nameof(channelId));
            }

            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException("日志目录不能为空。", nameof(directory));
            }

            _optionsProvider = optionsProvider;
            var initialOptions = optionsProvider();
            _isBinary = initialOptions.IsBinary;

            Directory.CreateDirectory(directory);
            var safeLabel = SanitizeFileNameSegment(string.IsNullOrWhiteSpace(sourceLabel) ? channelId : sourceLabel);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var extension = _isBinary ? ".bin" : ".log";
            FilePath = Path.Combine(directory, $"{safeLabel}_{stamp}{extension}");

            _stream = new FileStream(
                FilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            // 日志记录器使用 Wait 背压策略，确保突发流量下尽可能不丢数据。
            _subscription = bus.Subscribe(
                new RawDataSubscriptionOptions(channelId, Capacity: 4096, OverflowPolicy: RawDataOverflowPolicy.Wait),
                WritePacketAsync);

            Log.Information("串口日志记录已启动：{FilePath}", FilePath);
        }

        private async ValueTask WritePacketAsync(RawDataPacket packet, CancellationToken ct)
        {
            if (_isBinary)
            {
                if (!packet.Payload.IsEmpty)
                {
                    await _stream.WriteAsync(packet.Payload, ct);
                }

                return;
            }

            var options = _optionsProvider();
            var line = FormatTextLine(packet, options);
            if (line.Length == 0)
            {
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
            await _stream.WriteAsync(bytes, ct);
        }

        /// <summary>
        /// 按当前启用的列与扩展列动态拼接单行日志，未开启的列不会写入文件。
        /// </summary>
        public static string FormatTextLine(RawDataPacket packet, SerialLogOptions options)
        {
            var columns = new List<string>(4);

            if (options.ShowTimestamp)
            {
                var time = DateTimeOffset.FromUnixTimeMilliseconds(packet.TimestampMs)
                    .ToLocalTime()
                    .ToString("HH:mm:ss.fff");
                columns.Add($"[{time}]");
            }

            if (options.ShowDirection)
            {
                var dir = packet.Direction == PacketDirection.Tx ? "TX" : "RX";
                columns.Add($"[{dir}]");
            }

            if (options.ExtraColumns is not null)
            {
                foreach (var column in options.ExtraColumns)
                {
                    var value = column.Format(packet);
                    if (!string.IsNullOrEmpty(value))
                    {
                        columns.Add(value);
                    }
                }
            }

            if (options.ShowPayload)
            {
                var payloadText = options.FormatPayloadAsHex
                    ? BitConverter.ToString(packet.Payload.ToArray()).Replace("-", " ", StringComparison.Ordinal)
                    : Encoding.UTF8.GetString(packet.Payload.Span);
                columns.Add(payloadText);
            }

            return string.Join(" ", columns);
        }

        private static string SanitizeFileNameSegment(string raw)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(raw.Length);
            foreach (var ch in raw)
            {
                builder.Append(invalidChars.Contains(ch) ? '_' : ch);
            }

            return builder.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                // 先退订并等待总线把邮箱里剩余的包全部写完，保证不丢尾包。
                await _subscription.DisposeAsync();
            }
            finally
            {
                try
                {
                    await _stream.FlushAsync();
                }
                finally
                {
                    await _stream.DisposeAsync();
                }

                Log.Information("串口日志记录已停止：{FilePath}", FilePath);
            }
        }
    }
}
