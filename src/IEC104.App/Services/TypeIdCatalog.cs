using System;
using System.Collections.Generic;
using System.Linq;
using IEC104.Core.CS101;

namespace IEC104.App.Services
{
    /// <summary>
    /// ASDU 类型目录 —— 把 TypeID 映射为中文名与分类，供界面下拉与列表显示使用。
    ///
    /// 复刻说明：原程序为每个类型都建了 Model + ViewModel 两个类
    /// （ViewModels/Asdu/Monitor 下 22 组、Control 下 9 组，共 60 余个类型），
    /// 靠类型本身承载"名称/分类"信息。
    /// 本复刻版改用"类型目录 + 统一数据点模型"的配置驱动方式，
    /// 好处是新增点位无需新增类；代价是失去了每类型的强类型属性。
    ///
    /// 说明：此处只列出工程上实际会用到的类型；TypeIdentifier 枚举本身已覆盖全部 67 项。
    /// </summary>
    public sealed class TypeDescriptor
    {
        public TypeIdentifier Type { get; init; }

        /// <summary>规约代号，如 M_SP_NA_1</summary>
        public string Code { get; init; }

        /// <summary>中文名称</summary>
        public string DisplayName { get; init; }

        /// <summary>
        /// 分类：遥信 / 遥测 / 遥控 / 遥脉 / 其他。
        ///
        /// 这五个词就是电网里的口径，不再另立一套"内部细类"：
        /// 早先这里有"电度""档位"两个自造的名字，界面上摆出来用户要问一句"这算哪一类"，
        /// 而现场答案是现成的——累计量（电度）归遥脉，步位置（档位）归遥控。
        /// </summary>
        public string Category { get; init; }

        /// <summary>
        /// 是否为命令方向（主站 → 从站）。
        ///
        /// 判据用代号前缀 `C_` 而不是分类名：分类名是给人看的、会跟着现场口径改
        /// （这一轮就把"命令"改成了"遥控"），拿它当逻辑判据的话，改个显示名就会
        /// 悄悄改掉"哪些类型能进点表"这个行为。
        /// </summary>
        public bool IsCommand => Code != null && Code.StartsWith("C_", StringComparison.Ordinal);

        /// <summary>true = 用户在设置里自己加的，可改可删；false = 内置</summary>
        public bool IsCustom { get; init; }

        /// <summary>信息元素格式（自定义类型必填；内置类型留默认值即可）</summary>
        public ElementFormat Format { get; init; } = ElementFormat.SinglePoint;

        /// <summary>时标方式（自定义类型必填）</summary>
        public TimeTagKind TimeTag { get; init; } = TimeTagKind.None;

        /// <summary>来源，设置页表格里显示</summary>
        public string Source => IsCustom ? "自定义" : "内置";

        /// <summary>悬停提示：代号 + 分类 + 编码格式，三样都是现场排障时真正要看的东西</summary>
        public string ToolTip => IsCustom
            ? $"{Code} · {Category} · {InformationObjectRegistry.DescribeFormat(Format)}"
              + (TimeTag == TimeTagKind.None ? string.Empty : $" + {InformationObjectRegistry.DescribeTimeTag(TimeTag)}")
              + "（自定义类型）"
            : $"{Code} · {Category}";

        public override string ToString() => $"{Code}  {DisplayName}";
    }

