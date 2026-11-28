using LinkStudio.Core.Bus.Metadata;

namespace LinkStudio.Core.Bus
{
    /// <summary>原始数据的收发方向。Rx 表示接收，Tx 表示发送。</summary>
    public enum PacketDirection : byte { Rx, Tx }

    /// <summary>通道的类别。同一类别可以有多个不同 ID 的通道实例。</summary>
    public enum ChannelKind : byte { Serial, VirtualMcu, TcpClient, TcpServer, Udp, HttpServer, MqttBroker, LogReplay }

    /// <summary>在总线中传递的原始数据包，可由多个订阅者共同读取。</summary>
    /// <param name="ChannelId">通道实例的 ID，用于匹配订阅过滤条件。</param>
    /// <param name="ChannelKind">通道类别，例如串口、HTTP 或 MQTT。</param>
    /// <param name="Direction">数据的收发方向。</param>
    /// <param name="TimestampMs">由通道记录的数据收发时间，单位为毫秒。</param>
    /// <param name="SeqNo">由通道分配的序号，用于判断同一通道内的顺序。</param>
    /// <param name="Payload">原始字节。发布后可能仍有订阅者在读取，生产者不得修改或复用其底层缓冲区。</param>
    /// <param name="Meta">跟随数据包传递的通道附加信息。不需要时为 <see langword="null"/>。</param>
    public readonly record struct RawDataPacket(
        string ChannelId,
        ChannelKind ChannelKind,
        PacketDirection Direction,
        long TimestampMs,
        uint SeqNo,
        ReadOnlyMemory<byte> Payload,
        PacketMeta? Meta = null);
}
