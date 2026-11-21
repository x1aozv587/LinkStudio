# LinkStudio — 硬件与物联网一体化调试工作台详细设计文档（v1.3）

> **正式项目名称**：`LinkStudio`（已确认）  
> **技术路线**：全栈 C#（`.NET 8` + Windows `WPF BlazorWebView` + macOS `Blazor Server` + 原始字节广播总线 + Roslyn 热加载 MCU 二进制协议引擎）  
> **复用来源**：`E:\Space\alist3\c#wpf\link\SerialLink`（.NET 8 串口核心、分发、发送与持久化底座）

---

## 1. 项目概述与定位

**LinkStudio** 是一款面向 **MCU 嵌入式开发、硬件产测与物联网联调** 的一体化调试工作台。它突破传统串口助手“只能看枯燥文本、只能 1 对 1 死板配协议、无法与本地网络服务端联动”的局限，以 **“串口调试为主、本地网络服务端为辅、原始字节广播驱动多协议并行自治、自由拖拽组态仪表盘”** 为核心设计思想。

### 核心设计原则
1. **原始字节至上（Raw-Byte First）**：串口与网络通道只负责采集和投递纯粹的原始二进制流，不做内容假设，不强迫转 JSON。
2. **一对多广播自治**：通过 `RawDataBus` 原始数据广播总线，同一根物理串口线上混跑的多种二进制帧与 `printf` 文本日志可由多个独立协议同时各自解包、互不干扰。
3. **面向 MCU 的 C# 协议插件**：直接用 C#（支持 `[StructLayout(LayoutKind.Sequential, Pack = 1)]`、`Span<byte>`、私有转义/校验）编写协议并一键热重载，彻底告别“复杂二进制协议塞不进 JSON 配置模板”的窘境；同时保留简易模板与文本行插件作为补充。
4. **全栈 C# 零胶水层**：Windows 下 UI 与底层驱动运行在同一 `.NET 8` 进程内存中，无 HTTP/WebSocket 序列化损耗，支持从界面按钮事件一路 F11 单步调试进串口驱动与协议代码；macOS 复用同一套 `.razor` UI 以本机浏览器模式运行。
5. **所见即所得的自由工作台**：支持运行模式与编辑模式自由切换，用户可按板卡/测试场景任意拖拽缩放仪表卡片、滑块、指示灯与实时波形图，并一键导出方案（Profile JSON）分享复用。
6. **单一所有者并发模型**：有状态资源统一由一个 Worker Task 持有，跨模块只通过有界 `Channel<T>` 传递不可变消息；串行性依靠唯一消费循环与完整 `await`，不依赖固定物理线程，也不允许多个模块直接共享并修改同一资源。

### 目标平台与视觉基座
- **主力平台**：Windows 10 / 11（WPF + `Microsoft.AspNetCore.Components.WebView.Wpf` 单进程原生桌面程序，体积极小，无端口/后台进程残留）。
- **辅助平台**：macOS（Apple Silicon / Intel，运行同一份后端 + Blazor Server 托管同一套 `LinkStudio.UI`，启动后自动拉起系统默认浏览器，避开跨平台桌面壳 WKWebView 兼容坑）。
- **默认视觉风格**：现代暗黑工程风（参考专业示波器 / Grafana / VS Code，高信息密度、1px 细边框、等宽对齐数字），并通过 CSS 变量支持一键切换浅色主题。

---

## 2. 总体架构与技术栈

### 2.1 分层架构图

```text
┌─────────────────────────────────────────────────────────────────────────┐
│  LinkStudio.UI (.NET 8 共享 Razor Class Library)                         │
│  ├─ 核心视图: 串口/通道终端 | 自由拖拽工作台 | C# 协议工坊 | 本地服务端中心    │
│  ├─ 暗黑 / 浅色 CSS Design Tokens (零 Node/npm 构建依赖)                   │
│  └─ 2 个薄 JS 互操作底座: <DashboardGrid>(GridStack.js) + <WaveformChart>(uPlot) │
└───────────────────▲─────────────────────────────────▲───────────────────┘
                    │ 进程内直接 DI 注入与事件回调         │ SignalR (127.0.0.1)
      ┌─────────────┴─────────────┐     ┌─────────────┴─────────────┐
      │ LinkStudio.Desktop.Win    │     │ LinkStudio.Server         │
      │ (Windows 主力 · net8-win) │     │ (macOS 辅助 · net8.0)     │
      │ WPF + BlazorWebView       │     │ ASP.NET Core Blazor Server│
      │ 自定义无边框标题栏 / 文件框  │     │ 启动后自动打开系统默认浏览器   │
      └─────────────┬─────────────┘     └─────────────┬─────────────┘
                    └─────────────────┬───────────────┘
                                      ▼
┌─────────────────────────────────────────────────────────────────────────┐
│  LinkStudio.Core (.NET 8 纯跨平台核心引擎 · 零 UI 依赖)                    │
│  ├─ Channels     : 串口 / 虚拟MCU源 / TCP C&S / UDP / HTTP S&C / MQTT Broker│
│  ├─ RawDataBus   : 有界异步队列一对多广播总线（Worker 隔离 + 背压策略）       │
│  ├─ Protocols    : Roslyn .cs 热编译器 + Binary/Text SDK + 沙箱测试引擎    │
│  ├─ Pipeline     : 内存环形日志队列 + 异步磁盘落盘 + 30fps 遥测聚合器         │
│  ├─ Automation   : 后端高精度 PeriodicTimer 定时发送与快捷指令调度           │
│  └─ Storage      : Workspace Profile JSON 与系统配置本地持久化             │
└─────────────────────────────────────────────────────────────────────────┘
```

### 2.2 技术选型与核心依赖（零 Node.js / npm 依赖）

| 模块 | 选型方案 | 选用理由 |
| :--- | :--- | :--- |
| 运行时与框架 | `.NET 8 (LTS)` | 长期支持版，C# 12 + 顶级 `Span<T>`/`Memory<T>` 性能，Windows/macOS 跨平台稳定 |
| 桌面壳 (Windows) | `WPF` + `Microsoft.AspNetCore.Components.WebView.Wpf` | 贴合 C#/WPF 经验，复用系统 Edge WebView2 运行时，发布体积小 |
| 辅助端 (macOS) | `ASP.NET Core Blazor Server` | 零修改复用 `LinkStudio.UI` 组件库，启动即开本机浏览器 |
| 串口驱动与热插拔 | `System.IO.Ports` + Windows `HwndSource` (`WM_DEVICECHANGE`) | Windows 复用原 `DeviceChangeMonitor` 的 `HwndSource` 零额外依赖热插拔监听（按需再补充友好设备名）；macOS 轻量轮询并仅枚举 `/dev/cu.*` |
| 日志框架 | `Serilog`（`Serilog.Sinks.Debug` / `Serilog.Sinks.File`） | 延续旧 `SerialLink` 排障体系，技术日志与用户提示分离，Worker 热路径仅打前 64B HEX 预览 |
| 协议动态编译 | `Microsoft.CodeAnalysis.CSharp` (Roslyn) + 可卸载 `AssemblyLoadContext` | 支持 `protocols/*.cs` 改完一键热重载编译并卸载旧程序集防内存泄漏 |
| 内置 MQTT Server | `MQTTnet.Server` | 进程内拉起标准 MQTT Broker，拦截连接/订阅/消息并直连 `RawDataBus` |
| 内置 HTTP Server | 动态独立 `Kestrel` 实例（`WebApplication`） | 与前端托管完全隔离，支持监听 `0.0.0.0:port` Mock 回复与抓包 |
| 自由拖拽网格 | 本地随包 `GridStack.js` (`wwwroot/lib/`) | 60fps 浏览器原生网格拖拽与边角缩放，松手回传坐标，格内渲染全用 Razor 组件 |
| 高速波形图 | 本地随包 `uPlot` (`wwwroot/lib/`) | Canvas 级高性能时序波形绘制，万点多通道 30fps 刷新不掉帧 |
| 单元测试 | `xUnit` (`LinkStudio.Core.Tests`) | 保障拆帧缓冲、CRC 算法族、发送预设循环、原子配置落盘与总线并发绝对可靠 |

### 2.3 平台能力抽象（`IHostWindowService`）

为了保证 `LinkStudio.UI` 在 WPF 和 macOS 浏览器两种宿主下完全一致地运行，窗口控制与系统对话框通过接口隔离：

```csharp
public interface IHostWindowService
{
    bool IsDesktopShell { get; }          // Windows WPF = true; macOS Browser = false
    string PlatformName { get; }          // "Windows" | "macOS"
    void Minimize();
    void ToggleMaximize();
    void Close();
    void StartWindowDrag();
    Task<string?> PickSaveFileAsync(string defaultFileName, string filterDesc, string ext);
    Task<string?> PickOpenFileAsync(string filterDesc, string ext);
}
```
- **Windows 实现**：绑定 WPF `Window` 操作及原生 `SaveFileDialog` / `OpenFileDialog`。
- **macOS Server 实现**：`IsDesktopShell` 返回 `false`（顶部标题栏自动隐藏最小化/最大化/关闭按钮），文件导出/导入走浏览器下载/文件上传流。

