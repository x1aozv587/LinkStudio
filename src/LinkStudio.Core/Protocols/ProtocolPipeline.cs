using LinkStudio.Core.Bus;
using Serilog;

namespace LinkStudio.Core.Protocols
{
    /// <summary>
    /// 协议解析管线（RawDataBus 的独立订阅者）。
    /// 串联「分帧器 IFramer → 多协议解析器 IProtocolParser」，并将解析结果异步交给订阅回调。
    /// </summary>
    public sealed class ProtocolPipeline : IAsyncDisposable
    {
        private readonly IFramer _framer;
        private readonly IReadOnlyList<IProtocolParser> _parsers;
        private readonly Func<ParsedFrame, ValueTask> _onParsed;
        private readonly IAsyncDisposable _subscription;
        private readonly List<RawFrame> _frameScratch = new(8);
        private int _disposed;

        public ProtocolPipeline(
            IRawDataBus bus,
            string channelId,
            IFramer framer,
            IReadOnlyList<IProtocolParser> parsers,
            Func<ParsedFrame, ValueTask> onParsed,
            int capacity = 4096)
        {
            ArgumentNullException.ThrowIfNull(bus);
            ArgumentNullException.ThrowIfNull(framer);
            ArgumentNullException.ThrowIfNull(parsers);
            ArgumentNullException.ThrowIfNull(onParsed);

            _framer = framer;
            _parsers = parsers;
            _onParsed = onParsed;

            // 使用 DropOldest 避免协议解析慢时反压拖慢真实串口接收。
            _subscription = bus.Subscribe(
                new RawDataSubscriptionOptions(channelId, Capacity: capacity, OverflowPolicy: RawDataOverflowPolicy.DropOldest),
                HandlePacketAsync);
        }

        private async ValueTask HandlePacketAsync(RawDataPacket packet, CancellationToken ct)
        {
            _frameScratch.Clear();
            _framer.Feed(packet, _frameScratch);

            foreach (var rawFrame in _frameScratch)
            {
                ct.ThrowIfCancellationRequested();

                foreach (var parser in _parsers)
                {
                    ParsedFrame? parsed;
                    try
                    {
                        if (!parser.TryParse(rawFrame, out parsed) || parsed is null)
                        {
                            continue;
                        }
                    }
                    catch (Exception error)
                    {
                        // 单个解析器异常不能中断整个管线。
                        Log.Warning(error, "协议解析器 {Parser} 处理帧失败", parser.Name);
                        continue;
                    }

                    await _onParsed(parsed);
                    break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _subscription.DisposeAsync();
            _framer.Reset();
        }
    }
}
