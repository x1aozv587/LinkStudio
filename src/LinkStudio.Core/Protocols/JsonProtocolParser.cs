using System.Text;
using System.Text.Json;

namespace LinkStudio.Core.Protocols
{
    /// <summary>
    /// JSON 协议解析器（同时作为实现 IProtocolParser 的参考范例）。
    /// 支持行内提取 {...}（兼容前面带固件时间戳/模块前缀的日志），并自动将嵌套对象展平为点分键名。
    /// </summary>
    public sealed class JsonProtocolParser : IProtocolParser
    {
        public string Name => "JSON";

        public bool TryParse(RawFrame frame, out ParsedFrame? parsed)
        {
            parsed = null;
            if (frame.Payload.IsEmpty)
            {
                return false;
            }

            var rawText = Encoding.UTF8.GetString(frame.Payload.Span).Trim();
            if (!TryExtractJsonObject(rawText, out var jsonSlice))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(jsonSlice);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var fields = new List<ParsedField>();
                FlattenObject(document.RootElement, prefix: string.Empty, fields);

                parsed = new ParsedFrame(
                    ChannelId: frame.ChannelId,
                    ProtocolName: Name,
                    FrameType: "JSON",
                    TimestampMs: frame.TimestampMs,
                    IsValid: true,
                    RawText: jsonSlice,
                    Fields: fields);
                return true;
            }
            catch (JsonException)
            {
                // 包含花括号但不是合法 JSON（例如普通日志里的 "{0}"），静默忽略以免误报。
                return false;
            }
        }

        private static bool TryExtractJsonObject(string text, out string jsonSlice)
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                jsonSlice = string.Empty;
                return false;
            }

            jsonSlice = text.Substring(start, end - start + 1);
            return true;
        }

        private static void FlattenObject(JsonElement element, string prefix, List<ParsedField> fields)
        {
            foreach (var property in element.EnumerateObject())
            {
                var key = string.IsNullOrEmpty(prefix)
                    ? property.Name
                    : $"{prefix}.{property.Name}";

                AppendValue(key, property.Value, fields);
            }
        }

        private static void AppendValue(string key, JsonElement value, List<ParsedField> fields)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Number:
                    if (value.TryGetDouble(out var number))
                    {
                        fields.Add(ParsedField.Number(key, number));
                    }
                    break;

                case JsonValueKind.String:
                    fields.Add(ParsedField.Text(key, value.GetString() ?? string.Empty));
                    break;

                case JsonValueKind.True:
                    fields.Add(ParsedField.Bool(key, true));
                    break;

                case JsonValueKind.False:
                    fields.Add(ParsedField.Bool(key, false));
                    break;

                case JsonValueKind.Null:
                    fields.Add(ParsedField.Null(key));
                    break;

                case JsonValueKind.Object:
                    FlattenObject(value, key, fields);
                    break;

                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        AppendValue($"{key}[{index++}]", item, fields);
                    }
                    break;
            }
        }
    }
}
