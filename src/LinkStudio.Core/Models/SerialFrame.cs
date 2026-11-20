using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LinkStudio.Core.Models
{
    /// <summary>
    /// 帧的方向：接收（Rx）或发送（Tx）。
    /// </summary>
    public enum FrameDirection
    {
        Rx = 0,
        Tx
    }

    /// <summary>
    /// 接收/发送区表格里的一行数据。
    /// 各字段彼此独立，便于以后：
    /// 1) 在设置里用 checkbox 控制某一列的显示/隐藏；
    /// 2) 新增更多列（长度、校验等），只需加属性 + 加一列 XAML。
    /// </summary>
    public class SerialFrame
    {
        /// <summary>自增序号（ID）。</summary>
        public int Index { get; init; }

        /// <summary>数据到达（或发送）时间。</summary>
        public DateTime Time { get; init; }

        /// <summary>方向：接收 Rx / 发送 Tx。</summary>
        public FrameDirection Direction { get; init; }

        /// <summary>原始字节（数据源，供后续帧解析 / 保存 / 重发使用）。</summary>
        public byte[] Data { get; init; } = Array.Empty<byte>();

        /// <summary>HEX 显示（带空格，如 "99 91 9A 1B"）。</summary>
        public string Hex => BitConverter.ToString(Data).Replace("-", " ");

        /// <summary>
        /// ASCII 显示：可打印字符原样；\r、\n 转成字面 \r、\n（让日志换行可见）；
        /// 其余控制字符 / 高位字节显示为 '.'。
        /// </summary>
        public string Ascii
        {
            get
            {
                var sb = new StringBuilder(Data.Length);
                foreach (var b in Data)
                {
                    if (b >= 0x20 && b <= 0x7E)
                    {
                        sb.Append((char)b);
                    }
                    else if (b == 0x0D)
                    {
                        sb.Append(@"\r");
                    }
                    else if (b == 0x0A)
                    {
                        sb.Append(@"\n");
                    }
                    else
                    {
                        sb.Append('.');
                    }
                }
                return sb.ToString();
            }
        }
    }
}