### 2.4 统一并发模型（强制工程约束）

LinkStudio 的通信、协议、落盘和自动化模块统一采用 **“单一所有者 Worker Task + 有界 `Channel<T>` 邮箱 + 不可变消息”** 模型。除本节明确列出的例外，后续实现不得自行引入共享可变对象、多 Worker 直接操作同一资源、无界队列或无法追踪的后台任务。

#### 2.4.1 核心定义与不变量

1. **Worker 表示逻辑执行流，不表示固定 OS 线程**：Worker 是只启动一次的长期 `Task`。它可以在一次 `await` 前后由不同 ThreadPool 线程继续执行，但同一 Worker 必须始终按顺序处理消息，任意时刻最多处理一条命令。
2. **资源只有一个逻辑所有者**：`SerialPort`、单个 TCP 连接、协议半包状态、单个日志文件等可变资源，只能由其所属 Worker 访问。其他模块必须通过命令或数据邮箱交互，禁止绕过队列直接调用或修改内部对象。
3. **正确 `await` 保证串行控制流**：Worker 处理异步命令时必须 `await` 完成后才能读取下一条命令。禁止用 `_ = HandleAsync(...)`、未管理的 `Task.Run(...)` 或 `async void` 把工作从 Worker 生命周期中分离。
4. **队列必须有界**：所有长期运行的 `Channel<T>` 必须明确容量、满载策略、单/多 Reader、单/多 Writer 和丢弃计数。除一次性且有严格上限的局部流程外，禁止使用 `Channel.CreateUnbounded`。
5. **消息入队后不可修改**：命令优先使用不可变 `record`；字节载荷在入口建立独立所有权，入队后生产者不得修改或复用其底层内存。
6. **锁只保护低频短临界区**：订阅表替换、极小状态快照等控制面操作可以使用 `lock` / `Interlocked` / `Volatile`。禁止在锁内 `await`，也禁止在锁内执行串口、网络、协议、磁盘或 UI 操作。
7. **线程亲和对象必须显式调度**：WPF 控件、Blazor 组件状态及明确要求固定线程的 COM/厂商 SDK 不适用普通 ThreadPool Worker。它们必须通过 `Dispatcher`、`InvokeAsync` 或专用 `SynchronizationContext` 回到所属线程；若第三方 SDK 明确要求同一物理线程，则使用专用 `Thread`，并在适配层隔离。

#### 2.4.2 模块所有权矩阵

| 模块/资源 | 唯一所有者 | 输入邮箱 | 禁止行为 |
| :--- | :--- | :--- | :--- |
| `SerialPortDevice` / `_port` | Serial Command Worker | `Channel<SerialCommand>` | UI、服务或事件回调直接 `Open/Read/Write/Close`；外部直接读取 `_port` |
| `RawDataBus` 输入与路由 | Dispatcher Worker | `Channel<RawDataPacket>` | 在发布者 I/O 上下文中直接执行协议、日志或 UI 回调 |
| 每个 `(ChannelId, ProtocolId)` 协议实例 | Protocol Worker | 独立原始数据邮箱 | 多 Worker 并发调用同一协议实例；跨实例共享半包缓冲 |
| 单个 TCP Client 连接 | Connection Worker | 连接命令/发送邮箱 | 其他连接或 UI 直接操作该 Socket/Stream |
| HTTP/MQTT 框架回调 | 框架只负责生产消息 | `RawDataBus` 输入邮箱 | 在 Kestrel/MQTT 回调中长时间解析协议、写磁盘或刷新 UI |
| 原始抓包文件 / 技术日志文件 | File Writer Worker | 独立落盘邮箱 | 多线程直接写同一流；在通信 Worker 中同步落盘 |
| 遥测聚合状态 | Telemetry Worker | 遥测更新邮箱 | 协议 Worker 直接修改 UI 组件状态 |
| 串口热插拔/设备枚举状态 | Monitor Worker | 控制命令或系统事件 | Monitor 直接打开/关闭业务通道；高频轮询 UI |
| Razor/WPF 视图状态 | UI Dispatcher / Blazor Renderer | 状态快照或 UI 事件 | 后台 Worker 直接修改控件、DOM 或组件字段 |

对外状态不得通过内部资源对象临时计算。例如 `SerialPortDevice.IsOpen` 不直接读取 `_port.IsOpen`，而应读取由 Serial Worker 更新的 `Volatile`/`Interlocked` 状态快照。外部只能获得状态 DTO、统计快照或事件，不能获得资源实例。

#### 2.4.3 标准命令 Worker 形态

所有管理状态或 I/O 资源的 Worker 应保持相同骨架：

```csharp
public sealed class ExampleWorker : IAsyncDisposable
{
    private readonly Channel<ExampleCommand> _commands;
    private readonly Task _workerTask;
    private int _disposeState;

    public ExampleWorker(int capacity = 256)
    {
        _commands = Channel.CreateBounded<ExampleCommand>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });

        // 构造完成后只启动一次；后续不得再创建第二个 RunAsync。
        _workerTask = Task.Run(RunAsync);
    }

    public async Task ExecuteAsync(object value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new ExecuteCommand(value, completion, ct);

        await _commands.Writer.WriteAsync(command, ct);
        await completion.Task.WaitAsync(ct);
    }

    private async Task RunAsync()
    {
        await foreach (ExampleCommand command in _commands.Reader.ReadAllAsync())
        {
            try
            {
                // 必须等待当前命令完整结束后，才能读取下一条命令。
                await HandleAsync(command);
            }
            catch (Exception ex)
            {
                CompleteWithException(command, ex);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _commands.Writer.TryComplete();
        await _workerTask;
    }
}
```

标准命令必须携带完成回执和必要的取消令牌。`TaskCompletionSource` 必须使用 `RunContinuationsAsynchronously`，避免调用方的后续代码内联到 Worker 调用栈。调用方取消后，Worker 取到尚未开始的命令时必须检查令牌并跳过；已经开始的物理 I/O 是否可取消，由具体驱动能力决定，不能把“调用方停止等待”误报成“设备一定没有执行”。

#### 2.4.4 `async` / `await` 与任务生命周期规则

- `Task` 是可等待的工作与完成状态，不等于独占线程；代码正确性不得依赖 `Environment.CurrentManagedThreadId` 在 `await` 前后保持不变。
- 外层 `await` 的方法会随内层未完成 Task 一起暂停，线程归还 ThreadPool；内层完成后从内向外恢复。Worker 串行性来自唯一消费循环和完整 `await`，不是固定线程编号。
- 禁止在业务代码中使用 `.Result` / `.Wait()`；同步入口必须一路改造成异步调用链。UI 事件允许 `async void` 仅限框架规定的事件签名，内部必须立即 `await` 可追踪服务方法并捕获顶层异常。
- 禁止 fire-and-forget。确需与主流程并行的长期任务必须保存 `Task` 引用、纳入统一取消/关闭流程，并在退出时等待；短期并行必须通过 `Task.WhenAll` 汇合并观察全部异常。
- `ConfigureAwait(false)` 不是并发保护工具。Core 层不依赖 UI 上下文；UI 层需要更新视图时显式使用宿主调度接口。

#### 2.4.5 背压、异常与关闭规则

- `Wait` 用于不能静默丢失的命令、协议字节流和原始落盘；`DropOldest` 用于终端显示和波形；`DropIncoming` 用于拒绝满载后的新任务；`LatestOnly` 等价于容量 1 + `DropOldest`。
- 丢弃策略必须记录丢弃计数；等待策略必须记录当前深度、历史高水位和过载诊断。禁止静默吞掉队列满载、订阅异常或 Worker 意外退出。
- 单条消息处理异常应完成该消息回执并记录技术日志，不能默认杀死整个 Worker；若错误导致资源不可继续使用，Worker 负责转换为明确故障状态并结清剩余命令。
- 正常关闭顺序统一为：停止生产者 → `TryComplete` 输入 Writer → 排空输入队列 → 完成下游邮箱 → 等待所有 Worker → 释放资源。`CancellationToken.Cancel` 仅用于用户明确取消、故障隔离或优雅关闭超时后的强制终止。
- `DisposeAsync` 必须幂等。不得在同步 `Dispose` 中用 `.Wait()` 阻塞等待异步 Worker；宿主退出链必须提供确定性的异步等待与超时保护。

#### 2.4.6 不应创建 Worker 的代码

以下代码保持普通同步/无状态实现，不得为了形式统一而增加队列：CRC/Sum/Xor、HEX 与 UTF-8 转换、大小端读写、`FrameBuilder`、`BitReader`、不可变 DTO/配置、只在单个方法内部使用且不逃逸的局部对象。判断标准是：若模块不拥有长期可变状态、不管理 I/O 资源、不要求跨调用排序，也不被多个执行流共享，就不需要 Worker。

