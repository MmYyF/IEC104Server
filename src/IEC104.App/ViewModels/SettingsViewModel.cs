using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using IEC104.App.Infrastructure;
using IEC104.App.Services;
using IEC104.Core.CS101;

namespace IEC104.App.ViewModels
{
    /// <summary>
    /// 设置界面的一页（左侧标签栏的一项）。
    ///
    /// 做成"基类 + 每页一个子类"，是因为左边的标签是**可增长的**：
    /// 以后再加一个可配置项，只需要新增一个子类 + 在 XAML 里补一份 DataTemplate，
    /// 不用动左侧栏、容器、切换逻辑。这也是这一轮把"设置"单独立出来的目的——
    /// 而不是又散落到菜单和对话框里。
    /// </summary>
    public abstract class SettingsPageViewModel : ViewModelBase
    {
        protected SettingsPageViewModel(SettingsViewModel owner) => Owner = owner;

        protected SettingsViewModel Owner { get; }

        /// <summary>左侧栏显示的名字</summary>
        public abstract string Title { get; }

        /// <summary>右侧内容顶部的说明，一句话讲清这一页管什么</summary>
        public abstract string Subtitle { get; }

        /// <summary>
        /// 是否在页头显示那句说明。
        ///
        /// 加这个开关是因为"一句话讲清这页管什么"对配置页有用（用户第一次点进来要知道
        /// 这一页能改什么），对"视图""关于"这种内容自解释的页就是噪音——
        /// 开头一段"主界面默认怎么摆…"顶在那儿，用户还得先读完才看见选项。
        /// </summary>
        public virtual bool ShowSubtitle => true;
    }

    /// <summary>
    /// 设置总容器：内置四页 + 左侧选中项。
    ///
    /// 四页是怎么定的：
    ///   · ASDU 类型：可扩展的类型目录；
    ///   · 视图：主界面默认怎么摆（侧边栏扁平/树形、点表标签按类型还是按大类）；
    ///   · 报文监视：报文缓冲上限（联调时要看多长的时序，是真正会想调的东西）；
    ///   · 存储：设置文件在哪、怎么清空——把"配置存在哪"从口头约定变成界面上看得见的事实。
    /// 后两页本身不大，但它们证明了"设置"是个能装东西的地方，而不是为单一功能开的特例。
    /// </summary>
    public sealed class SettingsViewModel : ViewModelBase
    {
        private SettingsPageViewModel _selectedPage;

        public SettingsViewModel()
        {
            AsduTypesPage = new AsduTypesPageViewModel(this);
            ViewPage = new ViewPageViewModel(this);
            ProjectPage = new ProjectPageViewModel(this);
            TrafficPage = new TrafficPageViewModel(this);
            StoragePage = new StoragePageViewModel(this);
            AboutPage = new AboutPageViewModel(this);

            // 顺序 = 左侧标签栏从上到下的顺序。
            // "视图"排第一：它是这几页里唯一"改完马上看出区别"的，也是用户最常回来调的一项；
            // "工程"紧跟其后：它管的是"改动什么时候落盘"，和"界面怎么摆"同属使用习惯，
            // 都属于"进来调一次就不太再动"的那类；
            // "关于"不在 Pages 里——它钉在标签栏最下面单独一格（见 MainWindow.axaml）。
            Pages = new ObservableCollection<SettingsPageViewModel>
            {
                ViewPage,
                ProjectPage,
                AsduTypesPage,
                TrafficPage,
                StoragePage
            };

            OpenAboutCommand = new RelayCommand(() => SelectedPage = AboutPage);

            _selectedPage = Pages[0];
        }

        public ObservableCollection<SettingsPageViewModel> Pages { get; }

        public AsduTypesPageViewModel AsduTypesPage { get; }

        public ViewPageViewModel ViewPage { get; }

        /// <summary>「工程」页：启动行为 + 自动保存 + 信号值落盘节奏</summary>
        public ProjectPageViewModel ProjectPage { get; }

        public TrafficPageViewModel TrafficPage { get; }

