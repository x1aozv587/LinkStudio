using LinkStudio.Core.Bus;

namespace LinkStudio.Core.Protocols
{
    /// <summary>
    /// 分帧器接口：负责将串口/网络收到的零散字节流重组为边界完整的 RawFrame。
    /// 只在单个 ProtocolPipeline 的后台 Worker 中串行调用，实现类无需额外加锁。
    /// </summary>
    public interface IFramer
    {
        void Feed(RawDataPacket packet, List<RawFrame> output);

        void Reset();
    }
}
