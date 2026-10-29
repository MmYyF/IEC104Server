using System;
using System.Collections.Generic;
using Avalonia.Media;
using IEC104.App.Infrastructure;
using IEC104.App.Services;
using IEC104.Core.CS101;
using IEC104.Simulator;
using IEC104.Simulator.IoModels;

namespace IEC104.App.ViewModels
{
    /// <summary>点表里可以就地编辑的字段</summary>
    public enum PointField
    {
        None = 0,
        Address,
        Name,
        Value
    }

    /// <summary>
    /// 信息对象（数据点）的界面模型。
    ///
    /// 复刻说明：原程序为每种 ASDU 类型各建一对 Model/ViewModel
    /// （如 SinglePointInformationModel + SinglePointInformationViewModel），
    /// 共 60 余个类，每种类型有一套自己的强类型字段。
    /// 本复刻版用一个统一模型 + 类型目录来承载，换取"配置驱动、加类型不改代码"。
    ///
    /// 与协议层的连接：Value 的 setter 直接写入底层 <see cref="DataPoint"/>，
    /// 由它触发从站的自发上送（COT=3）——也就是说界面上改值，主站会立刻收到变位报文。
    /// </summary>
    public class PointViewModel : ViewModelBase
    {
        private readonly DataPoint _point;
        private readonly SlaveStation _station;

        // 1.9.7 内存优化：原先这里还缓存了 _name / _isInvalid / _isBlocked /
        // _isSubstituted / _lastChangedAt 五个字段，与底层 DataPoint 各存一份。
        // 一百万点下这些缓存合计约 30 MB，而它们唯一的作用是"读属性时少跳一次引用"——
        // 一次字段访问换 30 MB，不划算。现在统一读 _point，只有
        //   · _type：要查目录（TypeIdCatalog.Get）才缓存
        //   · _value / _lastChangedAt：表格每行每帧都在读，缓存一次省一次间接寻址
        // 这两个高频只读量还留在本类。
        private TypeDescriptor _type;
        private double _value;
        private DateTime _lastChangedAt;

        // 内联编辑状态：同一时刻一行只会编辑一列，所以用一个字段表示即可
        private PointField _editingField;
        private string _editText = string.Empty;

        public PointViewModel(DataPoint point, SlaveStation station)
        {
            _point = point ?? throw new ArgumentNullException(nameof(point));
            _station = station;

            _type = TypeIdCatalog.Get(point.Type);
            _value = point.Value;
            _lastChangedAt = point.LastChangedAt;
        }

        /// <summary>信息对象地址（点号）</summary>
        public int ObjectAddress
        {
            get => _point.ObjectAddress;
            set
            {
                if (_point.ObjectAddress == value)
                {
                    return;
                }
                // 点号变更需要重建索引，交给上层统一处理（见 SlaveViewModel.UpdatePointAddress）
                OnPropertyChanged();
            }
        }

        /// <summary>点名称</summary>
        public string Name
        {
            get => _point.Name;
            set
            {
                if (string.Equals(_point.Name, value, StringComparison.Ordinal))
                {
                    return;
                }
                _point.Name = value;
                OnPropertyChanged();
            }
        }

        /// <summary>ASDU 类型</summary>
        public TypeDescriptor Type
        {
            get => _type;
            set
            {
                if (SetField(ref _type, value) && value != null)
                {
                    _point.Type = value.Type;
                    OnPropertyChanged(nameof(TypeCode));
                    OnPropertyChanged(nameof(TypeName));
                    OnPropertyChanged(nameof(TypeTip));
                    OnPropertyChanged(nameof(QualityText));
                    OnPropertyChanged(nameof(ValueText));
                    OnPropertyChanged(nameof(ValueBrush));
                    OnPropertyChanged(nameof(EditableValueText));
                }
            }
        }

        /// <summary>规约代号，供表格列显示</summary>
        public string TypeCode => _type?.Code ?? string.Empty;

