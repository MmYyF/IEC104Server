using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using IEC104.Core.CS101;
using IEC104.Core.CS104;
using IEC104.Core.LinkLayer;

namespace IEC104.App.Views
{
    /// <summary>
    /// 报文解析器。
    /// 复刻说明：对应原程序
    /// Windows.Utils.IEC104FrameParserWindow / IEC101FrameParserWindow
    /// （原实现为两个独立窗口，此处合并为一个，用下拉切换规约）。
    ///
    /// 这是排障时最常用的功能：把抓到的报文粘进来，直接看每层字段，
    /// 比对着规约表逐字节数要快得多。
    /// </summary>
    public partial class FrameParserWindow : DialogWindow
    {
        /// <summary>初始规约（"104" 或 "101"）</summary>
        public string InitialProtocol { get; set; } = "104";

        /// <summary>
        /// 初始报文长度（三个地址字段的字节宽度）。
        /// 从主窗口打开时会把当前通道的配置带进来——现场抓到的是非默认宽度的报文时，
        /// 解析器必须按同样的宽度切字段，否则每一层都会错位。
        /// </summary>
        public ApplicationLayerParameters InitialParameters { get; set; }

        public FrameParserWindow()
        {
            InitializeComponent();
            Opened += (_, _) =>
            {
                ComboBox box = this.FindControl<ComboBox>("ProtocolBox");
                if (box != null)
                {
                    box.SelectedIndex = InitialProtocol == "101" ? 1 : 0;
                }

                ApplicationLayerParameters widths = InitialParameters
                    ?? (InitialProtocol == "101"
                        ? ApplicationLayerParameters.CreateForCs101()
                        : ApplicationLayerParameters.CreateForCs104());

                SetComboIndex("CotLengthBox", widths.SizeOfCauseOfTransmission - 1);
                SetComboIndex("CaLengthBox", widths.SizeOfCommonAddress - 1);
                SetComboIndex("IoaLengthBox", widths.SizeOfInformationObjectAddress - 1);

                UpdateSampleHint();
            };

            // 规约一换，示例报文跟着换（默认宽度也随之翻到该规约的标准值）
            ComboBox protocolBox = this.FindControl<ComboBox>("ProtocolBox");
            if (protocolBox != null)
            {
                protocolBox.SelectionChanged += (_, _) => UpdateSampleHint();
            }
        }

        private void SetComboIndex(string name, int index)
        {
            ComboBox box = this.FindControl<ComboBox>(name);
            if (box != null)
            {
                box.SelectedIndex = Math.Clamp(index, 0, box.ItemCount - 1);
            }
        }

        private int ComboIndex(string name, int fallback)
        {
            ComboBox box = this.FindControl<ComboBox>(name);
            return box == null || box.SelectedIndex < 0 ? fallback : box.SelectedIndex;
        }

        private void UpdateSampleHint()
        {
            ComboBox box = this.FindControl<ComboBox>("ProtocolBox");
            TextBlock hint = this.FindControl<TextBlock>("SampleHint");
            if (box == null || hint == null)
            {
                return;
            }

            hint.Text = box.SelectedIndex == 1
                ? "示例：68 09 09 68 73 01 64 01 06 00 01 00 00 00 00 00 00 16（101 可变帧长帧）"
                : "示例：68 0E 00 00 00 00 64 01 06 00 01 00 00 00 00 00（104 总召激活，默认宽度 COT 2B / CA 2B / IOA 3B）";
        }

        /// <summary>按界面上三个下拉拼出应用层参数</summary>
        private ApplicationLayerParameters BuildParameters()
        {
            return new ApplicationLayerParameters
            {
                SizeOfCauseOfTransmission = ComboIndex("CotLengthBox", 1) + 1,
                SizeOfCommonAddress = ComboIndex("CaLengthBox", 1) + 1,
                SizeOfInformationObjectAddress = ComboIndex("IoaLengthBox", 2) + 1
            };
        }

        private void OnFillSampleClick(object sender, RoutedEventArgs e)
        {
            ComboBox box = this.FindControl<ComboBox>("ProtocolBox");
            TextBox input = this.FindControl<TextBox>("InputBox");
            if (input == null || box == null)
            {
                return;
            }

            // 104 示例：总召激活（C_IC_NA_1, COT=6）
            // 101 示例：可变帧长帧，链路功能码 3 + 地址 1
            input.Text = box.SelectedIndex == 1
                ? "68 09 09 68 73 01 64 01 06 00 01 00 00 00 00 00 00 16"
                : "68 0E 00 00 00 00 64 01 06 00 01 00 00 00 00 00";
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            this.FindControl<TextBox>("InputBox").Text = string.Empty;
            this.FindControl<TextBox>("OutputBox").Text = string.Empty;
            this.FindControl<TextBlock>("StatusText").Text = string.Empty;
        }

