using System.IO.Ports;
using LinkStudio.Core.Models;

namespace LinkStudio.UI.State
{
    /// <summary>
    /// 串口终端接收区的文本显示模式（精简为 UTF-8 中文 / HEX 两档）。
    /// </summary>
    public enum TerminalDisplayMode
    {
        Utf8,
        Hex
    }

    /// <summary>
    /// 界面外观主题。
    /// </summary>
    public enum AppTheme
    {
        Light,
        WarmPaper,
        SlateDark,
        Midnight
    }

    /// <summary>
    /// LinkStudio 全局设置（由左下角「设置」页管理：仅放真正属于整个软件的配置）。
    /// </summary>
    public sealed class StudioSettings
    {
        public AppTheme Theme { get; set; } = AppTheme.Light;

        // 应用运行日志（Serilog 诊断与故障记录，独立于串口收发数据日志）
        public bool AutoSaveAppLogOnStartup { get; set; } = true;
        public string AppLogDirectory { get; set; } =
            Path.Combine(AppContext.BaseDirectory, "logs", "app");
    }

    /// <summary>
    /// 串口终端工作区专属设置（由串口终端右上角设置抽屉管理）。
    /// </summary>
    public sealed class SerialWorkspaceSettings
    {
        // 串口高级参数
        public int DefaultBaudRate { get; set; } = 1_000_000;
        public int DataBits { get; set; } = 8;
        public StopBits StopBits { get; set; } = StopBits.One;
        public Parity Parity { get; set; } = Parity.None;
        public Handshake FlowControl { get; set; } = Handshake.None;

        // 串口终端显示与列控制
        public int FontSizePx { get; set; } = 16;
        public TerminalDisplayMode DisplayMode { get; set; } = TerminalDisplayMode.Utf8;
        public bool ShowTimestamp { get; set; } = true;
        public bool ShowDirection { get; set; } = true;
        public bool ShowPayload { get; set; } = true;
        public bool AutoScroll { get; set; } = true;
        public int MaxDisplayLines { get; set; } = 500;

        // 串口发送与快捷键行为
        public bool EnterToSend { get; set; } = true;
        public bool SendAsHex { get; set; }
        public bool AppendCr { get; set; }
        public bool AppendLf { get; set; }

        // 串口收发数据日志（与全局应用运行日志完全独立）
        public bool AutoSaveSerialLogOnOpen { get; set; }
        public string SerialLogDirectory { get; set; } =
            Path.Combine(AppContext.BaseDirectory, "logs", "serial");
        public string SerialLogFormat { get; set; } = ".log";
    }

    /// <summary>
    /// 运行时共享设置容器：Studio 全局设置与串口工作区设置严格分离。
    /// </summary>
    public sealed class WorkbenchSettings
    {
        public const int SliderMinFontSize = 10;
        public const int SliderMaxFontSize = 30;
        public const int InputMinFontSize = 7;
        public const int InputMaxFontSize = 40;

        public StudioSettings Studio { get; } = new();
        public SerialWorkspaceSettings Serial { get; } = new();

        public event Action? Changed;

        public void SetFontSize(int size)
        {
            var clamped = Math.Clamp(size, InputMinFontSize, InputMaxFontSize);
            if (Serial.FontSizePx != clamped)
            {
                Serial.FontSizePx = clamped;
                NotifyChanged();
            }
        }

        public string GetThemeKey() => Studio.Theme switch
        {
            AppTheme.WarmPaper => "warm",
            AppTheme.SlateDark => "slate",
            AppTheme.Midnight => "midnight",
            _ => "light"
        };

        public SerialPortConfig CreateSerialConfig(string portName, int baudRate)
        {
            return new SerialPortConfig
            {
                PortName = portName,
                BaudRate = baudRate,
                DataBits = Serial.DataBits,
                StopBits = Serial.StopBits,
                Parity = Serial.Parity,
                FlowControl = Serial.FlowControl
            };
        }

        public void NotifyChanged() => Changed?.Invoke();
    }
}
