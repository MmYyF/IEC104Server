using System;
using System.Collections.ObjectModel;
using IEC104.App.Infrastructure;

namespace IEC104.App.ViewModels
{
    /// <summary>
    /// 信号区的一个标签页。
    ///
    /// 第一个标签恒为"全部"（不分组，直接引用从站原点表）；
    /// 之后每个标签对应一种 ASDU 类型 —— 现场看报文时，
    /// "这条总召里到底有几个遥信、几个遥测"是最常问的问题，
    /// 按类型分页比在几千行里翻要快得多。
    ///
    /// 注意：分组是**引用**同一个 PointViewModel 对象，不复制数据。
    /// 所以值变化、品质变化在哪个标签里都能实时反映；
    /// 但点位增删需要重建标签（由 MainWindowViewModel 监听点表集合变化）。
    /// </summary>
    public class PointTabViewModel : ViewModelBase
    {
        public PointTabViewModel(string title, string code, string category,
            ObservableCollection<PointViewModel> points, bool isAll = false, int typeValue = 0,
            string note = null)
        {
            Title = title ?? string.Empty;
            Code = code ?? string.Empty;
            Category = category ?? string.Empty;
            Points = points ?? new ObservableCollection<PointViewModel>();
            IsAll = isAll;
            TypeValue = typeValue;
            Note = note ?? string.Empty;
        }

        /// <summary>ASDU 类型枚举值（TypeID 数值），标签顺序按它升序排</summary>
        public int TypeValue { get; }

        /// <summary>中文名，如"单点信息"</summary>
        public string Title { get; }

        /// <summary>规约代号，如 M_SP_NA_1；"全部"标签为空</summary>
        public string Code { get; }

        /// <summary>分类（遥信 / 遥测 / 遥控 / 遥脉 / 其他）</summary>
        public string Category { get; }

        /// <summary>是否"全部"标签</summary>
        public bool IsAll { get; }

        /// <summary>
        /// 自定义提示语。为空时由 Title/Code/Category 拼一句默认的。
        /// 为什么需要：按电力大类分组的标签，它自己的 Title 就等于 Category，
        /// 再按默认格式拼就是"遥测（遥测）· 遥测 · 4 个"，同一个词念三遍。
        /// 这类标签真正的信息是"它由哪几个 ASDU 类型合成"，得单独给。
        /// </summary>
        public string Note { get; }

        /// <summary>本标签对应的点位集合</summary>
        public ObservableCollection<PointViewModel> Points { get; }

        /// <summary>
        /// 标签上显示的标题：只显示中文名。
        ///
        /// 以前是"单点信息 · M_SP_NA_1"，加上角标后一行放不下，标签栏只能横向滚动；
        /// 而规约代号对看报文的人不需要常驻——放到 ToolTip 里，要看再悬停。
        /// </summary>
        public string Caption => Title;

        /// <summary>标签上的数量角标</summary>
        public string CountText => Points.Count.ToString();

        /// <summary>
        /// 有没有点。标签栏用它决定要不要画角标。
        ///
        /// 电力大类是固定四类（遥测/遥信/遥控/遥脉）。"遥控"这一栏在点表里通常很少——
        /// 点表只存监视方向的信息对象，真正的遥控命令由主站下发、不占点表，
        /// 只有档位（步位置）这类可监视的"遥控类状态量"会落在这里。
        /// 这种"按口径必须有、但眼下没东西"的标签，画一个"0"只是噪音；
        /// 不给数字，标签本身照样在，位置也不会跳。
        /// </summary>
        public bool HasPoints => Points.Count > 0;

        /// <summary>
        /// 空标签页中间显示的一句话。
        ///
        /// 为什么要有：按电力大类分组时四类恒定出现，而"遥控"这一栏常常是 0 个。
        /// 点进去一片空白，第一反应是"点是不是丢了/软件坏了"，而不是"这类本来就没有"。
        /// 一句话把原因说清，比留一张白纸好。
        /// </summary>
        public string EmptyHint
        {
            get
            {
                if (IsAll)
                {
                    return "还没有信息对象。用工具条的「新增点位」、或在这个区域点右键添加。";
                }

                if (Category == "遥控")
                {
                    return "本点表没有遥控类的信息对象——这一类收的是档位（步位置）等可监视的状态量，"
                           + "遥控与设点命令由主站下发，不占点表。";
                }

                return string.IsNullOrEmpty(Title)
                    ? "这个标签下还没有信息对象。"
                    : $"这个标签下还没有「{Title}」类的信息对象。";
            }
        }

        /// <summary>提示：这个标签是什么</summary>
        public string ToolTip =>
            !string.IsNullOrEmpty(Note)
                ? Note
                : string.IsNullOrEmpty(Code)
                    ? $"全部信息对象，共 {Points.Count} 个"
                    : $"{Title}（{Code}）· {Category} · {Points.Count} 个";
    }
}