#### 2.4.7 强制并发测试门槛

每个新增 Worker 至少覆盖：消息顺序；处理函数最大并发数恒为 1；多生产者并发入队；队列满载策略；单条异常后继续处理；排队期间取消；正常排空关闭；强制取消；重复 `DisposeAsync`；Worker 意外退出后所有等待回执得到完成、取消或异常，禁止永久悬挂。涉及状态快照时还要覆盖读写可见性，涉及丢弃策略时要校验丢弃计数。

---

## 3. 仓库与项目目录结构

```text
LinkStudio/
├── LinkStudio.sln
├── docs/
│   └── DESIGN.md                              # 本详细设计文档
├── protocols/                                 # 用户可编辑的热加载 C# 协议目录
│   ├── MotorProto.cs                          # 示例：无刷电机二进制结构体双向协议
│   └── BmsProto.cs                            # 示例：BMS 电池电源监控二进制协议
├── profiles/                                  # 工作区方案 JSON 目录（自动/手动导出）
│   └── default-motor-bench.json
├── src/
│   ├── LinkStudio.Core/                       # 核心跨平台类库 (net8.0)
│   │   ├── Bus/
│   │   │   ├── IRawDataBus.cs
│   │   │   ├── RawDataBus.cs                  # 有界输入队列 + 单调度 Worker + 订阅者独立邮箱
│   │   │   ├── RawDataSubscription.cs         # 订阅者有界邮箱 + 单一消费 Worker
│   │   │   ├── RawDataBusOptions.cs            # 输入容量、订阅容量与背压策略
│   │   │   ├── RawDataPacket.cs               # 不可变原始数据包
│   │   │   └── Metadata/                     # 各通道的附加信息
│   │   │       ├── PacketMeta.cs
│   │   │       ├── HttpPacketMeta.cs
│   │   │       ├── MqttPacketMeta.cs
│   │   │       ├── NetworkPacketMeta.cs
│   │   │       └── RemoteEndpoint.cs
│   │   ├── Channels/
│   │   │   ├── IChannel.cs                    # 统一通道契约与统计指标
│   │   │   ├── ChannelManager.cs              # 通道生命周期与路由管理
│   │   │   ├── Serial/
│   │   │   │   ├── ISerialPortDevice.cs       # 复用：串口设备契约
│   │   │   │   ├── SerialPortDevice.cs        # 复用：单 Worker Channel<SerialCommand> 串行命令队列 + 优雅排空
│   │   │   │   ├── SerialCommand.cs           # 复用：Open/Close/Write/ReadAvailable 不可变命令
│   │   │   │   ├── SerialConnectionService.cs # 复用：连接协调 + 拔线检测 + Closed/ConnectionLost 事件
│   │   │   │   ├── IDeviceChangeMonitor.cs    # 复用：跨平台串口热插拔与枚举接口
│   │   │   │   └── SerialChannel.cs           # IChannel 适配器（含 DTR/RTS 控制）
│   │   │   ├── Virtual/
│   │   │   │   └── VirtualMcuChannel.cs       # 内置虚拟 MCU（M1 交付：无板可完整联调）
│   │   │   ├── Tcp/                           # M4: TcpClientChannel / TcpServerChannel
│   │   │   ├── Udp/                           # M4: UdpChannel
│   │   │   ├── Http/                          # M4: HttpMockServerChannel / SimpleHttpClient
│   │   │   └── Mqtt/                          # M4: MqttBrokerChannel (MQTTnet)
│   │   ├── Helpers/
│   │   │   └── DataHelper.cs                  # 复用：宽容版 TryParseHex 与中文错误提示
│   │   ├── Sending/
│   │   │   ├── SendPayloadBuilder.cs          # 复用：Hex/\r/\n 三独立开关 + 严格 UTF-8 无损转换
│   │   │   └── PresetCycleSender.cs           # 复用：多预设 Seq 编组、开轮快照与取消令牌循环发送
│   │   ├── Protocols/
│   │   │   ├── IProtocol.cs                   # 自治协议统一契约 (OnRawData + Encode)
│   │   │   ├── IProtocolSink.cs               # 解析输出汇 (帧/遥测/诊断)
│   │   │   ├── ProtocolDescriptor.cs          # 对外变量与命令元数据（供仪表盘绑定）
│   │   │   ├── ProtocolRegistry.cs            # 协议注册表、通道订阅绑定与沙箱调度
│   │   │   ├── Sdk/
│   │   │   │   ├── BinaryProtocolBase.cs      # 自动管理环形缓冲/半包/滑1字节重同步/超时
│   │   │   │   ├── RingByteBuffer.cs          # 高性能字节环形缓冲区
│   │   │   │   ├── CrcLib.cs                  # CRC8/16/32/Sum/Xor 校验库
│   │   │   │   ├── FrameBuilder.cs            # 链式二进制组帧器
│   │   │   │   ├── BitReader.cs               # 大小端跨字节位域读取器
│   │   │   │   └── ByteUnescaper.cs           # 7E/7D 等转义字符还原与封包转义
│   │   │   ├── BuiltIn/
│   │   │   │   ├── TextLineProtocol.cs        # 吸纳原 DefaultDataParse (\r\n+Idle+64KB上限) + 正则/KV/JSON
│   │   │   │   └── SimpleBinaryProtocol.cs    # 可选简易无代码二进制模板插件
│   │   │   └── Compiler/
│   │   │       ├── RoslynProtocolCompiler.cs  # .cs 动态编译 + 错误行号报告
│   │   │       └── CollectibleProtoContext.cs # 可卸载 AssemblyLoadContext
│   │   ├── Pipeline/
│   │   │   ├── TerminalLogBuffer.cs           # 融合原 ReceiveViewModel 有界队列/Volatile短路/水位裁剪(5500->5000) + 异步落盘
│   │   │   ├── TelemetryStore.cs              # 最新遥测值 + 波形时序环形缓冲 + 30fps 节流
│   │   │   └── FrameHistoryStore.cs           # 已解析帧字段明细索引（供终端点击展开）
│   │   ├── Automation/
│   │   │   └── PeriodicSenderService.cs       # 后端高精度 PeriodicTimer 定时发送
│   │   └── Storage/
│   │       ├── AtomicJsonStore.cs             # 复用原 AppsSettingsService: SemaphoreSlim + .tmp WriteThrough + File.Replace
│   │       └── ProfileRepository.cs           # 工作区 Profile JSON 与应用设置持久化
│   │
│   ├── LinkStudio.UI/                         # 共享 Razor 组件库 (net8.0)
│   │   ├── wwwroot/
│   │   │   ├── css/theme.css                  # 暗黑/浅色 CSS Design Tokens
│   │   │   ├── lib/gridstack/                 # 本地内置 GridStack 静态资源
│   │   │   ├── lib/uplot/                     # 本地内置 uPlot 静态资源
│   │   │   └── js/linkstudio-interop.js       # GridStack & uPlot 薄 JS 互操作脚本
│   │   ├── Layout/                            # 40px 标题栏 / 56px 侧边栏 / 28px 状态栏
│   │   ├── Pages/
│   │   │   ├── TerminalPage.razor             # 视图 1：通道与串口终端 + 预设循环 + 帧明细展开
│   │   │   ├── DashboardPage.razor            # 视图 2：自由拖拽与缩放工作台
│   │   │   ├── ProtocolsPage.razor            # 视图 3：C# 协议工坊 + RawDataBus 沙箱
│   │   │   └── ServersPage.razor              # 视图 4：MQTT Broker / HTTP Mock / TCP / UDP
│   │   ├── Widgets/                           # 自由工作台 9 种 Razor 组件实现
│   │   └── Interop/                           # <DashboardGrid> / <WaveformChart> C# 包装
│   │
│   ├── LinkStudio.Desktop.Win/                # Windows 主力桌面壳 (net8.0-windows, WPF)
│   │   ├── App.xaml(.cs)                      # 配置 DI 容器，串联 Core + UI + WinHost
│   │   ├── MainWindow.xaml(.cs)               # 无边框暗黑窗口 + 全屏 BlazorWebView
│   │   └── Services/
│   │       ├── WinHostWindowService.cs        # 窗口控制与原生文件对话框
│   │       └── WinDeviceChangeMonitor.cs      # 复用原 WPF HwndSource WM_DEVICECHANGE 监听
│   │
│   └── LinkStudio.Server/                     # macOS 辅助入口 (net8.0, ASP.NET Core)
│       ├── Program.cs                         # 绑定空闲本机端口 + 可选自动打开默认浏览器
│       ├── Components/App.razor
│       └── Services/
│           ├── BrowserHostWindowService.cs
│           └── MacDeviceChangeMonitor.cs      # macOS /dev/cu.* 轻量监听
│
└── tests/
    └── LinkStudio.Core.Tests/                 # xUnit 核心单元测试
        ├── AtomicJsonStoreTests.cs            # 迁移原 AppsSettingsServiceTests
        ├── SendAndPresetCycleTests.cs         # 迁移原 ReceiveSettingsTests / SendPresetViewModelTests + Hex/\r/\n 转换
        ├── BinaryProtocolBaseTests.cs         # 粘包、半包、错位滑1字节、超时丢弃测试
        ├── CrcAndFrameBuilderTests.cs         # 各种 CRC 算法与反向编解码一致性测试
        ├── RawDataBusTests.cs                 # 多协议并行订阅、异常隔离与并发安全测试
        └── RoslynCompilerTests.cs             # 热编译、报错行号定位与重复重载测试
```

