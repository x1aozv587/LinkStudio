namespace LinkStudio.Core.Devices.Serial
{
    /// <summary>串口命令的基类。打开、关闭、发送和可读通知都由同一个 Worker 处理。</summary>
    internal abstract record SerialCommand;

    /// <summary>按构造时保存的配置打开串口。命令 Worker 在调用同步 Open 前检查取消。</summary>
    internal sealed record OpenSerialCommand(
        TaskCompletionSource Completion,
        CancellationToken CancellationToken) : SerialCommand;

    /// <summary>关闭串口。发布 Worker 处理完此前入队的数据包后填写回执。</summary>
    internal sealed record CloseSerialCommand(
        TaskCompletionSource Completion) : SerialCommand;

    /// <summary>
    /// 发送原始字节。Payload 是入队前复制的副本，发送成功时由发布 Worker 在 TX 包进入总线后填写回执。
    /// </summary>
    internal sealed record SendSerialCommand(
        byte[] Payload,
        TaskCompletionSource Completion,
        CancellationToken CancellationToken) : SerialCommand;

    /// <summary>串口事件投递的可读通知。Worker 根据当前可读字节数读取，无需回执。</summary>
    internal sealed record ReadAvailableSerialCommand : SerialCommand;
}
