using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LinkStudio.Core.Helper
{
    public static class DataHelper
    {

        /// <summary>
        /// 宽容解析 HEX 文本：忽略空格 / 逗号 / 横线 / 冒号 / 0x 前缀，每两个字符一个字节。
        /// </summary>
        /// <param name="str">要解析的文本，例如 <c>"41 54 - 0D"</c>、<c>"41:54"</c>、<c>"0x4154"</c>。</param>
        /// <param name="hex">解析结果；失败时是空数组（不会是 null），可直接安全判空。</param>
        /// <param name="error">失败原因；成功时是空串。</param>
        /// <returns>解析成功返回 true。</returns>
        /// <remarks>
        /// 本方法不抛异常，可用于逐字符的实时校验——之所以不用 <c>Convert.ToByte(str, 16)</c>，
        /// 是因为它遇到非法字符会抛 FormatException，那就不是 Try 语义了。
        /// <paramref name="error"/> 是中文且面向用户，会被界面直接显示（发送区的红字提示、
        /// 循环发送的启动前校验），因此文案要写成人能看懂的话。
        /// </remarks>
        public static bool TryParseHex(string str, out byte[] hex, out string error)
        {
            hex = [];
            error = "";

            if ((str == null) || (str.Length == 0))
            {
                error = "没有可解析的十六进制内容";
                return false;
            }

            // 去掉 ` `(空格)
            // 去掉 `-`(横线)
            // 去掉 `:`(冒号)
            // 去掉 `,`(逗号)
            // 去掉 `0x`/`0X`
            var new_str = str.Replace(" ", "")
                             .Replace("-", "")
                             .Replace(":", "")
                             .Replace(",", "")
                             .Replace("0x", "")
                             .Replace("0X", "");

            int new_str_lenght = new_str.Length;

            // 清理后什么都不剩（例如用户只敲了一个 "-"）
            if (new_str_lenght == 0)
            {
                error = "没有可解析的十六进制内容";
                return false;
            }

            // 长度为奇数：多半是还没敲完，或者本来就写错了
            if ((new_str_lenght % 2) != 0)
            {
                error = "HEX 长度必须为偶数（每两个字符一个字节）";
                return false;
            }

            var bytes = new byte[new_str_lenght / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                if (false == byte.TryParse(new_str.Substring(i * 2, 2), NumberStyles.HexNumber,
                           CultureInfo.InvariantCulture, out bytes[i]))
                {
                    error = $"第 {i + 1} 个字节含非法十六进制字符";
                    return false;
                }
            }

            hex = bytes;
            error = string.Empty;
            return true;
        }

        /// <summary>
        /// 按 \r / \n 拆分单次接收的字节块为多行，自动合并连续的 \r\n、\r\r\n 等换行序列，不产生空行。
        /// 若整包不含换行则原样返回 1 项。
        /// </summary>
        public static List<byte[]> SplitByteLines(ReadOnlySpan<byte> data)
        {
            var lines = new List<byte[]>();
            if (data.IsEmpty)
            {
                return lines;
            }

            var start = 0;
            for (var i = 0; i < data.Length; i++)
            {
                var b = data[i];
                if (b == (byte)'\n' || b == (byte)'\r')
                {
                    if (i > start)
                    {
                        lines.Add(data.Slice(start, i - start).ToArray());
                    }

                    start = i + 1;
                }
            }

            if (start < data.Length)
            {
                lines.Add(data.Slice(start).ToArray());
            }

            return lines;
        }

    }
}