        public StoragePageViewModel StoragePage { get; }

        /// <summary>
        /// "关于"页。刻意**不放进 <see cref="Pages"/>**：
        /// 它是"这一版是什么"的静态信息，不是可改的配置项，
        /// 混在 ASDU 类型 / 报文监视之间会让人以为它也是个设置项。
        /// 钉在左栏最下面单独一格，跟其它几项用一条分隔线隔开。
        /// </summary>
        public AboutPageViewModel AboutPage { get; }

        public ICommand OpenAboutCommand { get; }

        /// <summary>当前选中的是不是"关于"（左栏底部那一格据此高亮）</summary>
        public bool IsAboutPageSelected => ReferenceEquals(_selectedPage, AboutPage);

        /// <summary>当前选中的是普通设置页（左栏列表据此显示/隐藏高亮）</summary>
        public bool IsSettingsPageSelected => !IsAboutPageSelected;

        /// <summary>左栏最下面那行小字：产品名 + 版本，想确认版本时不用点进去</summary>
        public string VersionCaption => $"{AppInfo.ProductName} {AppInfo.Version}";

        /// <summary>
        /// "视图"页改完之后通知主界面按新偏好重排。
        /// 由主视图模型注入——设置页说的是"我要什么样的界面"，
        /// 具体怎么摆是主界面的责任，两边不互相引用。
        /// </summary>
        public Action ViewPreferenceChanged { get; set; }

        /// <summary>轻量提示框（标题 + 内容），由视图层注入</summary>
        public Action<string, string> ShowMessage { get; set; }

        /// <summary>用系统浏览器打开链接，由视图层注入</summary>
        public Action<string> OpenUrl { get; set; }

        /// <summary>设置页里的提示；视图层没接线时静默跳过（自检直接跑逻辑）</summary>
        internal void Notify(string title, string message) => ShowMessage?.Invoke(title, message);

        public SettingsPageViewModel SelectedPage
        {
            get => _selectedPage;
            set
            {
                // 忽略 null：左侧 ListBox 只认 Pages 里的项，选中"关于"（不在 Pages 里）时
                // 它会把 SelectedItem 归零并回写 null——不挡一下，点"关于"会立刻被弹回去。
                if (value == null)
                {
                    return;
                }

                if (SetField(ref _selectedPage, value))
                {
                    OnPropertyChanged(nameof(CurrentPage));
                    OnPropertyChanged(nameof(IsAboutPageSelected));
                    OnPropertyChanged(nameof(IsSettingsPageSelected));
                }
            }
        }

        /// <summary>右侧内容区绑定的当前页</summary>
        public SettingsPageViewModel CurrentPage => _selectedPage;

        /// <summary>
        /// 统计某个类型被多少个点位使用。
        /// 由主视图模型注入——设置页不该自己去遍历整棵通道/从站/点表树。
        /// 删类型前必须先问一句，否则那些点会变成"代号显示、分类归到其他"的半残状态。
        /// </summary>
        public Func<TypeIdentifier, int> CountPointsUsing { get; set; }

        /// <summary>
        /// 弹"新增 / 编辑自定义类型"对话框。
        /// 参数是草稿对象与完成回调（true = 确认）。
        /// 为什么不用返回值直接拿结果：模态对话框在 Avalonia 里是异步的，
        /// 想同步拿到结果只能阻塞 UI 线程，而阻塞 UI 线程又会让对话框自己弹不出来 —— 死锁。
        /// 这也是本工程所有对话框统一采用"对象 + 回调"的原因。
        /// </summary>
        public Action<AsduTypeEditModel, Action<bool>> EditAsduTypeDialog { get; set; }

        /// <summary>弹二次确认框（标题, 内容, 完成回调）</summary>
        public Action<string, string, Action<bool>> ConfirmDialog { get; set; }

        /// <summary>二次确认；视图层没接线时按"已确认"处理（便于自检直接跑逻辑）</summary>
        internal void Confirm(string title, string message, Action<bool> done)
        {
            if (ConfirmDialog == null)
            {
                done(true);
                return;
            }
            ConfirmDialog(title, message, done);
        }
    }

