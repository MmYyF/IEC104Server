using System;
using System.Collections.Generic;
using System.Linq;
using IEC104.App.Infrastructure;
using IEC104.App.Services;
using IEC104.Core.CS101;

namespace IEC104.App.ViewModels
{
    /// <summary>
    /// 新增 / 编辑自定义 ASDU 类型的草稿模型。
    ///
    /// 【为什么类型号要能手填】
    /// 1.7.x 及以前，"类型号"是一个只读下拉，选项来自 TypeIdentifier 枚举——
    /// 也就是"规约里已经写好名字的那批号"。这等于把扩展能力又锁回编译期：
    ///   · 主站厂家在私有文档里约定一个号（比如 200），工具发不出来；
    ///   · 规约修订新增一个号，要等我们发版才能在软件里选到。
    /// 现在改成手填 1..255：
    ///   · 号是规约里现成的（如 8 = M_BO_TA_1）→ 自动填代号、猜格式与时标；
    ///   · 号是我们自己约定的（如 200）→ 用户自己起代号、自己选格式。
    /// 两条路最后都落到"登记一条编码格式"，从站就能按它上送。
    ///
    /// 【为什么还必须让用户选"编码格式"和"时标"】
    /// TypeID 只是报文第一个字节，它自己不能说明载荷长什么样。如果只让用户填一个类型号，
    /// 程序就只能猜一种格式去编码，猜错了发出去的帧就是"类型写着 A、载荷却是 B"——
    /// 主站按 A 的长度解析，后面所有信息对象全部错位。
    /// 所以这里把两个决定字节布局的维度显式摆出来，并在界面上直接给出长度预览：
    /// 用户选完能立刻看到"这个类型发出去是 12 字节"，选错也能自己发现。
    /// </summary>
    public class AsduTypeEditModel : ViewModelBase
    {
        private static readonly ElementFormat[] FormatOptionsValue =
        {
            ElementFormat.SinglePoint,
            ElementFormat.DoublePoint,
            ElementFormat.Normalized,
            ElementFormat.NormalizedNoQuality,
            ElementFormat.Scaled,
            ElementFormat.ShortFloat,
            ElementFormat.IntegratedTotal,
            ElementFormat.StepPosition,
            ElementFormat.Bitstring32
        };

        private static readonly TimeTagKind[] TimeTagOptionsValue =
        {
            TimeTagKind.None,
            TimeTagKind.Cp24Time2a,
            TimeTagKind.Cp56Time2a
        };

        /// <summary>类型号输入框的原始文本。留一份文本而不是只留 int，才能边打字边给提示</summary>
        private string _typeNumberText;

        private string _code;
        private string _displayName;
        private int _categoryIndex;
        private int _formatIndex;
        private int _timeTagIndex;

        /// <summary>
        /// 上一次自动填进"代号"的值。
        /// 有了它才能分清"这个代号是用户自己敲的"还是"我们按类型号猜的"——
        /// 用户改过之后，再改类型号就不该把他的字覆盖掉。
        /// </summary>
        private string _autoCode = string.Empty;

        /// <summary>
        /// 上一次自动填进"中文名"的值（同上，避免覆盖用户手写的名字）。
        /// </summary>
        private string _autoDisplayName = string.Empty;

        /// <summary>
        /// 新建：默认挑一个"规约里有、目录还没收录"的号，用户也可以直接改成任意 1..255。
        ///
        /// 为什么给默认值而不是留空：留空时格式/时标/分类全是瞎猜的默认项，
        /// 用户得先把号填对，前面那些自动推断才有意义；先给一个合法号，界面一打开就是自洽的。
        /// </summary>
        public AsduTypeEditModel()
        {
            IsCreateMode = true;

            int seed = TypeIdCatalog.AddableTypes.Count > 0
                ? (int)TypeIdCatalog.AddableTypes[0]
                : (TypeIdCatalog.MaxTypeNumber >= 200 ? 200 : TypeIdCatalog.MaxTypeNumber);

            _typeNumberText = seed.ToString();
            ApplyTypeDefaults(seed);
        }

        /// <summary>编辑已有自定义类型：类型号与代号固定，只改中文名/分类/编码</summary>
        public AsduTypeEditModel(TypeDescriptor existing)
        {
            IsCreateMode = false;

            _typeNumberText = ((int)existing.Type).ToString();
            _code = existing.Code;
            _displayName = existing.DisplayName;
            _categoryIndex = Math.Max(0,
                TypeIdCatalog.Categories.ToList().IndexOf(TypeIdCatalog.NormalizeCategory(existing.Category)));
            _formatIndex = Math.Max(0, Array.IndexOf(FormatOptionsValue, existing.Format));
            _timeTagIndex = Math.Max(0, Array.IndexOf(TimeTagOptionsValue, existing.TimeTag));
        }