---

## 4. 核心数据流与原始字节广播总线（`RawDataBus`）

### 4.1 广播总线拓扑

```text
[串口读取 Task] [TCP Client Task] [TCP Server 连接 Task] [HTTP/MQTT 回调]
       │               │                  │                    │
       └───────────────┴──────────────────┴────────────────────┘
                               │ 打包为拥有稳定 byte[] 的 RawDataPacket
                               ▼
                 ┌──────────────────────────────┐
                 │ RawDataBus 有界输入 Channel  │
                 └──────────────┬───────────────┘
                                ▼ 单一 Dispatcher Worker 顺序路由
        ┌───────────────────────┼────────────────────────┐
        ▼                       ▼                        ▼
 [终端独立邮箱]          [MotorProto 独立邮箱]      [McuLog 独立邮箱]
  DropOldest              Wait / 不静默丢帧          Wait / 不静默丢帧
        │                       │                        │
        ▼                       ▼                        ▼
 [终端 Worker]            [协议 Worker]              [协议 Worker]
  HEX/ASCII + 落盘         拆帧/CRC/遥测              文本行提取
        │                       └──────────┬─────────────┘
        ▼                                  ▼
 30fps UI 节流批刷              FrameStore / TelemetryStore
```

### 4.2 核心数据结构与接口契约

`RawDataPacket` 保存通道 ID、通道类型、收发方向和时间戳，以及通道内序号 `SeqNo` 和原始载荷 `Payload`。可选的 `Meta` 由通道按数据来源填写。串口等不需要附加信息的通道可以保留 `null`。

各类型存放在 `Bus/Metadata/` 下，每个类型一个文件。

| 类型 | 用途 |
| :--- | :--- |
| `PacketMeta` | 附加信息的公共基类型 |
| `HttpPacketMeta` | HTTP 方法、路径及可选的远端地址 |
| `MqttPacketMeta` | MQTT Topic、QoS 和客户端 ID |
| `NetworkPacketMeta` | TCP 和 UDP 的远端地址 |
| `RemoteEndpoint` | 供 HTTP、TCP 和 UDP 共用的只读 IP 地址与端口 |

总线只按通道分发。需要附加信息的订阅者可以用 `packet.Meta is HttpPacketMeta http` 判断类型并读取 HTTP 字段。

```csharp
using LinkStudio.Core.Bus.Metadata;

namespace LinkStudio.Core.Bus
{
    public enum PacketDirection : byte { Rx = 0, Tx = 1 }

    public enum ChannelKind : byte { Serial, VirtualMcu, TcpClient, TcpServer, Udp, HttpServer, MqttBroker }

    /// <summary>
    /// 投递至广播总线的只读原始数据包
    /// </summary>
    public readonly record struct RawDataPacket(
        string ChannelId,
        ChannelKind ChannelKind,
        PacketDirection Direction,
        long TimestampMs,
        uint SeqNo,
        ReadOnlyMemory<byte> Payload,
        PacketMeta? Meta = null
    );

    public enum RawDataOverflowPolicy : byte
    {
        Wait,           // 不静默丢数据；队列满时让生产者异步等待
        DropOldest,     // 丢弃最旧数据，适合终端显示与波形
        DropIncoming,   // 保留已经排队的数据，拒绝本次新写入数据
        LatestOnly      // 容量固定为 1，只保留最新状态
    }

    public sealed record RawDataBusOptions(int IngressCapacity = 4096);

    public sealed record RawDataSubscriptionOptions(
        string ChannelIdFilter,
        int Capacity = 1024,
        RawDataOverflowPolicy OverflowPolicy = RawDataOverflowPolicy.Wait
    );

    public interface IRawDataSubscription : IAsyncDisposable
    {
    }

    public interface IRawDataBus : IAsyncDisposable
    {
        /// <summary>
        /// 将数据包异步写入有界输入队列；队列满时按总线背压规则等待
        /// </summary>
        ValueTask PublishAsync(RawDataPacket packet, CancellationToken ct = default);

        /// <summary>
        /// 创建带独立有界邮箱和单一消费 Worker 的订阅；"*" 表示全通道
        /// </summary>
        IRawDataSubscription Subscribe(
            RawDataSubscriptionOptions options,
            Func<RawDataPacket, CancellationToken, ValueTask> handler
        );
    }
}
```

### 4.3 异步队列、Worker 与线程模型

1. **I/O 生产者不执行下游业务**：串口、TCP、UDP 等通道在各自异步读取循环中只完成“读取字节 → 建立稳定载荷 → 生成 `RawDataPacket` → `PublishAsync` 入队”。协议解析、日志格式化、磁盘写入和 UI 更新都不得在 I/O 回调或读取循环内直接执行。
2. **一个输入队列，一个调度 Worker**：`RawDataBus` 内部使用 `Channel.CreateBounded<RawDataPacket>`。单一 Dispatcher Worker 通过 `ReadAllAsync(ct)` 零空转等待数据，并按进入总线的先后顺序路由到匹配通道 ID 和 `"*"` 的订阅者邮箱。不同物理通道之间只保证“入队顺序”，业务上依靠各自 `SeqNo` 判断本通道顺序，不假设跨通道时间戳严格有序。
3. **每个订阅者拥有独立邮箱**：`Subscribe` 为日志、每个协议实例和遥测消费者分别创建有界 `Channel<RawDataPacket>` 与单一消费 Worker。同一订阅者永远不会被并发调用，因此协议的半包缓冲和状态机无需额外加锁；一个订阅者处理缓慢或抛出异常，也不会直接占用串口/TCP 的 I/O 执行上下文。
4. **异步 Task 不等于独占 OS 线程**：架构中的“串口 Worker”“广播 Worker”“协议 Worker”表示长期异步任务，而不是为每个对象手工创建 `Thread`。队列为空或网络无数据时，`await` 会归还线程；数据到达后由 .NET ThreadPool 安排任意可用线程继续执行。逻辑上的 Worker 保证职责和顺序，实际线程可在不同 `await` 之间切换。
5. **低频配置仍可使用短锁**：订阅表的增加、删除与快照替换属于低频控制面操作，可以在极短临界区内使用 `lock`；高频数据面只进行 `Channel` 入队、出队和快照读取，不在锁内执行用户协议或磁盘 I/O。
6. **统一取消与排空**：应用退出或通道关闭时先停止生产者，再完成总线输入 Writer，等待 Dispatcher 排空，随后完成各订阅者邮箱并等待 Worker 退出。所有后台任务接受同一生命周期树下的 `CancellationToken`，不得使用无法追踪的 `async void`。

### 4.4 有界背压、丢弃策略与内存所有权

队列必须有界，不能用无限队列掩盖“生产速度长期大于消费速度”的故障。不同消费者按数据价值采用不同策略：

| 消费者 | 建议容量/策略 | 原因 |
| :--- | :--- | :--- |
| 二进制/文本协议 | `Wait` | 拆帧状态依赖完整且有序的字节流，不允许静默丢包；持续积压时记录过载诊断并提示用户 |
| 原始抓包落盘 | 较大容量 + `Wait` | 以保存完整证据为目标；磁盘长期跟不上时必须显式告警 |
| 终端屏幕显示 | `DropOldest` | 屏幕只需展示最近内容，旧记录可由落盘文件追溯 |
| 实时波形 | `DropOldest` | 优先保持最新趋势，避免旧采样拖慢实时显示 |
| 数字卡片/在线状态 | `LatestOnly` | 中间状态没有展示价值，只需要最新值 |

当单个 `Wait` 订阅的邮箱满载时，调度 Worker 会等它腾出容量。等待期间其他订阅的后续投递也可能延迟。若入口队列随之填满，`PublishAsync` 同样会等待。总线与订阅分别提供当前深度、观测到的高水位、丢弃数和背压次数。

