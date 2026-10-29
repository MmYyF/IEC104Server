using System;

namespace IEC104.Core.CS104
{
    /// <summary>
    /// APCI（应用规约控制信息）—— 104 帧前 6 字节，负责链路与序号管理。
    ///
    /// ┌────────┬──────────┬─────────────────────────────┐
    /// │ 0x68   │ APDU长度 │ 控制域（4 字节）             │
    /// └────────┴──────────┴─────────────────────────────┘
    ///
    /// 控制域有三种帧格式，靠第 1 字节的最低 2 位区分：
    ///
    ///   I 帧（信息传输）—— bit0 = 0
    ///       字节1-2：发送序号 N(S)，实际值 = 该 16 位数 >> 1
    ///       字节3-4：接收序号 N(R)，实际值 = 该 16 位数 >> 1
    ///       承载 ASDU，序号用于滑动窗口与丢失重传
    ///
    ///   S 帧（监视确认）—— bit0 = 1 且 bit1 = 0
    ///       字节1 = 0x01，字节2 = 0x00
    ///       字节3-4：接收序号 N(R)
    ///       不承载 ASDU，仅用于确认已收到的 I 帧
    ///
    ///   U 帧（未编号控制）—— bit0 = 1 且 bit1 = 1
    ///       字节1 为功能码，字节2-4 为 0
    ///       STARTDT 激活 0x07 / 确认 0x0B   —— 启动数据传输
    ///       STOPDT  激活 0x13 / 确认 0x23   —— 停止数据传输
    ///       TESTFR  激活 0x43 / 确认 0x83   —— 链路测试保活
    ///
    /// 关键工程提示：TCP 连上后必须等主站发 STARTDT 激活、从站回确认，
    /// 之后才能传 I 帧数据。若不等 STARTDT 就发数据，对端会直接丢弃——
    /// "socket 连上了但收不到数据"这一经典问题，八成出在这里。
    ///
    /// 复刻说明（源程序集：lib60870.dll 中 lib60870.CS104 的 Server / Connection /
    /// ClientConnection 里内嵌的 APCI 编解码逻辑）：
    /// 原实现把 APCI 的拼装与解析散落在 Connection 类内部（随连接状态一起维护），
    /// 此处抽成独立的静态工具类 Apci，使其可以脱离连接状态被单独测试——
    /// 帧格式是纯函数，本来就不该依赖连接状态。
    /// </summary>
    public static class Apci
    {
        /// <summary>APCI 固定 6 字节（0x68 + 长度 + 4 字节控制域）</summary>
        public const int ApciLength = 6;

        /// <summary>启动字符，恒为 0x68</summary>
        public const byte StartByte = 0x68;

        /// <summary>U 帧功能码</summary>
        public enum UFrameFunction : byte
        {
            /// <summary>STARTDT 激活（启动数据传输）</summary>
            StartDtActivate = 0x07,

            /// <summary>STARTDT 确认</summary>
            StartDtConfirm = 0x0B,

            /// <summary>STOPDT 激活（停止数据传输）</summary>
            StopDtActivate = 0x13,

            /// <summary>STOPDT 确认</summary>
            StopDtConfirm = 0x23,

            /// <summary>TESTFR 激活（链路测试）</summary>
            TestFrActivate = 0x43,

            /// <summary>TESTFR 确认</summary>
            TestFrConfirm = 0x83
        }

        /// <summary>帧类型</summary>
        public enum FrameType
        {
            /// <summary>I 帧：带序号的信息传输帧，承载 ASDU</summary>
            Information,

            /// <summary>S 帧：监视帧，仅用于确认接收序号</summary>
            Supervisory,

            /// <summary>U 帧：未编号控制帧，用于链路启停与保活</summary>
            Unnumbered,

            /// <summary>无法识别的帧（格式错误，应丢弃并考虑断链）</summary>
            Unknown
        }

