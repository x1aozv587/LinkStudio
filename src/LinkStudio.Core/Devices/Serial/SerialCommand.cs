using System.Threading.Tasks;
using LinkStudio.Core.Models;

namespace LinkStudio.Core.Devices.Serial
{
    /// <summary>
    /// 串口 Worker 要处理的所有命令的基类。
    /// 用 record 是因为命令是"一次性的、不可变的消息"。
    /// </summary>
    public abstract record SerialCommand;

    /// <summary>
    /// 打开串口。
    /// </summary>
    /// <param name="Config">串口配置。</param>
    /// <param name="Completion">命令的"回执单"，Worker 执行完后填结果。</param>
    public sealed record OpenCommand(SerialPortConfig Config, TaskCompletionSource Completion) : SerialCommand;

    /// <summary>
    /// 关闭串口。
    /// </summary>
    public sealed record CloseCommand(TaskCompletionSource Completion) : SerialCommand;

    /// <summary>
    /// 写数据。
    /// </summary>
    /// <param name="Data">要发送的原始字节。</param>
    /// <param name="Completion">命令的"回执单"，Worker 执行完后填结果。</param>
    /// <param name="CancellationToken">
    /// 调用方的取消令牌。Worker 在动手写之前用它判断命令是否已作废，已取消的直接跳过不写。
    /// 注意：串口写一旦开始就无法中途收回，所以令牌只对"尚未开始"的写生效。
    /// </param>
    public sealed record WriteCommand(
        byte[] Data,
        TaskCompletionSource Completion,
        CancellationToken CancellationToken) : SerialCommand;

    /// <summary>
    /// 内部信号：串口有数据可读了，让 Worker 去读。
    /// 不是外部入队的命令，所以没有 Completion。
    /// </summary>
    public sealed record ReadAvailableCommand : SerialCommand;
}