M1 阶段采用“**入口单次归属拷贝、广播阶段零拷贝**”：通道把驱动/网络缓冲区复制到独立 `byte[]`，封装为 `ReadOnlyMemory<byte>` 后把逻辑所有权交给总线；该数组入队后任何一方都不得修改。多个订阅者共享只读载荷，由 GC 自动保证存活时间。只有性能数据证明分配成为瓶颈后，才允许引入 `ArrayPool<byte>` / `IMemoryOwner<byte>`，并且必须同时实现明确的引用计数和归还协议，禁止把池化内存直接排入异步队列后立即归还。

### 4.5 高波特率防卡顿与并发保护机制

针对 `1,000,000` (1 Mbps) 及以上高波特率连续喷数场景，继承旧 `SerialLink` 真机实测机制并形成 **四道防线**：
1. **收发 Worker 隔离**：底层串口发送复用 `SerialPortDevice` 单 Worker `Channel<SerialCommand>` 模型；接收循环只打包并异步入总线，不执行协议和 UI 逻辑。Debug 日志只预览前 64B，写前检查 `CancellationToken`，避免停止循环后继续发送。
2. **零空转异步等待**：所有 Worker 使用 `WaitToReadAsync` / `ReadAllAsync` 等待队列，空闲期不轮询、不唤醒；文本协议在自己的消费 Worker 中实现收到数据后才启动的可配置静默窗口（默认 `30ms`）与 `FlushIdle`，不把分帧职责混入广播总线。
3. **有界背压 + 水位裁剪 + 异步落盘**：各邮箱使用明确的容量和溢出策略；`TerminalLogBuffer` 采用高低水位分批裁剪（攒到 `5500` 条裁剪、一次删 `500` 条、稳在 `5000` 条），原始抓包通过独立落盘订阅者写入 `.bin` / `.log`。
4. **UI 固定 30fps 节流批刷**：绝不在每收到一帧数据时调用 `StateHasChanged()`。UI 侧统一由 `PeriodicTimer(TimeSpan.FromMilliseconds(33))`（33~50ms 可配）批量拉取待显帧（每 Tick 限流最多刷 `100` 帧，可配）与最新遥测样本点：
   - 终端区采用 **视口虚拟化（Blazor `<Virtualize>`）**，DOM 节点永远只保留可见区域 ~40 行。
   - 波形图直接将最近批次采样数组批量推入 `uPlot` Canvas。

---

## 5. 面向 MCU 的插件化协议引擎（`IProtocol`）

### 5.1 核心设计思想
- **不绑定单一协议**：每个通道可勾选订阅 `0 ~ N` 个协议，也可把某协议同时订阅到「串口」和「MQTT Broker」两个通道上，实现**协议与传输介质彻底解耦**。
- **独立状态机**：在 `ProtocolRegistry` 中，会为每个 `(ChannelId, ProtocolId)` 组合实例化独立的 `IProtocol` 实例，确保协议 A 的粘包/半包缓冲区与协议 B 绝对隔离。
- **双向能力**：同一份 `IProtocol` 既能通过 `OnRawData` 解包上行字节，也能通过 `Describe()` 暴露指令清单、通过 `Encode()` 将界面按钮/滑块的参数值打包为完整二进制帧发送给 MCU。

### 5.2 统一契约：`IProtocol` 与 `IProtocolSink`

```csharp
namespace LinkStudio.Core.Protocols
{
    public interface IProtocol
    {
        string Id { get; }
        string Name { get; }
        string Description { get; }

        /// <summary>
        /// 声明本协议输出的遥测变量与支持下发的指令参数，供工作台下拉绑定
        /// </summary>
        ProtocolDescriptor Describe();

        /// <summary>
        /// 消费广播进来的原始数据包；协议自行管理缓冲、拆帧、转义、校验与字段提取
        /// </summary>
        void OnRawData(in RawDataPacket packet, IProtocolSink sink);

        /// <summary>
        /// 反向组帧：根据指令 ID 和界面参数生成发送给设备的原始字节（不支持可返回 null）
        /// </summary>
        byte[]? Encode(string commandId, IReadOnlyDictionary<string, object?> args);

        /// <summary>
        /// 清空内部半包状态机（通道重连或用户清屏时由管理器调用）
        /// </summary>
        void Reset();
    }

    public interface IProtocolSink
    {
        void EmitFrame(DecodedFrameResult frame);
        void EmitTelemetry(string variableKey, double value, long timestampMs);
        void EmitDiagnostic(DiagnosticLevel level, string message);
    }
}
```

### 5.3 三种平级协议来源

1. **C# 热加载协议脚本（主力，`protocols/*.cs`）**
   - 直接在工作区 `protocols/` 目录下放置标准 `.cs` 文件，可通过外部任意 IDE（VS / Rider / VS Code）或软件内置编辑器修改。
   - 点击「热重载编译」时，`RoslynProtocolCompiler` 将源码编译到独立的可卸载 `CollectibleProtoContext : AssemblyLoadContext(isCollectible: true)`：
     - **编译失败**：立即返回精确的 `文件 / 行号 / 错误码` 列表，**同时保留旧版本协议继续运行，不中断调试现场**。
     - **编译成功**：原子替换 `ProtocolRegistry` 中的工厂并调用 `Unload()` 释放旧程序集内存。
2. **内置 MCU 协议开发工具箱（`LinkStudio.Core.Protocols.Sdk`）**
   - **`BinaryProtocolBase`**：自动托管 `RingByteBuffer`；子类只需实现 `TryParseFrame(ReadOnlySpan<byte> buf, IProtocolSink sink)` 并遵循极简三态返回值：
     - 返回 **`0`**：数据不够（半包），保留字节等待下次喂入；
     - 返回 **`-1`**：帧头未对齐或 CRC 校验失败，自动滑动丢弃 1 字节重试（假帧头自愈）；
     - 返回 **`> 0`（如 `totalLen`）**：成功消费一帧，立即推进指针继续循环处理粘包中的下一帧。
     - 内置 **`FrameTimeoutMs`（默认 50ms）** 与 **`MaxFrameLength`（默认 4096B）** 保护，防半包死锁或杂波爆内存。
   - **`CrcLib`**：开箱即用 `Crc8`、`Crc16Modbus`、`Crc16Ccitt`、`Crc16Xmodem`、`Crc32`、`Sum8`、`Sum16`、`Xor8`。
   - **`FrameBuilder`**：链式二进制组帧器（`WriteHex` / `WriteByte` / `WriteInt16LE` / `WriteFloat32BE` / `AppendCrc16Modbus` / `EscapeHdlc7E`）。
   - **`BitReader` / `ByteUnescaper`**：处理非字节对齐位域与 `7E/7D` 字节填充转义还原。
   - **C 结构体直转**：原生引用 `System.Runtime.InteropServices`，可直接用 `[StructLayout(LayoutKind.Sequential, Pack = 1)]` + `MemoryMarshal.Read<T>` 1:1 映射 MCU 固件里的 `struct`。
3. **可选内置轻量插件（免写代码场景）**
   - **`TextLineProtocol`**：从混杂串口流中按换行/超时提取可读 ASCII 行，内置 `printf` 日志过滤、AT 指令正则命名捕获、KV 键值（`temp=26.4`）与 JSON Path 提取（特别适合 HTTP / MQTT JSON 上报场景）。
   - **`SimpleBinaryProtocol`**：供结构极简单的固定帧头+长度+基础数值协议快速通过界面表单临时创建一个实例。

### 5.4 MCU 协议开发示例（`protocols/MotorProto.cs`）

