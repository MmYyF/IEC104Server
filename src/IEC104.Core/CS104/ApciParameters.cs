namespace IEC104.Core.CS104
{
    /// <summary>
    /// IEC 60870-5-104 链路层参数（对应原实现的 lib60870.CS104.APCIParameters）。
    ///
    /// 这六个参数直接决定链路的稳定性与吞吐，是 104 区别于 101 的核心机制。
    /// 现场最常见的问题就是参数不匹配导致链路反复中断，逐条说明：
    ///
    ///   k  —— 发送方在收到确认前最多可连续发出的 I 帧数（默认 12）
    ///   w  —— 接收方在发出确认前最多可接收的 I 帧数（默认 8）
    ///         注意：w 必须小于 k，否则发送方永远不会收到确认
    ///   t0 —— TCP 连接建立超时（默认 30 秒）
    ///   t1 —— 发出 I/U 帧后等待确认的超时（默认 15 秒），超时即认为链路断
    ///   t2 —— 收到 I 帧后等待下一帧以发送 S 确认的超时（默认 10 秒），小于 t1
    ///   t3 —— 链路空闲多久后主动发测试帧保活（默认 20 秒）
    ///
    /// 工程建议：
    ///   - 无线/卫星链路这类高时延场景，t1/t3 要调大，否则会频繁误判断链
    ///   - w &lt; k 是硬约束，配错会出现"数据发着发着就停了"的诡异现象
    ///   - 主站与从站的 t3 不要设成相同值，否则可能同时对发测试帧
    /// </summary>
    public class ApciParameters
    {
        /// <summary>k：未确认 I 帧的最大发送窗口</summary>
        public int K { get; set; } = 12;

        /// <summary>w：未确认 I 帧的最大接收窗口，必须小于 k</summary>
        public int W { get; set; } = 8;

        /// <summary>t0：TCP 建链超时（毫秒）</summary>
        public int T0Milliseconds { get; set; } = 30_000;

        /// <summary>t1：等待确认超时（毫秒）</summary>
        public int T1Milliseconds { get; set; } = 15_000;

        /// <summary>t2：S 帧确认等待超时（毫秒），应小于 t1</summary>
        public int T2Milliseconds { get; set; } = 10_000;

        /// <summary>t3：空闲保活间隔（毫秒）</summary>
        public int T3Milliseconds { get; set; } = 20_000;

        /// <summary>单个 APDU 的最大长度（含 APCI 6 字节），104 规定为 255</summary>
        public int MaxApduLength { get; set; } = 255;

        /// <summary>因此 ASDU 部分的最大长度为 249 字节（255 - 6）</summary>
        public int MaxAsduLength => MaxApduLength - Apci.ApciLength;

        /// <summary>校验参数合法性，返回错误说明；合法则返回 null</summary>
        public string Validate()
        {
            if (W >= K)
            {
                return $"参数非法：接收窗口 w({W}) 必须小于发送窗口 k({K})。";
            }
            if (T2Milliseconds >= T1Milliseconds)
            {
                return $"参数非法：t2({T2Milliseconds}ms) 必须小于 t1({T1Milliseconds}ms)。";
            }
            if (K < 1 || K > 32767 || W < 1 || W > 32767)
            {
                return "参数非法：k 与 w 需在 1..32767 之间。";
            }
            return null;
        }
    }
}
