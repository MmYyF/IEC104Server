using System;
using System.Collections.Generic;
using IEC104.Core.CS101;

namespace IEC104.App.ViewModels
{
    /// <summary>
    /// 一根通信报文记录 —— 用于界面上的"报文监视 / ASDU 监视"列表。
    ///
    /// 复刻说明：对应原程序的
    /// Windows.Connection.CommunicatingTrafficWindow（通信报文窗口）
    /// 与 ViewModels.MsgViewModel（消息模型）。
    /// </summary>
    public class AsduRecord
    {
        /// <summary>发生时间</summary>
        public DateTime Timestamp { get; init; } = DateTime.Now;

        /// <summary>方向：收（主站→从站）或 发（从站→主站）</summary>
        public string Direction { get; init; }

        /// <summary>类型标识代号，如 C_IC_NA_1</summary>
        public string TypeCode { get; init; }

        /// <summary>类型中文名</summary>
        public string TypeName { get; init; }

        /// <summary>传送原因（含数值）</summary>
        public string CauseText { get; init; }

        /// <summary>公共地址</summary>
        public int CommonAddress { get; init; }

        /// <summary>信息对象摘要（点号）</summary>
        public string ObjectSummary { get; init; }

        /// <summary>
        /// 信息元素里的值（遥信 合/分、双点 中间态/分/合、遥测数值、累计量…）。
        ///
        /// 【为什么要单独一列】原来只显示"类型 + IOA=1001"，用户看一条
        /// M_ME_NC_1 变位报文，知道是哪个点变了，但**不知道变了多少**——
        /// 而这正是联调时唯一想确认的东西。口径与点表一致（见 PointViewModel.ValueText），
        /// 免得同一份数据在两个界面显示成两种写法。
        /// </summary>
        public string ValueText { get; init; } = string.Empty;

        /// <summary>原始报文十六进制，便于对照规约抓包</summary>
        public string Hex { get; init; }

        public string TimeText => Timestamp.ToString("HH:mm:ss.fff");

        public bool IsReceived => Direction == "收";

        /// <summary>从一条 ASDU 构造记录（用于解析后的展示）</summary>
        public static AsduRecord FromAsdu(Asdu asdu, string direction, byte[] rawFrame)
        {
            string typeCode = asdu.TypeId.ToString();
            string objects = string.Empty;
            string valueText = string.Empty;

            if (asdu.InformationObjects.Count > 0)
            {
                InformationObject first = asdu.InformationObjects[0];
                objects = asdu.InformationObjects.Count == 1
                    ? $"IOA={first.ObjectAddress}"
                    : $"IOA={first.ObjectAddress} …(共 {asdu.InformationObjects.Count} 个)";

                valueText = DescribeValue(first);
                if (asdu.InformationObjects.Count > 1 && valueText.Length > 0)
                {
                    // 多对象报文只解第一个（再多也塞不进一列）；
                    // 加省略号是为了别让人误以为"整条报文就这一个值"。
                    valueText += " …";
                }
            }

            return new AsduRecord
            {
                Direction = direction,
                TypeCode = typeCode,
                TypeName = Services.TypeIdCatalog.DisplayNameOf(asdu.TypeId),
                CauseText = $"{asdu.Cot}({(int)asdu.Cot})",
                CommonAddress = asdu.CommonAddress,
                ObjectSummary = objects,
                ValueText = valueText,
                Hex = rawFrame == null ? string.Empty : BitConverter.ToString(rawFrame).Replace('-', ' ')
            };
        }