        private void OnParseClick(object sender, RoutedEventArgs e)
        {
            TextBox input = this.FindControl<TextBox>("InputBox");
            TextBox output = this.FindControl<TextBox>("OutputBox");
            TextBlock status = this.FindControl<TextBlock>("StatusText");
            ComboBox box = this.FindControl<ComboBox>("ProtocolBox");

            output.Text = string.Empty;
            status.Text = string.Empty;

            byte[] bytes;
            try
            {
                bytes = ParseHex(input.Text);
            }
            catch (Exception exception)
            {
                output.Text = "输入解析失败：" + exception.Message;
                return;
            }

            if (bytes.Length == 0)
            {
                output.Text = "请输入报文内容。";
                return;
            }

            output.Text = ParseToText(bytes);
            status.Text = $"共 {bytes.Length} 字节";
        }

        /// <summary>
        /// 按界面上当前的规约与三个长度，把一段报文字节解析成可读文本。
        /// 抽成独立方法是为了让自检能走"同一条解析路径"——自检若自己另写一份，
        /// 界面上下拉接错了他也发现不了。
        /// </summary>
        internal string ParseToText(byte[] bytes)
        {
            StringBuilder builder = new();
            try
            {
                ComboBox box = this.FindControl<ComboBox>("ProtocolBox");
                if (box?.SelectedIndex == 1)
                {
                    ParseCs101(bytes, builder);
                }
                else
                {
                    ParseCs104(bytes, builder);
                }
            }
            catch (Exception exception)
            {
                builder.AppendLine();
                builder.AppendLine("解析中断：" + exception.Message);
            }

            return builder.ToString();
        }

        /// <summary>
        /// 解析 104 报文（可一次包含多个 APDU）。
        /// 分帧规则：0x68 + 长度 + 控制域 4 字节 + ASDU。
        /// </summary>
        private void ParseCs104(byte[] bytes, StringBuilder builder)
        {
            ApplicationLayerParameters parameters = BuildParameters();
            builder.AppendLine($"  参数    传送原因 {parameters.SizeOfCauseOfTransmission}B · "
                + $"公共地址 {parameters.SizeOfCommonAddress}B · "
                + $"信息体地址 {parameters.SizeOfInformationObjectAddress}B");
            builder.AppendLine();

            int offset = 0;
            int index = 0;

            while (offset < bytes.Length)
            {
                if (bytes[offset] != Apci.StartByte)
                {
                    builder.AppendLine($"偏移 {offset}：启动字符不是 0x68（实际 0x{bytes[offset]:X2}），停止解析。");
                    return;
                }
                if (offset + 2 > bytes.Length)
                {
                    builder.AppendLine("报文不完整：缺少长度字段。");
                    return;
                }

                int apduLength = bytes[offset + 1] + 2;
                if (offset + apduLength > bytes.Length)
                {
                    builder.AppendLine($"偏移 {offset}：声明长度 {apduLength} 字节，实际不足（剩余 {bytes.Length - offset}），可能被抓包截断。");
                    return;
                }

                builder.AppendLine($"──────── APDU #{++index}  （偏移 {offset}，长度 {apduLength}）────────");
                builder.AppendLine($"  原始    {BitConverter.ToString(bytes, offset, apduLength).Replace('-', ' ')}");

                byte[] controlField = new byte[4];
                Array.Copy(bytes, offset + 2, controlField, 0, 4);

                Apci.FrameType frameType = Apci.GetFrameType(controlField);
                builder.AppendLine($"  帧类型  {DescribeFrameType(frameType)}");

                switch (frameType)
                {
                    case Apci.FrameType.Information:
                        builder.AppendLine($"  N(S)    发送序号 {Apci.GetSendSequenceNumber(controlField)}");
                        builder.AppendLine($"  N(R)    接收序号 {Apci.GetReceiveSequenceNumber(controlField)}");
                        break;

                    case Apci.FrameType.Supervisory:
                        builder.AppendLine($"  N(R)    接收序号 {Apci.GetReceiveSequenceNumber(controlField)}");
                        break;

                    case Apci.FrameType.Unnumbered:
                        builder.AppendLine($"  功能    {DescribeUFunction(controlField[0])}（0x{controlField[0]:X2}）");
                        break;
                }

                int asduLength = apduLength - Apci.ApciLength;
                if (asduLength > 0)
                {
                    builder.AppendLine();
                    try
                    {
                        Asdu asdu = Asdu.Decode(bytes, offset + Apci.ApciLength, parameters, out int consumed);
                        AppendAsdu(builder, asdu, consumed);
                    }
                    catch (Exception exception)
                    {
                        builder.AppendLine($"  [ASDU 解析失败] {exception.Message}");
                        builder.AppendLine("  提示：若类型标识为文件传输（F_*）或该类型尚未注册，属于预期内的未支持。");
                    }
                }
                else
                {
                    builder.AppendLine("  （无 ASDU，纯链路帧）");
                }

                builder.AppendLine();
                offset += apduLength;
            }
        }

