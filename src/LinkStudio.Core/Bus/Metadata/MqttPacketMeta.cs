namespace LinkStudio.Core.Bus.Metadata
{
    /// <summary>MQTT 数据包附带的发布信息。</summary>
    /// <param name="MqttTopic">消息主题，例如 device/status。未记录时为 <see langword="null"/>。</param>
    /// <param name="MqttQoS">消息的服务质量等级（0、1 或 2）。未记录时为 <see langword="null"/>。</param>
    /// <param name="MqttClientId">关联的 MQTT 客户端标识。未记录时为 <see langword="null"/>。</param>
    public sealed record MqttPacketMeta(
        string? MqttTopic = null,
        byte? MqttQoS = null,
        string? MqttClientId = null) : PacketMeta;
}
