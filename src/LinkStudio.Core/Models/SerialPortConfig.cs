using System.IO.Ports;

namespace LinkStudio.Core.Models
{
    /// <summary>
    /// 串口连接配置
    /// </summary>
    public class SerialPortConfig
    {
        /// <summary>
        /// 串口名称，例如 COM3
        /// </summary>
        public string PortName { get; set; } = string.Empty;

        /// <summary>
        /// 波特率
        /// </summary>
        public int BaudRate { get; set; } = 115200;

        /// <summary>
        /// 数据位
        /// </summary>
        public int DataBits { get; set; } = 8;

        /// <summary>
        /// 停止位
        /// </summary>
        public StopBits StopBits { get; set; } = StopBits.One;

        /// <summary>
        /// 校验位
        /// </summary>
        public Parity Parity { get; set; } = Parity.None;

        /// <summary>
        /// 流控
        /// </summary>
        public Handshake FlowControl { get; set; } = Handshake.None;
    }
}
