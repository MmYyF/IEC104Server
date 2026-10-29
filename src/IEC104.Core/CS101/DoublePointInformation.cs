using System.Collections.Generic;
using IEC104.Core.Time;

namespace IEC104.Core.CS101
{
    /// <summary>
    /// M_DP_NA_1 双点信息（遥信）—— TypeID 3。
    /// 信息元素：DIQ 1 字节，总长 1 字节。
    ///
    /// 复刻说明（源程序集：lib60870.dll 中 lib60870.CS101 下的
    /// DoublePointInformation / DoublePointWithCP24Time2a / DoublePointWithCP56Time2a）：
    /// 原实现同样按"不带时标 / CP24 / CP56"拆成三个类，此处沿用该拆分。
    ///
    /// 与单点的区别：双点用 2 位表示状态，能表达"中间态"（开关正在动作）
    /// 与"不确定态"（辅助触点故障）。断路器、隔离开关必须用双点，
    /// 用单点会在开关动作过程中误报分/合。这是选型时的硬规则。
    /// </summary>
    public class DoublePointInformation : InformationObject
    {
        /// <summary>双点状态与品质（DIQ）</summary>
        public DoublePointQuality Quality { get; set; }

        public DoublePointInformation(int objectAddress) : base(objectAddress) { }

        public DoublePointInformation(int objectAddress, DoublePointValue value) : base(objectAddress)
        {
            Quality = new DoublePointQuality { Value = value };
        }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            output.Add(Quality.Encode());
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            Quality = DoublePointQuality.Decode(buffer[startIndex]);
            return 1;
        }

        public override string ToString() => $"双点信息 IOA={ObjectAddress} 状态={Quality}";
    }

    /// <summary>
    /// M_DP_TA_1 带 CP24Time2a 短时标的双点信息 —— TypeID 4。总长 4 字节。
    /// </summary>
    public class DoublePointWithCp24Time2a : InformationObject
    {
        public DoublePointQuality Quality { get; set; }
        public Cp24Time2a Timestamp { get; set; }

        public DoublePointWithCp24Time2a(int objectAddress) : base(objectAddress) { }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            output.Add(Quality.Encode());
            byte[] timeBuffer = new byte[Cp24Time2a.EncodedLength];
            Timestamp.Encode(timeBuffer, 0);
            output.AddRange(timeBuffer);
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            Quality = DoublePointQuality.Decode(buffer[startIndex]);
            Timestamp = Cp24Time2a.Decode(buffer, startIndex + 1);
            return 4;
        }

        public override string ToString() => $"双点(CP24) IOA={ObjectAddress} 状态={Quality} {Timestamp}";
    }

    /// <summary>
    /// M_DP_TB_1 带 CP56Time2a 长时标的双点信息 —— TypeID 31。总长 8 字节。
    /// 开关变位 SOE 的标准载体，事故分析主要靠它。
    /// </summary>
    public class DoublePointWithCp56Time2a : InformationObject
    {
        public DoublePointQuality Quality { get; set; }
        public Cp56Time2a Timestamp { get; set; }

        public DoublePointWithCp56Time2a(int objectAddress) : base(objectAddress) { }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            output.Add(Quality.Encode());
            byte[] timeBuffer = new byte[Cp56Time2a.EncodedLength];
            Timestamp.Encode(timeBuffer, 0);
            output.AddRange(timeBuffer);
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            Quality = DoublePointQuality.Decode(buffer[startIndex]);
            Timestamp = Cp56Time2a.Decode(buffer, startIndex + 1);
            return 8;
        }

        public override string ToString() => $"双点(CP56) IOA={ObjectAddress} 状态={Quality} {Timestamp}";
    }
}
