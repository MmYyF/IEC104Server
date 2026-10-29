using System;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace IEC104.App.Views
{
    /// <summary>
    /// 冗余校验计算器。
    /// 复刻说明：对应原程序 Windows.Utils.RedundancyCheckWindow。
    ///
    /// 电力规约里用到的校验方式不止 CRC16：101 的 FT1.2 用 8 位算术累加和，
    /// Modbus 用 CRC16/MODBUS，部分厂家私有协议用 LRC 或 XOR。
    /// 现场核对报文时经常要手算，所以这里一次把常用算法都列出来。
    /// </summary>
    public partial class RedundancyCheckWindow : DialogWindow
    {
        public RedundancyCheckWindow()
        {
            InitializeComponent();
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            this.FindControl<TextBox>("InputBox").Text = string.Empty;
            this.FindControl<TextBox>("OutputBox").Text = string.Empty;
            this.FindControl<TextBlock>("StatusText").Text = string.Empty;
        }

        private void OnComputeClick(object sender, RoutedEventArgs e)
        {
            TextBox input = this.FindControl<TextBox>("InputBox");
            TextBox output = this.FindControl<TextBox>("OutputBox");
            TextBlock status = this.FindControl<TextBlock>("StatusText");

            try
            {
                byte[] data = FrameParserWindow.ParseHex(input.Text);
                if (data.Length == 0)
                {
                    output.Text = "请输入十六进制字节。";
                    return;
                }

                ushort crcModbus = Crc16Modbus(data);
                ushort crcCcitt = Crc16CcittFalse(data);
                ushort crcX25 = Crc16X25(data);
                byte sum8 = Sum8(data);
                byte lrc = Lrc(data);
                byte xor8 = Xor8(data);

                StringBuilder builder = new StringBuilder();
                builder.AppendLine($"输入字节数：{data.Length}");
                builder.AppendLine($"输入内容  ：{BitConverter.ToString(data).Replace('-', ' ')}");
                builder.AppendLine();
                builder.AppendLine("算法                    结果        低字节在前（报文中的字节序）");
                builder.AppendLine("--------------------------------------------------------------------------");
                builder.AppendLine($"8 位算术累加和 (SUM8)   0x{sum8:X2}       ——   101 FT1.2 使用此算法");
                builder.AppendLine($"8 位异或校验 (XOR8)     0x{xor8:X2}       ——");
                builder.AppendLine($"LRC（纵向冗余校验）     0x{lrc:X2}       ——   Modbus ASCII 使用");
                builder.AppendLine($"CRC16/MODBUS            0x{crcModbus:X4}     {(byte)(crcModbus & 0xFF):X2} {(byte)(crcModbus >> 8):X2}");
                builder.AppendLine($"CRC16/CCITT-FALSE       0x{crcCcitt:X4}     {(byte)(crcCcitt >> 8):X2} {(byte)(crcCcitt & 0xFF):X2}");
                builder.AppendLine($"CRC16/X25               0x{crcX25:X4}     {(byte)(crcX25 & 0xFF):X2} {(byte)(crcX25 >> 8):X2}");
                builder.AppendLine();
                builder.AppendLine("提示：");
                builder.AppendLine("  · 101 的 FT1.2 校验和是从控制域到用户数据末字节的算术累加和取低 8 位，");
                builder.AppendLine("    不包含启动字符与长度字段，计算时请只输入参与校验的那一段。");
                builder.AppendLine("  · CRC16 的高低字节顺序各厂家不一致，表里同时给出了两种排列。");

                output.Text = builder.ToString();
                status.Text = "计算完成";
            }
            catch (Exception exception)
            {
                output.Text = "计算失败：" + exception.Message;
            }
        }

        /// <summary>8 位算术累加和（FT1.2 用）</summary>
        private static byte Sum8(byte[] data)
        {
            int sum = 0;
            foreach (byte b in data)
            {
                sum += b;
            }
            return (byte)(sum & 0xFF);
        }

        /// <summary>8 位异或</summary>
        private static byte Xor8(byte[] data)
        {
            byte result = 0;
            foreach (byte b in data)
            {
                result ^= b;
            }
            return result;
        }

        /// <summary>LRC：累加和取反加一（即补码）</summary>
        private static byte Lrc(byte[] data) => (byte)((byte)(0x100 - Sum8(data)) & 0xFF);

        /// <summary>CRC16/MODBUS：多项式 0xA001（反向），初值 0xFFFF</summary>
        private static ushort Crc16Modbus(byte[] data)
        {
            ushort crc = 0xFFFF;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    bool lsb = (crc & 0x0001) != 0;
                    crc >>= 1;
                    if (lsb)
                    {
                        crc ^= 0xA001;
                    }
                }
            }
            return crc;
        }

        /// <summary>CRC16/CCITT-FALSE：多项式 0x1021，初值 0xFFFF，不反转</summary>
        private static ushort Crc16CcittFalse(byte[] data)
        {
            ushort crc = 0xFFFF;
            foreach (byte b in data)
            {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++)
                {
                    crc = (crc & 0x8000) != 0
                        ? (ushort)((crc << 1) ^ 0x1021)
                        : (ushort)(crc << 1);
                }
            }
            return crc;
        }

        /// <summary>CRC16/X25：多项式 0x1021 反向，初值与结果均取反</summary>
        private static ushort Crc16X25(byte[] data)
        {
            ushort crc = 0xFFFF;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    bool lsb = (crc & 0x0001) != 0;
                    crc >>= 1;
                    if (lsb)
                    {
                        crc ^= 0x8408;
                    }
                }
            }
            return (ushort)~crc;
        }
    }
}