        /// <summary>
        /// 点表里显示的类型名 —— 用中文名，不用规约代号。
        /// 代号留在 <see cref="TypeTip"/> 的悬停提示里：现场核对报文时要它，
        /// 但日常看表时"双点信息"比 M_DP_NA_1 一眼就读得懂。
        /// </summary>
        public string TypeName => _type?.DisplayName ?? string.Empty;

        /// <summary>悬停提示：代号 · 分类（自定义类型还会带上编码格式）</summary>
        public string TypeTip => _type?.ToolTip ?? string.Empty;

        /// <summary>
        /// 当前值。
        /// 注意：这里是界面改值 → 写协议层 → 触发自发上送的完整链路，
        /// 所以 setter 里必须真正调用 SetValue，不能只改本地字段。
        /// </summary>
        public double Value
        {
            get => _value;
            set
            {
                if (SetField(ref _value, value))
                {
                    _point.SetValue(value);
                    _lastChangedAt = DateTime.Now;
                    OnPropertyChanged(nameof(ValueText));
                    OnPropertyChanged(nameof(ValueBrush));
                    OnPropertyChanged(nameof(LastChangedAtText));
                }
            }
        }

        /// <summary>值的显示文本：遥信用"合/分"，遥测保留 3 位小数</summary>
        public string ValueText
        {
            get
            {
                if (_type == null)
                {
                    return "-";
                }

                return _type.Type switch
                {
                    TypeIdentifier.M_SP_NA_1 or TypeIdentifier.M_SP_TA_1 or TypeIdentifier.M_SP_TB_1
                        => _value != 0 ? "合 (1)" : "分 (0)",

                    TypeIdentifier.M_DP_NA_1 or TypeIdentifier.M_DP_TA_1 or TypeIdentifier.M_DP_TB_1
                        => _value switch
                        {
                            2 => "合 (2)",
                            1 => "分 (1)",
                            0 => "中间态 (0)",
                            _ => "不确定 (3)"
                        },

                    TypeIdentifier.M_ST_NA_1 or TypeIdentifier.M_ST_TA_1 or TypeIdentifier.M_ST_TB_1
                        => $"档位 {(int)_value}",

                    TypeIdentifier.M_IT_NA_1 or TypeIdentifier.M_IT_TA_1 or TypeIdentifier.M_IT_TB_1
                        => $"{(long)_value:N0}",

                    TypeIdentifier.M_BO_NA_1 or TypeIdentifier.M_PS_NA_1
                        => $"0x{(uint)_value:X8}",

                    _ => _value.ToString("0.###")
                };
            }
        }

        /// <summary>品质是否无效（IV）</summary>
        public bool IsInvalid
        {
            get => _point.IsInvalid;
            set
            {
                if (_point.IsInvalid == value)
                {
                    return;
                }
                _point.IsInvalid = value;
                OnPropertyChanged();
                NotifyQualityChanged();
            }
        }

        /// <summary>品质是否封锁（BL）</summary>
        public bool IsBlocked
        {
            get => _point.IsBlocked;
            set
            {
                if (_point.IsBlocked == value)
                {
                    return;
                }
                _point.IsBlocked = value;
                OnPropertyChanged();
                NotifyQualityChanged();
            }
        }

        /// <summary>品质是否人工置数（SB）</summary>
        public bool IsSubstituted
        {
            get => _point.IsSubstituted;
            set
            {
                if (_point.IsSubstituted == value)
                {
                    return;
                }
                _point.IsSubstituted = value;
                OnPropertyChanged();
                NotifyQualityChanged();
            }
        }

        /// <summary>品质描述（界面用紧凑文本表示）</summary>
        public string QualityText
        {
            get
            {
                // 1.9.7：原先每次都 new List<string> + string.Join，
                // 而品质列是常驻可见列（换主题、刷新都会重读）。
                // 八个组合是固定的，直接查表，零分配。
                int index = QualityIndex();
                return QualityLabels[index];
            }
        }

