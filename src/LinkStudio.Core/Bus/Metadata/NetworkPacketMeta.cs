namespace LinkStudio.Core.Bus.Metadata
{
    /// <summary>TCP 或 UDP 数据包附带的网络信息。</summary>
    /// <param name="RemoteEndpoint">发送时的目标端点，或接收时的来源端点。</param>
    public sealed record NetworkPacketMeta(RemoteEndpoint RemoteEndpoint) : PacketMeta;
}
