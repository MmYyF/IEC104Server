using System;
using System.Collections.Generic;

namespace IEC104.Core.CS101
{
    /// <summary>
    /// ASDU 类型 → 信息元素格式的运行期注册表。
    ///
    /// 【它解决什么问题】
    /// 规约里 TypeID 就是报文第一个字节，取值 1..255。原先"一个 TypeID 一个类"的写法
    /// 把类型集合钉死在编译期，标准出个新类型或项目自己约定一个类型号，就得改代码重编译。
    /// 有了这张表，新增类型只要在运行期登记一条"这个 TypeID 用哪种元素格式、带不带时标"，
    /// 编解码就走 <see cref="GenericInformationObject"/>，不需要新类。
    ///
    /// 【为什么默认值只登记了 16 个】
    /// 内置的主力类型（M_SP_NA_1 / M_DP_NA_1 / M_ME_NA_1 / M_ME_NC_1 / M_IT_NA_1 / M_ST_NA_1…）
    /// 已经有强类型类，字节序和换算都写死在类里、经过联调验证。这张表**不去覆盖**它们，
    /// 免得"为了扩展性把跑通的老链路改坏"。
    /// 真正登记的是"目录里能选、但一直没有对应实现"的那批（带 CP24/CP56 时标的测量值、
    /// 标度化值、带时标的累计量、32 位比特串…）——它们此前会落到兜底分支被当成单点发出去，
    /// 帧长度和主站解析都对不上，属于隐藏了很久的真 bug。
    ///
    /// 【线程安全】
    /// 界面启动时一次性登记，之后只读；协议收发线程读表不加锁（读 dictionary 与写并发会有风险，
    /// 所以约定：登记只发生在启动/设置保存的 UI 线程，且登记后立刻生效，不做增量替换）。
    /// 设置页保存时会 Clear + 重新登记，为避免协议线程读到半截状态，替换成一个新字典而不是原地改。
    /// </summary>
    public static class InformationObjectRegistry
    {
        /// <summary>一条登记：元素格式 + 时标方式</summary>
        public readonly struct FormatEntry
        {
            public FormatEntry(ElementFormat format, TimeTagKind timeTag)
            {
                Format = format;
                TimeTag = timeTag;
            }

            public ElementFormat Format { get; }

            public TimeTagKind TimeTag { get; }
        }

        /// <summary>
        /// 内置登记：目录里有、但强类型实现缺失的标准类型。
        /// 每个条目的字节长度都能在 101/104 规约的信息元素定义里逐条对上。
        /// </summary>
        private static readonly Dictionary<TypeIdentifier, FormatEntry> BuiltIn = new()
        {
            // 带 CP24 短时标的监视方向类型
            { TypeIdentifier.M_SP_TA_1, new FormatEntry(ElementFormat.SinglePoint, TimeTagKind.Cp24Time2a) },
            { TypeIdentifier.M_DP_TA_1, new FormatEntry(ElementFormat.DoublePoint, TimeTagKind.Cp24Time2a) },
            { TypeIdentifier.M_ST_TA_1, new FormatEntry(ElementFormat.StepPosition, TimeTagKind.Cp24Time2a) },
            { TypeIdentifier.M_ME_TA_1, new FormatEntry(ElementFormat.Normalized, TimeTagKind.Cp24Time2a) },
            { TypeIdentifier.M_ME_TB_1, new FormatEntry(ElementFormat.Scaled, TimeTagKind.Cp24Time2a) },
            { TypeIdentifier.M_ME_TC_1, new FormatEntry(ElementFormat.ShortFloat, TimeTagKind.Cp24Time2a) },
            { TypeIdentifier.M_IT_TA_1, new FormatEntry(ElementFormat.IntegratedTotal, TimeTagKind.Cp24Time2a) },

            // 带 CP56 长时标的监视方向类型
            { TypeIdentifier.M_DP_TB_1, new FormatEntry(ElementFormat.DoublePoint, TimeTagKind.Cp56Time2a) },
            { TypeIdentifier.M_ST_TB_1, new FormatEntry(ElementFormat.StepPosition, TimeTagKind.Cp56Time2a) },
            { TypeIdentifier.M_ME_TD_1, new FormatEntry(ElementFormat.Normalized, TimeTagKind.Cp56Time2a) },
            { TypeIdentifier.M_ME_TE_1, new FormatEntry(ElementFormat.Scaled, TimeTagKind.Cp56Time2a) },
            { TypeIdentifier.M_ME_TF_1, new FormatEntry(ElementFormat.ShortFloat, TimeTagKind.Cp56Time2a) },
            { TypeIdentifier.M_IT_TB_1, new FormatEntry(ElementFormat.IntegratedTotal, TimeTagKind.Cp56Time2a) },

            // 无时标
            { TypeIdentifier.M_ME_NB_1, new FormatEntry(ElementFormat.Scaled, TimeTagKind.None) },
            { TypeIdentifier.M_ME_ND_1, new FormatEntry(ElementFormat.NormalizedNoQuality, TimeTagKind.None) },
            { TypeIdentifier.M_BO_NA_1, new FormatEntry(ElementFormat.Bitstring32, TimeTagKind.None) },
        };