        public bool IsCreateMode { get; }

        public string Title => IsCreateMode ? "新增 ASDU 类型" : "编辑 ASDU 类型";

        /// <summary>TypeID 数值与代号在编辑模式下不可改，界面据此禁用输入框</summary>
        public bool CanEditIdentity => IsCreateMode;

        /// <summary>
        /// 类型号输入框绑定的文本。
        ///
        /// 不用 NumericUpDown 的原因：这个框的主要用法是"照文档敲一个号"，
        /// 而类型号在文档里常写成十进制（200）也常写成两位十六进制（0xC8）。
        /// 文本框可以两种都收，加个步进按钮反而要用户按几十下。
        /// </summary>
        public string TypeNumberText
        {
            get => _typeNumberText;
            set
            {
                if (!SetField(ref _typeNumberText, value))
                {
                    return;
                }

                if (TryParseTypeNumber(out int number))
                {
                    // 号一变，代号/格式/时标就跟着重猜一遍（用户手改过的那几项不动）
                    ApplyTypeDefaults(number);
                }
                else
                {
                    OnPropertyChanged(nameof(TypeNumberHint));
                    OnPropertyChanged(nameof(PreviewText));
                }
            }
        }

        /// <summary>当前类型号（解析失败时为 -1）</summary>
        public int TypeNumber => TryParseTypeNumber(out int number) ? number : -1;

        /// <summary>类型标识——允许是规约枚举里没有的号，编解码靠注册表里登记的格式</summary>
        public TypeIdentifier CurrentType => TypeNumber < 0
            ? TypeIdentifier.M_SP_NA_1
            : (TypeIdentifier)TypeNumber;

        public int CurrentTypeNumber => TypeNumber;

        /// <summary>
        /// 类型号下方的实时提示 —— 这个号意味着什么，当场说清。
        ///
        /// 三种情况分开讲，因为用户的下一步动作完全不同：
        ///   ① 规约里有这个号   → 代号已帮你填好，核对一下就行；
        ///   ② 规约里没这个号   → 代号要自己起（如 T_200），格式要按对方文档选；
        ///   ③ 号被占用了       → 换号或去编辑那一条。
        /// </summary>
        public string TypeNumberHint
        {
            get
            {
                if (!TryParseTypeNumber(out int number))
                {
                    return $"类型号要填 {TypeIdCatalog.MinTypeNumber}~{TypeIdCatalog.MaxTypeNumber} 的整数"
                           + "（报文第一个字节就是它，可以填十进制，也可以填 0x 开头的十六进制）。";
                }

                TypeDescriptor taken = TypeIdCatalog.AllTypes.FirstOrDefault(t => (int)t.Type == number);
                if (taken != null)
                {
                    return $"{number} 已被{(taken.IsCustom ? "自定义" : "内置")}类型"
                           + $"「{taken.DisplayName}」（{taken.Code}）占用，换一个号。";
                }

                if (TypeIdCatalog.TryGetStandardType(number, out TypeIdentifier standard))
                {
                    return $"{number} 在规约里是 {standard}，代号与编码格式已按它填好；"
                           + "核对无误直接确定即可。";
                }

                return $"{number} 不是规约里的标准类型号（属于自定义/扩展开口子），"
                       + "代号自己起一个（如 T_" + number + "），编码格式按对方文档选——"
                       + "选错的话主站会按错误长度解析，帧就错位了。";
            }
        }