```csharp
using System.Runtime.InteropServices;
using LinkStudio.Core.Protocols;
using LinkStudio.Core.Protocols.Sdk;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MotorStatusPayload
{
    public byte  Cmd;        // 0x01
    public short RpmRaw;     // 0.1 RPM / LSB
    public float TempC;      // °C
    public byte  ModeFault;  // 高4位模式, 低4位故障位域
}

public sealed class MotorProto : BinaryProtocolBase
{
    public override string Id => "mcu.motor";
    public override string Name => "电机控制器二进制协议";
    public override string Description => "AA 55 帧头 + Len + CRC16-Modbus 双向协议";

    public override ProtocolDescriptor Describe() => new(
        Id, Name,
        Variables: [
            new("motor.rpm", "当前转速", "RPM"),
            new("motor.temp", "主板温度", "°C"),
            new("fault.overcurrent", "过流故障", "")
        ],
        Commands: [
            new("set_rpm", "设定目标转速", [new("rpm", "目标转速", "number", 1500, 0, 5000)]),
            new("estop", "紧急停机", [])
        ]);

    protected override int TryParseFrame(ReadOnlySpan<byte> buf, IProtocolSink sink)
    {
        if (buf.Length < 5) return 0;                        // 等更多字节
        if (buf[0] != 0xAA || buf[1] != 0x55) return -1;     // 滑动 1 字节寻找帧头

        int totalLen = buf[2] + 5;                           // 帧头2 + 长度1 + Payload + CRC2
        if (totalLen > MaxFrameLength) return -1;
        if (buf.Length < totalLen) return 0;                 // 半包等待

        var frame = buf[..totalLen];
        if (!CrcLib.VerifyCrc16Modbus(frame)) return -1;     // 假帧头自愈

        if (frame[3] == 0x01 && frame.Length == 13)
        {
            var st = MemoryMarshal.Read<MotorStatusPayload>(frame[3..^2]);
            double rpm = st.RpmRaw * 0.1;
            int fault = st.ModeFault & 0x0F;

            sink.EmitTelemetry("motor.rpm", rpm, CurrentPacketTimestampMs);
            sink.EmitTelemetry("motor.temp", st.TempC, CurrentPacketTimestampMs);
            sink.EmitTelemetry("fault.overcurrent", (fault & 0x01) != 0 ? 1 : 0, CurrentPacketTimestampMs);

            sink.EmitFrame(new DecodedFrameResult(
                ProtocolId: Id,
                FrameName: "状态上报帧 (0x01)",
                ChecksumOk: true,
                RawSliceLength: totalLen,
                Fields: [
                    new("motor.rpm", "当前转速", "[4..5]", $"{rpm:F1} RPM"),
                    new("motor.temp", "主板温度", "[6..9]", $"{st.TempC:F1} °C"),
                    new("motor.mode", "运行模式", "[10] hi", $"{(st.ModeFault >> 4) & 0x0F}"),
                    new("fault.bits", "告警位域", "[10] lo", $"0x{fault:X}")
                ]));
        }
        return totalLen;
    }

    public override byte[]? Encode(string commandId, IReadOnlyDictionary<string, object?> args) => commandId switch
    {
        "set_rpm" => FrameBuilder.Create()
            .WriteHex("AA 55").WriteByte(0x03).WriteByte(0x10)
            .WriteInt16LE((short)Math.Round(Convert.ToDouble(args["rpm"]) * 10))
            .AppendCrc16Modbus()
            .ToArray(),
        "estop" => FrameBuilder.Create()
            .WriteHex("AA 55 02 FF 00")
            .AppendCrc16Modbus()
            .ToArray(),
        _ => null
    };
}
```

### 5.5 原始字节广播沙箱（`ProtocolsPage`）
协议工坊右侧内嵌沙箱：用户输入一段任意 HEX 字节或文本，点击「广播测试」后，系统会创建临时隔离的协议实例，将字节并行喂给当前已加载的全部协议，逐个展示：
- 协议是否认领（已解析出几帧 / 残余半包字节数 / 已忽略）；
- CRC 校验状态与期望值对比（方便现场定位字节序或长度计算错 1 位的问题）；
- 完整字段解析树，以及反向调用 `Encode` 预览下发帧。

---

## 6. 多通道通信模块设计

### 6.1 统一通道接口（`IChannel`）

```csharp
public interface IChannel : IAsyncDisposable
{
    string Id { get; }
    string Name { get; }
    ChannelKind Kind { get; }
    bool IsOpen { get; }
    ChannelStats Stats { get; }

    Task OpenAsync(CancellationToken ct = default);
    Task CloseAsync();
    Task SendAsync(ReadOnlyMemory<byte> data, SendOptions? options = null, CancellationToken ct = default);

    event Action<ChannelStateChangedEvent>? StateChanged;
}
```
所有通道在打开、关闭与收发数据时均向 `IRawDataBus` 投递 `RawDataPacket`（包含 `Rx` 与 `Tx` 两个方向），从而实现**串口、网络、虚拟 MCU 共享同一套日志终端、协议解析与仪表盘**。

### 6.2 通道家族规划

| 通道类型 | 交付里程碑 | 核心能力与平台细节 |
| :--- | :---: | :--- |
| **SerialChannel (串口主力)** | **M1** | 默认波特率 **`1,000,000` (1 Mbps)**，支持自定义波特率、数据位/停止位/校验位、流控与 DTR/RTS 实时控制；底层复用 `SerialPortDevice` 单 Worker 命令队列与 `SerialConnectionService` 拔线检测；Windows 复用 `HwndSource` (`WM_DEVICECHANGE`) 零依赖监听热插拔；macOS 扫描并过滤 `/dev/cu.*` 节点防止 `tty.*` 阻塞。 |
| **VirtualMcuChannel (虚拟硬件源)** | **M1** | 内置软件仿真 MCU 节点：按固定周期向总线吐出符合 `MotorProto`、`BmsProto` 的二进制状态帧和 `McuLog` 文本行，且收到上位机发来的 `set_rpm` / `estop` 二进制帧后真实改变仿真转速/电流波形，实现“零硬件即可验证全链路”。 |
| **TcpClient / TcpServer** | **M4** | Client 支持断线重连选项；Server 支持多客户端连接管理列表，支持界面勾选向指定单个客户端发送或广播给全部在线客户端。 |
| **UdpChannel** | **M4** | 支持指定本地绑定端口、单播目标 `IP:Port` 以及局域网广播地址（`255.255.255.255`）。 |
| **HttpMockServerChannel & SimpleHttpClient** | **M4** | 动态拉起独立的 Kestrel 监听实例（与主程序端口彻底隔离），可配多条 Mock 路由（Method、Path、状态码、响应头/体、可选模拟延迟），所有入站请求的 Body 无论文本还是二进制字节流均直通 `RawDataBus`，并记录完整请求头/体抓包详情；附带简洁 HTTP Client 用于主动发测试请求。 |
| **MqttBrokerChannel (内置 Broker)** | **M4** | 基于 `MQTTnet.Server` 进程内运行完整 MQTT Broker（可配端口如 `1883` 及可选用户名/密码校验），实时面板展示在线客户端列表（ClientId / IP / 订阅 Topic），所有设备 Publish 的二进制或文本 Payload 携带 `MqttPacketMeta` 投入 `RawDataBus`，界面支持选定 Topic 直接发布 HEX/文本/协议指令。 |

### 6.3 串口收发与预设实战规范（继承自旧 SerialLink 实测习惯）

1. **默认分帧与 `\r\n` 4 种显示模式**：
   - 默认内置 `\r\n` + 可配置 Idle 超时（默认 `30ms`）混合分帧，保留原始分隔符，并设 `64KB` 单半帧上限防无分隔符数据爆内存；
   - ASCII 显示列默认把 `\r`（`0x0D`）与 `\n`（`0x0A`）渲染为字面 `\r`、`\n`（便于直观辨识设备日志换行），同时在设置中支持切换 **4 种显示模式**：① 保留全部、② 归一化 `\r\r\n → \r\n`、③ 仅空行帧保留、④ 隐藏全部结尾换行。
2. **发送区 `Hex` / `\r` / `\n` 独立三开关 + 严格 UTF-8 无损转换**：
   - 输入框仅存放纯内容，`\r` / `\n` 作为“发送时末尾追加修饰”在 **文本与 HEX 两种模式下均保持可勾、可见、生效**，固定顺序 `0x0D` 在前、`0x0A` 在后；
   - 切换 `Hex` 开关时自动对纯内容做「文本 ↔ HEX」双向转换，保证**转换前后实际发出的字节流完全不变**；HEX → 文本采用严格 UTF-8（`throwOnInvalidBytes: true`），遇到非法字节（如 `FF FE`）拒绝转换并保留原文、提示中文原因，且**不回滚开关状态**；
   - 校验时机仅放在用户主动点击「发送」或切换模式时（不在打字过程中弹红字误报）；允许发送状态使用 `!string.IsNullOrEmpty` 判定，保证空格、纯 `\r\n` 均可发送。
3. **多预设 `Seq` 编组循环发送（上限 100 条）**：
   - 每条预设包含 `Seq`（`0` = 不参与循环，仅单发；`≥ 1` = 按升序分组循环）、`Text`、`IsHex`、`IntervalMs`；
   - 同 `Seq` 组内条目并发提交至底层的单线程串行写队列，组间按**该组内最大间隔**等待（兜底最小 `10ms` 防空转卡死）；每轮循环开头取一次内容快照防“编辑一半被发出”；启动前统一用 `DataHelper.TryParseHex` 校验，停止/断线通过取消令牌跳过尚未开始的写。
4. **清空选项与实时异步落盘**：
   - 接收区清空按钮可在设置中选择「清空时归零 RX/TX/帧计数」或「仅清屏幕保留计数」；
   - 工具栏提供一键开关实时抓包落盘（可选保存原始字节流 `.bin` 或带时间戳切分帧日志 `.log`），并全部持久化保存上次串口参数与预设集合。

---

## 7. 自由拖拽工作台与方案（Profile JSON）规范

### 7.1 网格引擎与波形渲染边界