    /// <summary>
    /// 设置页之一：ASDU 类型目录。
    ///
    /// 这一页要回答三个问题，缺一个用户就不敢用：
    ///   ① 现在有哪些类型？（内置 + 自定义，一张表看全）
    ///   ② 哪些能改？（内置只读，自定义可改可删——改了内置会波及导出 CSV 与页签命名）
    ///   ③ 加一个类型之后，发出去的帧对不对？（所以新增时强制选"编码格式"和"时标"，
    ///      并在界面上直接给出字节长度预览）
    /// </summary>
    public sealed class AsduTypesPageViewModel : SettingsPageViewModel
    {
        private AsduTypeRowViewModel _selectedRow;

        public AsduTypesPageViewModel(SettingsViewModel owner) : base(owner)
        {
            // 新增不再受"枚举里还剩几个号"限制：类型号可手填 1..255，
            // 只要有空号就能加，所以这里恒定可执行
            AddCommand = new RelayCommand(Add, () => TypeIdCatalog.AllTypes.Count < TypeIdCatalog.MaxTypeNumber);
            EditCommand = new RelayCommand(Edit, () => _selectedRow?.IsCustom == true);
            RemoveCommand = new RelayCommand(Remove, () => _selectedRow?.IsCustom == true);
            Reload();
        }

        public override string Title => "ASDU 类型";

        public override string Subtitle =>
            "点表里可选的类型由这张表决定。内置类型只读；自定义类型可以自己加，用来适配规约扩展"
            + "或项目内部的类型约定——不需要等新版本。";

        public ObservableCollection<AsduTypeRowViewModel> Rows { get; } = new();

        public AsduTypeRowViewModel SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (SetField(ref _selectedRow, value))
                {
                    (EditCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (RemoveCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(SelectionHint));
                }
            }
        }

        public ICommand AddCommand { get; }

        public ICommand EditCommand { get; }

        public ICommand RemoveCommand { get; }

        /// <summary>
        /// 表格上方的一句统计。
        ///
        /// 只报数字，不写"自定义类型会存进设置文件"这类解释——副标题里已经说了，
        /// 而这一行在 940px 宽的表格上方只放得下一行，多写几个字就被截断成"重启…"。
        /// </summary>
        public string Summary
        {
            get
            {
                int builtIn = Rows.Count(row => !row.IsCustom);
                int custom = Rows.Count - builtIn;
                return $"内置 {builtIn} 项 · 自定义 {custom} 项 · 类型号可手填 1~255"
                       + $"（规约里现成、尚未收录的还有 {TypeIdCatalog.AddableTypes.Count} 个）";
            }
        }

        public string SelectionHint => _selectedRow == null
            ? "选中一行可以编辑或删除；内置类型不能改。"
            : _selectedRow.IsCustom
                ? $"已选中自定义类型「{_selectedRow.DisplayName}」，可以编辑或删除。"
                : $"「{_selectedRow.DisplayName}」是内置类型，只能查看——改内置名会让导出点表与页签对不上。";