    public static class TypeIdCatalog
    {
        /// <summary>
        /// 内置类型表 —— 编译期固定，设置界面里只能看、不能改。
        ///
        /// 为什么不许改：这些名字和分类是全工具的公共约定（页签标题、导出 CSV 的表头、
        /// 范围边界都靠它），允许改一个 M_ME_NC_1 的中文名，会让同一个人手上的两份工作区
        /// 导出的点表对不上。要"用自己项目的叫法"，就新增一个自定义类型。
        /// </summary>
        private static readonly List<TypeDescriptor> BuiltIn = new()
        {
            // ── 遥信（状态量）─────────────────────────────────────
            new() { Type = TypeIdentifier.M_SP_NA_1, Code = "M_SP_NA_1", DisplayName = "单点信息", Category = "遥信" },
            new() { Type = TypeIdentifier.M_SP_TA_1, Code = "M_SP_TA_1", DisplayName = "单点信息(CP24时标)", Category = "遥信" },
            new() { Type = TypeIdentifier.M_SP_TB_1, Code = "M_SP_TB_1", DisplayName = "单点信息(CP56时标)", Category = "遥信" },
            new() { Type = TypeIdentifier.M_DP_NA_1, Code = "M_DP_NA_1", DisplayName = "双点信息", Category = "遥信" },
            new() { Type = TypeIdentifier.M_DP_TA_1, Code = "M_DP_TA_1", DisplayName = "双点信息(CP24时标)", Category = "遥信" },
            new() { Type = TypeIdentifier.M_DP_TB_1, Code = "M_DP_TB_1", DisplayName = "双点信息(CP56时标)", Category = "遥信" },

            // ── 遥测（测量值）─────────────────────────────────────
            new() { Type = TypeIdentifier.M_ME_NA_1, Code = "M_ME_NA_1", DisplayName = "归一化测量值", Category = "遥测" },
            new() { Type = TypeIdentifier.M_ME_NB_1, Code = "M_ME_NB_1", DisplayName = "标度化测量值", Category = "遥测" },
            new() { Type = TypeIdentifier.M_ME_NC_1, Code = "M_ME_NC_1", DisplayName = "短浮点测量值", Category = "遥测" },
            new() { Type = TypeIdentifier.M_ME_ND_1, Code = "M_ME_ND_1", DisplayName = "归一化测量值(无品质)", Category = "遥测" },
            new() { Type = TypeIdentifier.M_ME_TA_1, Code = "M_ME_TA_1", DisplayName = "归一化测量值(CP24时标)", Category = "遥测" },
            new() { Type = TypeIdentifier.M_ME_TB_1, Code = "M_ME_TB_1", DisplayName = "标度化测量值(CP24时标)", Category = "遥测" },
            new() { Type = TypeIdentifier.M_ME_TC_1, Code = "M_ME_TC_1", DisplayName = "短浮点测量值(CP24时标)", Category = "遥测" },
            new() { Type = TypeIdentifier.M_ME_TD_1, Code = "M_ME_TD_1", DisplayName = "归一化测量值(CP56时标)", Category = "遥测" },
            new() { Type = TypeIdentifier.M_ME_TE_1, Code = "M_ME_TE_1", DisplayName = "标度化测量值(CP56时标)", Category = "遥测" },
            new() { Type = TypeIdentifier.M_ME_TF_1, Code = "M_ME_TF_1", DisplayName = "短浮点测量值(CP56时标)", Category = "遥测" },

            // ── 遥脉（累计量 / 电度）───────────────────────────────
            // 电能量在电网里叫"遥脉"：累计量（M_IT_*）就是脉冲累加出来的电度，
            // 界面上直接写遥脉，用户不用再做一次翻译。
            new() { Type = TypeIdentifier.M_IT_NA_1, Code = "M_IT_NA_1", DisplayName = "累计量", Category = "遥脉" },
            new() { Type = TypeIdentifier.M_IT_TA_1, Code = "M_IT_TA_1", DisplayName = "累计量(CP24时标)", Category = "遥脉" },
            new() { Type = TypeIdentifier.M_IT_TB_1, Code = "M_IT_TB_1", DisplayName = "累计量(CP56时标)", Category = "遥脉" },

            // ── 遥控（含档位）／其他 ─────────────────────────────
            // 步位置（变压器档位、升降档命令）在现场归遥控类；
            // 比特串与初始化结束既不是模拟量也不是开关状态，落"其他"，不硬塞进四大类。
            new() { Type = TypeIdentifier.M_ST_NA_1, Code = "M_ST_NA_1", DisplayName = "步位置信息(档位)", Category = "遥控" },
            new() { Type = TypeIdentifier.M_ST_TA_1, Code = "M_ST_TA_1", DisplayName = "步位置信息(CP24时标)", Category = "遥控" },
            new() { Type = TypeIdentifier.M_ST_TB_1, Code = "M_ST_TB_1", DisplayName = "步位置信息(CP56时标)", Category = "遥控" },
            new() { Type = TypeIdentifier.M_BO_NA_1, Code = "M_BO_NA_1", DisplayName = "32位比特串", Category = "其他" },
            new() { Type = TypeIdentifier.M_PS_NA_1, Code = "M_PS_NA_1", DisplayName = "带变位检测的32位比特串", Category = "其他" },
            new() { Type = TypeIdentifier.M_EI_NA_1, Code = "M_EI_NA_1", DisplayName = "初始化结束", Category = "其他" },

            // ── 命令方向（主站 → 从站）────────────────────────────
            new() { Type = TypeIdentifier.C_SC_NA_1, Code = "C_SC_NA_1", DisplayName = "单点遥控", Category = "遥控" },
            new() { Type = TypeIdentifier.C_DC_NA_1, Code = "C_DC_NA_1", DisplayName = "双点遥控", Category = "遥控" },
            new() { Type = TypeIdentifier.C_RC_NA_1, Code = "C_RC_NA_1", DisplayName = "步调节命令", Category = "遥控" },
            new() { Type = TypeIdentifier.C_SE_NA_1, Code = "C_SE_NA_1", DisplayName = "归一化设点命令", Category = "遥控" },
            new() { Type = TypeIdentifier.C_SE_NB_1, Code = "C_SE_NB_1", DisplayName = "标度化设点命令", Category = "遥控" },
            new() { Type = TypeIdentifier.C_SE_NC_1, Code = "C_SE_NC_1", DisplayName = "短浮点设点命令", Category = "遥控" },
            new() { Type = TypeIdentifier.C_BO_NA_1, Code = "C_BO_NA_1", DisplayName = "32位比特串命令", Category = "遥控" },
        };

