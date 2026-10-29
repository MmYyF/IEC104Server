using System;

namespace IEC104.Core.LinkLayer
{
    /// <summary>
    /// IEC 60870-5-101 链路层 —— FT1.2 帧格式。
    ///
    /// 复刻说明（源程序集：lib60870.dll 中 lib60870.linklayer 命名空间，共 22 个类型）：
    /// 101 走串行链路（RS-232/485），其链路层是自成一体的 FT1.2 帧；
    /// 而 104 是把 101 的应用层（ASDU）直接搬到 TCP 上，用 APCI 取代了 FT1.2。
    /// 也就是说：101 = FT1.2 + ASDU，104 = APCI + ASDU，两者的 ASDU 完全相同。
    ///
    /// 原实现的链路层包含 PrimaryLinkLayer / SecondaryLinkLayer / LinkLayerState 等，
    /// 支持平衡式与非平衡式传输、请求/响应/确认重传等完整状态机。
    /// 此处复刻最核心的【帧格式编解码】与【校验和】，
    /// 完整状态机（含重传与超时管理）未复刻——这部分在 104 场景下用不到，
    /// 且实现体量较大，如需支持 101 串口通信可在此基础上扩展。
    ///
    /// FT1.2 三种帧格式：
    ///   固定帧长（5 字节）  ：10 功能码 地址 校验和 16   —— 用于链路查询/复位/确认
    ///   可变帧长（不定长）  ：68 L L 68 功能码 地址 ASDU 校验和 16 —— 用于传数据
    ///   单字符（1 字节）    ：E5                          —— 仅用于确认
    /// </summary>
    public static class Ft12
    {
        /// <summary>固定帧长帧的启动字符</summary>
        public const byte FixedFrameStart = 0x10;

        /// <summary>可变帧长帧的启动字符</summary>
        public const byte VariableFrameStart = 0x68;

        /// <summary>帧结束字符</summary>
        public const byte FrameEnd = 0x16;

        /// <summary>单字符确认（无从站数据时用它应答）</summary>
        public const byte SingleAcknowledge = 0xE5;

        /// <summary>固定帧长帧的字节数</summary>
        public const int FixedFrameLength = 5;

        /// <summary>
        /// 计算校验和：从控制域到用户数据最后一个字节的算术和，取低 8 位。
        /// 101 用的是最简单的累加和（不是 CRC），这也是 FT1.2 抗干扰能力较弱的原因之一——
        /// 现场 RS-485 干扰大时，偶发的校验和错误帧要靠链路层重传来兜底。
        /// </summary>
        public static byte ComputeChecksum(byte[] frame, int startIndex, int length)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            int sum = 0;
            for (int index = startIndex; index < startIndex + length; index++)
            {
                sum += frame[index];
            }
            return (byte)(sum & 0xFF);
        }

        /// <summary>组装固定帧长帧（5 字节）</summary>
        public static byte[] BuildFixedFrame(byte functionCode, int linkAddress)
        {
            byte[] frame = new byte[FixedFrameLength];
            frame[0] = FixedFrameStart;
            frame[1] = functionCode;
            frame[2] = (byte)(linkAddress & 0xFF);
            frame[3] = ComputeChecksum(frame, 1, 2);
            frame[4] = FrameEnd;
            return frame;
        }

        /// <summary>组装可变帧长帧，承载 ASDU</summary>
        public static byte[] BuildVariableFrame(byte functionCode, int linkAddress, byte[] asdu)
        {
            byte[] payload = asdu ?? Array.Empty<byte>();

            // 可变帧长帧结构：68 L L 68 C A <ASDU> CS 16
            // 其中的 L 只统计控制域 + 地址域 + ASDU 的字节数（不含 4 字节头尾）
            byte[] frame = new byte[6 + payload.Length];
            int lengthField = 2 + payload.Length;

            frame[0] = VariableFrameStart;
            frame[1] = (byte)lengthField;
            frame[2] = (byte)lengthField;
            frame[3] = VariableFrameStart;
            frame[4] = functionCode;
            frame[5] = (byte)(linkAddress & 0xFF);
            Array.Copy(payload, 0, frame, 6, payload.Length);

            frame[frame.Length - 2] = ComputeChecksum(frame, 4, 2 + payload.Length);
            frame[frame.Length - 1] = FrameEnd;
            return frame;
        }

