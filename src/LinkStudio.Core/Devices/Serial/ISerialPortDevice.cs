using LinkStudio.Core.Models;

namespace LinkStudio.Core.Devices.Serial
{
    /// <summary>
    /// 串口设备的对外契约（"一个串口设备怎么操作"）。
    ///
    /// 为什么需要接口：
    /// 上层（ViewModel / SerialConnectionService）只依赖本接口编程，
    /// 不直接接触 System.IO.Ports.SerialPort 实例，也不关心内部
    /// 究竟是"命令队列 + Worker"还是别的实现——这就是依赖倒置。
    ///
    /// 实现类（SerialPortDevice）必须实现本接口的全部成员，缺一个都无法编译。
    /// </summary>
    public interface ISerialPortDevice : IDisposable
    {
        /// <summary>
        /// 串口当前是否已打开。
        /// </summary>
        /// <remarks>
        /// 只读属性：打开/关闭状态由实现类内部维护，外部只能查询。
        /// 界面上的"连接/断开"按钮文案、发送按钮是否可用，都依赖此属性。
        /// </remarks>
        bool IsOpen { get; }

        /// <summary>
        /// 使用指定配置打开串口。
        /// </summary>
        /// <param name="config">端口、波特率、数据位、停止位、校验位、流控等参数。</param>
        /// <param name="cancellationToken">取消打开操作的令牌；默认值（default）表示不可取消。</param>
        /// <remarks>
        /// 打开失败（端口被占用、设备不存在、配置非法等）时抛出异常，
        /// 由调用方捕获并决定如何提示用户——本接口约定"不静默失败"。
        ///
        /// 注意：内部真正的 SerialPort.Open() 是同步 API，但这里暴露为异步方法，
        /// 因为调用方等待的是"把 Open 命令送入 Worker 并等 Worker 完成"，而不是单纯的同步打开动作。
        /// </remarks>
        Task OpenAsync(SerialPortConfig config, CancellationToken cancellationToken = default);

        /// <summary>
        /// 关闭串口。
        /// </summary>
        /// <param name="cancellationToken">取消关闭操作的令牌；默认值（default）表示不可取消。</param>
        /// <remarks>未打开时调用是安全的（无操作）。</remarks>
        Task CloseAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 异步写入原始字节。
        /// </summary>
        /// <param name="data">要发送的字节。</param>
        /// <param name="cancellationToken">取消写入的令牌；默认值（default）表示不可取消。</param>
        /// <remarks>
        /// 异步方法：调用方应使用 await，禁止用 .Result / .Wait() 阻塞 UI 线程。
        /// 串口未打开时抛出 <see cref="InvalidOperationException"/>。
        /// </remarks>
        Task WriteAsync(byte[] data, CancellationToken cancellationToken = default);

        /// <summary>
        /// 串口收到数据时触发，载荷为本次读到的原始字节。
        /// </summary>
        /// <remarks>
        /// 载荷是"原始字节"（byte[]），本接口不做 Hex/ASCII 转换、不做帧切分——
        /// 那属于上层（FrameParser / ViewModel）的职责，Device 保持纯粹的数据搬运。
        ///
        /// 线程要求：事件在后台线程（Serial Worker）触发，订阅方若需更新 UI，
        /// 必须自行调度回 UI 线程（Dispatcher）。
        /// </remarks>
        event EventHandler<byte[]>? DataReceived;

        /// <summary>
        /// 串口发送数据时触发，载荷为本次发送的原始字节。
        /// </summary>
        /// <remarks>
        /// 载荷是"原始字节"（byte[]），本接口不做 Hex/ASCII 转换、不做帧切分——
        /// 那属于上层（FrameParser / ViewModel）的职责，Device 保持纯粹的数据搬运。
        ///
        /// 线程要求：同 <see cref="DataReceived"/>。
        /// </remarks>
        event EventHandler<byte[]>? DataSent;

        /// <summary>
        /// 串口发送或者接收过程中抛出的异常
        /// </summary>
        event EventHandler? Faulted;
    }
}