        /// <summary>
        /// 用户自定义类型（来自设置文件）。
        ///
        /// 与内置表合起来才是"当前可用类型"。之所以做成运行期可变的列表，
        /// 是因为规约本身在扩展：标准出新类型、或者项目内部约定了一个类型号，
        /// 用户不应该为此等一个新版本。
        /// </summary>
        private static readonly List<TypeDescriptor> Custom = new();

        /// <summary>类型目录变化（增删自定义类型）后触发，界面据此重建下拉与页签</summary>
        public static event Action Changed;

        /// <summary>全部类型：内置在前（保持既有顺序），自定义追加在后</summary>
        public static IReadOnlyList<TypeDescriptor> AllTypes =>
            Custom.Count == 0 ? BuiltIn : BuiltIn.Concat(Custom).ToList();

        /// <summary>该类型是不是内置的</summary>
        public static bool IsBuiltIn(TypeIdentifier type) => BuiltIn.Any(t => t.Type == type);

        /// <summary>
        /// 电力行业四大类的固定顺序 —— 界面上按这个顺序出标签。
        ///
        /// 为什么顺序写死而不是按出现先后：这四个名字是行业通用口径，
        /// 每个人的预期顺序都一样；跟着数据变会让标签位置在两次刷新之间跳来跳去。
        /// </summary>
        public static readonly IReadOnlyList<string> BroadCategories = new[] { "遥测", "遥信", "遥控", "遥脉" };

        /// <summary>
        /// 可选分类 —— 下拉与校验共用这一份，避免"界面能选、保存时又判非法"。
        ///
        /// 四大类 + "其他"。不给"电度""档位"单独留位置：它们不是行业口径，
        /// 摆在下拉里只会让人再问一遍"这个该选哪个"。
        /// </summary>
        public static readonly IReadOnlyList<string> Categories = new[] { "遥信", "遥测", "遥控", "遥脉", "其他" };

        /// <summary>
        /// 把旧分类名折算成现行口径。
        ///
        /// 为什么要兼容：1.8.0 之前的设置文件里，自定义类型的分类可能存着
        /// "电度""档位""命令"这几个已经废弃的名字。不折算的话它们会：
        ///   · 在编辑对话框里落到下拉第一项（也就是被静默改成"遥信"）；
        ///   · 在大类页签里掉进"其他"。
        /// 两者都是无声的错，比报错更难查。
        /// </summary>
        public static string NormalizeCategory(string category)
        {
            switch ((category ?? string.Empty).Trim())
            {
                case "电度": return "遥脉";
                case "档位":
                case "命令": return "遥控";
                case "遥信":
                case "遥测":
                case "遥控":
                case "遥脉":
                case "其他":
                    return category.Trim();
                default: return "其他";
            }
        }

        /// <summary>
        /// 把 ASDU 类型折到电力行业四大类里。
        ///
        /// 分类本身就是四大类口径（见 <see cref="Categories"/>），这里只剩两件事：
        ///   ① 把历史分类名折算过来（旧设置文件里还有"电度""档位"）；
        ///   ② 兜一个"其他"桶——万一将来加了没登记的类型，宁可多一个标签也不能把点藏起来。
        /// </summary>
        public static string BroadCategoryOf(TypeIdentifier type)
        {
            string category = NormalizeCategory(Get(type).Category);
            return BroadCategories.Contains(category) ? category : "其他";
        }

        /// <summary>监视方向（从站可上送）的类型，用于点表选择</summary>
        public static IReadOnlyList<TypeDescriptor> MonitorTypes =>
            AllTypes.Where(t => !t.IsCommand).ToList();

        /// <summary>按类型查描述，未收录时给出回退描述</summary>
        public static TypeDescriptor Get(TypeIdentifier type)
        {
            TypeDescriptor found = AllTypes.FirstOrDefault(t => t.Type == type);
            return found ?? new TypeDescriptor
            {
                Type = type,
                Code = type.ToString(),
                DisplayName = type.ToString(),
                Category = "其他"
            };
        }

        // ── 自定义类型的增删（设置界面用）──────────────────────────

