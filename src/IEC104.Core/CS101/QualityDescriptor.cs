using System;

namespace IEC104.Core.CS101
{
    /// <summary>
    /// 品质描述词（QDS，Quality Descriptor）—— 1 字节，描述某个测量值/状态是否可信。
    ///
    /// 位定义（IEC 60870-5-101 7.2.6.3）：
    ///   bit0 OV  溢出 —— 测量值超出量程
    ///   bit1 BL  封锁 —— 该值被人工封锁，不再刷新
    ///   bit2 SB  取代 —— 该值由人工置入，非实测
    ///   bit3 NT  非当前 —— 该值不是最新采集的（链路中断后常见）
    ///   bit4 IV  无效 —— 该值无效，绝不可用于控制判断
    ///   bit5-7 备用
    ///
    /// 复刻说明（源程序集：lib60870.dll 中 lib60870.CS101.QualityDescriptor，
    /// 以及遥信专用的 SIQ / DIQ）：
    /// 原实现把品质封装为带位域读写属性的类，此处保留同样的属性语义（OV/BL/SB/NT/IV），
    /// 并额外给出遥信专用的 SinglePointQuality（SIQ）与双点品质，便于直接构造遥信帧。
    ///
    /// 工程提示：IV 位是最容易被忽略、后果又最严重的一位。
    /// 自动化系统若不做 IV 判断，装置检修期间上送的无效值会被当成真实值参与控制逻辑。
    ///
    /// 工程提示：主站侧在把遥测送进控制逻辑前必须先查 IV 与 NT。
    /// 现场出过的事故：未判 IV 位，把无效遥测当成真实值参与计算，导致误判。
    /// </summary>
    public struct QualityDescriptor
    {
        public bool Overflow { get; set; }
        public bool Blocked { get; set; }
        public bool Substituted { get; set; }
        public bool NotTopical { get; set; }

        /// <summary>IV：无效。为 true 时该值不应被采信</summary>
        public bool Invalid { get; set; }

        public static QualityDescriptor Decode(byte value)
        {
            return new QualityDescriptor
            {
                Overflow = (value & 0x01) != 0,
                Blocked = (value & 0x02) != 0,
                Substituted = (value & 0x04) != 0,
                NotTopical = (value & 0x08) != 0,
                Invalid = (value & 0x10) != 0
            };
        }

        public byte Encode()
        {
            byte result = 0;
            if (Overflow) result |= 0x01;
            if (Blocked) result |= 0x02;
            if (Substituted) result |= 0x04;
            if (NotTopical) result |= 0x08;
            if (Invalid) result |= 0x10;
            return result;
        }

        /// <summary>该值是否可以安全使用（未无效、未封锁）</summary>
        public bool IsUsable => !Invalid && !Blocked;

        public override string ToString()
        {
            return Invalid ? "QDS(无效)" : "QDS(有效)";
        }
    }

    /// <summary>
    /// 单点信息品质（SIQ）—— 1 字节，状态位与品质位合并在同一字节里。
    ///
    /// 位定义：
    ///   bit0 SPI  单点状态：0=分（开），1=合（闭）
    ///   bit1-3    备用
    ///   bit4 BL   封锁
    ///   bit5 SB   取代
    ///   bit6 NT   非当前
    ///   bit7 IV   无效
    /// </summary>
    public struct SinglePointQuality
    {
        /// <summary>true 表示合位（1），false 表示分位（0）</summary>
        public bool Value { get; set; }

        public bool Blocked { get; set; }
        public bool Substituted { get; set; }
        public bool NotTopical { get; set; }
        public bool Invalid { get; set; }

        public static SinglePointQuality Decode(byte value)
        {
            return new SinglePointQuality
            {
                Value = (value & 0x01) != 0,
                Blocked = (value & 0x10) != 0,
                Substituted = (value & 0x20) != 0,
                NotTopical = (value & 0x40) != 0,
                Invalid = (value & 0x80) != 0
            };
        }

        public byte Encode()
        {
            byte result = 0;
            if (Value) result |= 0x01;
            if (Blocked) result |= 0x10;
            if (Substituted) result |= 0x20;
            if (NotTopical) result |= 0x40;
            if (Invalid) result |= 0x80;
            return result;
        }

        public override string ToString() => Value ? "合(1)" : "分(0)";
    }

    /// <summary>双点状态值（DPI）—— 2 位编码，比单点多一个中间态</summary>
    public enum DoublePointValue : byte
    {
        /// <summary>0：不确定或中间状态（例如开关正在动作中）</summary>
        IndeterminateOrIntermediate = 0,

        /// <summary>1：确定分位（OFF）</summary>
        Off = 1,

        /// <summary>2：确定合位（ON）</summary>
        On = 2,

        /// <summary>3：不确定（故障态）</summary>
        Indeterminate = 3
    }

    /// <summary>
    /// 双点信息品质（DIQ）—— 1 字节。
    ///   bit0-1 DPI  双点状态
    ///   bit2-3      备用
    ///   bit4   BL / bit5 SB / bit6 NT / bit7 IV
    /// </summary>
    public struct DoublePointQuality
    {
        public DoublePointValue Value { get; set; }

        public bool Blocked { get; set; }
        public bool Substituted { get; set; }
        public bool NotTopical { get; set; }
        public bool Invalid { get; set; }

        public static DoublePointQuality Decode(byte value)
        {
            return new DoublePointQuality
            {
                Value = (DoublePointValue)(value & 0x03),
                Blocked = (value & 0x10) != 0,
                Substituted = (value & 0x20) != 0,
                NotTopical = (value & 0x40) != 0,
                Invalid = (value & 0x80) != 0
            };
        }

        public byte Encode()
        {
            byte result = (byte)((byte)Value & 0x03);
            if (Blocked) result |= 0x10;
            if (Substituted) result |= 0x20;
            if (NotTopical) result |= 0x40;
            if (Invalid) result |= 0x80;
            return result;
        }

        /// <summary>是否为确定的分/合态（排除中间态与不确定态）</summary>
        public bool IsDefinite => Value == DoublePointValue.Off || Value == DoublePointValue.On;

        public override string ToString() => Value.ToString();
    }
}
