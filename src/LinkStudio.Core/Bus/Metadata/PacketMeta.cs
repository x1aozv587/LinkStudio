namespace LinkStudio.Core.Bus.Metadata
{
    /// <summary>各类通道附加信息的公共基类型。订阅者可按需判断具体类型并读取对应字段。</summary>
    public abstract record PacketMeta;
}