        /// <summary>品质文本查表，顺序与 <see cref="QualityCycle"/> 一一对应</summary>
        private static readonly string[] QualityLabels =
        {
            "正常",
            "IV",
            "BL",
            "SB",
            "IV BL",
            "IV SB",
            "BL SB",
            "IV BL SB"
        };

        // ── 就地编辑（双击单元格 → 输入 → 回车生效）─────────────
        //
        // 为什么不用 DataGrid 自带的编辑模式：本表的"值"是强类型 double，
        // 但显示文本是"合 (1)""档位 3""0x00000001"这类规约化文本，
        // 直接进编辑会露出裸数字，体验很差。这里用"显示文本 / 输入框"两套控件
        // 按 IsXxxEditing 切换，输入框里放的是**可解析的原始值**，显示框保持规约文本。

        /// <summary>
        /// 改点号的真正动作（要重建协议层字典索引），由 <see cref="SlaveViewModel"/> 注入。
        /// 返回 false 表示被拒（重号/越界），界面据此提示并不改变点号。
        ///
        /// 1.9.7 内存优化：原先这里是 <c>Func&lt;PointViewModel,int,bool&gt; AddressChangeHandler</c>，
        /// 铺点时每行都要新建一个捕获 this 的委托（≥32 字节 × 点位数）。
        /// 改成持有从站引用后，一行只多一个 8 字节引用，语义完全一样。
        /// </summary>
        internal SlaveViewModel Owner { get; set; }

        /// <summary>当前正在编辑哪一列</summary>
        public PointField EditingField => _editingField;

        public bool IsAddressEditing => _editingField == PointField.Address;

        public bool IsNameEditing => _editingField == PointField.Name;

        public bool IsValueEditing => _editingField == PointField.Value;

        /// <summary>输入框里的文本（双向绑定）</summary>
        public string EditText
        {
            get => _editText;
            set => SetField(ref _editText, value);
        }

        /// <summary>进入编辑态，并把当前值填进输入框</summary>
        public void BeginEdit(PointField field)
        {
            if (field == PointField.None || _editingField == field)
            {
                return;
            }

            _editingField = field;
            EditText = field switch
            {
                PointField.Address => ObjectAddress.ToString(),
                PointField.Name => Name,
                PointField.Value => EditableValueText,
                _ => string.Empty
            };
            NotifyEditingChanged();
        }

        /// <summary>
        /// 提交编辑。返回 false 表示输入非法或被拒，调用方负责提示。
        /// 无论成功失败都退出编辑态——留在编辑态等用户重输，反而会卡住滚轮操作。
        /// </summary>
        public bool CommitEdit()
        {
            if (_editingField == PointField.None)
            {
                return false;
            }

            PointField field = _editingField;
            _editingField = PointField.None;
            bool ok = true;

            switch (field)
            {
                case PointField.Name:
                    string name = (EditText ?? string.Empty).Trim();
                    if (name.Length > 0)
                    {
                        Name = name;
                    }
                    else
                    {
                        ok = false;
                    }
                    break;

                case PointField.Value:
                    if (TryParseValue(EditText, out double parsed))
                    {
                        Value = NormalizeForType(parsed);
                    }
                    else
                    {
                        ok = false;
                    }
                    break;

                case PointField.Address:
                    ok = int.TryParse((EditText ?? string.Empty).Trim(), out int address)
                        && address >= 1 && address <= 0xFFFFFF;
                    if (ok && address != ObjectAddress)
                    {
                        ok = Owner != null && Owner.TryChangePointAddress(this, address);
                    }
                    break;
            }

            NotifyEditingChanged();
            return ok;
        }

        /// <summary>放弃编辑，恢复原样</summary>
        public void CancelEdit()
        {
            if (_editingField == PointField.None)
            {
                return;
            }

            _editingField = PointField.None;
            NotifyEditingChanged();
        }