        /// <summary>
        /// 整体替换自定义类型列表，并把编码格式同步登记到 Core 的注册表。
        ///
        /// 为什么是"整体替换"而不是逐个增删：
        /// 设置界面的编辑是"先改草稿、点保存才生效"，保存时一次性提交全部条目最不容易出现
        /// "删了一个又漏了另一个"的半截状态；注册表也随之整体重建，不会残留已删类型的编码。
        /// </summary>
        public static void ReplaceCustomTypes(IEnumerable<TypeDescriptor> descriptors)
        {
            Custom.Clear();
            foreach (TypeDescriptor descriptor in descriptors ?? Enumerable.Empty<TypeDescriptor>())
            {
                // 内置类型不允许被自定义条目盖掉——否则同一个 TypeID 会有两套中文名，
                // 页签分组和下拉显示就各说各话了
                if (descriptor == null || IsBuiltIn(descriptor.Type))
                {
                    continue;
                }

                if (Custom.Any(existing => existing.Type == descriptor.Type))
                {
                    continue;
                }

                Custom.Add(descriptor);
                InformationObjectRegistry.Register(descriptor.Type, descriptor.Format, descriptor.TimeTag);
            }

            Changed?.Invoke();
        }

        /// <summary>当前自定义类型（设置界面表格的数据源）</summary>
        public static IReadOnlyList<TypeDescriptor> CustomTypes => Custom;

        /// <summary>类型号允许的最小值（报文首字节即 TypeID，0 在规约里没有含义）</summary>
        public const int MinTypeNumber = 1;

        /// <summary>类型号允许的最大值（TypeID 是 1 字节）</summary>
        public const int MaxTypeNumber = 255;

        /// <summary>
        /// 规约枚举里"有名字"的类型号集合。
        ///
        /// 为什么不直接 Enum.IsDefined：TypeIdentifier 的底层类型是 byte，
        /// 传 int 进去会在运行期抛 ArgumentException（"枚举底层类型与对象类型不一致"），
        /// 而这里每次改类型号都要查一次，预先把值摊平成 HashSet&lt;int&gt; 既躲开这个坑又快。
        /// </summary>
        private static readonly HashSet<int> StandardNumbers =
            new(Enum.GetValues<TypeIdentifier>().Select(type => (int)type));

        /// <summary>
        /// 该数值在规约类型枚举里有没有定义（有名字）。
        ///
        /// 用来区分两种"新类型"：
        ///   · 规约里有、只是目录没收录（如 M_BO_TA_1=8）——代号/格式都能自动推出来；
        ///   · 规约里没有、项目自己约定的号（如 200）——代号要自己起，格式要自己选。
        /// 这两种现在都允许新增，界面上只是提示不同。
        /// </summary>
        public static bool TryGetStandardType(int number, out TypeIdentifier type)
        {
            type = (TypeIdentifier)number;
            return number >= MinTypeNumber
                && number <= MaxTypeNumber
                && StandardNumbers.Contains(number);
        }

        /// <summary>
        /// 标准枚举里"还没被目录收录"的类型号 —— 新增对话框用它给建议。
        ///
        /// 1.8.0 起类型号可以手填任意 1..255（项目内部约定的号、规约新扩的号都能用），
        /// 这张表只负责回答"哪些号是规约里现成的、还没加进来"，不再限制用户只能选它。
        /// 定义在枚举里但目录没收录的（例如 M_BO_TA_1、M_EP_TA_1）正是最该被补上的那批。
        /// </summary>
        public static IReadOnlyList<TypeIdentifier> AddableTypes
        {
            get
            {
                HashSet<TypeIdentifier> taken = new(AllTypes.Select(t => t.Type));
                return Enum.GetValues<TypeIdentifier>()
                    .Where(type => !taken.Contains(type))
                    .OrderBy(type => (int)type)
                    .ToList();
            }
        }

        /// <summary>取类型的中文显示名</summary>
        public static string DisplayNameOf(TypeIdentifier type) => Get(type).DisplayName;

        /// <summary>
        /// 该类型是否带时标（CP24/CP56）。
        /// 上送时需要用当前时间填充时标字段，否则主站看到的时间是 1970 年。
        ///
        /// 先查注册表再退回"看代号里有没有 _TA_/_TB_"：
        /// 自定义类型可以起任意代号（`T_200`），代号里没有时标痕迹，
        /// 但它登记的时标方式是真有其事的，按代号猜会漏。
        /// </summary>
        public static bool HasTimeTag(TypeIdentifier type)
        {
            if (InformationObjectRegistry.TryGet(type, out _, out TimeTagKind timeTag))
            {
                return timeTag != TimeTagKind.None;
            }

            string code = type.ToString();
            return code.Contains("_TA_") || code.Contains("_TB_")
                || code.Contains("_TC_") || code.Contains("_TD_")
                || code.Contains("_TE_") || code.Contains("_TF_");
        }
    }
}
