using System.Collections.Generic;
using IEC104.Core.Time;

namespace IEC104.Core.CS101
{
    /// <summary>
    /// M_SP_NA_1 单点信息（遥信）—— TypeID 1。
    /// 信息元素：SIQ 1 字节。总长 1 字节。
    ///
    /// 复刻说明（源程序集 lib60870.CS101.SinglePointInformation）：
    /// 原实现把"值 + 品质"打包在单个字节的 SIQ 中，此处保持一致。
    /// 三个变体（无时标 / CP24 / CP56）按规约分别对应 TypeID 1 / 2 / 30。
    /// </summary>
    public class SinglePointInformation : InformationObject
    {
        /// <summary>单点状态与品质（SIQ）</summary>
        public SinglePointQuality Quality { get; set; }

        public SinglePointInformation(int objectAddress) : base(objectAddress) { }

        /// <summary>便捷构造：直接给状态值，品质默认有效</summary>
        public SinglePointInformation(int objectAddress, bool value) : base(objectAddress)
        {
            Quality = new SinglePointQuality { Value = value };
        }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            output.Add(Quality.Encode());
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            Quality = SinglePointQuality.Decode(buffer[startIndex]);
            return 1;
        }

        public override string ToString() => $"单点信息 IOA={ObjectAddress} 状态={Quality}";
    }

    /// <summary>
    /// M_SP_TA_1 带 CP24Time2a 短时标的单点信息 —— TypeID 2。
    /// 信息元素：SIQ(1) + CP24Time2a(3)，总长 4 字节。
    /// </summary>
    public class SinglePointWithCp24Time2a : InformationObject
    {
        public SinglePointQuality Quality { get; set; }

        /// <summary>三分字节短时标（只有毫秒与分钟，无日期）</summary>
        public Cp24Time2a Timestamp { get; set; }

        public SinglePointWithCp24Time2a(int objectAddress) : base(objectAddress) { }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            output.Add(Quality.Encode());
            // 时标需要连续 3 字节，先写入临时缓冲再整体追加，
            // 避免在 List 上逐字节 Add 时把偏移量算错
            byte[] timeBuffer = new byte[Cp24Time2a.EncodedLength];
            Timestamp.Encode(timeBuffer, 0);
            output.AddRange(timeBuffer);
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            Quality = SinglePointQuality.Decode(buffer[startIndex]);
            Timestamp = Cp24Time2a.Decode(buffer, startIndex + 1);
            return 4;
        }

        public override string ToString() => $"单点(CP24) IOA={ObjectAddress} 状态={Quality} {Timestamp}";
    }

    /// <summary>
    /// M_SP_TB_1 带 CP56Time2a 长时标的单点信息 —— TypeID 30。
    /// 信息元素：SIQ(1) + CP56Time2a(7)，总长 8 字节。SOE 事件记录的标准载体。
    /// </summary>
    public class SinglePointWithCp56Time2a : InformationObject
    {
        public SinglePointQuality Quality { get; set; }

        /// <summary>七字节完整时标（含年月日时分秒毫秒）</summary>
        public Cp56Time2a Timestamp { get; set; }

        public SinglePointWithCp56Time2a(int objectAddress) : base(objectAddress) { }

        public override void Encode(List<byte> output, ApplicationLayerParameters parameters)
        {
            output.Add(Quality.Encode());
            byte[] timeBuffer = new byte[Cp56Time2a.EncodedLength];
            Timestamp.Encode(timeBuffer, 0);
            output.AddRange(timeBuffer);
        }

        public override int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters)
        {
            Quality = SinglePointQuality.Decode(buffer[startIndex]);
            Timestamp = Cp56Time2a.Decode(buffer, startIndex + 1);
            return 8;
        }

        public override string ToString() => $"单点(CP56) IOA={ObjectAddress} 状态={Quality} {Timestamp}";
    }
}
