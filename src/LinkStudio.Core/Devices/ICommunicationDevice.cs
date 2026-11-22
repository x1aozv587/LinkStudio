using LinkStudio.Core.Bus;

namespace LinkStudio.Core.Devices
{
    /// <summary>
    /// 可以打开、关闭和收发原始字节的通信设备。
    /// 具体设备负责自己的 I/O，并把收发结果发布到 RawDataBus。
    /// </summary>
    public interface ICommunicationDevice : IAsyncDisposable
    {
        string Id { get; }
        string Name { get; }
        ChannelKind Kind { get; }
        bool IsOpen { get; }
        Task OpenAsync(CancellationToken ct = default);
        Task CloseAsync();
        Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    }
}