        private static Dictionary<TypeIdentifier, FormatEntry> _entries = new(BuiltIn);

        /// <summary>登记（重复登记则覆盖）。用于用户自定义类型</summary>
        public static void Register(TypeIdentifier type, ElementFormat format, TimeTagKind timeTag)
        {
            Dictionary<TypeIdentifier, FormatEntry> next = new(_entries)
            {
                [type] = new FormatEntry(format, timeTag)
            };
            _entries = next;
        }

        /// <summary>清除全部自定义登记，回到只剩内置的状态（设置里"恢复默认"用）</summary>
        public static void ResetToBuiltIn() => _entries = new Dictionary<TypeIdentifier, FormatEntry>(BuiltIn);

        public static bool TryGet(TypeIdentifier type, out ElementFormat format, out TimeTagKind timeTag)
        {
            if (_entries.TryGetValue(type, out FormatEntry entry))
            {
                format = entry.Format;
                timeTag = entry.TimeTag;
                return true;
            }

            format = ElementFormat.SinglePoint;
            timeTag = TimeTagKind.None;
            return false;
        }

        /// <summary>该类型能不能按注册表编解码（内置 + 自定义都算）</summary>
        public static bool IsRegistered(TypeIdentifier type) => _entries.ContainsKey(type);

        /// <summary>当前登记的全部类型（设置界面展示用）</summary>
        public static IReadOnlyDictionary<TypeIdentifier, FormatEntry> All => _entries;

        /// <summary>格式的中文名，日志与界面上都用它</summary>
        public static string DescribeFormat(ElementFormat format) => format switch
        {
            ElementFormat.SinglePoint => "单点 SIQ（1 字节）",
            ElementFormat.DoublePoint => "双点 DIQ（1 字节）",
            ElementFormat.Normalized => "归一化 NVA（2+1 字节）",
            ElementFormat.NormalizedNoQuality => "归一化 NVA（无品质，2 字节）",
            ElementFormat.Scaled => "标度化 SVA（2+1 字节）",
            ElementFormat.ShortFloat => "短浮点 R32（4+1 字节）",
            ElementFormat.IntegratedTotal => "累计量 BCR（4+1 字节）",
            ElementFormat.StepPosition => "步位置 VTI（1+1 字节）",
            ElementFormat.Bitstring32 => "32 位比特串 BSI（4+1 字节）",
            _ => format.ToString()
        };

        /// <summary>时标方式的中文名</summary>
        public static string DescribeTimeTag(TimeTagKind timeTag) => timeTag switch
        {
            TimeTagKind.Cp24Time2a => "CP24 短时标（3 字节）",
            TimeTagKind.Cp56Time2a => "CP56 长时标（7 字节）",
            _ => "不带时标"
        };

