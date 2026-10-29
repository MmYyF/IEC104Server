namespace IEC104.Core.CS101
{
    /// <summary>
    /// 信息元素格式 —— 一个 ASDU 类型在报文中"长什么样"，本质上是两件事之一：
    /// 信息元素怎么编码，以及后面跟不跟时标。
    ///
    /// 为什么要把"格式"从 TypeID 里拆出来：
    /// 规约里 TypeID 只是报文第一个字节的取值，真正决定字节布局的是"这属于哪一族"。
    /// 比如 M_ME_NC_1 / M_ME_TC_1 / M_ME_TF_1 三个 TypeID 的元素都是 R32 + QDS，
    /// 区别只在时标（无 / CP24 / CP56）。把它们的历史原因照抄成一个类型一个类，
    /// 结果是 67 个枚举值要写 67 个类，绝大多数只是"换个时标"。
    ///
    /// 拆出格式之后：
    ///   · 标准类型仍走各自的强类型类（字节序、换算都写死，不容易错）；
    ///   · 未预置的类型（自定义 / 未来扩展）由 <see cref="GenericInformationObject"/>
    ///     按"格式 × 时标"统一编解码，一个类覆盖 8 × 3 种组合。
    /// </summary>
    public enum ElementFormat : byte
    {
        /// <summary>单点 SIQ：1 字节（状态位 + 品质位合并在一字节里）</summary>
        SinglePoint = 0,

        /// <summary>双点 DIQ：1 字节（2 位状态 + 品质位）</summary>
        DoublePoint = 1,

        /// <summary>归一化值 NVA：2 字节有符号 + QDS，真实值 = 原始值 / 32768</summary>
        Normalized = 2,
        /// <summary>标度化值 SVA：2 字节有符号 + QDS，需配合外部小数点位</summary>
        Scaled = 3,

        /// <summary>短浮点数 R32：IEEE 754 单精度 4 字节（小端） + QDS</summary>
        ShortFloat = 4,

        /// <summary>累计量 BCR：4 字节计数器 + 1 字节标志（顺序号/CY/CA/IV）</summary>
        IntegratedTotal = 5,

        /// <summary>步位置 VTI：1 字节（7 位值 + 暂态位） + QDS</summary>
        StepPosition = 6,

        /// <summary>32 位比特串 BSI：4 字节 + QDS</summary>
        Bitstring32 = 7,

        /// <summary>
        /// 归一化值但**不带品质**（M_ME_ND_1）：只有 2 字节 NVA。
        /// 单列一项而不是加个 bool 参数，是因为"有没有 QDS"会改变元素长度（2 还是 3 字节），
        /// 长度一变，后面所有对象的偏移就全错——这种差别必须显式写在类型里。
        /// </summary>
        NormalizedNoQuality = 8
    }

    /// <summary>
    /// 时标方式 —— 信息元素后面跟不跟时标、跟哪种。
    ///
    /// CP24Time2a 只有毫秒 + 分钟（3 字节），没有日期，跨零点要靠主站自己补；
    /// CP56Time2a 带完整年月日时分秒（7 字节）。除了字节数不同，
    /// 它们的位置也固定"永远在信息元素和 QDS 之后"，所以能作为一个独立维度处理。
    /// </summary>
    public enum TimeTagKind : byte
    {
        /// <summary>不带时标</summary>
        None = 0,

        /// <summary>CP24Time2a（3 字节，短时标）</summary>
        Cp24Time2a = 1,

        /// <summary>CP56Time2a（7 字节，长时标）</summary>
        Cp56Time2a = 2
    }
}