        /// <summary>解析 101 报文（FT1.2 帧）</summary>
        private void ParseCs101(byte[] bytes, StringBuilder builder)
        {
            ApplicationLayerParameters parameters = BuildParameters();
            int offset = 0;

            builder.AppendLine($"参数    传送原因 {parameters.SizeOfCauseOfTransmission}B · "
                + $"公共地址 {parameters.SizeOfCommonAddress}B · "
                + $"信息体地址 {parameters.SizeOfInformationObjectAddress}B");
            builder.AppendLine();

            while (offset < bytes.Length)
            {
                byte start = bytes[offset];
                if (start == Ft12.SingleAcknowledge)
                {
                    builder.AppendLine("──────── 单字符确认帧 E5 ────────");
                    offset += 1;
                    continue;
                }

                int frameLength;
                if (start == Ft12.FixedFrameStart)
                {
                    frameLength = Ft12.FixedFrameLength;
                }
                else if (start == Ft12.VariableFrameStart)
                {
                    if (offset + 2 > bytes.Length)
                    {
                        builder.AppendLine("可变帧长帧缺少长度字段。");
                        return;
                    }
                    frameLength = bytes[offset + 1] + 6;
                }
                else
                {
                    builder.AppendLine($"偏移 {offset}：未知启动字符 0x{start:X2}，停止解析。");
                    return;
                }

                if (offset + frameLength > bytes.Length)
                {
                    builder.AppendLine($"偏移 {offset}：帧声明 {frameLength} 字节，实际不足，可能截断。");
                    return;
                }

                builder.AppendLine("──────── FT1.2 帧 ────────");
                builder.AppendLine($"  原始    {BitConverter.ToString(bytes, offset, frameLength).Replace('-', ' ')}");

                if (Ft12.TryValidate(bytes, offset, frameLength, out string error))
                {
                    builder.AppendLine("  校验    通过");
                }
                else
                {
                    builder.AppendLine($"  校验    失败 —— {error}");
                }

                if (start == Ft12.FixedFrameStart)
                {
                    builder.AppendLine($"  控制域  0x{bytes[offset + 1]:X2}（功能码 {bytes[offset + 1] & 0x0F}，ACD/DFC 见高位）");
                    builder.AppendLine($"  链路地址 {bytes[offset + 2]}");
                }
                else
                {
                    builder.AppendLine($"  控制域  0x{bytes[offset + 4]:X2}（功能码 {bytes[offset + 4] & 0x0F}）");
                    builder.AppendLine($"  链路地址 {bytes[offset + 5]}");

                    if (Ft12.TryGetAsdu(bytes.AsSpan(offset).ToArray(), 0, out byte[] asduBytes) && asduBytes.Length > 0)
                    {
                        builder.AppendLine();
                        builder.AppendLine($"  ASDU    {BitConverter.ToString(asduBytes).Replace('-', ' ')}");
                        try
                        {
                            Asdu asdu = Asdu.Decode(asduBytes, 0, parameters, out int consumed);
                            AppendAsdu(builder, asdu, consumed);
                        }
                        catch (Exception exception)
                        {
                            builder.AppendLine($"  [ASDU 解析失败] {exception.Message}");
                        }
                    }
                }

                builder.AppendLine();
                offset += frameLength;
            }
        }

        /// <summary>输出 ASDU 各字段</summary>
        private static void AppendAsdu(StringBuilder builder, Asdu asdu, int consumed)
        {
            builder.AppendLine($"  ── ASDU（{consumed} 字节）──");
            builder.AppendLine($"  类型标识 {asdu.TypeId}（{(int)asdu.TypeId}）  {Services.TypeIdCatalog.DisplayNameOf(asdu.TypeId)}");
            builder.AppendLine($"  传送原因 {asdu.Cot}（{(int)asdu.Cot}）{(asdu.IsNegative ? " [否定确认]" : string.Empty)}{(asdu.IsTest ? " [测试帧]" : string.Empty)}");
            builder.AppendLine($"  公共地址 {asdu.CommonAddress}");
            builder.AppendLine($"  对象数量 {asdu.NumberOfObjects}    序列(SQ) {(asdu.IsSequence ? "是（地址连续）" : "否")}");

            if (asdu.InformationObjects.Count == 0)
            {
                return;
            }

            builder.AppendLine("  ── 信息对象 ──");
            int max = Math.Min(asdu.InformationObjects.Count, 20);
            for (int i = 0; i < max; i++)
            {
                InformationObject item = asdu.InformationObjects[i];
                builder.AppendLine($"    [{i}] IOA={item.ObjectAddress}  {DescribeObject(item)}");
            }

            if (asdu.InformationObjects.Count > max)
            {
                builder.AppendLine($"    … 其余 {asdu.InformationObjects.Count - max} 个对象省略");
            }
        }