        /// <summary>
        /// 走"专用类"编码的那批内置类型的格式说明 —— **只给界面看，不参与编码路由**。
        ///
        /// 为什么另立一张表：M_SP_NA_1、M_ME_NC_1 这些类型有各自的专用类
        /// （SinglePointInformation / ShortFloatInformation…），它们的字节布局是写死在
        /// 那些类的 Encode/Decode 里的，注册表里查不到，设置界面就只好显示"专用实现"——
        /// 在一列叫"编码格式"的格子里，"专用实现"等于什么都没说。
        ///
        /// 之所以不把它们塞进 _entries：那会让 TryGet 命中、从而把这些类型从专用类
        /// 抢到通用编码器上，帧里的位就变了。表里只放文字，谁也影响不到。
        /// </summary>
        private static readonly Dictionary<TypeIdentifier, (string Format, string TimeTag)> DedicatedFormat = new()
        {
            { TypeIdentifier.M_SP_NA_1, ("单点 SIQ（1 字节）", "不带时标") },
            { TypeIdentifier.M_SP_TB_1, ("单点 SIQ（1 字节）", "CP56 长时标（7 字节）") },
            { TypeIdentifier.M_DP_NA_1, ("双点 DIQ（1 字节）", "不带时标") },
            { TypeIdentifier.M_ST_NA_1, ("步位置 VTI（1+1 字节）", "不带时标") },
            { TypeIdentifier.M_ME_NA_1, ("归一化 NVA（2+1 字节）", "不带时标") },
            { TypeIdentifier.M_ME_NC_1, ("短浮点 R32（4+1 字节）", "不带时标") },
            { TypeIdentifier.M_IT_NA_1, ("二进制计数 BCR（4+1 字节）", "不带时标") },
            { TypeIdentifier.M_PS_NA_1, ("32 位比特串 + 变位检测（4+1 字节）", "不带时标") },
            { TypeIdentifier.M_EI_NA_1, ("初始化结束 QOI（1 字节）", "不带时标") },

            // 命令方向：SCO/DCO/RCO 与对应的监视元素只差几个控制位，长度一样
            { TypeIdentifier.C_SC_NA_1, ("单点命令 SCO（1 字节）", "不带时标") },
            { TypeIdentifier.C_DC_NA_1, ("双点命令 DCO（1 字节）", "不带时标") },
            { TypeIdentifier.C_RC_NA_1, ("步调节命令 RCO（1 字节）", "不带时标") },
            { TypeIdentifier.C_SE_NA_1, ("归一化设点 NVA（2+1 字节）", "不带时标") },
            { TypeIdentifier.C_SE_NB_1, ("标度化设点 SVA（2+1 字节）", "不带时标") },
            { TypeIdentifier.C_SE_NC_1, ("短浮点设点 R32（4+1 字节）", "不带时标") },
            { TypeIdentifier.C_BO_NA_1, ("比特串命令 BSI（4 字节）", "不带时标") },
        };

        /// <summary>
        /// 描述一个类型的编码格式：先查注册表（真按通用编码器走的那批），
        /// 再查专用类说明表，两处都没有才算"说不清"。
        /// </summary>
        public static bool TryDescribe(TypeIdentifier type, out string format, out string timeTag)
        {
            if (_entries.TryGetValue(type, out FormatEntry entry))
            {
                format = DescribeFormat(entry.Format);
                timeTag = DescribeTimeTag(entry.TimeTag);
                return true;
            }

            if (DedicatedFormat.TryGetValue(type, out (string Format, string TimeTag) dedicated))
            {
                format = dedicated.Format;
                timeTag = dedicated.TimeTag;
                return true;
            }

            format = "不适用";
            timeTag = "不适用";
            return false;
        }
    }
}