        /// <summary>重建表格（类型目录变化后调用）</summary>
        public void Reload()
        {
            AsduTypeRowViewModel keep = _selectedRow;

            Rows.Clear();

            // 按 TypeID 数值排序：规约里这个数字就是报文第一个字节，
            // 排查报文时"第 30 号是什么类型"要能一眼捞到
            foreach (TypeDescriptor descriptor in TypeIdCatalog.AllTypes.OrderBy(t => (int)t.Type))
            {
                Rows.Add(new AsduTypeRowViewModel(descriptor));
            }

            SelectedRow = keep == null
                ? Rows.FirstOrDefault()
                : Rows.FirstOrDefault(row => row.TypeValue == keep.TypeValue) ?? Rows.FirstOrDefault();

            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(SelectionHint));
            (AddCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void Add()
        {
            AsduTypeEditModel draft = new();
            void Commit(bool confirmed)
            {
                if (!confirmed)
                {
                    return;
                }

                CommitCustomTypes(extra: draft.ToDescriptor(), removing: null);
                SelectedRow = Rows.FirstOrDefault(row => row.TypeValue == (int)draft.CurrentType);
            }

            if (Owner.EditAsduTypeDialog == null)
            {
                Commit(true);
                return;
            }

            Owner.EditAsduTypeDialog(draft, Commit);
        }

        private void Edit()
        {
            if (_selectedRow?.IsCustom != true)
            {
                return;
            }

            TypeDescriptor existing = _selectedRow.Descriptor;
            AsduTypeEditModel draft = new(existing);

            void Commit(bool confirmed)
            {
                if (!confirmed)
                {
                    return;
                }

                CommitCustomTypes(extra: null, removing: existing.Type, replacing: draft.ToDescriptor());
                SelectedRow = Rows.FirstOrDefault(row => row.TypeValue == (int)draft.CurrentType);
            }

            if (Owner.EditAsduTypeDialog == null)
            {
                Commit(true);
                return;
            }

            Owner.EditAsduTypeDialog(draft, Commit);
        }

        private void Remove()
        {
            if (_selectedRow?.IsCustom != true)
            {
                return;
            }

            TypeDescriptor target = _selectedRow.Descriptor;

            int inUse = Owner.CountPointsUsing?.Invoke(target.Type) ?? 0;
            if (inUse > 0)
            {
                // 硬拦：删掉之后这些点会退化成"用代号当名字、分类归到其他"，
                // 而点表里看不出发生过什么——这种无声降级比直接拒绝糟得多
                Owner.Confirm("无法删除",
                    $"还有 {inUse} 个信息点在使用「{target.DisplayName}」（{target.Code}）。\n\n"
                    + "请先把这些点改成别的类型，再回来删除。",
                    _ => { });
                return;
            }

            Owner.Confirm("删除自定义类型",
                $"确定删除「{target.DisplayName}」（{target.Code}）？\n\n"
                + "删除后它不会再出现在新增/修改信息对象的类型下拉里。",
                confirmed =>
                {
                    if (confirmed)
                    {
                        CommitCustomTypes(extra: null, removing: target.Type);
                    }
                });
        }

        /// <summary>
        /// 把改动落进目录 + 写文件。
        ///
        /// 顺序很关键：先改目录（界面立刻有反馈），再写盘。
        /// 写盘失败不回滚目录——用户已经看到类型生效了，此刻把界面改回去只会更困惑，
        /// 改为把失败原因显示在页面上（见 <see cref="LastError"/>）。
        /// </summary>
        private void CommitCustomTypes(TypeDescriptor extra, TypeIdentifier? removing,
            TypeDescriptor replacing = null)
        {
            List<TypeDescriptor> next = TypeIdCatalog.CustomTypes.ToList();

            if (removing.HasValue)
            {
                next.RemoveAll(item => item.Type == removing.Value);
            }

            if (replacing != null)
            {
                next.RemoveAll(item => item.Type == replacing.Type);
            }

            if (extra != null)
            {
                next.Add(extra);
            }

            TypeIdCatalog.ReplaceCustomTypes(next);
            Owner.AsduTypesPage.Reload();

            AppSettingsStore.CaptureFromCatalog();
            AppSettingsStore.Save();
            AppSettingsStore.NotifyChanged();

            OnPropertyChanged(nameof(LastError));
        }

        /// <summary>设置文件写入失败时的提示（正常为空）</summary>
        public string LastError => AppSettingsStore.LastError;
    }

    /// <summary>ASI 类型表格的一行</summary>
    public sealed class AsduTypeRowViewModel
    {
        public AsduTypeRowViewModel(TypeDescriptor descriptor) => Descriptor = descriptor;

        public TypeDescriptor Descriptor { get; }

        public TypeIdentifier Type => Descriptor.Type;

