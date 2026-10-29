using System;
using System.Collections.Generic;
using IEC104.Core.Time;

namespace IEC104.Core.CS101
{
    /// <summary>
    /// 命令方向的信息对象集合（C_ 开头，主站 → 从站）。
    ///
    /// 复刻说明（对应原实现 lib60870.CS101 下的
    /// InterrogationCommand / CounterInterrogationCommand / ClockSynchronizationCommand /
    /// SingleCommand / DoubleCommand / SetpointCommandNormalized / SetpointCommandShort / TestCommand）：
    /// 原实现为每一条命令各建一个类，此处沿用同样的"一命令一类"结构，
    /// 因为各命令的信息元素字节数与语义完全不同，强行合并会让编解码到处是分支。
    ///
    /// 这些类是从站能否正确响应主站的关键——
    /// 少了任何一类，从站收到对应命令后都只能回否定确认或抛异常。
    /// </summary>

    /// <summary>
    /// C_IC_NA_1 总召唤命令 —— TypeID 100。
    /// 信息元素：QOI 召唤限定词（1 字节）。
    ///   QOI = 0    ：站总召（召唤本站全部数据）
    ///   QOI = 1..16：第 1..16 组召唤
    ///   QOI = 20..36：自定义组（厂家扩展）
    /// 注意：QOI 是信息元素而非信息对象地址，
    /// 一些调试工具把它填在 IOA 位置上是常见错误——从站解析时务必按信息元素读。
    /// </summary>
    public class InterrogationCommand : InformationObject
    {
        /// <summary>召唤限定词 QOI</summary>
        public byte Qoi { get; set; }

        public InterrogationCommand(int objectAddress, byte qoi = 0) : base(objectAddress)
        {
            Qoi = qoi;
        }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            output.Add(Qoi);
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            Qoi = buffer[startIndex];
            return 1;
        }

