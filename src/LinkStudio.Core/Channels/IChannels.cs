using LinkStudio.Core.Bus;


namespace LinkStudio.Core.Channels
{
    internal interface IChannels : IAsyncDisposable
    {
        // 
        string Id {  get; }

        // 
        string Name { get; }

        ChannelKind Kind { get; }

        bool IsOpen { get; }

        Task OpenAsync( CancellationToken ct = default );

        Task CloseAsync();
    }
}