为了兼具 **全栈 C# 维护简单性** 与 **浏览器原生 60fps 交互流畅度**：
1. **`<DashboardGrid>`（封装 `GridStack.js`）**：
   - 采用 `12 列`（或可配 `24 列`）自由网格；运行模式下锁定防误触，开启 **「编辑布局」** 后显示工程点阵网格与拖拽/缩放句柄。
   - 拖动与边角缩放动画全部在 WebView 浏览器层 60fps 执行；松手时通过 `[JSInvokable]` 仅将最终 `(widgetId, col, row, w, h)` 坐标回传 C# 更新模型。
   - 每个格子的内部 UI 全部分配唯一的 DOM 挂载点，交由 Blazor `<DynamicComponent>` 用纯 C# Razor 组件渲染。
2. **`<WaveformChart>`（封装 `uPlot`）**：
   - C# 侧 `TelemetryStore` 为每个通道维护定长时序环形数组；UI 定时器以 30fps 仅把自上次渲染以来的增量时间点推入 JS `uPlot.setData(...)`。
   - 原生支持多通道同屏、Y 轴自适应/锁定、暂停定格、框选局部放大、十字游标读数及导出标准 CSV 文件。

### 7.2 首批内置 Widget 组件库（M3 交付）

| 类别 | `type` 标识 | 组件名称 | 核心功能与绑定方式 |
| :--- | :--- | :--- | :--- |
| **展示类（收）** | `metric_card` | 数字卡片 | 绑定任意协议变量（如 `motor.rpm`），等宽 `tabular-nums` 大字号 + 单位 + 上下限超标变色告警 |
| | `led_indicator` | 状态指示灯 | 绑定变量/位域，按阈值或枚举值亮绿/琥珀/红/灭（支持多灯组合卡片） |
| | `gauge_meter` | 仪器表盘 | 纯 SVG 弧形仪表盘，支持量程、分段警戒色与峰值显示 |
| | `waveform_chart` | 实时波形图 | 基于 `uPlot`，多变量叠加实时曲线、暂停/缩放/游标/导出 CSV |
| | `mini_terminal` | 嵌入式微终端 | 在自定义仪表盘角落嵌入过滤后的精简收发日志流 |
| **控制类（发）** | `action_button` | 快捷按钮 | 点击发送固定 HEX/文本，或调用指定通道协议的 `Encode(commandId, args)` |
| | `toggle_switch` | 双态开关 | ON / OFF 状态分别绑定协议命令或固定帧 |
| | `slider_control` | 参数滑块 | 支持步进、拖动实时发或松手发（防抖节流）并预览待发 HEX 帧 |
| | `param_form` | 多参数下发表单 | 一次填写指令的多个字段参数，点「发送」统一组帧下发 |

### 7.3 工作区方案（Profile JSON）规范

**重要原则**：Profile JSON **只保存工作区拓扑**（开了哪些通道、每个通道订阅了哪些协议 ID、接收/发送习惯配置、多预设组以及自由工作台布局绑定），**不把复杂二进制协议硬编码成 JSON**。一套方案对应一块硬件板子或一套测试台，通过 `AtomicJsonStore` 原子落盘并支持一键导出分享给同事。

```json
{
  "schemaVersion": "1.0",
  "id": "profile_motor_bench",
  "name": "无刷电机测试台",
  "receiveSettings": {
    "idleTimeoutMs": 30,
    "lineEndingMode": "KeepAll",
    "resetCountersOnClear": true,
    "maxFramesPerTick": 100,
    "displayHighWatermark": 5500,
    "displayLowWatermark": 5000
  },
  "channels": [
    {
      "id": "ch_serial_1",
      "kind": "Serial",
      "name": "主控串口",
      "serial": { "portName": "COM3", "baudRate": 1000000, "dataBits": 8, "parity": "None", "stopBits": "One" },
      "subscribedProtocolIds": ["mcu.motor", "mcu.bms", "builtin.textline"]
    },
    {
      "id": "ch_mqtt_1",
      "kind": "MqttBroker",
      "name": "本地 MQTT Broker",
      "mqtt": { "listenIp": "0.0.0.0", "port": 1883 },
      "subscribedProtocolIds": ["mcu.motor", "builtin.textline"]
    }
  ],
  "sendPresets": [
    { "seq": 1, "text": "AA 55 01 00 44 1A", "isHex": true, "intervalMs": 200 },
    { "seq": 2, "text": "AT+STATUS?", "isHex": false, "intervalMs": 500 }
  ],
  "quickCommands": [
    { "id": "qc_1", "name": "查询版本", "channelId": "ch_serial_1", "format": "hex", "payload": "AA 55 01 00 44 1A" },
    { "id": "qc_2", "name": "急停 STOP", "channelId": "ch_serial_1", "format": "hex", "payload": "AA 55 02 FF 00 F1 0B", "tone": "danger" }
  ],
  "dashboards": [
    {
      "id": "dash_main",
      "name": "主控仪表盘",
      "cols": 12,
      "rowHeight": 66,
      "widgets": [
        {
          "id": "w_rpm",
          "type": "metric_card",
          "title": "当前转速",
          "layout": { "col": 0, "row": 0, "w": 3, "h": 2 },
          "binding": { "channelId": "ch_serial_1", "variableKey": "motor.rpm", "decimals": 1, "warnAbove": 3800 }
        },
        {
          "id": "w_wave",
          "type": "waveform_chart",
          "title": "实时多通道波形监控",
          "layout": { "col": 0, "row": 2, "w": 8, "h": 4 },
          "series": [
            { "channelId": "ch_serial_1", "variableKey": "motor.rpm", "color": "#22D3EE" },
            { "channelId": "ch_serial_1", "variableKey": "motor.temp", "color": "#F59E0B" }
          ]
        },
        {
          "id": "w_slider",
          "type": "slider_control",
          "title": "目标转速设定",
          "layout": { "col": 0, "row": 6, "w": 6, "h": 2 },
          "action": {
            "channelId": "ch_serial_1",
            "protocolId": "mcu.motor",
            "commandId": "set_rpm",
            "argKey": "rpm",
            "min": 0, "max": 5000, "step": 50,
            "trigger": "on_release"
          }
        }
      ]
    }
  ]
}
```

---

## 8. 视觉与交互规范

### 8.1 窗口空间布局
- **顶部自定义标题栏（40px）**：左侧展示应用标志、当前工作区方案切换下拉、已激活通道实时状态胶囊；右侧提供视图快速导航/主题切换按钮，以及 Windows 原生最小化/最大化/关闭按钮（macOS 浏览器自动隐藏）。
- **左侧窄导航栏（56px）**：4 个主视图图标切换——① 调试终端、② 自由工作台、③ 协议工坊、④ 本地服务端中心。
- **底部全局状态栏（28px）**：实时展示当前激活通道参数、`RawDataBus` 运行协议数、累计 RX/TX 字节数、已解析帧数与错误计数、磁盘落盘指示灯。

### 8.2 主题 Design Tokens（`theme.css`）

| Token | 暗黑工程风（默认） | 浅色工程风 | 用途 |
| :--- | :--- | :--- | :--- |
| `--wb-bg` | `#0B0F14` | `#F3F6FA` | 画布与底层背景 |
| `--wb-surface` | `#131922` | `#FFFFFF` | 侧边栏、标题栏、Widget 卡片底色 |
| `--wb-elevated` | `#1A222E` | `#F8FAFC` | 输入框、按钮、次级容器底色 |
| `--wb-border` | `#222C3A` | `#CBD5E1` | 1px 精密分割线与卡片描边 |
| `--wb-text` | `#E6EDF3` | `#0F172A` | 主文本 |
| `--wb-muted` | `#7D8590` | `#64748B` | 标签、时间戳、辅助说明 |
| `--wb-cyan` | `#22D3EE` | `#0891B2` | 主强调色（选中态、拖拽线、关键主读数） |
| `--wb-emerald` | `#10B981` | `#059669` | RX 接收标签、在线状态、CRC 通过 |
| `--wb-amber` | `#F59E0B` | `#D97706` | TX 发送标签、告警预警 |
| `--wb-rose` | `#F43F5E` | `#E11D48` | 校验失败、急停按钮、断开操作 |

**排版铁律**：所有 HEX 字节流、时间戳、IP/端口、遥测读数与统计数字强制使用等宽字体（`'JetBrains Mono', 'Cascadia Mono', Consolas, monospace`）并开启 `font-variant-numeric: tabular-nums`，确保高速跳数时字符零抖动、HEX 字节上下严格对齐。

---

## 9. 分阶段里程碑与验收标准

