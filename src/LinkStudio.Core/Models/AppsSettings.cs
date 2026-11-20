using System.Collections.Generic;
using System.IO.Ports;

namespace LinkStudio.Core.Models
{
    /// <summary>
    /// 应用程序持久化配置的根对象。
    /// </summary>
    public class AppsSettings
    {
        public const int CurrentVersion = 1;

        /// <summary>
        /// 配置格式版本号。
        /// </summary>
        /// <remarks>
        /// 读取旧配置文件时据此判断是否需要迁移；格式发生不兼容变化时应递增。
        /// </remarks>
        public int Version { get; set; } = CurrentVersion;

        /// <summary>
        /// 串口连接配置。
        /// </summary>
        public SerialPortConfig Connection { get; set; } = new();

        /// <summary>
        /// 接收区设置。
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
        /// 默认配置
        /// </summary>
        public static AppsSettings CreateDefault()
        {
            var settings = new AppsSettings
            {
                Version = CurrentVersion,
                Connection = new SerialPortConfig
                {
                    PortName = string.Empty,
                    BaudRate = 1000000,
                    DataBits = 8,
                    StopBits = StopBits.One,
                    Parity = Parity.None,
                    FlowControl = Handshake.None,
                },
                Receive = new ReceiveConfig
                {
                    FontSize = 13,
                    IsAutoScroll = true,
                },
                Send = new SendConfig
                {
                    IsHex = false,
                    AppendCr = false,
                    AppendLf = false,
                },
            };

            for (var i = 0; i < 5; i++)
            {
                settings.SendPreset.Add(new SendPresetConfig
                {
                    Id = 0,
                    Interval = 0,
                    Text = string.Empty,
                    IsHex = false,
                });
            }

            return settings;
        }
    }

    /// <summary>
    /// 接收区显示设置。
    /// </summary>
    public class ReceiveConfig
    {
        /// <summary>
        /// 收到新数据时是否自动滚动到末尾。
        /// </summary>
        public bool IsAutoScroll { get; set; } = true;

        /// <summary>
        /// 接收区字体大小。
        /// </summary>
        public int FontSize { get; set; } = 13;
    }

    /// <summary>
    /// 发送区设置。
    /// </summary>
    public class SendConfig
    {
        /// <summary>
        /// 是否按 Hex 解释输入内容。
        /// </summary>
        public bool IsHex { get; set; } = false;

        /// <summary>
        /// 发送内容末尾是否追加 CR。
        /// </summary>
        public bool AppendCr { get; set; } = false;

        /// <summary>
        /// 发送内容末尾是否追加 LF。
        /// </summary>
        public bool AppendLf { get; set; } = false;
    }

    /// <summary>
    /// 预设发送项。
    /// </summary>
    public class SendPresetConfig
    {
        /// <summary>
        /// 循环发送序号，对应预设界面的 Seq；相同序号的预设可以组成一组。
        /// </summary>
        public int Id { get; set; } = 0;

        /// <summary>
        /// 预设的发送内容。
        /// </summary>
        /// <remarks>
        /// <see cref="IsHex"/> 为 true 时该字符串按 Hex 文本解释。
        /// </remarks>
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// 循环发送的间隔时间。
        /// 单位： ms
        /// </summary>
        public int Interval { get; set; } = 0;

        /// <summary>
        /// 该项是否按 Hex 方式解释 <see cref="Text"/>。
        /// </summary>
        public bool IsHex { get; set; } = false;
    }
}
