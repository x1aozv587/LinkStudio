namespace LinkStudio.Core.Bus.Metadata
{
    /// <summary>HTTP 数据包附带的请求信息。</summary>
    /// <param name="HttpMethod">HTTP 方法，例如 GET 或 POST。未记录时为 <see langword="null"/>。</param>
    /// <param name="HttpPath">请求路径，例如 /status。未记录时为 <see langword="null"/>。</param>
    /// <param name="RemoteEndpoint">连接对端的 IP 地址和端口。未记录时为 <see langword="null"/>。</param>
    public sealed record HttpPacketMeta(
        string? HttpMethod = null,
        string? HttpPath = null,
        RemoteEndpoint? RemoteEndpoint = null) : PacketMeta;
}
