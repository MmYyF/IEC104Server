using System;
using System.Collections.Generic;
using IEC104.Core.Time;

namespace IEC104.Core.CS101
{
    /// <summary>
    /// 监视方向的信息对象：测量值、累计量、步位置、比特串、初始化结束。
    ///
    /// 复刻说明（源程序集：lib60870.dll 中 lib60870.CS101 下的
    /// MeasuredValueNormalized / MeasuredValueScaled / MeasuredValueShort /
    /// MeasuredValueNormalizedWithoutQuality / IntegratedTotals /
    /// StepPositionInformation / Bitstring32 / EndOfInitialization）：
    /// 原实现为每一种都单独成文件，此处为便于对照阅读合并到一个文件中，
    /// 每个类的信息元素字节数与语义与原实现保持一致。
    ///
    /// 之所以按"信息元素不同"拆成多个类而不是用一个通用类：
    /// 各类型的字节长度与换算方式都不一样（2 字节整数 / 4 字节浮点 / 5 字节 BCR…），
    /// 合并后编解码会变成一堆 switch，反而更难维护。
    /// </summary>

    /// <summary>
    /// M_ME_NA_1 测量值·归一化值 —— TypeID 9。
    /// 信息元素：NVA(2 字节有符号) + QDS(1 字节)，总长 3 字节。
    ///
    /// 归一化值的含义：2 字节补码，量程约定为 -1 .. +32767/32768，
    /// 即真实值 = 原始值 / 32768（满量程系数为 1）。
    /// 现场换算出错的高频原因：不同厂家对"满量程"的定义不同，
    /// 联调时必须确认从站的量程系数（有的用 32767，有的用 32768，还有的按 PT/CT 变比换算）。
    /// </summary>
    public class MeasuredValueNormalized : InformationObject
    {
        /// <summary>归一化原始值，-32768..32767</summary>
        public short RawValue { get; set; }

        public QualityDescriptor Quality { get; set; }

        /// <summary>按 32768 满量程换算出的工程值</summary>
        public double EngineeringValue => RawValue / 32768.0;

        public MeasuredValueNormalized(int objectAddress) : base(objectAddress) { }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            output.Add((byte)(RawValue & 0xFF));
            output.Add((byte)((RawValue >> 8) & 0xFF));
            output.Add(Quality.Encode());
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            RawValue = (short)(buffer[startIndex] | (buffer[startIndex + 1] << 8));
            Quality = QualityDescriptor.Decode(buffer[startIndex + 2]);
            return 3;
        }