        /// <summary>TypeID 数值（报文第一个字节）</summary>
        public int TypeValue => (int)Descriptor.Type;

        public string Code => Descriptor.Code;

        public string DisplayName => Descriptor.DisplayName;

        public string Category => Descriptor.Category;

        public bool IsCustom => Descriptor.IsCustom;

        public string Source => Descriptor.Source;

        /// <summary>
        /// 编码格式。自定义类型用自己的声明；内置类型先问注册表（走通用编码器的那批），
        /// 再问专用类说明表——两处都没有才显示"专用实现"，而不是所有内置类型一律这么写。
        /// </summary>
        public string FormatText => Descriptor.IsCustom
            ? InformationObjectRegistry.DescribeFormat(Descriptor.Format)
            : InformationObjectRegistry.TryDescribe(Descriptor.Type, out string format, out _)
                ? format
                : "专用实现";

        public string TimeTagText => Descriptor.IsCustom
            ? (Descriptor.TimeTag == TimeTagKind.None
                ? "无时标"
                : InformationObjectRegistry.DescribeTimeTag(Descriptor.TimeTag))
            : InformationObjectRegistry.TryDescribe(Descriptor.Type, out _, out string timeTag)
                ? timeTag.Replace("不带时标", "无时标")
                : "按类型";

        public string ToolTip => Descriptor.ToolTip;
    }

    /// <summary>
    /// 设置页之二：视图。
    ///
    /// 这一页回答的是"这工具默认长什么样"，两项都是**启动时**的默认值：
    ///   ① 侧边栏是"只显示从站"还是"通道 / 从站分开显示"；
    ///   ② 信号表格的标签按 ASDU 类型分，还是按电力四大类分。
    ///
    /// 两项都和主界面上的快捷切换写的是同一个设置字段：在主界面切过、这里会跟着变，
    /// 在这儿改完、主界面立刻重排。不做"设置页的默认值 vs 运行期临时值"两套——
    /// 那会让用户遇到"我明明设过了，怎么又变回来了"，而这正是设置页该消灭的问题。
    /// </summary>
    public sealed class ViewPageViewModel : SettingsPageViewModel
    {
        public ViewPageViewModel(SettingsViewModel owner) : base(owner)
        {
            // 主界面上的按钮改了值，这一页的单选钮要跟着挪，否则两处显示互相打架
            AppSettingsStore.Changed += RaiseAll;
        }

        public override string Title => "视图";

        public override string Subtitle =>
            "主界面默认怎么摆。这里改完立刻生效，下次启动还是这个样子——"
            + "和主界面上的快捷切换是同一份设置，不会出现两处各说各话。";

        /// <summary>
        /// 不显示页头说明。
        ///
        /// 这一页只有四张卡片，卡片上各自带一句"选了它会怎样"，
        /// 顶上再压一段"主界面默认怎么摆…"等于让人先读一段再看见选项。
        /// 设置页的说明应当是**贴着选项**的，不是先来一段导语。
        /// </summary>
        public override bool ShowSubtitle => false;

        private void RaiseAll()
        {
            OnPropertyChanged(nameof(SidebarTableView));
            OnPropertyChanged(nameof(SidebarListView));
            OnPropertyChanged(nameof(GroupByAsduType));
            OnPropertyChanged(nameof(GroupByCategory));
        }

        private void Commit()
        {
            AppSettingsStore.Save();
            AppSettingsStore.NotifyChanged();
            Owner.ViewPreferenceChanged?.Invoke();
        }

        // ── ① 侧边栏 ────────────────────────────────────────────

        /// <summary>只显示从站列表（扁平，默认）</summary>
        public bool SidebarListView
        {
            get => !AppSettingsStore.Current.SidebarTreeView;
            set
            {
                if (value && AppSettingsStore.Current.SidebarTreeView)
                {
                    AppSettingsStore.Current.SidebarTreeView = false;
                    Commit();
                }
            }
        }