        public string Code
        {
            get => _code;
            set => SetField(ref _code, value);
        }

        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (SetField(ref _displayName, value))
                {
                    OnPropertyChanged(nameof(PreviewText));
                }
            }
        }

        public IReadOnlyList<string> CategoryOptions => TypeIdCatalog.Categories;

        public int CategoryIndex
        {
            get => _categoryIndex;
            set
            {
                if (SetField(ref _categoryIndex, value))
                {
                    OnPropertyChanged(nameof(PreviewText));
                }
            }
        }

        public string Category =>
            TypeIdCatalog.Categories[Math.Clamp(_categoryIndex, 0, TypeIdCatalog.Categories.Count - 1)];

        public IReadOnlyList<string> FormatOptions => FormatOptionsValue
            .Select(InformationObjectRegistry.DescribeFormat).ToList();

        public int FormatIndex
        {
            get => _formatIndex;
            set
            {
                if (SetField(ref _formatIndex, value))
                {
                    RefreshDisplayNameSuggestion();
                    OnPropertyChanged(nameof(PreviewText));
                }
            }
        }

        public ElementFormat Format =>
            FormatOptionsValue[Math.Clamp(_formatIndex, 0, FormatOptionsValue.Length - 1)];

        public IReadOnlyList<string> TimeTagOptions => TimeTagOptionsValue
            .Select(InformationObjectRegistry.DescribeTimeTag).ToList();

        public int TimeTagIndex
        {
            get => _timeTagIndex;
            set
            {
                if (SetField(ref _timeTagIndex, value))
                {
                    RefreshDisplayNameSuggestion();
                    OnPropertyChanged(nameof(PreviewText));
                }
            }
        }

        public TimeTagKind TimeTag =>
            TimeTagOptionsValue[Math.Clamp(_timeTagIndex, 0, TimeTagOptionsValue.Length - 1)];

        /// <summary>
        /// 报文长度预览。用一个临时信息对象算出"每个信息对象占多少字节"，
        /// 而不是在界面里另写一份长度表——那样一旦底层改了，界面就会说谎。
        /// </summary>
        public string PreviewText
        {
            get
            {
                GenericInformationObject sample = new(1, Format, TimeTag);
                int element = sample.ElementLength;
                int tag = sample.TimeTagLength;
                return $"每个信息对象占 {sample.EncodedLength} 字节"
                       + $"（信息元素 {element} 字节{(tag > 0 ? $" + 时标 {tag} 字节" : string.Empty)}）。"
                       + (TypeNumber < 0
                           ? "类型号还不是合法数值，暂时无法确定报文的 TypeID。"
                           : $"上送时会以 TypeID={TypeNumber} 写入报文首字节。");
            }
        }

        public string Validate()
        {
            if (!TryParseTypeNumber(out int number))
            {
                return $"类型号要填 {TypeIdCatalog.MinTypeNumber}~{TypeIdCatalog.MaxTypeNumber} 的整数"
                       + "（报文第一个字节就是它）。";
            }

            string code = (_code ?? string.Empty).Trim();
            if (code.Length == 0)
            {
                return "规约代号不能为空（规约里现成的号可以留空自动填，自定义号请起一个，如 T_200）。";
            }

            if (code.Length > 32)
            {
                return "规约代号太长了，点表页签与导出的 CSV 表头会放不下（32 个字符以内）。";
            }

            if (string.IsNullOrWhiteSpace(_displayName))
            {
                return "中文名不能为空——表格与下拉里显示的就是它。";
            }

            if (TypeIdCatalog.IsBuiltIn(CurrentType))
            {
                return $"类型号 {number} 是内置类型（{TypeIdCatalog.Get(CurrentType).DisplayName}），不需要新增。";
            }

            if (IsCreateMode && TypeIdCatalog.CustomTypes.Any(item => (int)item.Type == number))
            {
                return $"类型号 {number} 已经有自定义条目了，请直接编辑它。";
            }

            // 代号重复不拦运行（TypeID 才是报文里的身份），但要拦：
            // 页签标题、导出的 CSV 表头、报文监视里的类型列显示的都是代号，
            // 两个类型共用一个代号，事后没人分得清这张表里哪列是谁
            if (TypeIdCatalog.AllTypes.Any(item =>
                    (int)item.Type != number
                    && string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase)))
            {
                return $"代号 {code} 已经被另一个类型用了，换一个（例如加个后缀区分方向）。";
            }

            return null;
        }

        /// <summary>产出要写进目录的条目</summary>
        public TypeDescriptor ToDescriptor() => new()
        {
            Type = CurrentType,
            Code = (_code ?? string.Empty).Trim(),
            DisplayName = (_displayName ?? string.Empty).Trim(),
            Category = Category,
            IsCustom = true,
            Format = Format,
            TimeTag = TimeTag
        };

        /// <summary>
        /// 解析类型号文本：十进制（200）与十六进制（0xC8 / C8h）都收。
        ///
        /// 为什么收十六进制：规约文档与抓包工具里 TypeID 常按十六进制写（0x64=100 总召唤），
        /// 让人自己换算成十进制纯属添堵，而且换算错一位就是另一种类型。
        /// </summary>
        private bool TryParseTypeNumber(out int number)
        {
            number = -1;

            string text = (_typeNumberText ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return false;
            }

            bool parsed;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                parsed = int.TryParse(text.Substring(2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out number);
            }
            else if (text.EndsWith("h", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(text.Substring(0, text.Length - 1),
                         System.Globalization.NumberStyles.HexNumber,
                         System.Globalization.CultureInfo.InvariantCulture, out number))
            {
                parsed = true;
            }
            else
            {
                parsed = int.TryParse(text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out number);
            }

            return parsed
                && number >= TypeIdCatalog.MinTypeNumber
                && number <= TypeIdCatalog.MaxTypeNumber;
        }

        /// <summary>
        /// 类型号确定之后，把代号/格式/时标/分类按号猜一遍。
        ///
        /// 猜测规则完全来自规约命名本身（IEC 60870-5-101 的类型标识命名法）：
        ///   M_SP_* 单点 / M_DP_* 双点 / M_ST_* 步位置 / M_BO_* 比特串 / M_ME_* 测量值 / M_IT_* 累计量
        ///   _NA_ 无时标 / _TA_ CP24 / _TB_ CP56
        /// 号在枚举里没有名字时（项目自定义），猜不出来——此时代号留空让用户自己起，
        /// 格式与时标保持原样（用户已经选过的那两项不能被重置）。
        /// </summary>
        private void ApplyTypeDefaults(int number)
        {
            if (TypeIdCatalog.TryGetStandardType(number, out TypeIdentifier standard))
            {
                string code = standard.ToString();

                // 代号只在"空的"或"还是我们上次自动填的"时才写，用户自己改过就不动
                if (string.IsNullOrWhiteSpace(_code) || _code == _autoCode)
                {
                    _autoCode = code;
                    Code = code;
                }

                (ElementFormat format, TimeTagKind tag) = GuessFormat(code);
                _formatIndex = Math.Max(0, Array.IndexOf(FormatOptionsValue, format));
                _timeTagIndex = Math.Max(0, Array.IndexOf(TimeTagOptionsValue, tag));
                _categoryIndex = Math.Max(0, TypeIdCatalog.Categories.ToList()
                    .IndexOf(GuessCategory(code)));

                // 中文名预填成"元素 + 时标"的读法，和内置类型的命名保持一致。
                // 原来这里填的是代号——而它下面那行提示正好写着"别用代号当名字"，
                // 用户照做就得先删掉我们塞进去的东西，等于凭空多一步。
                RefreshDisplayNameSuggestion();
            }
            else if (string.IsNullOrWhiteSpace(_code) || _code == _autoCode)
            {
                // 自定义号：给一个明显的占位代号，提醒用户这是"自己起的名字"
                _autoCode = "T_" + number;
                Code = _autoCode;

                // 中文名按当前格式/时标补一个（用户手写过的不动），
                // 否则换了号之后中文名是空的，确定时才报错，白填一遍
                RefreshDisplayNameSuggestion();
            }

            OnPropertyChanged(nameof(TypeNumber));
            OnPropertyChanged(nameof(CurrentType));
            OnPropertyChanged(nameof(CurrentTypeNumber));
            OnPropertyChanged(nameof(TypeNumberHint));
            OnPropertyChanged(nameof(Code));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(FormatIndex));
            OnPropertyChanged(nameof(TimeTagIndex));
            OnPropertyChanged(nameof(CategoryIndex));
            OnPropertyChanged(nameof(Category));
            OnPropertyChanged(nameof(Format));
            OnPropertyChanged(nameof(TimeTag));
            OnPropertyChanged(nameof(PreviewText));
        }

        /// <summary>
        /// 按当前格式/时标给一个中文名建议。
        /// 只在"中文名还是空的"或"还是上次我们猜的那个"时才写进去——
        /// 用户一旦动手改过，这里就不该再插手。
        /// </summary>
        private void RefreshDisplayNameSuggestion()
        {
            string suggestion = SuggestDisplayName(Format, TimeTag);
            if (string.IsNullOrWhiteSpace(_displayName) || _displayName == _autoDisplayName)
            {
                _autoDisplayName = suggestion;
                DisplayName = suggestion;
            }
        }

        /// <summary>
        /// 格式 + 时标 → 中文名。用词刻意与内置类型对齐（"单点""双点""比特串"…），
        /// 这样自定义类型出现在点表里时，读起来和内置的那批是一套话。
        /// </summary>
        private static string SuggestDisplayName(ElementFormat format, TimeTagKind tag)
        {
            string name = format switch
            {
                ElementFormat.SinglePoint => "单点信息",
                ElementFormat.DoublePoint => "双点信息",
                ElementFormat.Normalized => "归一化测量值",
                ElementFormat.NormalizedNoQuality => "归一化测量值(无品质)",
                ElementFormat.Scaled => "标度化测量值",
                ElementFormat.ShortFloat => "短浮点测量值",
                ElementFormat.IntegratedTotal => "累计量",
                ElementFormat.StepPosition => "步位置信息",
                ElementFormat.Bitstring32 => "32位比特串",
                _ => "自定义信息"
            };

            return tag switch
            {
                TimeTagKind.Cp24Time2a => name + "(CP24时标)",
                TimeTagKind.Cp56Time2a => name + "(CP56时标)",
                _ => name
            };
        }

        /// <summary>
        /// 按代号猜分类（只用于预填，用户可改）。
        ///
        /// 口径与 TypeIdCatalog 一致：步位置（档位）归遥控、累计量归遥脉、
        /// 比特串与初始化结束归其他——不另立"电度""档位"这种中间名。
        /// </summary>
        private static string GuessCategory(string code)
        {
            // 命令方向（C_ 开头）：电网口径里遥控、设点都归"遥控"
            if (code.StartsWith("C_", StringComparison.Ordinal))
            {
                return "遥控";
            }
            if (code.StartsWith("M_ME_", StringComparison.Ordinal))
            {
                return "遥测";
            }
            if (code.StartsWith("M_IT_", StringComparison.Ordinal))
            {
                return "遥脉";
            }
            if (code.StartsWith("M_ST_", StringComparison.Ordinal))
            {
                return "遥控";
            }
            if (code.StartsWith("M_SP_", StringComparison.Ordinal)
                || code.StartsWith("M_DP_", StringComparison.Ordinal))
            {
                return "遥信";
            }
            return "其他";
        }

        /// <summary>按代号猜编码格式与时标（只用于预填，用户可改）</summary>
        private static (ElementFormat Format, TimeTagKind Tag) GuessFormat(string code)
        {
            TimeTagKind tag = TimeTagKind.None;
            if (code.Contains("_TA_"))
            {
                tag = TimeTagKind.Cp24Time2a;
            }
            else if (code.Contains("_TB_") || code.Contains("_TC_")
                     || code.Contains("_TD_") || code.Contains("_TE_") || code.Contains("_TF_"))
            {
                // 测量值的 TC/TF 也是短/长时标之分，见下面的细分
                tag = TimeTagKind.Cp56Time2a;
            }

            if (code.StartsWith("M_SP_", StringComparison.Ordinal))
            {
                return (ElementFormat.SinglePoint, tag);
            }
            if (code.StartsWith("M_DP_", StringComparison.Ordinal))
            {
                return (ElementFormat.DoublePoint, tag);
            }
            if (code.StartsWith("M_ST_", StringComparison.Ordinal))
            {
                return (ElementFormat.StepPosition, tag);
            }
            if (code.StartsWith("M_BO_", StringComparison.Ordinal)
                || code.StartsWith("M_PS_", StringComparison.Ordinal))
            {
                return (ElementFormat.Bitstring32, tag);
            }
            if (code.StartsWith("M_IT_", StringComparison.Ordinal))
            {
                return (ElementFormat.IntegratedTotal, tag);
            }
            if (code.StartsWith("M_ME_", StringComparison.Ordinal))
            {
                // M_ME_xy_z：第三段那个字母**同时**编码了"数值类型"和"时标"，
                // 这是 1.8.0 才发现的一处老错：
                //   NA/NB/NC（N = 无时标）  → A 归一化 / B 标度化 / C 短浮点
                //   TA/TB/TC（T + A/B/C）   → CP24，数值类型同上
                //   TD/TE/TF（T + D/E/F）   → CP56，D 归一化 / E 标度化 / F 短浮点
                //   例外：ND = 归一化但**不带品质**（省那一个 QDS 字节，正是它存在的意义）
                // 原来这里只认 A/B/C/D 一套，于是 34（M_ME_TD_1）被猜成"归一化无品质"，
                // 编出来少一个品质字节、主站按 3+7 解析必然错位。
                char kind = code.Length > 6 ? code[6] : 'A';

                if (code.Contains("_ND_"))
                {
                    return (ElementFormat.NormalizedNoQuality, tag);
                }

                ElementFormat format = kind switch
                {
                    'B' or 'E' => ElementFormat.Scaled,
                    'C' or 'F' => ElementFormat.ShortFloat,
                    _ => ElementFormat.Normalized
                };

                // _TA_/_TB_/_TC_ 是 CP24（对应 N A/N B/N C），_TD_/_TE_/_TF_ 是 CP56
                if (code.Contains("_TA_") || code.Contains("_TB_") || code.Contains("_TC_"))
                {
                    tag = TimeTagKind.Cp24Time2a;
                }
                else if (code.Contains("_TD_") || code.Contains("_TE_") || code.Contains("_TF_"))
                {
                    tag = TimeTagKind.Cp56Time2a;
                }

                return (format, tag);
            }

            return (ElementFormat.SinglePoint, tag);
        }
    }
}
