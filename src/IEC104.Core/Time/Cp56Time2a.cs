using System;

namespace IEC104.Core.Time
{
    /// <summary>
    /// CP56Time2a —— IEC 60870-5-101/104 的七字节二进制时标。
    ///
    /// 复刻说明（源程序集：lib60870.dll 中的 lib60870.CP56Time2a）：
    /// 原实现为可变长度时标（CP16/CP24/CP32/CP56 共用一套读写逻辑），
    /// 此处按 104 规约实际使用的 7 字节固定格式重新实现，字段语义保持一致。
    ///
    /// 报文中的字节布局（低字节在前，小端序）：
    ///   byte0-1 : 毫秒  0..59999
    ///   byte2   : 分钟  0..59，bit7 = IV（无效标志）
    ///   byte3   : 小时  0..23，bit7 = SU（夏令时标志）
    ///   byte4   : 日    1..31，bit5-7 = 星期（1=周一 .. 7=周日，0=未用）
    ///   byte5   : 月    1..12
    ///   byte6   : 年    0..99（相对 2000 年）
    ///
    /// 注意：104 规约里 S/U 位与 IV 位在部分厂家实现中会互换，
    /// 联调时若发现时间差 1 小时，优先检查 byte3 的 bit7 语义。
    /// </summary>
    public struct Cp56Time2a : IEquatable<Cp56Time2a>
    {
        /// <summary>毫秒部分，取值范围 0..59999</summary>
        public ushort Milliseconds { get; set; }

        /// <summary>分钟，0..59</summary>
        public byte Minutes { get; set; }

        /// <summary>小时，0..23</summary>
        public byte Hours { get; set; }

        /// <summary>日，1..31</summary>
        public byte DayOfMonth { get; set; }

        /// <summary>星期，1=周一 .. 7=周日；0 表示不使用</summary>
        public byte DayOfWeek { get; set; }

        /// <summary>月，1..12</summary>
        public byte Month { get; set; }

        /// <summary>年，0..99，相对 2000 年</summary>
        public byte Year { get; set; }

        /// <summary>IV：时标无效。对端时钟未同步时会置位，收到时应丢弃该时标而非当作真实时间</summary>
        public bool IsInvalid { get; set; }

        /// <summary>SU：夏令时标志。置位表示本地时间处于夏令时区间</summary>
        public bool IsSummerTime { get; set; }

        /// <summary>报文中的字节数，固定 7</summary>
        public const int EncodedLength = 7;

        /// <summary>从 .NET DateTime 构造时标（年月日时分秒毫秒 + 星期）</summary>
        public static Cp56Time2a FromDateTime(DateTime timestamp)
        {
            return new Cp56Time2a
            {
                Milliseconds = (ushort)((timestamp.Second * 1000) + timestamp.Millisecond),
                Minutes = (byte)timestamp.Minute,
                Hours = (byte)timestamp.Hour,
                DayOfMonth = (byte)timestamp.Day,
                // .NET 的 DayOfWeek 以周日=0 开头，规约要求周一=1，故做一次映射
                DayOfWeek = MapToIecDayOfWeek(timestamp.DayOfWeek),
                Month = (byte)timestamp.Month,
                Year = (byte)(timestamp.Year % 100),
                IsInvalid = false,
                IsSummerTime = false
            };
        }

        /// <summary>
        /// 从报文字节流解析时标。
        /// </summary>
        /// <param name="buffer">报文缓冲区</param>
        /// <param name="startIndex">CP56Time2a 起始下标</param>
        public static Cp56Time2a Decode(byte[] buffer, int startIndex)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            if (startIndex + EncodedLength > buffer.Length)
            {
                throw new ArgumentException(
                    $"CP56Time2a 需要 {EncodedLength} 字节，但缓冲区从 {startIndex} 起不足。");
            }

