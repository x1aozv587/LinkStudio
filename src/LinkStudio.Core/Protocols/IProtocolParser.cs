namespace LinkStudio.Core.Protocols
{
    /// <summary>
    /// 协议解析器统一契约（纯逻辑，不碰线程与串口）。
    /// </summary>
    public interface IProtocolParser
    {
        /// <summary>协议名称，例如 "JSON"、"NMEA-0183"。</summary>
        string Name { get; }

        /// <summary>
        /// 尝试解析一帧完整数据。
        /// 返回 false 表示该帧不属于本协议（例如普通调试日志），管线会交给下一个解析器或忽略；
        /// 返回 true 表示本协议已认领该帧（即便校验失败也可返回 IsValid = false 的 ParsedFrame 供界面提示）。
        /// </summary>
        bool TryParse(RawFrame frame, out ParsedFrame? parsed);
    }
}