        public override string ToString() => $"总召唤命令 IOA={ObjectAddress} QOI={Qoi}";
    }

    /// <summary>
    /// C_CI_NA_1 计数量召唤命令 —— TypeID 101。
    /// 信息元素：QCC 计数量召唤限定词（1 字节）= 请求位 RQT(bit0-5) + 冻结位 FRZ(bit6-7)。
    ///   RQT = 0 无效 / 1 请求计数量组1 / .. / 5 请求计数量组1..4 全部
    ///   FRZ = 0 读（不冻结）/ 1 冻结不复位 / 2 冻结并复位 / 3 复位
    /// 电度采集里的"冻结"语义很关键：冻结后读才能让多个间隔的数据在同一时刻断面。
    /// </summary>
    public class CounterInterrogationCommand : InformationObject
    {
        /// <summary>请求位 RQT（低 6 位）</summary>
        public byte Request { get; set; }

        /// <summary>冻结位 FRZ（高 2 位）</summary>
        public byte Freeze { get; set; }

        /// <summary>完整的 QCC 字节</summary>
        public byte Qualifier
        {
            get => (byte)(((Freeze & 0x03) << 6) | (Request & 0x3F));
            set
            {
                Request = (byte)(value & 0x3F);
                Freeze = (byte)((value >> 6) & 0x03);
            }
        }

        public CounterInterrogationCommand(int objectAddress, byte qualifier = 0) : base(objectAddress)
        {
            Qualifier = qualifier;
        }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            output.Add(Qualifier);
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            Qualifier = buffer[startIndex];
            return 1;
        }

        public override string ToString()
            => $"计数量召唤命令 IOA={ObjectAddress} RQT={Request} FRZ={Freeze}";
    }

    /// <summary>
    /// C_CS_NA_1 时钟同步命令 —— TypeID 103。
    /// 信息元素：CP56Time2a（7 字节）。
    ///
    /// 从站收到后应校准本地时钟，并在激活确认帧中【原样回显】该时标，
    /// 主站据此计算通道延时并做补偿——这是变电站对时精度的基本保证。
    ///
    /// 复刻说明：原实现 ClockSynchronizationCommand 内部即持有一个 CP56Time2a 成员，
    /// 与此处结构一致。
    /// </summary>
    public class ClockSynchronizationCommand : InformationObject
    {
        /// <summary>主站下发的新时间</summary>
        public Cp56Time2a NewTime { get; set; }

        public ClockSynchronizationCommand(int objectAddress, Cp56Time2a newTime) : base(objectAddress)
        {
            NewTime = newTime;
        }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            output.AddRange(NewTime.Encode());
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            NewTime = Cp56Time2a.Decode(buffer, startIndex);
            return 7;
        }

        public override string ToString()
            => $"时钟同步命令 IOA={ObjectAddress} 时间={(NewTime.ToDateTime()?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "无效")}";
    }

    /// <summary>
    /// 单点命令的限定词 SCO（Select/Execute 语义）。
    /// 遥控的"选择—执行"两阶段是防止误动的核心机制：
    ///   1) 主站先发"选择"（S/E=0），从站校验点号与合法性后回确认
    ///   2) 主站再发"执行"（S/E=1），从站才真正出口
    /// 直接发"执行"而不先"选择"，正规主站/从站都应拒绝。
    /// </summary>
    public enum SingleCommandQualifier : byte
    {
        /// <summary>执行（Execute）</summary>
        Execute = 0x00,

        /// <summary>选择（Select）</summary>
        Select = 0x80
    }

    /// <summary>
    /// C_SC_NA_1 单点遥控命令 —— TypeID 45。
    /// 信息元素：SCO（1 字节）= S/E 位(bit7) + QU 位(bit2-6) + 单点状态(bit0)。
    /// </summary>
    public class SingleCommand : InformationObject
    {
        /// <summary>true = 合（ON），false = 分（OFF）</summary>
        public bool Value { get; set; }

        /// <summary>选择/执行</summary>
        public bool IsSelect { get; set; }

        /// <summary>限定词 QU（0=无附加定义，1=短脉冲，2=长脉冲，3=持续输出）</summary>
        public byte Qualifier { get; set; }

        public SingleCommand(int objectAddress) : base(objectAddress) { }

        public SingleCommand(int objectAddress, bool value, bool isSelect) : base(objectAddress)
        {
            Value = value;
            IsSelect = isSelect;
        }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            byte sco = (byte)(((Qualifier & 0x1F) << 2) | (Value ? 0x01 : 0x00));
            if (IsSelect)
            {
                sco |= 0x80;
            }
            output.Add(sco);
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            byte sco = buffer[startIndex];
            IsSelect = (sco & 0x80) != 0;
            Qualifier = (byte)((sco >> 2) & 0x1F);
            Value = (sco & 0x01) != 0;
            return 1;
        }

        public override string ToString()
            => $"单点遥控 IOA={ObjectAddress} {(Value ? "合" : "分")} {(IsSelect ? "选择" : "执行")}";
    }

    /// <summary>
    /// 双点状态值（DPI 的 2 位编码）。
    /// 双点比单点多一个"中间态/不确定态"，断路器分合闸位置必须用双点：
    /// 单点只能表达 0/1，断路器在分闸与合闸之间的中间位置无法表达，
    /// 而这个状态在现场判定"拒动/误动"时至关重要。
    /// </summary>
    public enum DoubleCommandValue : byte
    {
        /// <summary>0：不允许/中间态</summary>
        NotPermitted = 0,

        /// <summary>1：分（OFF）</summary>
        Off = 1,

        /// <summary>2：合（ON）</summary>
        On = 2,

        /// <summary>3：不确定</summary>
        Indeterminate = 3
    }

    /// <summary>
    /// C_DC_NA_1 双点遥控命令 —— TypeID 46。
    /// 信息元素：DCO（1 字节）= S/E 位(bit7) + QU 位(bit2-6) + DPI(bit0-1)。
    /// </summary>
    public class DoubleCommand : InformationObject
    {
        public DoubleCommandValue Value { get; set; }

        public bool IsSelect { get; set; }

        public byte Qualifier { get; set; }

        public DoubleCommand(int objectAddress) : base(objectAddress) { }

        public DoubleCommand(int objectAddress, DoubleCommandValue value, bool isSelect) : base(objectAddress)
        {
            Value = value;
            IsSelect = isSelect;
        }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            byte dco = (byte)(((Qualifier & 0x1F) << 2) | ((byte)Value & 0x03));
            if (IsSelect)
            {
                dco |= 0x80;
            }
            output.Add(dco);
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            byte dco = buffer[startIndex];
            IsSelect = (dco & 0x80) != 0;
            Qualifier = (byte)((dco >> 2) & 0x1F);
            Value = (DoubleCommandValue)(dco & 0x03);
            return 1;
        }

        public override string ToString()
            => $"双点遥控 IOA={ObjectAddress} {Value} {(IsSelect ? "选择" : "执行")}";
    }

    /// <summary>
    /// 设定命令限定词 QOS = 设定命令限定词(bit2-6) + S/E 位(bit7)。
    /// 与遥控同理，设点也分"选择"与"执行"两步。
    /// </summary>
    public class SetpointQualifier
    {
        /// <summary>限定词（0=默认）</summary>
        public byte Qualifier { get; set; }

        /// <summary>true = 选择阶段</summary>
        public bool IsSelect { get; set; }

        public byte Encode()
        {
            byte qos = (byte)((Qualifier & 0x7F) << 2);
            if (IsSelect)
            {
                qos |= 0x80;
            }
            return qos;
        }

        public static SetpointQualifier Decode(byte value)
        {
            return new SetpointQualifier
            {
                Qualifier = (byte)((value >> 2) & 0x7F),
                IsSelect = (value & 0x80) != 0
            };
        }
    }

    /// <summary>
    /// C_SE_NA_1 设定值命令·归一化值 —— TypeID 48。
    /// 信息元素：NVA(2 字节) + QOS(1 字节)，共 3 字节。
    /// </summary>
    public class SetpointCommandNormalized : InformationObject
    {
        public short RawValue { get; set; }

        public SetpointQualifier Qualifier { get; set; } = new SetpointQualifier();

        public SetpointCommandNormalized(int objectAddress) : base(objectAddress) { }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            output.Add((byte)(RawValue & 0xFF));
            output.Add((byte)((RawValue >> 8) & 0xFF));
            output.Add(Qualifier.Encode());
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            RawValue = (short)(buffer[startIndex] | (buffer[startIndex + 1] << 8));
            Qualifier = SetpointQualifier.Decode(buffer[startIndex + 2]);
            return 3;
        }

        public override string ToString() => $"归一化设点 IOA={ObjectAddress} 值={RawValue}";
    }

    /// <summary>
    /// C_SE_NC_1 设定值命令·短浮点数 —— TypeID 50。
    /// 信息元素：IEEE754 单精度(4 字节) + QOS(1 字节)，共 5 字节。
    /// </summary>
    public class SetpointCommandShort : InformationObject
    {
        public float Value { get; set; }

        public SetpointQualifier Qualifier { get; set; } = new SetpointQualifier();

        public SetpointCommandShort(int objectAddress) : base(objectAddress) { }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            byte[] valueBytes = BitConverter.GetBytes(Value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(valueBytes);
            }
            output.AddRange(valueBytes);
            output.Add(Qualifier.Encode());
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
            Qualifier = SetpointQualifier.Decode(buffer[startIndex + 4]);
            return 5;
        }

        public override string ToString() => $"短浮点设点 IOA={ObjectAddress} 值={Value}";
    }

    /// <summary>
    /// C_TS_NA_1 测试命令 —— TypeID 104。
    /// 信息元素：FBP 固定测试位组（2 字节），规约规定必须为 0x55AA（位模式 1010 1010 0101 0101）。
    /// 固定位组的作用是排除"报文恰好是合法结构但内容随机"的误判，
    /// 收到非 0x55AA 的测试命令应判为非法并回否定确认。
    /// </summary>
    public class TestCommand : InformationObject
    {
        /// <summary>规约规定的固定测试位组</summary>
        public const ushort ExpectedBitPattern = 0x55AA;

        /// <summary>收到的固定测试位组</summary>
        public ushort FixedBitPattern { get; set; }

        /// <summary>位组是否符合规约要求</summary>
        public bool IsPatternValid => FixedBitPattern == ExpectedBitPattern;

        public TestCommand(int objectAddress) : base(objectAddress) { }

        public TestCommand(int objectAddress, ushort pattern) : base(objectAddress)
        {
            FixedBitPattern = pattern;
        }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            output.Add((byte)(FixedBitPattern & 0xFF));
            output.Add((byte)((FixedBitPattern >> 8) & 0xFF));
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            FixedBitPattern = (ushort)(buffer[startIndex] | (buffer[startIndex + 1] << 8));
            return 2;
        }

        public override string ToString()
            => $"测试命令 IOA={ObjectAddress} 位组=0x{FixedBitPattern:X4} 合法={IsPatternValid}";
    }

    /// <summary>
    /// C_RD_NA_1 读数据命令 —— TypeID 102。
    /// 信息元素为空（仅信息对象地址），从站收到后应回送该地址对应点的当前值。
    /// </summary>
    public class ReadCommand : InformationObject
    {
        public ReadCommand(int objectAddress) : base(objectAddress) { }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            // 无信息元素
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            return 0;
        }

        public override string ToString() => $"读数据命令 IOA={ObjectAddress}";
    }

    /// <summary>
    /// C_RP_NA_1 复位进程命令 —— TypeID 105。
    /// 信息元素：QRP 复位进程命令限定词（1 字节），0=未用，1=进程总复位，2=复位事件缓冲区。
    /// 联调提示：复位事件缓冲区会清掉未上送的 SOE，谨慎使用。
    /// </summary>
    public class ResetProcessCommand : InformationObject
    {
        public byte Qualifier { get; set; }

        public ResetProcessCommand(int objectAddress) : base(objectAddress) { }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            output.Add(Qualifier);
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            Qualifier = buffer[startIndex];
            return 1;
        }

        public override string ToString() => $"复位进程命令 IOA={ObjectAddress} QRP={Qualifier}";
    }
}
