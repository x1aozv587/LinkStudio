using System.IO.Ports;
using LinkStudio.Core.Models;
using LinkStudio.Core.Services;

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
        // 串口连接与高级参数
        public string LastPortName { get; set; } = string.Empty;
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
    /// 运行时共享设置容器：Studio 全局设置与串口工作区设置严格分离，并与 Core 的 AppsSettings 双向同步。
    /// </summary>
    public sealed class WorkbenchSettings
    {
        public const int SliderMinFontSize = 10;
        public const int SliderMaxFontSize = 30;
        public const int InputMinFontSize = 7;
        public const int InputMaxFontSize = 40;

        private List<SendPresetConfig> _sendPresets = AppsSettings.CreateDefault().SendPreset;

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

        public SerialLogOptions CreateSerialLogOptions(IReadOnlyList<ISerialLogColumn>? extraColumns = null)
        {
            return new SerialLogOptions(
                IsBinary: string.Equals(Serial.SerialLogFormat, ".bin", StringComparison.OrdinalIgnoreCase),
                ShowTimestamp: Serial.ShowTimestamp,
                ShowDirection: Serial.ShowDirection,
                ShowPayload: Serial.ShowPayload,
                FormatPayloadAsHex: Serial.DisplayMode == TerminalDisplayMode.Hex,
                ExtraColumns: extraColumns);
        }

        public void ApplyFrom(AppsSettings source)
        {
            ArgumentNullException.ThrowIfNull(source);

            if (Enum.TryParse<AppTheme>(source.Studio.Theme, ignoreCase: true, out var theme))
            {
                Studio.Theme = theme;
            }

            Studio.AutoSaveAppLogOnStartup = source.Studio.AutoSaveAppLogOnStartup;
            if (!string.IsNullOrWhiteSpace(source.Studio.AppLogDirectory))
            {
                Studio.AppLogDirectory = source.Studio.AppLogDirectory;
            }

            Serial.LastPortName = source.Connection.PortName ?? string.Empty;
            Serial.DefaultBaudRate = source.Connection.BaudRate > 0 ? source.Connection.BaudRate : 1_000_000;
            Serial.DataBits = source.Connection.DataBits;
            Serial.StopBits = source.Connection.StopBits;
            Serial.Parity = source.Connection.Parity;
            Serial.FlowControl = source.Connection.FlowControl;

            Serial.FontSizePx = Math.Clamp(source.Receive.FontSize, InputMinFontSize, InputMaxFontSize);
            if (Enum.TryParse<TerminalDisplayMode>(source.Receive.DisplayMode, ignoreCase: true, out var displayMode))
            {
                Serial.DisplayMode = displayMode;
            }

            Serial.ShowTimestamp = source.Receive.ShowTimestamp;
            Serial.ShowDirection = source.Receive.ShowDirection;
            Serial.ShowPayload = source.Receive.ShowPayload;
            Serial.AutoScroll = source.Receive.IsAutoScroll;
            Serial.MaxDisplayLines = source.Receive.MaxDisplayLines >= 50 ? source.Receive.MaxDisplayLines : 500;
            Serial.AutoSaveSerialLogOnOpen = source.Receive.AutoSaveSerialLogOnOpen;
            if (!string.IsNullOrWhiteSpace(source.Receive.SerialLogDirectory))
            {
                Serial.SerialLogDirectory = source.Receive.SerialLogDirectory;
            }

            Serial.SerialLogFormat = source.Receive.SerialLogFormat is ".bin" ? ".bin" : ".log";

            Serial.SendAsHex = source.Send.IsHex;
            Serial.AppendCr = source.Send.AppendCr;
            Serial.AppendLf = source.Send.AppendLf;
            Serial.EnterToSend = source.Send.EnterToSend;

            _sendPresets = source.SendPreset ?? [];
        }

        public AppsSettings ToAppsSettings()
        {
            return new AppsSettings
            {
                Version = AppsSettings.CurrentVersion,
                Studio = new StudioConfig
                {
                    Theme = Studio.Theme.ToString(),
                    AutoSaveAppLogOnStartup = Studio.AutoSaveAppLogOnStartup,
                    AppLogDirectory = Studio.AppLogDirectory
                },
                Connection = new SerialPortConfig
                {
                    PortName = Serial.LastPortName,
                    BaudRate = Serial.DefaultBaudRate,
                    DataBits = Serial.DataBits,
                    StopBits = Serial.StopBits,
                    Parity = Serial.Parity,
                    FlowControl = Serial.FlowControl
                },
                Receive = new ReceiveConfig
                {
                    FontSize = Serial.FontSizePx,
                    DisplayMode = Serial.DisplayMode.ToString(),
                    ShowTimestamp = Serial.ShowTimestamp,
                    ShowDirection = Serial.ShowDirection,
                    ShowPayload = Serial.ShowPayload,
                    IsAutoScroll = Serial.AutoScroll,
                    MaxDisplayLines = Serial.MaxDisplayLines,
                    AutoSaveSerialLogOnOpen = Serial.AutoSaveSerialLogOnOpen,
                    SerialLogDirectory = Serial.SerialLogDirectory,
                    SerialLogFormat = Serial.SerialLogFormat
                },
                Send = new SendConfig
                {
                    IsHex = Serial.SendAsHex,
                    AppendCr = Serial.AppendCr,
                    AppendLf = Serial.AppendLf,
                    EnterToSend = Serial.EnterToSend
                },
                SendPreset = _sendPresets
            };
        }

        public void NotifyChanged() => Changed?.Invoke();
    }
}