        /// <summary>通道 / 从站分开显示（树形）</summary>
        public bool SidebarTableView
        {
            get => AppSettingsStore.Current.SidebarTreeView;
            set
            {
                if (value && !AppSettingsStore.Current.SidebarTreeView)
                {
                    AppSettingsStore.Current.SidebarTreeView = true;
                    Commit();
                }
            }
        }

        // ── ② 信号表格的标签分组 ────────────────────────────────

        /// <summary>按 ASDU 类型分组（默认）</summary>
        public bool GroupByAsduType
        {
            get => !AppSettingsStore.Current.PointsGroupByCategory;
            set
            {
                if (value && AppSettingsStore.Current.PointsGroupByCategory)
                {
                    AppSettingsStore.Current.PointsGroupByCategory = false;
                    Commit();
                }
            }
        }

        /// <summary>按电力大类分组（遥测 / 遥信 / 遥控 / 遥脉）</summary>
        public bool GroupByCategory
        {
            get => AppSettingsStore.Current.PointsGroupByCategory;
            set
            {
                if (value && !AppSettingsStore.Current.PointsGroupByCategory)
                {
                    AppSettingsStore.Current.PointsGroupByCategory = true;
                    Commit();
                }
            }
        }

        // ── 卡片上的一句话说明 ──────────────────────────────────
        //
        // 每个选项一句话，贴着卡片写。不写"为什么这样设计"——
        // 那是文档的事；卡片上只回答"选了它会怎样"。
        // 早先这里是一整段解释，用户原话："这些文字不用展示，界面内容要简洁一些"。

        public string SidebarListCaption => "一个装置一个 IP，建从站时自动配通道";

        public string SidebarTreeCaption => "一个端口挂多台装置，靠公共地址区分";

        public string GroupByTypeCaption => "与报文里的 TypeID 一一对应";

        public string GroupByCategoryCaption => "遥测 / 遥信 / 遥控 / 遥脉";
    }

    /// <summary>
    /// 设置页之二：工程（1.10.1）。
    ///
    /// 为什么这一页必须存在：改动什么时候落盘，是用户真的会问的问题——
    /// "我改了一半它会不会自己存进去？""仿真跑着跑着会不会一直写盘把盘写满？"
    /// 这些问题在界面上答，比在文档里答有用。
    ///
    /// 页面上两个开关的分工是刻意分开的：
    ///   · 「自动保存」只管**结构/参数**改动（关掉就是"我改完自己按 Ctrl+S"）；
    ///   · 信号值那条**没有开关**——它是"最后一次的值要留下来"的实现方式，
    ///     关掉等于不存值，那不如不要这个功能。
    /// </summary>
    public sealed class ProjectPageViewModel : SettingsPageViewModel
    {
        public ProjectPageViewModel(SettingsViewModel owner) : base(owner)
        {
            // 主界面改了这些值（比如以后加的快捷开关），这一页要跟着变
            AppSettingsStore.Changed += RaiseAll;
        }

        public override string Title => "工程";

        public override string Subtitle => "改动什么时候写进工程文件。";

        /// <summary>不显示页头说明：四个分组标题已经说清了，顶上再压一句是多余的</summary>
        public override bool ShowSubtitle => false;

        private void RaiseAll()
        {
            OnPropertyChanged(nameof(ShowWelcomeOnStartup));
            OnPropertyChanged(nameof(AutoSaveEnabled));
            OnPropertyChanged(nameof(IsAutoSaveDelayEnabled));
            OnPropertyChanged(nameof(SelectedAutoSaveDelay));
            OnPropertyChanged(nameof(SelectedValueSaveDelay));
        }

        private void Commit()
        {
            AppSettingsStore.Save();
            AppSettingsStore.NotifyChanged();
        }

        // ── ① 启动行为 ──────────────────────────────────────────

        /// <summary>启动时显示欢迎页（工程仍会自动恢复）</summary>
        public bool ShowWelcomeOnStartup
        {
            get => AppSettingsStore.Current.ShowWelcomeOnStartup;
            set
            {
                if (AppSettingsStore.Current.ShowWelcomeOnStartup == value)
                {
                    return;
                }

                AppSettingsStore.Current.ShowWelcomeOnStartup = value;
                Commit();
            }
        }

