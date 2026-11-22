namespace LinkStudio.Core.Devices.Virtual
{
    /// <summary>
    /// 所有虚拟 MCU 命令的共同父类型。
    /// 有了这个父类型，Open、Close、Send、Tick 才能放进同一个命令队列，
    /// 再由唯一的命令 Worker 按照入队顺序逐个处理。
    /// abstract 表示只能创建下面的具体命令，不能创建一条含义不明确的“普通命令”。
    /// </summary>
    internal abstract record VirtualMcuCommand;

    /// <summary>
    /// 打开设备。Completion 是回执单，处理成功、失败或取消后由 Worker 填写；
    /// CancellationToken 保存调用方传进来的取消信号。
    /// </summary>
    internal sealed record OpenVirtualMcuCommand(
        TaskCompletionSource Completion,
        CancellationToken CancellationToken) : VirtualMcuCommand;

    /// <summary>
    /// 关闭设备。关闭过程需要停止定时器并排空已产生的数据，所以不允许中途取消。
    /// </summary>
    internal sealed record CloseVirtualMcuCommand(
        TaskCompletionSource Completion) : VirtualMcuCommand;

    /// <summary>
    /// 发送原始字节。Payload 是要发送的数据，另外两个参数分别是回执单和取消信号。
    /// </summary>
    internal sealed record SendVirtualMcuCommand(
        byte[] Payload,
        TaskCompletionSource Completion,
        CancellationToken CancellationToken) : VirtualMcuCommand;

    /// <summary>
    /// 定时器产生的内部命令。没有外部调用方等待结果，所以不需要回执单；
    /// 定时器只产生命令，不直接修改设备状态，也不直接写入 Bus。
    /// </summary>
    internal sealed record TickVirtualMcuCommand : VirtualMcuCommand;
}