            return new Cp56Time2a
            {
                Milliseconds = (ushort)(buffer[startIndex] | (buffer[startIndex + 1] << 8)),
                // 分钟占低 7 位，bit7 是 IV
                Minutes = (byte)(buffer[startIndex + 2] & 0x3F),
                IsInvalid = (buffer[startIndex + 2] & 0x80) != 0,
                // 小时占低 5 位，bit7 是 SU
                Hours = (byte)(buffer[startIndex + 3] & 0x1F),
                IsSummerTime = (buffer[startIndex + 3] & 0x80) != 0,
                // 日占低 5 位，bit5-7 是星期
                DayOfMonth = (byte)(buffer[startIndex + 4] & 0x1F),
                DayOfWeek = (byte)((buffer[startIndex + 4] >> 5) & 0x07),
                Month = (byte)(buffer[startIndex + 5] & 0x0F),
                Year = (byte)(buffer[startIndex + 6] & 0x7F)
            };
        }

        /// <summary>编码为独立的 7 字节数组（便于直接拼接到输出流中）</summary>
        public byte[] Encode()
        {
            byte[] encoded = new byte[EncodedLength];
            Encode(encoded, 0);
            return encoded;
        }

        /// <summary>编码为 7 字节写入缓冲区</summary>
        public void Encode(byte[] buffer, int startIndex)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            if (startIndex + EncodedLength > buffer.Length)
            {
                throw new ArgumentException("缓冲区空间不足以写入 CP56Time2a。");
            }

            buffer[startIndex] = (byte)(Milliseconds & 0xFF);
            buffer[startIndex + 1] = (byte)((Milliseconds >> 8) & 0xFF);
            buffer[startIndex + 2] = (byte)((Minutes & 0x3F) | (IsInvalid ? 0x80 : 0x00));
            buffer[startIndex + 3] = (byte)((Hours & 0x1F) | (IsSummerTime ? 0x80 : 0x00));
            buffer[startIndex + 4] = (byte)((DayOfMonth & 0x1F) | ((DayOfWeek & 0x07) << 5));
            buffer[startIndex + 5] = (byte)(Month & 0x0F);
            buffer[startIndex + 6] = (byte)(Year & 0x7F);
        }

        /// <summary>转换为 .NET DateTime；时标无效（IV=1）时返回 null</summary>
        public DateTime? ToDateTime()
        {
            if (IsInvalid)
            {
                return null;
            }

            int second = Milliseconds / 1000;
            int millisecond = Milliseconds % 1000;
            int fullYear = 2000 + Year;

            try
            {
                return new DateTime(fullYear, Month, DayOfMonth, Hours, Minutes, second, millisecond);
            }
            catch (ArgumentOutOfRangeException)
            {
                // 对端下发的时标字段越界时不宜抛异常，交由调用方按无效时标处理
                return null;
            }
        }

        /// <summary>
        /// 把 .NET 的星期枚举映射为规约的星期编码。
        /// 注意：本结构体内有一个同名的 DayOfWeek 属性，
        /// 会遮蔽 System.DayOfWeek 类型名，故此处必须使用完全限定名。
        /// </summary>
        private static byte MapToIecDayOfWeek(System.DayOfWeek value)
        {
            // .NET：周日=0 .. 周六=6 ；规约：周一=1 .. 周日=7
            return value == System.DayOfWeek.Sunday ? (byte)7 : (byte)value;
        }

        public bool Equals(Cp56Time2a other)
        {
            return Milliseconds == other.Milliseconds
                && Minutes == other.Minutes
                && Hours == other.Hours
                && DayOfMonth == other.DayOfMonth
                && Month == other.Month
                && Year == other.Year
                && IsInvalid == other.IsInvalid;
        }

        public override bool Equals(object obj) => obj is Cp56Time2a other && Equals(other);

        public override int GetHashCode()
        {
            return HashCode.Combine(Milliseconds, Minutes, Hours, DayOfMonth, Month, Year);
        }

        public override string ToString()
        {
            DateTime? decodedTime = ToDateTime();
            return decodedTime.HasValue
                ? decodedTime.Value.ToString("yyyy-MM-dd HH:mm:ss.fff")
                : "CP56Time2a(无效时标 IV=1)";
        }
    }
}
