namespace IEC104.Core.Time
{
    /// <summary>
    /// CP24Time2a —— IEC 60870-5-101/104 的三字节短时标。
    ///
    /// 复刻说明（源程序集：lib60870.dll 中的 lib60870.CP24Time2a）：
    /// 用于带时标的单点/双点/步位置等短报文，只保留毫秒与分钟，不含日期。
    /// 现场联调时的常见坑：CP24 没有日期字段，跨零点的数据需要主站侧自行补日期，
    /// 否则会出现"时间倒流"的假象。
    ///
    /// 报文布局（小端序）：
    ///   byte0-1 : 毫秒 0..59999
    ///   byte2   : 分钟 0..59，bit7 = IV（无效标志）
    /// </summary>
    public struct Cp24Time2a
    {
        /// <summary>毫秒，0..59999</summary>
        public ushort Milliseconds { get; set; }

        /// <summary>分钟，0..59</summary>
        public byte Minutes { get; set; }

        /// <summary>IV：时标无效</summary>
        public bool IsInvalid { get; set; }

        public const int EncodedLength = 3;

        public static Cp24Time2a Decode(byte[] buffer, int startIndex)
        {
            if (buffer == null)
            {
                throw new System.ArgumentNullException(nameof(buffer));
            }
            if (startIndex + EncodedLength > buffer.Length)
            {
                throw new System.ArgumentException(
                    $"CP24Time2a 需要 {EncodedLength} 字节，但缓冲区不足。");
            }

            return new Cp24Time2a
            {
                Milliseconds = (ushort)(buffer[startIndex] | (buffer[startIndex + 1] << 8)),
                Minutes = (byte)(buffer[startIndex + 2] & 0x3F),
                IsInvalid = (buffer[startIndex + 2] & 0x80) != 0
            };
        }

        public void Encode(byte[] buffer, int startIndex)
        {
            if (buffer == null)
            {
                throw new System.ArgumentNullException(nameof(buffer));
            }
            if (startIndex + EncodedLength > buffer.Length)
            {
                throw new System.ArgumentException("缓冲区空间不足以写入 CP24Time2a。");
            }

            buffer[startIndex] = (byte)(Milliseconds & 0xFF);
            buffer[startIndex + 1] = (byte)((Milliseconds >> 8) & 0xFF);
            buffer[startIndex + 2] = (byte)((Minutes & 0x3F) | (IsInvalid ? 0x80 : 0x00));
        }

        /// <summary>
        /// 换算为"自当日零点起的毫秒数"，便于主站侧与日期拼接。
        /// </summary>
        public long ToMillisecondsOfDay()
        {
            return (long)Minutes * 60_000 + Milliseconds;
        }

        public override string ToString()
        {
            return IsInvalid
                ? "CP24Time2a(无效 IV=1)"
                : $"{Minutes}分 {Milliseconds}毫秒";
        }
    }

    /// <summary>
    /// CP16Time2a —— 两字节毫秒时长，用于继电保护事件等的相对时间。
    /// 复刻自 lib60870.dll 的 lib60870.CP16Time2a。
    /// </summary>
    public struct Cp16Time2a
    {
        /// <summary>毫秒时长，0..65535</summary>
        public ushort Milliseconds { get; set; }

        public const int EncodedLength = 2;

        public static Cp16Time2a Decode(byte[] buffer, int startIndex)
        {
            if (buffer == null)
            {
                throw new System.ArgumentNullException(nameof(buffer));
            }
            if (startIndex + EncodedLength > buffer.Length)
            {
                throw new System.ArgumentException("缓冲区空间不足以解析 CP16Time2a。");
            }

            return new Cp16Time2a
            {
                Milliseconds = (ushort)(buffer[startIndex] | (buffer[startIndex + 1] << 8))
            };
        }

        public void Encode(byte[] buffer, int startIndex)
        {
            buffer[startIndex] = (byte)(Milliseconds & 0xFF);
            buffer[startIndex + 1] = (byte)((Milliseconds >> 8) & 0xFF);
        }

        public override string ToString() => $"{Milliseconds} ms";
    }
}
