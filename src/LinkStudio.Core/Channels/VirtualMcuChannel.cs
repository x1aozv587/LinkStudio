using LinkStudio.Core.Bus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LinkStudio.Core.Channels
{
    public class VirtualMcuChannel : IChannels
    {
        public string Id => "ID Virtual MCU Channel";

        public string Name => "Name Virtual MCU Channel";

        public ChannelKind Kind => ChannelKind.VirtualMcu;

        public bool IsOpen  { get; private set; }

        private readonly IRawDataBus _dataBus;

        private uint _seq;

        public VirtualMcuChannel(IRawDataBus bus)
        {
            _dataBus = bus;
        }

        public Task CloseAsync()
        {
            Task ret = Task.CompletedTask;

            if (IsOpen == true)
            {
                IsOpen = false;
            }

            return ret;
        }

        public async ValueTask DisposeAsync()
        {
            await CloseAsync();
        }

        public async Task OpenAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (IsOpen == true)
            {
                return;
            }

            var packet = new RawDataPacket(
                ChannelId: Id,
                ChannelKind: Kind,
                Direction: PacketDirection.Rx,
                TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SeqNo: _seq,
                Payload: Encoding.UTF8.GetBytes("Virtual MCU Channel ready")
                );

            await _dataBus.PublishAsync(packet, ct);
            _seq++;
            IsOpen = true;
        }
    }
}
