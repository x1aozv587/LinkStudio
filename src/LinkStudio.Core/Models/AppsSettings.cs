using System.Collections.Generic;
using System.IO.Ports;

namespace LinkStudio.Core.Models
{
    /// <summary>
    /// 应用程序持久化配置的根对象。
    /// </summary>
    public class AppsSettings
    {
        public const int CurrentVersion = 2;

        /// <summary>
        /// 配置格式版本号。
        /// </summary>
        public int Version { get; set; } = CurrentVersion;

        /// <summary>
        /// LinkStudio 全局设置（外观主题与应用运行日志）。
        /// </summary>
        public StudioConfig Studio { get; set; } = new();

        /// <summary>
        /// 串口连接配置。
        /// </summary>
        public SerialPortConfig Connection { get; set; } = new();

        /// <summary>
        /// 串口接收区与串口日志设置。
        /// </summary>
        public ReceiveConfig Receive { get; set; } = new();

        /// <summary>
        /// 发送区设置。
        /// </summary>
        public SendConfig Send { get; set; } = new();

        /// <summary>
        /// 预设发送项集合。
        /// </summary>
        public List<SendPresetConfig> SendPreset { get; set; } = new();

        /// <summary>
        /// 创建一份完整的默认配置。
        /// </summary>
        public static AppsSettings CreateDefault()
        {
            var settings = new AppsSettings
            {
                Version = CurrentVersion,
                Studio = new StudioConfig
                {
                    Theme = "Light",
                    AutoSaveAppLogOnStartup = true,
                    AppLogDirectory = Path.Combine(AppContext.BaseDirectory, "logs", "app")
                },
                Connection = new SerialPortConfig
                {
                    PortName = string.Empty,
                    BaudRate = 1000000,
                    DataBits = 8,
                    StopBits = StopBits.One,
                    Parity = Parity.None,
                    FlowControl = Handshake.None
                },
                Receive = new ReceiveConfig
                {
                    FontSize = 16,
                    DisplayMode = "Utf8",
                    ShowTimestamp = true,
                    ShowDirection = true,
                    ShowPayload = true,
                    SplitByNewLine = true,
                    IsAutoScroll = true,
                    MaxDisplayLines = 500,
                    AutoSaveSerialLogOnOpen = false,
                    SerialLogDirectory = Path.Combine(AppContext.BaseDirectory, "logs", "serial"),
                    SerialLogFormat = ".log"
                },
                Send = new SendConfig
                {
                    IsHex = false,
                    AppendCr = false,
                    AppendLf = false,
                    EnterToSend = true
                }
            };

            settings.SendPreset.Add(new SendPresetConfig
            {
                Id = 1,
                Name = "AT 测试",
                Text = "AT",
                Interval = 1000,
                IsHex = false,
                AppendCr = true,
                AppendLf = true,
                IsSelectedForCycle = false
            });
            settings.SendPreset.Add(new SendPresetConfig
            {
                Id = 2,
                Name = "HEX 心跳",
                Text = "AA 55 01 00",
                Interval = 1000,
                IsHex = true,
                AppendCr = false,
                AppendLf = false,
                IsSelectedForCycle = false
            });

            return settings;
        }
    }

    /// <summary>
    /// LinkStudio 全局配置。
    /// </summary>
    public class StudioConfig
    {
        public string Theme { get; set; } = "Light";
        public bool AutoSaveAppLogOnStartup { get; set; } = true;
        public string AppLogDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "logs", "app");
    }

    /// <summary>
    /// 串口接收区显示与串口数据日志设置。
    /// </summary>
    public class ReceiveConfig
    {
        public bool IsAutoScroll { get; set; } = true;
        public int FontSize { get; set; } = 16;
        public string DisplayMode { get; set; } = "Utf8";
        public bool ShowTimestamp { get; set; } = true;
        public bool ShowDirection { get; set; } = true;
        public bool ShowPayload { get; set; } = true;
        public bool SplitByNewLine { get; set; } = true;
        public int MaxDisplayLines { get; set; } = 500;
        public bool AutoSaveSerialLogOnOpen { get; set; }
        public string SerialLogDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "logs", "serial");
        public string SerialLogFormat { get; set; } = ".log";
    }

    /// <summary>
    /// 发送区设置。
    /// </summary>
    public class SendConfig
    {
        public bool IsHex { get; set; }
        public bool AppendCr { get; set; }
        public bool AppendLf { get; set; }
        public bool EnterToSend { get; set; } = true;
    }

    /// <summary>
    /// 预设发送项。
    /// </summary>
    public class SendPresetConfig
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public int Interval { get; set; } = 1000;
        public bool IsHex { get; set; }
        public bool AppendCr { get; set; }
        public bool AppendLf { get; set; }
        public bool IsSelectedForCycle { get; set; }
    }
}