        /// <summary>
        /// 校验一个 FT1.2 帧是否完整且校验和正确。
        /// 串口通信是字节流，必须靠启动字符 + 长度 + 结束字符三重定位帧边界，
        /// 只靠长度字段定位在干扰环境下极易错位。
        /// </summary>
        public static bool TryValidate(byte[] frame, int offset, int length, out string error)
        {
            error = null;

            if (frame == null || length < 1)
            {
                error = "帧为空";
                return false;
            }

            byte start = frame[offset];

            // 单字符确认帧，无需校验和
            if (start == SingleAcknowledge && length == 1)
            {
                return true;
            }

            if (start == FixedFrameStart)
            {
                if (length < FixedFrameLength)
                {
                    error = $"固定帧长帧不足 {FixedFrameLength} 字节";
                    return false;
                }
                if (frame[offset + 4] != FrameEnd)
                {
                    error = "固定帧长帧结束字符错误";
                    return false;
                }

                byte expected = ComputeChecksum(frame, offset + 1, 2);
                if (frame[offset + 3] != expected)
                {
                    error = $"固定帧长帧校验和错误：期望 0x{expected:X2}，实际 0x{frame[offset + 3]:X2}";
                    return false;
                }
                return true;
            }

            if (start == VariableFrameStart)
            {
                if (length < 6)
                {
                    error = "可变帧长帧过短";
                    return false;
                }
                if (frame[offset + 3] != VariableFrameStart)
                {
                    error = "可变帧长帧第二个启动字符错误";
                    return false;
                }

                int lengthField = frame[offset + 1];
                if (frame[offset + 2] != lengthField)
                {
                    error = "两个长度字段不一致";
                    return false;
                }

                int totalLength = lengthField + 6;
                if (length < totalLength)
                {
                    error = $"可变帧长帧未收全：需要 {totalLength} 字节，实际 {length}";
                    return false;
                }
                if (frame[offset + totalLength - 1] != FrameEnd)
                {
                    error = "可变帧长帧结束字符错误";
                    return false;
                }

                byte expected = ComputeChecksum(frame, offset + 4, lengthField);
                byte actual = frame[offset + totalLength - 2];
                if (actual != expected)
                {
                    error = $"可变帧长帧校验和错误：期望 0x{expected:X2}，实际 0x{actual:X2}";
                    return false;
                }
                return true;
            }

            error = $"未知启动字符 0x{start:X2}";
            return false;
        }

        /// <summary>
        /// 从可变帧长帧中取出 ASDU 部分。
        /// 这是 101 与 104 复用的关键：本方法返回的字节数组可直接交给
        /// <see cref="CS101.Asdu.Decode(byte[], int, CS101.ApplicationLayerParameters, out int)"/> 解析
        /// （命名空间从当前上下文按 IEC104.Core.CS101 解析）。
        /// </summary>
        public static bool TryGetAsdu(byte[] frame, int offset, out byte[] asdu)
        {
            asdu = null;

            if (frame == null || frame[offset] != VariableFrameStart)
            {
                return false;
            }

            int lengthField = frame[offset + 1];
            int payloadLength = lengthField - 2;
            if (payloadLength <= 0)
            {
                return false;
            }

            asdu = new byte[payloadLength];
            Array.Copy(frame, offset + 6, asdu, 0, payloadLength);
            return true;
        }
    }

    /// <summary>
    /// FT1.2 功能码（主站 → 从站方向，即"启动方向"）。
    /// 功能码决定了本帧是"要数据"还是"发数据"，以及是否要求确认。
    /// </summary>
    public enum PrimaryFunctionCode : byte
    {
        /// <summary>0：复位远方链路</summary>
        ResetRemoteLink = 0x00,

        /// <summary>3：发送/确认 用户数据（要求从站回确认）</summary>
        SendConfirmedUserData = 0x03,

        /// <summary>4：发送/无回答 用户数据（广播，不要求确认）</summary>
        SendNoReplyUserData = 0x04,

        /// <summary>9：请求链路状态</summary>
        RequestLinkStatus = 0x09,

        /// <summary>10：请求 1 级用户数据（突发/高优先级）</summary>
        RequestClass1Data = 0x0A,

        /// <summary>11：请求 2 级用户数据（循环/低优先级）</summary>
        RequestClass2Data = 0x0B
    }

    /// <summary>
    /// FT1.2 功能码（从站 → 主站方向，即"从动方向"）。
    /// ACD 位（bit5）是 101 的关键机制：从站有 1 级数据时置位，
    /// 主站检测到后必须立即发"请求 1 级数据"把它取走——相当于 104 里的自发上送。
    /// </summary>
    public enum SecondaryFunctionCode : byte
    {
        /// <summary>0：确认——肯定确认</summary>
        Acknowledge = 0x00,

        /// <summary>1：确认——否定（链路忙/未接收报文）</summary>
        NegativeAcknowledge = 0x01,

        /// <summary>8：以用户数据回答请求</summary>
        RespondWithUserData = 0x08,

        /// <summary>9：无用户数据（无所请求的数据）</summary>
        NoUserData = 0x09,

        /// <summary>11：以链路状态回答请求</summary>
        RespondWithLinkStatus = 0x0B
    }
}
