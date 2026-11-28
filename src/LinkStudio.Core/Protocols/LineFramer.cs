using LinkStudio.Core.Bus;

namespace LinkStudio.Core.Protocols
{
    /// <summary>
    /// 跨包换行分帧器：按 \r / \n 将碎片字节拼成完整一行 RawFrame。
    /// 自动解决串口半包（一条指令拆成两包）和粘包（多条指令挤在一包），并兼容 \r\r\n。
    /// </summary>
    public sealed class LineFramer : IFramer
    {
        private readonly int _maxFrameBytes;
        private readonly bool _includeTx;
        private readonly List<byte> _buffer = new(256);

        public LineFramer(int maxFrameBytes = 8192, bool includeTx = false)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxFrameBytes, 0);
            _maxFrameBytes = maxFrameBytes;
            _includeTx = includeTx;
        }

        public void Feed(RawDataPacket packet, List<RawFrame> output)
        {
            if (!_includeTx && packet.Direction != PacketDirection.Rx)
            {
                return;
            }

            var span = packet.Payload.Span;
            for (var i = 0; i < span.Length; i++)
            {
                var b = span[i];
                if (b == (byte)'\r' || b == (byte)'\n')
                {
                    // 连续的 \r\n 或 \r\r\n 在缓冲为空时直接跳过，不产出空帧。
                    if (_buffer.Count > 0)
                    {
                        output.Add(new RawFrame(
                            packet.ChannelId,
                            packet.Direction,
                            packet.TimestampMs,
                            _buffer.ToArray()));
                        _buffer.Clear();
                    }

                    continue;
                }

                _buffer.Add(b);

                // 防止收到连续二进制乱码（没有换行符）导致缓冲无限增长。
                if (_buffer.Count >= _maxFrameBytes)
                {
                    _buffer.Clear();
                }
            }
        }

        public void Reset() => _buffer.Clear();
    }
}
