using LinkStudio.Core.Bus;

namespace LinkStudio.Core.Protocols
{
    /// <summary>
    /// 经过分帧器（IFramer）重组后的完整原始帧。
    /// 相比 RawDataPacket 的零散字节块，RawFrame 保证是一条边界完整的消息。
    /// </summary>
    public sealed record RawFrame(
        string ChannelId,
        PacketDirection Direction,
        long TimestampMs,
        ReadOnlyMemory<byte> Payload);

    /// <summary>
    /// 结构化字段的值类型，供 V0.3 表格展示与 V0.4 折线图/仪表盘自动识别可绘图字段。
    /// </summary>
    public enum ParsedFieldKind
    {
        Number,
        String,
        Boolean,
        Null
    }

    /// <summary>
    /// 协议解析产出的单个键值对字段。
    /// </summary>
    public sealed record ParsedField(
        string Key,
        ParsedFieldKind Kind,
        double? NumberValue = null,
        string? TextValue = null,
        bool? BoolValue = null,
        string? Unit = null)
    {
        public static ParsedField Number(string key, double value, string? unit = null) =>
            new(key, ParsedFieldKind.Number, NumberValue: value, Unit: unit);

        public static ParsedField Text(string key, string value, string? unit = null) =>
            new(key, ParsedFieldKind.String, TextValue: value, Unit: unit);

        public static ParsedField Bool(string key, bool value) =>
            new(key, ParsedFieldKind.Boolean, BoolValue: value);

        public static ParsedField Null(string key) =>
            new(key, ParsedFieldKind.Null);

        public string DisplayValue => Kind switch
        {
            ParsedFieldKind.Number => NumberValue?.ToString("G") ?? string.Empty,
            ParsedFieldKind.Boolean => BoolValue == true ? "true" : "false",
            ParsedFieldKind.Null => "null",
            _ => TextValue ?? string.Empty
        };
    }

    /// <summary>
    /// 协议解析器输出的统一结构化帧。
    /// 无论底层是 JSON、NMEA 还是自定义二进制协议，最终都归一化为本结构。
    /// </summary>
    public sealed record ParsedFrame(
        string ChannelId,
        string ProtocolName,
        string FrameType,
        long TimestampMs,
        bool IsValid,
        string RawText,
        IReadOnlyList<ParsedField> Fields,
        string? ErrorMessage = null);
}
