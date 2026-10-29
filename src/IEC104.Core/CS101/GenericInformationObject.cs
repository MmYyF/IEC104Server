using System;
using System.Collections.Generic;
using IEC104.Core.Time;

namespace IEC104.Core.CS101
{
    /// <summary>
    /// 通用信息对象 —— 一个类覆盖"8 种信息元素 × 3 种时标"的全部组合。
    ///
    /// 它存在的理由只有一个：**让 ASDU 类型可以在运行时被扩展**。
    ///
    /// 原来"一个 TypeID 一个类"的写法，字节序和换算都写死在类里，读起来最清楚，
    /// 但它有个硬前提——类型集合在编译期就固定了。用户以后要加一个标准里新出的类型
    /// （或者项目自己约定了一个类型号），就必须改代码重编译。
    /// 而实际上规约里的 TypeID 只有两种信息：**元素格式**和**时标方式**。
    /// 把这两个维度提出来之后，新类型只是"注册一条映射"，不需要新类。
    ///
    /// 用在哪：
    ///   · 用户自定义的类型（设置 → ASDU 类型 → 新增）；
    ///   · 预置了类型但没写强类型类的标准类型（见 <see cref="InformationObjectRegistry"/>）。
    /// 内置的主力类型（M_SP_NA_1 / M_ME_NC_1…）仍走各自的强类型类，
    /// 那条路径一个字节都没动，避免"为了扩展性把老链路改坏"。
    ///
    /// 值统一用 double 承载：8 种格式里最宽的是 R32，double 装得下；
    /// 编码时再按格式窄化成 short / float / int，换算规则写在各自的 case 里。
    /// </summary>
    public sealed class GenericInformationObject : InformationObject
    {
        /// <summary>信息元素格式（决定前半段字节布局）</summary>
        public ElementFormat Format { get; }

        /// <summary>时标方式（决定后面跟 0 / 3 / 7 字节）</summary>
        public TimeTagKind TimeTag { get; }

        /// <summary>工程值。单点/双点用 0/1（双点 0..3），累计量用整数，比特串用按位取整后的值</summary>
        public double Value { get; set; }

        /// <summary>品质（QDS）。累计量用它表达 IV 位</summary>
        public QualityDescriptor Quality { get; set; }

        /// <summary>时标（仅 TimeTag != None 时参与编码）</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>步位置专用：VT 暂态标志（该步位置正在向目标值移动）</summary>
        public bool Transient { get; set; }

        /// <summary>累计量专用：CY 计数器进位标志</summary>
        public bool Carry { get; set; }

        /// <summary>累计量专用：CA 计数器被调整过</summary>
        public bool CounterAdjusted { get; set; }

        public GenericInformationObject(int objectAddress, ElementFormat format, TimeTagKind timeTag)
            : base(objectAddress)
        {
            Format = format;
            TimeTag = timeTag;
            Timestamp = DateTime.Now;
        }

        /// <summary>信息元素本身的字节数（不含时标）</summary>
        public int ElementLength => Format switch
        {
            ElementFormat.SinglePoint => 1,
            ElementFormat.DoublePoint => 1,
            ElementFormat.Normalized => 3,
            ElementFormat.Scaled => 3,
            ElementFormat.ShortFloat => 5,
            ElementFormat.IntegratedTotal => 5,
            ElementFormat.StepPosition => 2,
            ElementFormat.Bitstring32 => 5,
            ElementFormat.NormalizedNoQuality => 2,
            _ => 1
        };

        /// <summary>时标字节数</summary>
        public int TimeTagLength => TimeTag switch
        {
            TimeTagKind.Cp24Time2a => Cp24Time2a.EncodedLength,
            TimeTagKind.Cp56Time2a => Cp56Time2a.EncodedLength,
            _ => 0
        };