        /// <summary>按具体类型给出关键值描述</summary>
        private static string DescribeObject(InformationObject item)
        {
            return item switch
            {
                // QOI 20 是"站总召"（全数据），21..36 才是第 1..16 组
                InterrogationCommand cmd => $"QOI={cmd.Qoi}（{(cmd.Qoi == 0 || cmd.Qoi == 20
                    ? "站总召"
                    : cmd.Qoi > 20 && cmd.Qoi <= 36 ? "第 " + (cmd.Qoi - 20) + " 组" : "QOI=" + cmd.Qoi)}）",
                CounterInterrogationCommand cmd => $"QCC=0x{cmd.Qualifier:X2}（RQT={cmd.Request}, FRZ={cmd.Freeze}）",
                ClockSynchronizationCommand cmd => $"时标={cmd.NewTime}",
                SingleCommand cmd => $"{(cmd.Value ? "合" : "分")} {(cmd.IsSelect ? "选择" : "执行")} QU={cmd.Qualifier}",
                DoubleCommand cmd => $"{(int)cmd.Value} {(cmd.IsSelect ? "选择" : "执行")}",
                SetpointCommandShort cmd => $"设点={cmd.Value} {(cmd.Qualifier.IsSelect ? "选择" : "执行")}",
                SetpointCommandNormalized cmd => $"设点={cmd.RawValue}",
                TestCommand cmd => $"固定位组=0x{cmd.FixedBitPattern:X4}",
                ReadCommand => "读命令（无信息元素）",
                SinglePointInformation sp => $"SIQ=0x{sp.Quality.Encode():X2} 值={(sp.Quality.Value ? "合" : "分")}",
                SinglePointWithCp56Time2a sp => $"SIQ 值={(sp.Quality.Value ? "合" : "分")} 时标={sp.Timestamp}",
                // DoublePointInformation 只暴露 Quality（DoublePointQuality），
                // 双点状态值在 Quality.Value 里，不是对象自身的属性
                DoublePointInformation dp => $"DIQ=0x{dp.Quality.Encode():X2} 值={(int)dp.Quality.Value}",
                MeasuredValueShort mv => $"值={mv.Value} 品质={mv.Quality}",
                MeasuredValueNormalized mv => $"原始值={mv.RawValue} 工程值={mv.EngineeringValue:F4}",
                MeasuredValueScaled mv => $"标度值={mv.RawValue}",
                IntegratedTotals it => $"读数={it.CounterReading} 溢出={it.HasOverflowed}",
                StepPositionInformation st => $"档位={st.Value} 过渡态={st.IsTransient}",
                Bitstring32 bs => $"0x{bs.Value:X8}",
                EndOfInitialization eoi => $"COI={(byte)eoi.Cause}",
                _ => item.ToString()
            };
        }

        private static string DescribeFrameType(Apci.FrameType frameType)
        {
            return frameType switch
            {
                Apci.FrameType.Information => "I 帧（信息传输，承载 ASDU）",
                Apci.FrameType.Supervisory => "S 帧（监视确认，仅确认序号）",
                Apci.FrameType.Unnumbered => "U 帧（链路控制）",
                _ => "未知帧（格式错误）"
            };
        }

        private static string DescribeUFunction(byte function)
        {
            return function switch
            {
                0x07 => "STARTDT 激活",
                0x0B => "STARTDT 确认",
                0x13 => "STOPDT 激活",
                0x23 => "STOPDT 确认",
                0x43 => "TESTFR 激活",
                0x83 => "TESTFR 确认",
                _ => "未知功能码"
            };
        }

        /// <summary>
        /// 把用户输入解析为字节数组。
        /// 支持 "68 0E 00"、"680E00"、"0x68,0x0E" 等写法——现场从抓包工具复制过来的格式五花八门。
        /// </summary>
        public static byte[] ParseHex(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<byte>();
            }

            // 统一去掉常见分隔与前缀
            string cleaned = text
                .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("0X", string.Empty)
                .Replace(",", " ")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("\t", " ")
                .Replace("-", " ");

            List<byte> result = new List<byte>();
            foreach (string token in cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length <= 2)
                {
                    result.Add(Convert.ToByte(token, 16));
                    continue;
                }

                // 连续无分隔的长串，按每两字符切
                for (int i = 0; i + 1 < token.Length; i += 2)
                {
                    result.Add(Convert.ToByte(token.Substring(i, 2), 16));
                }
            }

            return result.ToArray();
        }
    }
}