        /// <summary>判定控制域所属的帧类型</summary>
        public static FrameType GetFrameType(byte[] controlField)
        {
            if (controlField == null || controlField.Length < 4)
            {
                throw new ArgumentException("控制域需 4 字节。", nameof(controlField));
            }

            byte firstByte = controlField[0];
            bool isLowestBitSet = (firstByte & 0x01) != 0;
            bool isSecondLowestBitSet = (firstByte & 0x02) != 0;

            if (!isLowestBitSet)
            {
                // 最低位为 0，且第 3 字节最低位也为 0 时才是合法 I 帧
                // （I 帧的 N(S) 与 N(R) 都左移了 1 位，故两处最低位必为 0）
                if ((controlField[2] & 0x01) == 0)
                {
                    return FrameType.Information;
                }
                return FrameType.Unknown;
            }

            if (!isSecondLowestBitSet)
            {
                // S 帧：字节1 = 0x01，字节2 = 0x00
                return firstByte == 0x01 && controlField[1] == 0x00
                    ? FrameType.Supervisory
                    : FrameType.Unknown;
            }

            return FrameType.Unnumbered;
        }

        /// <summary>从 I 帧控制域取发送序号 N(S)</summary>
        public static int GetSendSequenceNumber(byte[] controlField)
        {
            return (controlField[0] | (controlField[1] << 8)) >> 1;
        }

        /// <summary>从 I/S 帧控制域取接收序号 N(R)</summary>
        public static int GetReceiveSequenceNumber(byte[] controlField)
        {
            return (controlField[2] | (controlField[3] << 8)) >> 1;
        }

        /// <summary>组装 I 帧控制域（4 字节）</summary>
        public static byte[] BuildInformationFrame(int sendSequenceNumber, int receiveSequenceNumber)
        {
            if (sendSequenceNumber < 0 || sendSequenceNumber > 32767)
            {
                throw new ArgumentOutOfRangeException(nameof(sendSequenceNumber));
            }
            if (receiveSequenceNumber < 0 || receiveSequenceNumber > 32767)
            {
                throw new ArgumentOutOfRangeException(nameof(receiveSequenceNumber));
            }

            int sendField = sendSequenceNumber << 1;
            int receiveField = receiveSequenceNumber << 1;

            return new[]
            {
                (byte)(sendField & 0xFF),
                (byte)((sendField >> 8) & 0xFF),
                (byte)(receiveField & 0xFF),
                (byte)((receiveField >> 8) & 0xFF)
            };
        }

        /// <summary>组装 S 帧控制域（4 字节）</summary>
        public static byte[] BuildSupervisoryFrame(int receiveSequenceNumber)
        {
            int receiveField = receiveSequenceNumber << 1;
            return new[]
            {
                (byte)0x01,
                (byte)0x00,
                (byte)(receiveField & 0xFF),
                (byte)((receiveField >> 8) & 0xFF)
            };
        }

        /// <summary>组装 U 帧控制域（4 字节）</summary>
        public static byte[] BuildUnnumberedFrame(UFrameFunction function)
        {
            return new[] { (byte)function, (byte)0x00, (byte)0x00, (byte)0x00 };
        }

        /// <summary>
        /// 校验 APDU 首部：启动字符必须为 0x68，长度字段必须与实际一致。
        /// 这是收帧的第一道防线，长度对不上说明 TCP 粘包/拆包没处理好。
        /// </summary>
        public static bool TryValidateHeader(byte[] buffer, int offset, out int apduLength)
        {
            apduLength = 0;
            if (buffer == null || offset + 2 > buffer.Length)
            {
                return false;
            }
            if (buffer[offset] != StartByte)
            {
                return false;
            }

            // 长度字段表示控制域 + ASDU 的字节数，不含启动字符与长度字节本身
            apduLength = buffer[offset + 1] + 2;
            return apduLength >= ApciLength && offset + apduLength <= buffer.Length;
        }
    }
}