        // ── ② 自动保存（只控制结构/参数改动）────────────────────

        /// <summary>自动保存结构改动</summary>
        public bool AutoSaveEnabled
        {
            get => AppSettingsStore.Current.AutoSaveEnabled;
            set
            {
                if (AppSettingsStore.Current.AutoSaveEnabled == value)
                {
                    return;
                }

                AppSettingsStore.Current.AutoSaveEnabled = value;
                Commit();
                OnPropertyChanged(nameof(IsAutoSaveDelayEnabled));
            }
        }

        /// <summary>开关关着时那个下拉置灰——免得用户以为调了延迟就会开始自动存</summary>
        public bool IsAutoSaveDelayEnabled => AppSettingsStore.Current.AutoSaveEnabled;

        /// <summary>可选延迟。四个档位够用，不给任意输入：这是个手感项，不是精确参数</summary>
        public IReadOnlyList<DelayOption> DelayOptions { get; } = new List<DelayOption>
        {
            new(3), new(5), new(10), new(30)
        };

        /// <summary>停止操作多久后保存（结构改动）</summary>
        public DelayOption SelectedAutoSaveDelay
        {
            get => Find(AppSettingsStore.Current.AutoSaveDelaySeconds);
            set
            {
                if (value == null || AppSettingsStore.Current.AutoSaveDelaySeconds == value.Seconds)
                {
                    return;
                }

                AppSettingsStore.Current.AutoSaveDelaySeconds = value.Seconds;
                Commit();
            }
        }

        /// <summary>值停止变化多久后保存（无开关）</summary>
        public DelayOption SelectedValueSaveDelay
        {
            get => Find(AppSettingsStore.Current.ValueSaveDelaySeconds);
            set
            {
                if (value == null || AppSettingsStore.Current.ValueSaveDelaySeconds == value.Seconds)
                {
                    return;
                }

                AppSettingsStore.Current.ValueSaveDelaySeconds = value.Seconds;
                Commit();
            }
        }

        private DelayOption Find(int seconds) =>
            DelayOptions.FirstOrDefault(option => option.Seconds == seconds) ?? DelayOptions[1];
    }

    /// <summary>下拉里的一个延迟档位</summary>
    public sealed class DelayOption
    {
        public DelayOption(int seconds) => Seconds = seconds;

        public int Seconds { get; }

        public string Label => $"{Seconds} 秒";
    }

    /// <summary>
    /// 设置页之三：报文监视。
    ///
    /// 只有一项，但它是现场真的会想改的：联调抓到一段完整的时序往往超过 3000 条，
    /// 满了就把最早的那段挤掉，正好挤掉要看的那一段。
    /// </summary>
    public sealed class TrafficPageViewModel : SettingsPageViewModel
    {
        public TrafficPageViewModel(SettingsViewModel owner) : base(owner) { }

        public override string Title => "报文监视";

        public override string Subtitle => "报文监视的缓冲上限。超出上限后最旧的记录会被丢掉，避免长时间运行吃满内存。";

        public int BufferLimit
        {
            get => AppSettingsStore.Current.TrafficBufferLimit;
            set
            {
                int clamped = Math.Clamp(value, 500, 50000);
                if (clamped == AppSettingsStore.Current.TrafficBufferLimit)
                {
                    return;
                }

                AppSettingsStore.Current.TrafficBufferLimit = clamped;
                AppSettingsStore.Save();
                AppSettingsStore.NotifyChanged();

                OnPropertyChanged();
                OnPropertyChanged(nameof(Hint));
            }
        }

        public string Hint =>
            $"当前上限 {BufferLimit:N0} 条。1 万条以上会明显拖慢界面刷新，"
            + "只在需要回看长时序时调高。已打开的报文监视窗口按新上限滚动，无需重开。";

