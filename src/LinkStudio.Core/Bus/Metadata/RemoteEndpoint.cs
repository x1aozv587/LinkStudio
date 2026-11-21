namespace LinkStudio.Core.Bus.Metadata
{
    /// <summary>对端网络地址的只读快照，可以在异步订阅者之间传递。</summary>
    /// <param name="IpAddress">对端的 IPv4 或 IPv6 地址文本。</param>
    /// <param name="Port">对端的端口号。</param>
    public readonly record struct RemoteEndpoint(string IpAddress, int Port);
}