        /// <summary>
        /// 把一个信息对象渲染成"值"。
        /// 认不出来的类型返回空串（宁可不显示，也不要显示一个看不懂的东西）。
        /// </summary>
        private static string DescribeValue(InformationObject informationObject)
        {
            switch (informationObject)
            {
                case SinglePointInformation single:
                    return Qualify(single.Quality.Value ? "合 (1)" : "分 (0)", single.Quality);
                case SinglePointWithCp24Time2a single24:
                    return Qualify(single24.Quality.Value ? "合 (1)" : "分 (0)", single24.Quality);
                case SinglePointWithCp56Time2a single56:
                    return Qualify(single56.Quality.Value ? "合 (1)" : "分 (0)", single56.Quality);

                case DoublePointInformation doublePoint:
                    return Qualify(DescribeDoublePoint(doublePoint.Quality.Value), doublePoint.Quality);
                case DoublePointWithCp24Time2a doublePoint24:
                    return Qualify(DescribeDoublePoint(doublePoint24.Quality.Value), doublePoint24.Quality);
                case DoublePointWithCp56Time2a doublePoint56:
                    return Qualify(DescribeDoublePoint(doublePoint56.Quality.Value), doublePoint56.Quality);

                case MeasuredValueShort measuredShort:
                    return Qualify(measuredShort.Value.ToString("0.###"), measuredShort.Quality);
                case MeasuredValueScaled measuredScaled:
                    return Qualify(measuredScaled.RawValue.ToString(), measuredScaled.Quality);
                case MeasuredValueNormalized measuredNormalized:
                    // 归一化值同时给原始码和工程值：现场校点时两个都要能对上
                    return Qualify(
                        $"{measuredNormalized.RawValue}({measuredNormalized.EngineeringValue:0.####})",
                        measuredNormalized.Quality);
                case MeasuredValueNormalizedWithoutQuality measuredNormalizedRaw:
                    return measuredNormalizedRaw.RawValue.ToString();

                case IntegratedTotals totals:
                    return $"{totals.CounterReading:N0}";
                case StepPositionInformation step:
                    return $"档位 {(int)step.Value}";
                case Bitstring32 bitstring:
                    return $"0x{bitstring.Value:X8}";

                case SingleCommand singleCommand:
                    return singleCommand.Value ? "合 (1)" : "分 (0)";
                case DoubleCommand doubleCommand:
                    // 枚举值：0 不允许 / 1 分 / 2 合 / 3 不确定（见 DoubleCommandValue）
                    return doubleCommand.Value switch
                    {
                        DoubleCommandValue.On => "合 (2)",
                        DoubleCommandValue.Off => "分 (1)",
                        DoubleCommandValue.Indeterminate => "不确定 (3)",
                        _ => "不允许 (0)"
                    };

                case GenericInformationObject generic:
                    return DescribeGeneric(generic);

                default:
                    return string.Empty;
            }
        }

        private static string DescribeDoublePoint(DoublePointValue value)
        {
            // 与点表同口径：0 中间态 / 1 分 / 2 合 / 3 不确定
            return value switch
            {
                DoublePointValue.On => "合 (2)",
                DoublePointValue.Off => "分 (1)",
                DoublePointValue.IndeterminateOrIntermediate => "中间态 (0)",
                _ => "不确定 (3)"
            };
        }

        /// <summary>
        /// 通用信息对象（没有强类型类的那批类型）：按元素格式给值。
        /// 带时标的测量值走的都是这条路，所以这里必须处理。
        /// </summary>
        private static string DescribeGeneric(GenericInformationObject generic)
        {
            string text = generic.Format switch
            {
                ElementFormat.SinglePoint => generic.Value != 0 ? "合 (1)" : "分 (0)",
                ElementFormat.DoublePoint => DescribeDoublePoint((DoublePointValue)(int)generic.Value),
                ElementFormat.Normalized => $"{generic.Value:0.####}",
                ElementFormat.NormalizedNoQuality => $"{generic.Value:0.####}",
                ElementFormat.Scaled => $"{(long)generic.Value}",
                ElementFormat.ShortFloat => $"{generic.Value:0.###}",
                ElementFormat.IntegratedTotal => $"{(long)generic.Value:N0}",
                ElementFormat.StepPosition => $"档位 {(long)generic.Value}",
                ElementFormat.Bitstring32 => $"0x{(uint)generic.Value:X8}",
                _ => string.Empty
            };

            return text.Length == 0 ? text : Qualify(text, generic.Quality);
        }

        private static string Qualify(string text, SinglePointQuality quality) =>
            Qualify(text, quality.Invalid, quality.Blocked, quality.Substituted, quality.NotTopical);

        private static string Qualify(string text, DoublePointQuality quality) =>
            Qualify(text, quality.Invalid, quality.Blocked, quality.Substituted, quality.NotTopical);

        private static string Qualify(string text, QualityDescriptor quality) =>
            Qualify(text, quality.Invalid, quality.Blocked, quality.Substituted, quality.NotTopical);

        /// <summary>
        /// 品质位只在"不正常"时追加。
        /// 正常报文后面跟一串 (----) 会把真正的异常淹掉——报文监视一屏几十行，
        /// 需要的是"哪几行有问题"一眼可见。
        /// </summary>
        private static string Qualify(string text, bool invalid, bool blocked, bool substituted, bool notTopical)
        {
            if (!invalid && !blocked && !substituted && !notTopical)
            {
                return text;
            }

            List<string> flags = new(4);
            if (invalid) { flags.Add("IV"); }
            if (blocked) { flags.Add("BL"); }
            if (substituted) { flags.Add("SB"); }
            if (notTopical) { flags.Add("NT"); }
            return $"{text} ({string.Join(",", flags)})";
        }

        /// <summary>构造一条纯文本日志记录（链路事件等）</summary>
        public static AsduRecord Note(string text)
        {
            return new AsduRecord
            {
                Direction = "—",
                TypeCode = "链路",
                TypeName = text,
                CauseText = string.Empty,
                CommonAddress = 0,
                ObjectSummary = string.Empty,
                Hex = string.Empty
            };
        }
    }
}