| 阶段 | 核心交付范围 | 验收标准（DoD） |
| :--- | :--- | :--- |
| **M1 · 工程骨架 + 串口核心 MVP** | 建好 `.sln` 四工程结构、暗黑/浅色窗体壳、`RawDataBus` 有界输入队列、调度 Worker 与订阅者独立邮箱；复用 `SerialLink` 核心（`SerialPortDevice`、`SerialConnectionService`、`HwndSource` 热插拔、`DataHelper`、`AtomicJsonStore`、`SendPayloadBuilder`、`PresetCycleSender`）；默认 `1,000,000` 波特率、4 种换行符显示模式、清空计数开关、实时异步落盘、串口参数与预设持久化及 `VirtualMcuChannel` | Windows WPF 窗口启动即用；拔插串口自动刷新并处理断线；`Hex/\r/\n` 双向无损切换、多预设 `Seq` 循环发送与实时落盘正常；协议、日志与 UI 消费者在独立 Worker 中运行并具备可验证的背压策略；1M 波特率虚拟 MCU / 真实串口连续收发不卡顿；macOS `Server` 入口与核心复用测试全部通过 |
| **M2 · 面向 MCU 的协议引擎** | `BinaryProtocolBase`、`CrcLib`、`FrameBuilder`、`BitReader`、`ByteUnescaper`、`TextLineProtocol`、Roslyn `.cs` 热编译与 `CollectibleProtoContext`、终端帧展开卡、协议工坊沙箱、xUnit 核心测试集 | 修改 `protocols/MotorProto.cs` 点热重载无需重启即可生效；故意写错语法能精确返回行号且旧协议继续工作；同一串口同时勾选 `MotorProto` + `BmsProto` + `McuLog` 并行解包不串帧；`Core.Tests` 粘包/半包/错位/CRC 用例 100% 通过 |
| **M3 · 自由拖拽工作台与方案持久化** | `<DashboardGrid>` (`GridStack.js`)、`<WaveformChart>` (`uPlot`)、9 种内置 Widget 组件、`ProfileRepository` 方案 JSON 导入导出 | 编辑/锁定模式丝滑切换；卡片自由拖拽缩放且刷新/重启后布局完全恢复；滑块/按钮能调用 `IProtocol.Encode` 真实封包下发；多通道波形 30fps 流畅运行并可导出 CSV |
| **M4 · 本地网络服务端与客户端全家桶** | `TcpClient/Server`、`UdpChannel`、`HttpMockServerChannel` + 极简 HTTP Client、`MqttBrokerChannel` (`MQTTnet`) 与 `ServersPage` 完整监控面板 | 外部设备/测试脚本连入本机 `1883` MQTT Broker 与 `8080` HTTP Mock Server 后，二进制与 JSON 上报内容能自动进入 `RawDataBus`，由 M2 协议解析并直接驱动 M3 波形与仪表盘；支持从界面反向 Publish / Mock 回复 |

---

## 10. 关键工程风险与防护策略

1. **串口句柄占用与异常断线**：在 `App.OnExit` 与 WPF `Closing` 中统一执行 `ChannelManager.DisposeAllAsync()` 并设置超时保护，防止串口驱动在程序退出后被内核挂死；USB 突然拔出时捕获 `IOException` / `UnauthorizedAccessException` 自动降级状态并停止定时发送，不弹窗崩溃。
2. **Roslyn 热加载内存泄漏（重点）**：动态编译出的 `IProtocol` 实例运行在可卸载 `AssemblyLoadContext` 中。`RawDataBus`、`FrameHistoryStore` 和 `LinkStudio.UI` **严禁** 直接保存来自动态程序集的类型引用（例如直接把动态 `struct` 对象传给 UI）；`IProtocolSink` 只接收由 `LinkStudio.Core` 定义的标准不可变 DTO（`DecodedFrameResult` / `double` / `string`），确保热重载后 GC 能彻底回收旧程序集。
3. **高波特率 UI 卡顿与内存膨胀**：后台 `TerminalLogBuffer` 强制有界环形覆盖，UI 侧严格 30fps 节流批刷 + `<Virtualize>`；在 Blazor Server（macOS 模式）中显式调大 `HubOptions.MaximumReceiveMessageSize` 防止长帧日志详情展开时断链。
4. **macOS 串口节点阻塞**：macOS 上禁用打开 `/dev/tty.*`（会阻塞等待 DCD 信号），发现服务严格过滤仅暴露 `/dev/cu.*` 节点。
5. **内置服务端端口占用**：启动 MQTT (`1883`) 或 HTTP (`8080`) 等本地服务时捕获 `SocketException` / `AddressInUseException`，向界面返回明确的端口冲突提示并引导一键换端口，不影响串口主流程使用。

---

## 11. 旧代码复用映射与工程约束（继承自 SerialLink）

本节明确如何从既有 `E:\Space\alist3\c#wpf\link\SerialLink` 资产平滑迁移至 `LinkStudio`，并延续既有的工程规范。

### 11.1 旧资产复用对照表

| 复用等级 | 原文件 / 模块 | 新项目归属与改造方式 |
| :--- | :--- | :--- |
| **🟡 演化升级** | `Devices/ISerialPortDevice.cs`、`SerialPortDevice.cs`、`Models/SerialCommand.cs` | 迁入 `LinkStudio.Core/Channels/Serial/`，保留单 Worker 串行命令模型；将无界队列改为有界 `Channel`，把 `IsOpen` 改为 Worker 更新的线程安全状态快照，补齐 `IAsyncDisposable` 确定性排空、DTR/RTS 控制，并外包 `SerialChannel : IChannel`；注释统一使用“Worker Task”而非暗示固定线程 |
| **🟢 直接复用** | `Services/.../SerialConnectionService.cs`、`Models/SerialPortConfig.cs`、`SerialFrame.cs` | 迁入 `LinkStudio.Core`，保留拔线检测幂等置空与 `ConnectionLost`/`Closed` 契约 |
| **🟢 演化升级** | `Services/.../SerialReceiveWorker.cs`、`DefaultDataParse.cs`、`IDataParse.cs` | `SerialReceiveWorker` 的零空转等待演化为通道异步读取循环与 `RawDataBus` 有界队列；`DefaultDataParse`（`\r\n` + 30ms 静默窗口 + 64KB 上限）演化为拥有独立邮箱的内置 `TextLineProtocol` |
| **🟢 直接复用** | `Helper/DataHelper.cs` | 迁入 `LinkStudio.Core/Helpers/`，继续作为全项目唯一的宽容 HEX 解析与中文报错实现 |
| **🟢 直接复用** | `Services/.../AppsSettingsService.cs`、`Models/AppsSettings.cs` | 演化为 `LinkStudio.Core/Storage/AtomicJsonStore.cs`，完整保留 `SemaphoreSlim` + 同目录 `.tmp` `WriteThrough` + `File.Replace` 原子落盘 |
| **🟡 轻量解耦** | `Services/.../DeviceChangeMonitor.cs` | 接口 `IDeviceChangeMonitor` 留 `Core`；WPF `HwndSource` 热插拔监听迁入 `LinkStudio.Desktop.Win`，macOS 新增 `/dev/cu.*` 实现 |
| **🟡 剥离 UI 复用** | `ViewModels/SendViewModel.cs`、`SendPresetViewModel.cs`、`SendPresetItemViewModel.cs` | 剥离 `CommunityToolkit.Mvvm`，核心算法抽入 `SendPayloadBuilder`（`Hex`/`\r`/`\n` + 严格 UTF-8 互转）与 `PresetCycleSender`（`Seq` 编组快照循环） |
| **🟡 剥离 UI 复用** | `ViewModels/ReceiveViewModel.cs` | 剥离 `DispatcherTimer`，有界队列 + `Volatile` 释放短路 + 水位裁剪（`5500→5000`）迁入 `TerminalLogBuffer` 并补齐异步落盘 |
| **🟢 用例迁移** | `SerialLink.Tests/*` | 迁入 `tests/LinkStudio.Core.Tests/`，持续回归原子落盘、HEX 解析与预设循环逻辑 |
| **🔴 不迁移（替代）** | `Views/*.xaml(.cs)`、`Resources/*.xaml`、`WPF-UI` 依赖 | 由 `LinkStudio.UI`（Blazor 暗黑工程风 + `GridStack.js` + `uPlot`）全面取代 |

### 11.2 开发取舍原则与编码规范

1. **务实取舍原则（源自 `SerialLink/docs/TODO.md`）**：
   - 先把总体功能做完，只修**已知或高概率发生**的问题，不为低概率边缘场景或纯契约完善（如无实际症状的额外状态机包装）过度设计。
2. **代码与注释规范（源自 `SerialLink/AGENTS.md`）**：
   - 新增 C# 类型和设计文档中的 C# 示例采用块范围命名空间，将类型声明放在 namespace 的大括号内；
   - 注释使用 **中文**，写在目标代码**上方**（避免行尾注释），解释“为什么这样设计 / 线程与生命周期约束”而非复述“做了什么”；类型名、API 名与技术术语保持英文；
   - 异步 I/O 坚持 `async/await` 并遵守 §2.4 统一并发模型；严禁使用 `.Result` / `.Wait()`，`async void` 仅允许用于框架规定的 UI 事件签名；
   - 不写空 `catch` 吞掉异常，面向用户的中文提示与技术日志（`Serilog`）严格分离；
   - 遵循统一命名约定：`PascalCase` 类型/方法/属性、`_camelCase` 私有字段、`IXxxService` 接口、`XxxAsync` 异步方法。
