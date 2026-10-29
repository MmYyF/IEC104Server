using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using IEC104.App.Infrastructure;
using IEC104.App.Services;

namespace IEC104.App.ViewModels
{
    /// <summary>
    /// 关于页里的一行"标签 + 值"。做成对象而不是写死 7 个属性，
    /// 是因为这些行以后还会加（比如"许可到期"、"License 类型"），
    /// 加一行只在 <see cref="AboutPageViewModel.BuildRows"/> 里补一条。
    /// </summary>
    public sealed class AboutInfoRow
    {
        public AboutInfoRow(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }

        public string Value { get; }
    }

    /// <summary>
    /// 更新说明里的一行（一个版本）。
    ///
    /// 默认只露一行标题，点一下展开条目——因为最有用的永远是"当前这一版"，
    /// 全展开会让页面拉得很长，用户还得滚半天才能看到自己真正关心的那条。
    /// 当前版本默认展开：那是用户第一次打开这一页唯一想看的东西。
    /// </summary>
    public sealed class ReleaseNoteViewModel : ViewModelBase
    {
        private bool _isExpanded;

        public ReleaseNoteViewModel(ReleaseNote note, bool isCurrent)
        {
            Note = note;
            IsCurrent = isCurrent;
            _isExpanded = isCurrent;
            ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        }

        public ReleaseNote Note { get; }

        /// <summary>就是"正在运行的这一版"</summary>
        public bool IsCurrent { get; }

        public string VersionText => $"v{Note.Version}";

        public string Title => Note.Title;

        public string DateText => Note.Date;

        public IReadOnlyList<string> Items => Note.Items;

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetField(ref _isExpanded, value))
                {
                    OnPropertyChanged(nameof(ChevronExpanded));
                }
            }
        }

        /// <summary>折叠时是 ▸、展开时是 ▾（两个 Path 靠这两条切可见性）</summary>
        public bool ChevronExpanded => _isExpanded;

        public bool ChevronCollapsed => !_isExpanded;

        public ICommand ToggleCommand { get; }
    }

    /// <summary>
    /// 设置页之"关于"。
    ///
    /// 从主界面菜单栏挪进来的。原来"帮助 → 关于"是一个模态小窗，看完就得关，
    /// 而这一页的内容（版本、构建信息、更新说明）恰恰是"想看的时候顺手看一眼"的东西——
    /// 挂在设置里最下面，和"改配置"共用一套导航，不多占一个窗口。
    ///
    /// 页面上只放三块：这是什么（名称 + 一句话）、跑在什么环境（几行 key-value）、
    /// 改过什么（更新说明）。多余的说明文字一律不写——用户打开这一页的目的很具体。
    /// </summary>
    public sealed class AboutPageViewModel : SettingsPageViewModel
    {
        public AboutPageViewModel(SettingsViewModel owner) : base(owner)
        {
            CopyCommand = new RelayCommand(CopySummary);
            OpenRepositoryCommand = new RelayCommand(OpenRepository);

            BuildRows();
            BuildReleases();

            // 换主题后"当前主题"那一行要跟着变，否则页面会显示上一个主题名
            ThemeService.Changed += OnThemeChanged;
        }

        public override string Title => "关于";

        public override string Subtitle => "软件信息、运行环境与更新说明。";

        /// <summary>
        /// 不显示页头那句副标题：这一页的内容本身就是说明，
        /// 再顶一句"这是软件信息"只是多一行字。
        /// </summary>
        public override bool ShowSubtitle => false;

        // ── 头部 ────────────────────────────────────────────────

        public string ProductName => AppInfo.ProductName;

        public string Description => AppInfo.Description;

        // ── 信息表 ──────────────────────────────────────────────

        public ObservableCollection<AboutInfoRow> Rows { get; } = new();

        /// <summary>
        /// 这几行每次重建而不是只建一次：设置文件路径、当前主题、构建信息
        /// 都可能在运行期间变（换主题、便携目录回退），一次写死就会显示陈旧值。
        /// 行数只有个位数，重建的代价可以忽略。
        /// </summary>
        private void BuildRows()
        {
            Rows.Clear();
            Rows.Add(new AboutInfoRow("版本", AppInfo.VersionWithArch));
            Rows.Add(new AboutInfoRow("构建时间", AppInfo.BuildTimeText));
            Rows.Add(new AboutInfoRow("构建 ID", AppInfo.BuildId));
            Rows.Add(new AboutInfoRow("运行时", AppInfo.Framework));
            Rows.Add(new AboutInfoRow("操作系统", AppInfo.OperatingSystem));
            Rows.Add(new AboutInfoRow("当前主题", ThemeService.Current?.Name ?? "默认"));

            // 设置文件的完整路径特意排在最后：它长得能占两三行，
            // 落在两列网格的中间会把对面那格也撑高，后面短项全被推下去、
            // 右边留一片空洞。放在末格就只多占自己这一格。
            Rows.Add(new AboutInfoRow("设置文件", AppSettingsStore.FilePath));
        }

        private void OnThemeChanged()
        {
            BuildRows();
            OnPropertyChanged(nameof(Rows));
        }

        // ── 更新说明 ────────────────────────────────────────────

        public ObservableCollection<ReleaseNoteViewModel> Releases { get; } = new();

        private void BuildReleases()
        {
            Releases.Clear();
            foreach (ReleaseNote note in ReleaseNotes.All)
            {
                Releases.Add(new ReleaseNoteViewModel(
                    note,
                    ReleaseNotes.Compare(note.Version, AppInfo.Version) == 0));
            }
        }

        /// <summary>
        /// 更新状态一句话。现在是"已是最新"（内置数据里最新一条就是自己）；
        /// 接了在线更新之后，这里改成"发现 v1.10.0"即可，界面不用动。
        /// </summary>
        public string UpdateStatus => ReleaseNotes.HasNewerThanRunning
            ? $"发现新版本 v{ReleaseNotes.LatestVersion}"
            : $"已是最新版本 v{AppInfo.Version}";

        // ── 按钮 ────────────────────────────────────────────────

        public ICommand CopyCommand { get; }

        public ICommand OpenRepositoryCommand { get; }

        /// <summary>复制到剪贴板由视图层做（VM 不引用 TopLevel / Clipboard）</summary>
        public Action<string> CopyRequested { get; set; }

        private void CopySummary()
        {
            CopyRequested?.Invoke(AppInfo.Summary);
            Owner.Notify("已复制", "软件信息已复制到剪贴板，粘贴到反馈里即可。");
        }

        private void OpenRepository()
        {
            if (!AppInfo.HasRepository)
            {
                Owner.Notify("项目主页",
                    "仓库地址还没确定，等发布到 GitHub 后这里就能一键打开。\n\n"
                    + "（地址写在 Services/AppInfo.cs 的 RepositoryUrl 常量里）");
                return;
            }

            Owner.OpenUrl?.Invoke(AppInfo.RepositoryUrl);
        }
    }
}
