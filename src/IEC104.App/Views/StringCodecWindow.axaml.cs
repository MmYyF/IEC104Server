using System;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace IEC104.App.Views
{
    /// <summary>
    /// 字符串编解码工具。
    /// 复刻说明：对应原程序 Windows.Utils.StringEncodingDecodingWindow
    /// （原类名为 CharacterEncodingDecodingWindowViewModel）。
    ///
    /// .NET Core 默认不含 GBK 等中文代码页，必须注册 CodePagesEncodingProvider；
    /// 这是移植老工具时最常踩的一个坑——不注册会直接抛 NotSupportedException。
    /// </summary>
    public partial class StringCodecWindow : DialogWindow
    {
        static StringCodecWindow()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public StringCodecWindow()
        {
            InitializeComponent();
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            this.FindControl<TextBox>("InputBox").Text = string.Empty;
            this.FindControl<TextBox>("OutputBox").Text = string.Empty;
            this.FindControl<TextBlock>("ByteInfo").Text = string.Empty;
            this.FindControl<TextBlock>("StatusText").Text = string.Empty;
        }

        private void OnEncodeClick(object sender, RoutedEventArgs e)
        {
            TextBox input = this.FindControl<TextBox>("InputBox");
            TextBox output = this.FindControl<TextBox>("OutputBox");
            TextBlock info = this.FindControl<TextBlock>("ByteInfo");
            TextBlock status = this.FindControl<TextBlock>("StatusText");

            try
            {
                string text = input.Text ?? string.Empty;
                int mode = this.FindControl<ComboBox>("EncodingBox").SelectedIndex;

                switch (mode)
                {
                    case 4:
                        output.Text = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
                        break;

                    case 5:
                        output.Text = BitConverter.ToString(Encoding.UTF8.GetBytes(text)).Replace('-', ' ');
                        info.Text = "（十六进制模式按 UTF-8 取字节）";
                        break;

                    case 6:
                        output.Text = Uri.EscapeDataString(text);
                        break;

                    default:
                    {
                        Encoding encoding = ResolveEncoding(mode);
                        byte[] bytes = encoding.GetBytes(text);
                        output.Text = BitConverter.ToString(bytes).Replace('-', ' ');
                        info.Text = $"{encoding.WebName}：{bytes.Length} 字节";
                        break;
                    }
                }

                status.Text = "编码完成";
            }
            catch (Exception exception)
            {
                output.Text = "编码失败：" + exception.Message;
            }
        }

        private void OnDecodeClick(object sender, RoutedEventArgs e)
        {
            TextBox input = this.FindControl<TextBox>("InputBox");
            TextBox output = this.FindControl<TextBox>("OutputBox");
            TextBlock info = this.FindControl<TextBlock>("ByteInfo");
            TextBlock status = this.FindControl<TextBlock>("StatusText");

            try
            {
                string text = (input.Text ?? string.Empty).Trim();
                int mode = this.FindControl<ComboBox>("EncodingBox").SelectedIndex;

                switch (mode)
                {
                    case 4:
                        output.Text = Encoding.UTF8.GetString(Convert.FromBase64String(text));
                        break;

                    case 6:
                        output.Text = Uri.UnescapeDataString(text);
                        break;

                    default:
                    {
                        byte[] bytes = FrameParserWindow.ParseHex(text);
                        Encoding encoding = ResolveEncoding(mode);
                        output.Text = encoding.GetString(bytes);
                        info.Text = $"{encoding.WebName}：{bytes.Length} 字节";
                        break;
                    }
                }

                status.Text = "解码完成";
            }
            catch (Exception exception)
            {
                output.Text = "解码失败：" + exception.Message + "（请确认输入与所选编码方式匹配）";
            }
        }

        /// <summary>把下拉索引映射为 Encoding</summary>
        private static Encoding ResolveEncoding(int mode)
        {
            return mode switch
            {
                0 => Encoding.ASCII,
                2 => Encoding.GetEncoding("GBK"),
                3 => Encoding.Latin1,
                _ => Encoding.UTF8
            };
        }
    }
}