        /// <summary>本信息对象在报文里占用的总字节数（含时标）</summary>
        public int EncodedLength => ElementLength + TimeTagLength;

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            switch (Format)
            {
                case ElementFormat.SinglePoint:
                    // SIQ：bit0 SPI，品质位整体上移 4 位（BL=0x10 SB=0x20 NT=0x40 IV=0x80）
                    output.Add((byte)((Value != 0 ? 0x01 : 0x00) | ShiftQuality(Quality)));
                    break;

                case ElementFormat.DoublePoint:
                {
                    byte diq = (byte)((byte)ClampValue(0, 3) & 0x03);
                    if (Quality.Blocked) diq |= 0x10;
                    if (Quality.Substituted) diq |= 0x20;
                    if (Quality.NotTopical) diq |= 0x40;
                    if (Quality.Invalid) diq |= 0x80;
                    output.Add(diq);
                    break;
                }

                case ElementFormat.Normalized:
                    // NVA：满量程按 32768 换算，2 字节补码小端
                    AppendInt16(output, (short)Math.Clamp(Value * 32768.0, short.MinValue, short.MaxValue));
                    output.Add(Quality.Encode());
                    break;

                case ElementFormat.Scaled:
                    AppendInt16(output, (short)Math.Clamp(Value, short.MinValue, short.MaxValue));
                    output.Add(Quality.Encode());
                    break;

                case ElementFormat.NormalizedNoQuality:
                    // 只有 NVA，没有 QDS —— 少这一字节正是它存在的意义（省带宽）
                    AppendInt16(output, (short)Math.Clamp(Value * 32768.0, short.MinValue, short.MaxValue));
                    break;

                case ElementFormat.ShortFloat:
                    AppendSingle(output, (float)Value);
                    output.Add(Quality.Encode());
                    break;

                case ElementFormat.IntegratedTotal:
                {
                    int counter = (int)Math.Clamp(Value, int.MinValue, int.MaxValue);
                    output.Add((byte)(counter & 0xFF));
                    output.Add((byte)((counter >> 8) & 0xFF));
                    output.Add((byte)((counter >> 16) & 0xFF));
                    output.Add((byte)((counter >> 24) & 0xFF));

                    // BCR 标志字节：bit0-4 顺序号（本实现恒 0），bit5 CY，bit6 CA，bit7 IV
                    byte bcr = 0;
                    if (Carry) bcr |= 0x20;
                    if (CounterAdjusted) bcr |= 0x40;
                    if (Quality.Invalid) bcr |= 0x80;
                    output.Add(bcr);
                    break;
                }

                case ElementFormat.StepPosition:
                {
                    // VTI：bit0-6 七位补码（-64..63），bit7 暂态
                    sbyte step = (sbyte)Math.Clamp(Value, -64, 63);
                    byte vti = (byte)(step & 0x7F);
                    if (Transient) vti |= 0x80;
                    output.Add(vti);
                    output.Add(Quality.Encode());
                    break;
                }

                case ElementFormat.Bitstring32:
                    AppendUInt32(output, (uint)Math.Clamp(Value, uint.MinValue, uint.MaxValue));
                    output.Add(Quality.Encode());
                    break;
            }

            AppendTimeTag(output);
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            int offset = startIndex;

            switch (Format)
            {
                case ElementFormat.SinglePoint:
                {
                    byte siq = buffer[offset];
                    Value = (siq & 0x01) != 0 ? 1 : 0;
                    Quality = new QualityDescriptor
                    {
                        Blocked = (siq & 0x10) != 0,
                        Substituted = (siq & 0x20) != 0,
                        NotTopical = (siq & 0x40) != 0,
                        Invalid = (siq & 0x80) != 0
                    };
                    offset += 1;
                    break;
                }

                case ElementFormat.DoublePoint:
                {
                    byte diq = buffer[offset];
                    Value = diq & 0x03;
                    Quality = new QualityDescriptor
                    {
                        Blocked = (diq & 0x10) != 0,
                        Substituted = (diq & 0x20) != 0,
                        NotTopical = (diq & 0x40) != 0,
                        Invalid = (diq & 0x80) != 0
                    };
                    offset += 1;
                    break;
                }

                case ElementFormat.Normalized:
                    Value = ReadInt16(buffer, offset) / 32768.0;
                    Quality = QualityDescriptor.Decode(buffer[offset + 2]);
                    offset += 3;
                    break;

                case ElementFormat.Scaled:
                    Value = ReadInt16(buffer, offset);
                    Quality = QualityDescriptor.Decode(buffer[offset + 2]);
                    offset += 3;
                    break;

                case ElementFormat.NormalizedNoQuality:
                    Value = ReadInt16(buffer, offset) / 32768.0;
                    offset += 2;
                    break;

                case ElementFormat.ShortFloat:
                    Value = ReadSingle(buffer, offset);
                    Quality = QualityDescriptor.Decode(buffer[offset + 4]);
                    offset += 5;
                    break;

                case ElementFormat.IntegratedTotal:
                {
                    Value = buffer[offset]
                        | (buffer[offset + 1] << 8)
                        | (buffer[offset + 2] << 16)
                        | (buffer[offset + 3] << 24);
                    byte bcr = buffer[offset + 4];
                    Carry = (bcr & 0x20) != 0;
                    CounterAdjusted = (bcr & 0x40) != 0;
                    Quality = new QualityDescriptor { Invalid = (bcr & 0x80) != 0 };
                    offset += 5;
                    break;
                }

                case ElementFormat.StepPosition:
                {
                    byte vti = buffer[offset];
                    Transient = (vti & 0x80) != 0;
                    // 七位补码左对齐还原：0x40 是符号位
                    int raw = vti & 0x7F;
                    Value = (raw & 0x40) != 0 ? raw - 0x80 : raw;
                    Quality = QualityDescriptor.Decode(buffer[offset + 1]);
                    offset += 2;
                    break;
                }

                case ElementFormat.Bitstring32:
                    Value = ReadUInt32(buffer, offset);
                    Quality = QualityDescriptor.Decode(buffer[offset + 4]);
                    offset += 5;
                    break;
            }