        /// <summary>输入框初值：给可解析的数字，不是"合 (1)"这种展示文本</summary>
        public string EditableValueText
        {
            get
            {
                TypeIdentifier? type = _type?.Type;
                if (IsSinglePoint(type) || IsDoublePoint(type) || IsStepPosition(type) || IsCounter(type))
                {
                    return ((long)Math.Round(_value)).ToString();
                }
                return _value.ToString("0.###");
            }
        }

        /// <summary>
        /// 滚轮调值。direction 为 +1/-1；coarse 对应按住 Shift（步长 ×10）。
        ///
        /// 开关量是"轮换"而不是加减：单点在 0/1 之间翻，双点在
        /// 0 中间态 / 1 分 / 2 合 / 3 不确定 四态里转——现场校点时，
        /// "把双点从中间态拨到合"是最常做的一步。
        /// </summary>
        public void NudgeValue(int direction, bool coarse = false)
        {
            if (direction == 0)
            {
                return;
            }

            TypeIdentifier? type = _type?.Type;
            double next;

            if (IsSinglePoint(type))
            {
                next = _value > 0.5 ? 0 : 1;
            }
            else if (IsDoublePoint(type))
            {
                int state = (int)Math.Round(_value);
                next = ((state + direction) % 4 + 4) % 4;
            }
            else if (IsStepPosition(type))
            {
                next = Math.Clamp(Math.Round(_value) + direction, -64, 63);
            }
            else
            {
                double step = coarse ? 10 : 1;
                next = _value + direction * step;
            }

            Value = NormalizeForType(next);
        }

        /// <summary>
        /// 品质轮换：8 种组合按"置位个数 → IV/BL/SB 顺序"排列，
        /// 滚一格换一种，转到头回绕。现场核对"品质位变了主站认不认"很快。
        /// </summary>
        public void NudgeQuality(int direction)
        {
            if (direction == 0)
            {
                return;
            }

            int index = QualityIndex();
            index = ((index + direction) % QualityCycle.Length + QualityCycle.Length) % QualityCycle.Length;
            (bool iv, bool blocked, bool substituted) = QualityCycle[index];

            IsInvalid = iv;
            IsBlocked = blocked;
            IsSubstituted = substituted;
        }

        private static readonly (bool Iv, bool Blocked, bool Substituted)[] QualityCycle =
        {
            (false, false, false),   // 正常
            (true, false, false),    // IV
            (false, true, false),    // BL
            (false, false, true),    // SB
            (true, true, false),     // IV+BL
            (true, false, true),     // IV+SB
            (false, true, true),     // BL+SB
            (true, true, true)       // 全置位
        };

        private int QualityIndex()
        {
            (bool iv, bool blocked, bool substituted) = (_point.IsInvalid, _point.IsBlocked, _point.IsSubstituted);
            for (int i = 0; i < QualityCycle.Length; i++)
            {
                if (QualityCycle[i] == (iv, blocked, substituted))
                {
                    return i;
                }
            }
            return 0;
        }

        private void NotifyEditingChanged()
        {
            OnPropertyChanged(nameof(EditingField));
            OnPropertyChanged(nameof(IsAddressEditing));
            OnPropertyChanged(nameof(IsNameEditing));
            OnPropertyChanged(nameof(IsValueEditing));
        }

        /// <summary>三个品质位任意一个变了，文本和徽标配色都要跟着刷新</summary>
        private void NotifyQualityChanged()
        {
            OnPropertyChanged(nameof(QualityText));
            OnPropertyChanged(nameof(QualityBackground));
            OnPropertyChanged(nameof(QualityForeground));
        }

        /// <summary>
        /// 把任意输入归一化到该 ASDU 类型允许的取值。
        ///
        /// 注意与 BatchChangeService.RoundForType 的差别：那边把双点夹在 1..2
        /// （批量变化只模拟"分/合"），这里允许 0 中间态和 3 不确定——
        /// 手动改值时要能造出这两种异常态，主站侧才好验。
        /// </summary>
        private double NormalizeForType(double value)
        {
            TypeIdentifier? type = _type?.Type;

            if (IsSinglePoint(type))
            {
                return value > 0.5 ? 1 : 0;
            }
            if (IsDoublePoint(type))
            {
                return Math.Clamp(Math.Round(value), 0, 3);
            }
            if (IsStepPosition(type))
            {
                return Math.Clamp(Math.Round(value), -64, 63);
            }
            if (IsCounter(type))
            {
                return Math.Round(value);
            }
            return Math.Round(value, 3);
        }