        /// <summary>常用档位，点一下就设好，省得用微调按钮点几十下</summary>
        public ICommand UsePresetCommand => _usePreset ??= new RelayCommand<string>(text =>
        {
            if (int.TryParse(text, out int value))
            {
                BufferLimit = value;
            }
        });

        private ICommand _usePreset;
    }

    /// <summary>
    /// 设置页之三：存储。
    ///
    /// 把"设置存在哪"显式摆出来。原先这部分是口头知识（"在 %APPDATA% 里那个文件夹"），
    /// 现场换机器、备份、排查"为什么我的自定义类型没了"都卡在这一步。
    /// </summary>
    public sealed class StoragePageViewModel : SettingsPageViewModel
    {
        public StoragePageViewModel(SettingsViewModel owner) : base(owner)
        {
            OpenFolderCommand = new RelayCommand(() => OpenFolderRequested?.Invoke(AppSettingsStore.FolderPath));
            ResetCommand = new RelayCommand(Reset);
            ReloadCommand = new RelayCommand(() => Owner.AsduTypesPage.Reload());
        }

        public override string Title => "存储";

        public override string Subtitle =>
            "设置与工作区是两份数据：工作区是某个工程的点表，可以有很多份；设置是偏好，只有一份。"
            + "设置文件默认就放在 exe 旁边——整个文件夹拷到别的机器，配置跟着走。";

        /// <summary>由视图层执行（打开资源管理器）</summary>
        public Action<string> OpenFolderRequested { get; set; }

        public ICommand OpenFolderCommand { get; }

        public ICommand ResetCommand { get; }

        public ICommand ReloadCommand { get; }

        public string FilePath => AppSettingsStore.FilePath;

        public string FolderPath => AppSettingsStore.FolderPath;

        /// <summary>
        /// 位置说明：正常（就在 exe 目录、没搬过）时为空串，界面上整行不占位。
        ///
        /// 除了"出错"之外，还有一种情况必须说出来：旧版本把文件存在 %APPDATA%，
        /// 现在**搬**过来了。不说的话用户会以为旧设置丢了，而旧文件其实还在原处。
        /// </summary>
        public string LocationNote => AppSettingsStore.LocationNote;

        public bool HasLocationNote => !string.IsNullOrEmpty(AppSettingsStore.LocationNote);

        // 原来这里还有一段"为什么不用 SQLite"的长文案，界面上整块展示。
        // 那是给评审看的技术选型说明，不是用户打开设置要办的事——删掉了。
        // 四条理由记在 docs/代码分析报告.md 与 AppSettings 的注释里，改这块时去那儿看。

        public string FileStateText
        {
            get
            {
                try
                {
                    if (!System.IO.File.Exists(FilePath))
                    {
                        return "设置文件尚未创建（改任意一项设置就会生成）。";
                    }

                    System.IO.FileInfo info = new(FilePath);
                    return $"文件大小 {info.Length:N0} 字节 · 最后修改 {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
                }
                catch (Exception exception)
                {
                    return "读取文件信息失败：" + exception.Message;
                }
            }
        }

        /// <summary>写入失败时的原因；没出错时是空串（界面上整行隐藏）</summary>
        public string LastError => AppSettingsStore.LastError ?? string.Empty;

        /// <summary>有没有需要用户看到的错误——绑 IsVisible 用，避免露出一行"（无错误）"</summary>
        public bool HasError => !string.IsNullOrEmpty(AppSettingsStore.LastError);

        private void Reset()
        {
            Owner.Confirm("恢复默认设置",
                "将清空全部自定义 ASDU 类型，并把报文缓冲上限恢复为 3000 条。\n\n"
                + "工作区（点表）不受影响。确定继续？",
                confirmed =>
                {
                    if (!confirmed)
                    {
                        return;
                    }

                    AppSettingsStore.Reset();
                    Owner.AsduTypesPage.Reload();
                    OnPropertyChanged(nameof(FileStateText));
                    OnPropertyChanged(nameof(LastError));
                    OnPropertyChanged(nameof(HasError));
                });
        }
    }
}