            switch (TimeTag)
            {
                case TimeTagKind.Cp24Time2a:
                    Cp24Time2a cp24 = Cp24Time2a.Decode(buffer, offset);
                    Timestamp = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day,
                        DateTime.Now.Hour, cp24.Minutes, cp24.Milliseconds / 1000).AddMilliseconds(cp24.Milliseconds % 1000);
                    offset += Cp24Time2a.EncodedLength;
                    break;

                case TimeTagKind.Cp56Time2a:
                    Cp56Time2a cp56 = Cp56Time2a.Decode(buffer, offset);
                    Timestamp = new DateTime(
                        2000 + cp56.Year, cp56.Month, cp56.DayOfMonth,
                        cp56.Hours, cp56.Minutes, cp56.Milliseconds / 1000).AddMilliseconds(cp56.Milliseconds % 1000);
                    offset += Cp56Time2a.EncodedLength;
                    break;
            }

            return offset - startIndex;
        }

        public override string ToString()
            => $"{Format}{(TimeTag == TimeTagKind.None ? string.Empty : "(" + TimeTag + ")")} "
               + $"IOA={ObjectAddress} 值={Value}";

        // ── 内部工具 ────────────────────────────────────────────

        /// <summary>把 QDS 的 BL/SB/NT/IV 从 bit1-4 平移到 bit4-7（SIQ 的排版）</summary>
        private static byte ShiftQuality(QualityDescriptor quality)
        {
            int result = 0;
            if (quality.Blocked) result |= 0x10;
            if (quality.Substituted) result |= 0x20;
            if (quality.NotTopical) result |= 0x40;
            if (quality.Invalid) result |= 0x80;
            return (byte)result;
        }

        private int ClampValue(int min, int max)
            => (int)Math.Clamp(Math.Round(Value), min, max);

        private void AppendTimeTag(List<byte> output)
        {
            if (TimeTag == TimeTagKind.None)
            {
                return;
            }

            byte[] buffer = new byte[TimeTagLength];

            if (TimeTag == TimeTagKind.Cp24Time2a)
            {
                // CP24 没有日期，只写毫秒与分钟 —— 恰好是 DateTime 里够用的那部分
                new Cp24Time2a
                {
                    Milliseconds = (ushort)(Timestamp.Second * 1000 + Timestamp.Millisecond),
                    Minutes = (byte)Math.Clamp(Timestamp.Minute, 0, 59),
                    IsInvalid = false
                }.Encode(buffer, 0);
            }
            else
            {
                Cp56Time2a.FromDateTime(Timestamp).Encode(buffer, 0);
            }

            output.AddRange(buffer);
        }

        private static void AppendInt16(List<byte> output, short value)
        {
            output.Add((byte)(value & 0xFF));
            output.Add((byte)((value >> 8) & 0xFF));
        }

        private static void AppendSingle(List<byte> output, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            output.AddRange(bytes);
        }

        private static void AppendUInt32(List<byte> output, uint value)
        {
            output.Add((byte)(value & 0xFF));
            output.Add((byte)((value >> 8) & 0xFF));
            output.Add((byte)((value >> 16) & 0xFF));
            output.Add((byte)((value >> 24) & 0xFF));
        }

        private static short ReadInt16(byte[] buffer, int index)
            => (short)(buffer[index] | (buffer[index + 1] << 8));

        private static float ReadSingle(byte[] buffer, int index)
        {
            byte[] bytes = { buffer[index], buffer[index + 1], buffer[index + 2], buffer[index + 3] };
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            return BitConverter.ToSingle(bytes, 0);
        }

        private static uint ReadUInt32(byte[] buffer, int index)
            => (uint)(buffer[index]
                | (buffer[index + 1] << 8)
                | (buffer[index + 2] << 16)
                | (buffer[index + 3] << 24));
    }
}