        /// <summary>解析用户输入：数字，或开关量的中文/英文写法</summary>
        private bool TryParseValue(string text, out double value)
        {
            value = 0;
            string trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return false;
            }

            TypeIdentifier? type = _type?.Type;
            if (IsSinglePoint(type) || IsDoublePoint(type))
            {
                bool doublePoint = IsDoublePoint(type);
                switch (trimmed)
                {
                    case "合":
                    case "on":
                    case "ON":
                        value = doublePoint ? 2 : 1;
                        return true;
                    case "分":
                    case "off":
                    case "OFF":
                        value = doublePoint ? 1 : 0;
                        return true;
                    case "中间态":
                        value = 0;
                        return true;
                    case "不确定":
                        value = 3;
                        return true;
                }
            }

            return double.TryParse(trimmed, out value);
        }

        private static bool IsSinglePoint(TypeIdentifier? type) =>
            type is TypeIdentifier.M_SP_NA_1 or TypeIdentifier.M_SP_TA_1 or TypeIdentifier.M_SP_TB_1;

        private static bool IsDoublePoint(TypeIdentifier? type) =>
            type is TypeIdentifier.M_DP_NA_1 or TypeIdentifier.M_DP_TA_1 or TypeIdentifier.M_DP_TB_1;

        private static bool IsStepPosition(TypeIdentifier? type) =>
            type is TypeIdentifier.M_ST_NA_1 or TypeIdentifier.M_ST_TA_1 or TypeIdentifier.M_ST_TB_1;

        private static bool IsCounter(TypeIdentifier? type) =>
            type is TypeIdentifier.M_IT_NA_1 or TypeIdentifier.M_IT_TA_1 or TypeIdentifier.M_IT_TB_1;

        // ── 色彩提示 ────────────────────────────────────────────
        //
        // 原来这里是 static readonly 的一批写死画刷（换主题不会变）。
        // 现在统一从 ThemePalette 取：它按令牌名查当前主题的颜色并缓存，
        // 换主题时 ThemeService 会把缓存清掉，再由 MainWindowViewModel
        // 遍历所有点位调 RefreshThemeVisuals()，界面就跟着变了。
        // 视觉语义保持不变：合绿、分灰、中间态橙、不确定红。

        /// <summary>主题切换后刷新与颜色相关的属性（由 MainWindowViewModel 统一调用）</summary>
        public void RefreshThemeVisuals()
        {
            OnPropertyChanged(nameof(ValueBrush));
            OnPropertyChanged(nameof(QualityBackground));
            OnPropertyChanged(nameof(QualityForeground));
        }

        /// <summary>当前值文字颜色：合绿、分灰、中间态橙、不确定红</summary>
        public IBrush ValueBrush
        {
            get
            {
                TypeIdentifier? type = _type?.Type;

                if (IsSinglePoint(type))
                {
                    return _value > 0.5 ? ThemePalette.ValueOn : ThemePalette.ValueOff;
                }

                if (IsDoublePoint(type))
                {
                    return (int)Math.Round(_value) switch
                    {
                        2 => ThemePalette.ValueOn,
                        1 => ThemePalette.ValueOff,
                        0 => ThemePalette.ValueMid,
                        _ => ThemePalette.ValueBad
                    };
                }

                return ThemePalette.ValueNeutral;
            }
        }