        public override string ToString()
            => $"归一化测量值 IOA={ObjectAddress} 原始={RawValue} 工程值={EngineeringValue:F4}";
    }

    /// <summary>
    /// M_ME_NB_1 测量值·标度化值 —— TypeID 11。
    /// 信息元素：SVA(2 字节有符号) + QDS(1 字节)，总长 3 字节。
    /// 与归一化的区别：标度化值是整数，需配合外部的小数点位（如 1234 表示 12.34）。
    /// </summary>
    public class MeasuredValueScaled : InformationObject
    {
        /// <summary>标度化原始值</summary>
        public short RawValue { get; set; }

        public QualityDescriptor Quality { get; set; }

        public MeasuredValueScaled(int objectAddress) : base(objectAddress) { }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            output.Add((byte)(RawValue & 0xFF));
            output.Add((byte)((RawValue >> 8) & 0xFF));
            output.Add(Quality.Encode());
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            RawValue = (short)(buffer[startIndex] | (buffer[startIndex + 1] << 8));
            Quality = QualityDescriptor.Decode(buffer[startIndex + 2]);
            return 3;
        }

        public override string ToString() => $"标度化测量值 IOA={ObjectAddress} 值={RawValue}";
    }

    /// <summary>
    /// M_ME_NC_1 测量值·短浮点数 —— TypeID 13。
    /// 信息元素：IEEE 754 单精度(4 字节) + QDS(1 字节)，总长 5 字节。
    /// 精度最高、无需外部系数的类型，新工程优先选它。
    /// 注意字节序：104 规约中浮点按低字节在前传输（小端）。
    /// </summary>
    public class MeasuredValueShort : InformationObject
    {
        /// <summary>浮点测量值</summary>
        public float Value { get; set; }

        public QualityDescriptor Quality { get; set; }

        public MeasuredValueShort(int objectAddress) : base(objectAddress) { }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            byte[] valueBytes = BitConverter.GetBytes(Value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(valueBytes);
            }
            output.AddRange(valueBytes);
            output.Add(Quality.Encode());
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            byte[] valueBytes = new byte[4];
            Array.Copy(buffer, startIndex, valueBytes, 0, 4);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(valueBytes);
            }
            Value = BitConverter.ToSingle(valueBytes, 0);
            Quality = QualityDescriptor.Decode(buffer[startIndex + 4]);
            return 5;
        }

        public override string ToString() => $"短浮点测量值 IOA={ObjectAddress} 值={Value}";
    }

    /// <summary>
    /// M_ME_ND_1 不带品质描述词的归一化测量值 —— TypeID 21。
    /// 信息元素：NVA(2 字节)，总长 2 字节（比 TypeID 9 省 1 字节）。
    /// 用于周期性批量上送遥测、且品质恒定的场景，可显著降低带宽。
    /// </summary>
    public class MeasuredValueNormalizedWithoutQuality : InformationObject
    {
        public short RawValue { get; set; }

        public double EngineeringValue => RawValue / 32768.0;

        public MeasuredValueNormalizedWithoutQuality(int objectAddress) : base(objectAddress) { }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            output.Add((byte)(RawValue & 0xFF));
            output.Add((byte)((RawValue >> 8) & 0xFF));
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            RawValue = (short)(buffer[startIndex] | (buffer[startIndex + 1] << 8));
            return 2;
        }

        public override string ToString()
            => $"归一化测量值(无品质) IOA={ObjectAddress} 工程值={EngineeringValue:F4}";
    }

    /// <summary>
    /// M_IT_NA_1 累计量（电度）—— TypeID 15。
    /// 信息元素：BCR 5 字节 = 计数器读数(4 字节有符号) + 顺序记号(1 字节)。
    /// 顺序记号 bit5 为 1 表示计数器已溢出（累计量回绕），主站需据此做跨窗口处理。
    /// </summary>
    public class IntegratedTotals : InformationObject
    {
        /// <summary>二进制计数器读数（BCR 的前 4 字节）</summary>
        public int CounterReading { get; set; }

        /// <summary>顺序记号 SQ：bit0-4 为序号，bit5 为 CY（溢出），bit6 为 CA（被调整），bit7 为 IV（无效）</summary>
        public byte SequenceNotation { get; set; }

        /// <summary>CY：计数器溢出/回绕</summary>
        public bool HasOverflowed => (SequenceNotation & 0x20) != 0;

        /// <summary>IV：无效</summary>
        public bool Invalid => (SequenceNotation & 0x80) != 0;

        public IntegratedTotals(int objectAddress) : base(objectAddress) { }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            output.Add((byte)(CounterReading & 0xFF));
            output.Add((byte)((CounterReading >> 8) & 0xFF));
            output.Add((byte)((CounterReading >> 16) & 0xFF));
            output.Add((byte)((CounterReading >> 24) & 0xFF));
            output.Add(SequenceNotation);
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            CounterReading = buffer[startIndex]
                | (buffer[startIndex + 1] << 8)
                | (buffer[startIndex + 2] << 16)
                | (buffer[startIndex + 3] << 24);
            SequenceNotation = buffer[startIndex + 4];
            return 5;
        }

        public override string ToString()
            => $"累计量 IOA={ObjectAddress} 读数={CounterReading} 溢出={HasOverflowed}";
    }

    /// <summary>
    /// M_ST_NA_1 步位置信息（变压器档位）—— TypeID 5。
    /// 信息元素：VTI(1 字节) + QDS(1 字节)，总长 2 字节。
    /// VTI bit0-6 为带符号档位值(-64..63)，bit7 为过渡态标志（正在调档）。
    /// </summary>
    public class StepPositionInformation : InformationObject
    {
        /// <summary>档位值，-64..63</summary>
        public sbyte Value { get; set; }

        /// <summary>过渡态：true 表示档位正在切换过程中，读数不可采信</summary>
        public bool IsTransient { get; set; }

        public QualityDescriptor Quality { get; set; }

        public StepPositionInformation(int objectAddress) : base(objectAddress) { }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            byte vti = (byte)((byte)Value & 0x7F);
            if (IsTransient)
            {
                vti |= 0x80;
            }
            output.Add(vti);
            output.Add(Quality.Encode());
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            byte vti = buffer[startIndex];
            IsTransient = (vti & 0x80) != 0;
            // 低 7 位是补码形式的档位，需符号扩展到 sbyte
            Value = (sbyte)((vti & 0x7F) | ((vti & 0x40) != 0 ? 0x80 : 0x00));
            Quality = QualityDescriptor.Decode(buffer[startIndex + 1]);
            return 2;
        }

        public override string ToString()
            => $"步位置 IOA={ObjectAddress} 档位={Value} 过渡态={IsTransient}";
    }

    /// <summary>
    /// M_BO_NA_1 32 位比特串 —— TypeID 7。
    /// 信息元素：BSI(4 字节) + QDS(1 字节)，总长 5 字节。
    /// 常用于成组上送保护压板状态、告警字等按位定义的量。
    /// </summary>
    public class Bitstring32 : InformationObject
    {
        /// <summary>32 位比特串，bit0 对应最低位</summary>
        public uint Value { get; set; }

        public QualityDescriptor Quality { get; set; }

        public Bitstring32(int objectAddress) : base(objectAddress) { }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            output.Add((byte)(Value & 0xFF));
            output.Add((byte)((Value >> 8) & 0xFF));
            output.Add((byte)((Value >> 16) & 0xFF));
            output.Add((byte)((Value >> 24) & 0xFF));
            output.Add(Quality.Encode());
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            Value = (uint)(buffer[startIndex]
                | (buffer[startIndex + 1] << 8)
                | (buffer[startIndex + 2] << 16)
                | (buffer[startIndex + 3] << 24));
            Quality = QualityDescriptor.Decode(buffer[startIndex + 4]);
            return 5;
        }

        /// <summary>取指定位（0-based）</summary>
        public bool GetBit(int index)
        {
            if (index < 0 || index > 31)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            return (Value & (1u << index)) != 0;
        }

        public override string ToString() => $"32位比特串 IOA={ObjectAddress} 值=0x{Value:X8}";
    }

    /// <summary>
    /// M_EI_NA_1 初始化结束 —— TypeID 70。
    /// 信息元素：COI 1 字节。从站上电或复位后必须主动上送，主站收到后才会发起总召。
    /// COI bit0-6 为初始化原因，bit7 为"当地参数改变后是否需重新上装"。
    /// 联调提示：从站若不发这一帧，主站通常会一直等，表现为"连上了但没数据"。
    /// </summary>
    public class EndOfInitialization : InformationObject
    {
        /// <summary>初始化原因</summary>
        public enum InitializationCause : byte
        {
            /// <summary>0：当地电源合上</summary>
            LocalPowerOn = 0,

            /// <summary>1：当地手动复位</summary>
            LocalManualReset = 1,

            /// <summary>2：远方复位</summary>
            RemoteReset = 2
        }

        public InitializationCause Cause { get; set; }

        /// <summary>true 表示当地参数已改变，主站需重新召唤参数</summary>
        public bool ParametersChanged { get; set; }

        public EndOfInitialization(int objectAddress) : base(objectAddress) { }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            byte coi = (byte)((byte)Cause & 0x7F);
            if (ParametersChanged)
            {
                coi |= 0x80;
            }
            output.Add(coi);
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            byte coi = buffer[startIndex];
            Cause = (InitializationCause)(coi & 0x7F);
            ParametersChanged = (coi & 0x80) != 0;
            return 1;
        }

        public override string ToString()
            => $"初始化结束 IOA={ObjectAddress} 原因={Cause} 参数已改变={ParametersChanged}";
    }
}