        /// <summary>品质徽标底色：异常越多越红</summary>
        public IBrush QualityBackground
        {
            get
            {
                int count = (_point.IsInvalid ? 1 : 0) + (_point.IsBlocked ? 1 : 0) + (_point.IsSubstituted ? 1 : 0);
                if (count == 0)
                {
                    return ThemePalette.QualityOkBg;
                }
                return _point.IsInvalid ? ThemePalette.QualityBadBg : ThemePalette.QualityWarnBg;
            }
        }

        /// <summary>品质徽标文字色</summary>
        public IBrush QualityForeground
        {
            get
            {
                int count = (_point.IsInvalid ? 1 : 0) + (_point.IsBlocked ? 1 : 0) + (_point.IsSubstituted ? 1 : 0);
                if (count == 0)
                {
                    return ThemePalette.QualityOkFg;
                }
                return _point.IsInvalid ? ThemePalette.QualityBadFg : ThemePalette.QualityWarnFg;
            }
        }

        /// <summary>最近一次变化时间</summary>
        public DateTime LastChangedAt
        {
            get => _lastChangedAt;
            private set
            {
                if (SetField(ref _lastChangedAt, value))
                {
                    OnPropertyChanged(nameof(LastChangedAtText));
                }
            }
        }

        // ── 自动变化（缓变）配置 ────────────────────────────────
        //
        // 复刻说明：对应原程序 EaseFunctionConfig 里的“变化曲线 + 幅度”，
        // 由 AutoChangeService 定时驱动。放在点位上是刻意的——
        // 现场往往是“给这 20 个遥测开波动、其余保持不动”，按点配置最直接。

        private ChangePattern _changePattern = ChangePattern.None;
        private double _changeAmplitude = 10;

        /// <summary>变化模式</summary>
        public ChangePattern ChangePattern
        {
            get => _changePattern;
            set
            {
                if (SetField(ref _changePattern, value))
                {
                    OnPropertyChanged(nameof(ChangePatternText));
                    OnPropertyChanged(nameof(IsAutoChanging));
                    // 切换模式时把基准值定在当前值，避免曲线从 0 起跳
                    BaselineValue = _value;
                    SinePhase = 0;
                }
            }
        }

        public string ChangePatternText => _changePattern.DisplayName();

        /// <summary>下拉框可选项（每个点位自带，省得模板里跨层找 DataContext）</summary>
        // 注意必须写全限定名：本类有一个同名属性 ChangePatternText，
        // 直接写 ChangePatternText.All 会被解析成属性而不是那个静态类。
        public System.Collections.Generic.IReadOnlyList<ChangePattern> AvailablePatterns
            => IEC104.App.Services.ChangePatternText.All;

        /// <summary>是否参与自动变化（供界面显示“自动”标记）</summary>
        public bool IsAutoChanging => _changePattern != ChangePattern.None;

        /// <summary>变化幅度：正弦的峰峰值一半 / 随机游走单步上限 / 累计递增量</summary>
        public double ChangeAmplitude
        {
            get => _changeAmplitude;
            set => SetField(ref _changeAmplitude, value);
        }

        /// <summary>正弦相位（由 AutoChangeService 维护）</summary>
        internal double SinePhase { get; set; }

        /// <summary>变化基准值（由 AutoChangeService 维护）</summary>
        internal double BaselineValue { get; set; }

        public string LastChangedAtText => _lastChangedAt.ToString("MM-dd HH:mm:ss.fff");

        /// <summary>底层协议对象，供上层做地址重排等操作</summary>
        internal DataPoint Point => _point;

        internal SlaveStation Station => _station;

        /// <summary>由外部（收到主站遥控）通知界面刷新值</summary>
        internal void RefreshFromPoint()
        {
            _value = _point.Value;
            _lastChangedAt = _point.LastChangedAt;
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(ValueText));
            OnPropertyChanged(nameof(ValueBrush));
            OnPropertyChanged(nameof(LastChangedAtText));
        }

        /// <summary>点号已在协议层重建后，通知界面刷新地址列</summary>
        internal void NotifyAddressChanged()
        {
            OnPropertyChanged(nameof(ObjectAddress));
        }
    }
}
