using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using IEC104.App.Infrastructure;
using IEC104.App.Services;
using IEC104.Core.CS101;

namespace IEC104.App.ViewModels
{
    /// <summary>
    /// 主窗口 ViewModel —— 整个界面的中枢。
    ///
    /// 复刻说明（对应原程序 ViewModels.MainViewModel，反编译后 7228 行、34 个命令）：
    /// 原程序命令清单已完整还原，本类按同样的功能划分重建：
    ///
    ///   从站：CreateSlave / EditSlave / CopySlave / DeleteSlave
    ///   连接：CreateConnection / EditConnection / DeleteConnection
    ///         OpenConnection / CloseConnection / OpenAllConnection / CloseAllConnection
    ///   报文：OpenAsdu / CloseAsdu / CloseOtherAsdu / CloseAllAsdu
    ///         OpenCommunicatingTrafficWindow
    ///   点位：AddPoint / EditPoint / DeletePoint / ExportInformationObjects
    ///   工具：IEC104FrameParse / IEC101FrameParse / RedundancyCheck / StringEncodingDecoding
    ///   其他：OpenWorkspace / SaveWorkspace / SetChangeInterval / SetLanguage
    ///         AlwaysOnTop / CheckForUpdates / About / Exit
    ///
    /// 【已移除】原程序的 LicenseManagement（许可管理）命令与对应窗口——
    /// 复刻版不做激活，故该功能整体不存在。
    ///
    /// 对话框由视图层注入委托（ShowXxxDialog），这样 VM 不依赖 Avalonia 的 Window 类型，
    /// 便于将来换界面框架或做单元测试。
    /// </summary>
    public class MainWindowViewModel : ViewModelBase, IDisposable
    {
        /// <summary>
        /// 报文监视列表最多保留的条数，防止长时间运行吃满内存。
        /// 从设置里读（默认 3000）：联调时要看更长的时序，有人需要 2 万条；
        /// 但每一条都要占一行控件，上限太高会拖慢界面，所以给的是可配而不是放开。
        /// </summary>
        private static int MaxRecordCount => AppSettingsStore.Current.TrafficBufferLimit;

        private ConnectionViewModel _selectedConnection;
        private SlaveViewModel _selectedSlave;
        private PointViewModel _selectedPoint;
        private string _statusText = "就绪";

        /// <summary>
        /// 上一次写进 StatusText 的那句"工程已保存：xxx"。
        /// 存它是为了能认出"这行还是保存留下的、但现在已经有新改动了"——
        /// 不然左邻"工程已保存"和右邻"未保存"并排，读起来像自相矛盾。
        /// </summary>
        private string _saveStatusMessage;   // 未启用 nullable 上下文，这里不用 string?
        private bool _alwaysOnTop;

        /// <summary>底部监视面板默认展开（高度 240，由主窗口的行定义控制）</summary>
        private bool _isMonitorExpanded = true;
        private int _changeIntervalMilliseconds = 3000;

        /// <summary>侧边栏视图：true = 通道/从站树，false = 从站列表（扁平，默认）</summary>
        private bool _sidebarTreeView;

        /// <summary>扁平列表当前选中行</summary>
        private SlaveRowViewModel _selectedSlaveRow;

        /// <summary>防止"行选中 → 从站选中 → 行选中"互相触发成环</summary>
        private bool _syncingSlaveSelection;

        // ── 工程状态（1.10）────────────────────────────────────
        //
        // 两级"脏"要分清楚，它们的处理完全不同：
        //   · 结构脏（IsProjectDirty）—— 点表定义改了。要在标题栏挂 ●，关闭时提示保存。
        //   · 值脏（IsValueDirty）    —— 只是运行中的值变了。不挂 ●（仿真时值一直在变，
        //                                标题上永远挂着个点会让人以为存不完），
        //                                退出时静默保存。
        // 两个都必须能落到磁盘，但只有结构脏值得打断用户。

        /// <summary>当前工程的完整路径；null = 还没打开任何工程</summary>
        private string _currentProjectPath;

        /// <summary>工程显示名（来自文件里的 Name，缺省用文件名）</summary>
        private string _currentProjectName;

        private bool _isProjectDirty;
        private bool _isValueDirty;
        private bool _isSaving;
        private bool _isWelcomeVisible;

        /// <summary>
        /// 抑制标记：载入/新建工程时集合会大量变动，那不算"用户改了东西"。
        /// 不用这个标记的话，每打开一个工程就立刻变成"未保存"，用户会以为打开动作改了文件。
        /// </summary>
        private bool _suppressDirty;

        /// <summary>最近一次写盘时间，状态栏显示"已保存 09:42"用</summary>
        private DateTime? _lastSavedAt;

        // ── 自动保存（1.10.1）──────────────────────────────────
        //
        // 两个脏源、两个调度器，而不是一个共用的：
        //   · 结构脏 —— 受设置里的「自动保存」开关控制（关了就不在编辑过程中写盘，
        //              免得用户改了一半的文件被悄悄落盘）。
        //   · 值脏   —— 没有开关。这是"最后值要留下来"的实现方式，关掉就等于不存值。
        // 共用一个调度器会让"关掉自动保存"连带把值也停了，这正是要避免的。

        /// <summary>结构改动的防抖调度器（受「自动保存」开关控制）</summary>
        private SaveScheduler _structureSave;

        /// <summary>信号值变化的防抖调度器（无开关）</summary>
        private SaveScheduler _valueSave;

        /// <summary>
        /// 自检专用：把防抖延迟压到毫秒级。**null = 走设置里的秒数**。
        /// 自检不能真等 5 秒（一轮下来几十秒就白等了），
        /// 也不能把设置项的下限改成能填 0.3 秒——用户不该看见那个值。
        ///
        /// 用可空而不是"0 表示不覆盖"：0 在这里是有意义的值（"零延迟"），
        /// 混用会得到一个安静的错——自检设了 0 以为关了闸门，实际回落到设置里的 3 秒，
        /// 于是"停手就写"的断言全红，而代码看着没毛病（第一版就是这么红的）。
        /// </summary>
        internal int? DebounceOverrideMs { get; set; }

        /// <summary>自检专用：最小写盘间隔覆盖值。null = 走设置里的秒数，0 = 不设闸门</summary>
        internal int? MinGapOverrideMs { get; set; }

        /// <summary>自检专用：直接看结构脏的调度器（写了多少次、还欠不欠一次写）</summary>
        internal SaveScheduler StructureSaver => _structureSave;

        /// <summary>自检专用：直接看值脏的调度器</summary>
        internal SaveScheduler ValueSaver => _valueSave;

        public MainWindowViewModel()
        {
            // 视图偏好要在建任何列表之前定下来：LoadSampleData 会选中首个从站、
            // 进而重建点表标签，标签按哪种方式分组取决于这个值
            _sidebarTreeView = AppSettingsStore.Current.SidebarTreeView;
            _groupPointsByCategory = AppSettingsStore.Current.PointsGroupByCategory;

            Connections.CollectionChanged += OnConnectionsChanged;

            // 两个防抖调度器。延迟用委托而不是值：设置页改完立刻生效，
            // 不用重建调度器（也不会有"改了设置但这一轮还是旧延迟"的错觉）。
            _structureSave = new SaveScheduler(
                "structure",
                () => StructureDelayMs,
                () => MinGapMs,
                AutoSaveQuietly);

            _valueSave = new SaveScheduler(
                "value",
                () => ValueDelayMs,
                () => MinGapMs,
                AutoSaveQuietly);

            // 命令
            CreateConnectionCommand = new RelayCommand(() => CreateConnection());
            EditConnectionCommand = new RelayCommand(() => EditConnection(), () => SelectedConnection != null);
            DeleteConnectionCommand = new RelayCommand(() => DeleteConnection(), () => SelectedConnection != null);
            OpenConnectionCommand = new RelayCommand(() => ToggleConnection(SelectedConnection), () => SelectedConnection != null);
            OpenAllCommand = new RelayCommand(OpenAll, () => Connections.Any(c => !c.IsOpen));
            CloseAllCommand = new RelayCommand(CloseAll, () => Connections.Any(c => c.IsOpen));

            // 工具栏只留一个"启停合一"的按钮（原来是"启动"+"停止"两个并排）。
            // 一个通道不可能同时"正在启动"和"正在停止"，两个按钮里永远有一个是无效的，
            // 还占地方。这里按当前状态决定语义，点一下就是切换。
            ToggleSelectedConnectionCommand = new RelayCommand(
                () => ToggleConnection(SelectedConnection),
                () => SelectedConnection != null);
            ToggleAllConnectionsCommand = new RelayCommand(
                () =>
                {
                    if (AnyConnectionOpen)
                    {
                        CloseAll();
                    }
                    else
                    {
                        OpenAll();
                    }
                },
                () => Connections.Count > 0);

            // 即使当前没有选中通道也允许新建从站——通道可以在对话框里现建
            CreateSlaveCommand = new RelayCommand(() => CreateSlave(), () => true);
            EditSlaveCommand = new RelayCommand(() => EditSlave(), () => SelectedSlave != null);
            CopySlaveCommand = new RelayCommand(() => CopySlave(), () => SelectedSlave != null);
            DeleteSlaveCommand = new RelayCommand(() => DeleteSlave(), () => SelectedSlave != null);

            AddPointCommand = new RelayCommand(() => AddPoint(), () => SelectedSlave != null);
            EditPointCommand = new RelayCommand(() => EditPoint(), () => SelectedPoint != null);
            DeletePointCommand = new RelayCommand(() => DeletePoint(), () => SelectedPoint != null);
            ExportPointsCommand = new RelayCommand(ExportPoints, () => SelectedSlave != null);

            TogglePointGroupModeCommand = new RelayCommand(
                () => SetPointsGroupByCategory(!GroupPointsByCategory, persist: true));

            // 侧边栏视图切换（底部那颗长条按钮）。和设置 → 视图里的选项写同一个字段，
            // 所以在哪儿切都是"下次启动也是这样"
            ToggleSidebarViewCommand = new RelayCommand(() => SetSidebarView(!IsTreeView, persist: true));

            // 设置：切换主界面显示的是"工作区"还是"设置界面"
            ToggleSettingsCommand = new RelayCommand(() => IsSettingsOpen = !IsSettingsOpen);
            CloseSettingsCommand = new RelayCommand(() => IsSettingsOpen = false);

            // 设置页要问"这个类型还有多少点在用"，但设置页不该自己去遍历通道/从站/点表
            Settings.CountPointsUsing = CountPointsUsing;

            // 设置 → 视图 改完之后，主界面要立刻按新偏好重排（不用重启）
            Settings.ViewPreferenceChanged = ApplyViewPreferences;

            // 类型目录变了（新增/删除自定义类型）：页签分组、点表显示都要跟着重建。
            // 不重建的话会出现"下拉里已经有新类型了，但页签还是旧的"这种半更新状态。
            TypeIdCatalog.Changed += OnTypeCatalogChanged;

            ClearRecordsCommand = new RelayCommand(() =>
            {
                _pendingRecords.Clear();
                Records.Clear();
                Logs.Clear();
            });

            // 缓变驱动：供给函数每次取"当前所有连接下的所有从站"，
            // 这样用户新增/删除从站后不需要重新挂载
            AutoChange = new AutoChangeService();
            AutoChange.Attach(() => Connections.SelectMany(c => c.Slaves));
            AutoChange.SetInterval(_changeIntervalMilliseconds);

            ShowAutoChangeCommand = new RelayCommand(() => ShowAutoChangeDialog?.Invoke());

            // 批量建从站：一次生成多条"通道 + 从站"，解决"每个从站不同 IP"时的手工重复劳动
            BatchCreateSlaveCommand = new RelayCommand(() => ShowBatchCreateSlaveDialog?.Invoke());

            // ── 工程（1.10）──
            SaveProjectCommand = new RelayCommand(ExecuteSaveCommand, () => CanSaveProject);
            CloseProjectCommand = new RelayCommand(() => ShowCloseProjectRequest?.Invoke(), () => HasProject);

            // 批量变化会话：驱动器跟着主窗口走，关掉参数窗口后变化继续。
            // 订阅它的是"工具条提示 + 状态栏提示 + 停止按钮可用性"这三处显示。
            BatchChange = new BatchChangeSession();
            BatchChange.StateChanged += OnBatchChangeStateChanged;
            StopBatchChangeCommand = new RelayCommand(() => BatchChange.Stop(), () => IsBatchChanging);

            ResetToInitValueCommand = new RelayCommand(ConfirmResetToInitValue, () => SelectedSlave != null);
            DismissWelcomeCommand = new RelayCommand(() => IsWelcomeVisible = false);

            // 演示数据是构造函数里"无中生有"的，不是用户改的——
            // 这里必须屏蔽脏标记，否则没有工程文件时（自检模式、或跳过恢复启动）
            // 一启动就显示"未保存"，保存按钮也会莫名其妙亮着。
            _suppressDirty = true;
            try
            {
                LoadSampleData();
            }
            finally
            {
                _suppressDirty = false;
            }

            // 演示数据是直接塞进 Connections 的，扁平列表要自己再摊一次
            RebuildSlaveRows();

            // 主题切换后由这里统一刷新依赖状态的画刷
            ThemeService.Changed += OnThemeChanged;
        }

        /// <summary>
        /// 启动收尾：恢复上次工程，或者清空并显示欢迎页。
        ///
        /// 必须由视图层在构造完 ViewModel 之后调一次（见 App.axaml.cs），
        /// 不能放进构造函数：自检（--capture-ui）直接用构造出来的 VM 做断言，
        /// 它要的是"有一份可用的演示点表"，而这里会把它清掉。
        /// </summary>
        public void InitializeStartup()
        {
            string last = AppSettingsStore.Current.LastProjectPath;

            if (!string.IsNullOrWhiteSpace(last) && System.IO.File.Exists(last))
            {
                try
                {
                    OpenProject(last);
                }
                catch (Exception exception)
                {
                    // 上次的工程打不开了：不能因此起不来，退回到欢迎页并说明原因
                    StatusText = "上次的工程打不开：" + exception.Message;
                    ResetToEmptyWelcome();
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(last))
                {
                    // 文件被删/被移走了，顺手清掉这条记录，别让它一直挂着
                    AppSettingsStore.ForgetCurrentProject();
                    StatusText = "上次的工程已不存在";
                }

                ResetToEmptyWelcome();
            }

            _isWelcomeVisible = AppSettingsStore.Current.ShowWelcomeOnStartup;
            NotifyProjectState();
        }

        /// <summary>清空到"没有工程"的状态并显示欢迎页</summary>
        private void ResetToEmptyWelcome()
        {
            BatchChange.Stop();

            _suppressDirty = true;
            try
            {
                foreach (ConnectionViewModel existing in Connections.ToList())
                {
                    existing.Close();
                }
                Connections.Clear();
            }
            finally
            {
                _suppressDirty = false;
            }

            _currentProjectPath = null;
            _currentProjectName = null;
            _isProjectDirty = false;
            _isValueDirty = false;
            _isWelcomeVisible = true;

            StopAutoSave();
            RebuildSlaveRows();
            RefreshCommandStates();
        }

        /// <summary>点位自动变化驱动器（缓变）</summary>
        public AutoChangeService AutoChange { get; }

        /// <summary>
        /// 类型目录变了之后的收尾。
        /// 只做两件事：重建点表页签（自定义类型会影响分类页的构成）、
        /// 让设置页的表格重新读一遍统计数字；不给每个点刷属性——
        /// 20 万点逐个发通知只为改一列显示，代价比收益大得多。
        /// </summary>
        private void OnTypeCatalogChanged()
        {
            RebuildPointTabs();
            Settings.AsduTypesPage.Reload();
        }

        // ── 集合 ────────────────────────────────────────────────

        /// <summary>全部连接</summary>
        public ObservableCollection<ConnectionViewModel> Connections { get; } = new();

        // ── 侧边栏视图（从站列表 / 通道树）──────────────────────────
        //
        // 为什么要有两种视图：
        //   现场 99% 是一个装置 = 一台从站 = 一个 IP，用户心里的对象只有"从站"，
        //   "通道"是 104 这一层为了监听端口才存在的中间物。默认就得能直接看见从站，
        //   而不是每建一台装置先建一条"通道"再去下面挂从站（两步，还容易挂错）。
        //   但"一个通道挂多个从站"（一台前置机带多台 RTU）是规约里真实存在的形态，
        //   不能因为常见场景就删掉——所以做成视图切换，而不是二选一写死。
        //
        // 两个视图共用同一份数据和同一个选中项（SelectedConnection / SelectedSlave），
        // 切视图不丢选中、右侧点表不跳。区别只有"怎么摆"。

        /// <summary>
        /// 扁平视图的行集合：所有通道下的所有从站摊成一列。
        ///
        /// 只在集合结构变化时重建（通道增删、从站增删），状态/名称/端口的变化靠
        /// <see cref="SlaveRowViewModel"/> 自己转发——否则每接入一个主站就要重建整列。
        /// </summary>
        public ObservableCollection<SlaveRowViewModel> SlaveRows { get; } = new();

        /// <summary>true = 通道/从站树；false = 从站列表（扁平，默认）</summary>
        public bool IsTreeView
        {
            get => _sidebarTreeView;
            private set
            {
                if (!SetField(ref _sidebarTreeView, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(IsFlatView));
                OnPropertyChanged(nameof(SidebarViewSwitchText));
                OnPropertyChanged(nameof(SidebarViewToolTip));
                OnPropertyChanged(nameof(FlatSlaveCount));
            }
        }

        /// <summary>扁平视图（绑定 IsVisible 用，比取反表达式好读）</summary>
        public bool IsFlatView => !_sidebarTreeView;

        /// <summary>扁平列表的从站总数</summary>
        public int FlatSlaveCount => SlaveRows.Count;

        /// <summary>一台从站都没有（扁平列表显示引导文案用）</summary>
        public bool HasNoSlaves => SlaveRows.Count == 0;

        /// <summary>
        /// 侧栏底部那颗长条按钮上的字。
        ///
        /// 写"切换到 X"而不是"当前是 X"：这颗按钮是**动作**，
        /// 写当前状态会让人以为是个标签、不去点；写目标状态则一眼知道点了会变成什么。
        /// </summary>
        public string SidebarViewSwitchText => _sidebarTreeView ? "切换到 从站列表" : "切换到 通道 / 从站";

        public string SidebarViewToolTip => _sidebarTreeView
            ? "侧边栏当前按「通道 / 从站」分开显示：一个通道下可以挂多个从站。点击改为只显示从站列表。"
            : "侧边栏当前只显示从站列表：新建从站时会自动配一条通道。点击改为按「通道 / 从站」分开显示。";

        public ICommand ToggleSidebarViewCommand { get; }

        /// <summary>
        /// 切视图。<paramref name="persist"/> = true 时写进设置（侧边栏按钮、设置页都传 true）。
        /// 只有一个来源写盘，才不会出现"按钮改了设置没改"这种半状态。
        /// </summary>
        public void SetSidebarView(bool tree, bool persist)
        {
            IsTreeView = tree;

            if (!persist)
            {
                return;
            }

            AppSettingsStore.Current.SidebarTreeView = tree;
            AppSettingsStore.Save();
            AppSettingsStore.NotifyChanged();
        }

        /// <summary>扁平列表的选中行；选中即为"选中这台从站 + 它所在通道"</summary>
        public SlaveRowViewModel SelectedSlaveRow
        {
            get => _selectedSlaveRow;
            set
            {
                if (!SetField(ref _selectedSlaveRow, value) || value == null || _syncingSlaveSelection)
                {
                    return;
                }

                _syncingSlaveSelection = true;
                try
                {
                    // 顺序不能反：SelectedConnection 的 setter 会把 SelectedSlave 重置成
                    // 该通道下的第一个从站，先选通道再选从站才对
                    SelectedConnection = value.Channel;
                    SelectedSlave = value.Slave;
                }
                finally
                {
                    _syncingSlaveSelection = false;
                }
            }
        }

        /// <summary>
        /// 重建扁平列表。集合结构变了就调它（通道/从站增删、载入工作区）。
        /// 保留"当前选中的是从站 X"这件事——重建后按 X 找回新行，而不是把选中清掉。
        /// </summary>
        private void RebuildSlaveRows()
        {
            foreach (SlaveRowViewModel row in SlaveRows)
            {
                row.Dispose();
            }

            SlaveRows.Clear();

            foreach (ConnectionViewModel connection in Connections)
            {
                foreach (SlaveViewModel slave in connection.Slaves)
                {
                    SlaveRows.Add(new SlaveRowViewModel(connection, slave));
                }
            }

            OnPropertyChanged(nameof(FlatSlaveCount));
            OnPropertyChanged(nameof(HasNoSlaves));
            SyncSlaveRowSelection();
        }

        /// <summary>
        /// 让"选中行"跟"选中从站"对上。
        /// 直接改字段不发 setter：setter 会反向去改 SelectedConnection/SelectedSlave，
        /// 而这里本来就是在响应它们的变化，绕回去就是死循环。
        /// </summary>
        private void SyncSlaveRowSelection()
        {
            SlaveRowViewModel match = _selectedSlave == null
                ? null
                : SlaveRows.FirstOrDefault(row => ReferenceEquals(row.Slave, _selectedSlave));

            if (ReferenceEquals(_selectedSlaveRow, match))
            {
                return;
            }

            _selectedSlaveRow = match;
            OnPropertyChanged(nameof(SelectedSlaveRow));
        }

        /// <summary>
        /// 设置 → 视图 里改了偏好：主界面立刻按新偏好重排。
        /// 不是"下次启动才生效"——用户改完切回来发现没变，只会以为没保存。
        /// </summary>
        public void ApplyViewPreferences()
        {
            SetSidebarView(AppSettingsStore.Current.SidebarTreeView, persist: false);
            SetPointsGroupByCategory(AppSettingsStore.Current.PointsGroupByCategory, persist: false);
        }

        /// <summary>
        /// ASDU 报文监视记录。
        ///
        /// 性能（第十一轮改）：原来是每条报文一次 Insert(0)，超上限再 RemoveAt 一次。
        /// 100 台从站一次总召就是上万帧，等于上万次"整体搬数组 + 两次集合事件"，
        /// 实测灌 1 万条要 1 秒以上——期间界面完全不动，就是用户说的"卡死"。
        /// 现在改成先入缓冲区、每 150 ms 批量合并一次（只发一次 Reset）。
        /// </summary>
        public RangeObservableCollection<AsduRecord> Records { get; } = new();

        /// <summary>链路日志</summary>
        public ObservableCollection<string> Logs { get; } = new();

        // ── 选中项 ──────────────────────────────────────────────

        public ConnectionViewModel SelectedConnection
        {
            get => _selectedConnection;
            set
            {
                if (SetField(ref _selectedConnection, value))
                {
                    // 切换连接时自动选中其第一个从站，避免右侧表格显示上一个从站的点表
                    SelectedSlave = value?.Slaves.FirstOrDefault();
                    RefreshCommandStates();
                }
            }
        }

        public SlaveViewModel SelectedSlave
        {
            get => _selectedSlave;
            set
            {
                // 从侧边栏点开一个从站 = "我要开始干活了"，欢迎页自动让位。
                // 放在 SetField 之前：点的正好是当前那个从站时 SetField 返回 false，
                // 但用户的意图仍然是"收起欢迎页"，不能因为"选中项没变"就什么都不做。
                if (value != null && _isWelcomeVisible)
                {
                    IsWelcomeVisible = false;
                }

                if (SetField(ref _selectedSlave, value))
                {
                    if (value != null && _selectedConnection != null
                        && !_selectedConnection.Slaves.Contains(value))
                    {
                        _selectedConnection = Connections.FirstOrDefault(c => c.Slaves.Contains(value));
                        OnPropertyChanged(nameof(SelectedConnection));
                    }
                    SelectedPoint = null;
                    OnPropertyChanged(nameof(Points));
                    RebuildPointTabs();
                    RefreshCommandStates();
                    // 树形视图里点了一个从站，扁平列表要是正开着（IsVisible 只是隐藏，
                    // 选中项还在），两边的选中得是同一个
                    SyncSlaveRowSelection();
                }
            }
        }

        public PointViewModel SelectedPoint
        {
            get => _selectedPoint;
            set
            {
                if (SetField(ref _selectedPoint, value))
                {
                    RefreshCommandStates();
                }
            }
        }

        /// <summary>当前从站的点表，供表格绑定</summary>
        public ObservableCollection<PointViewModel> Points => _selectedSlave?.Points;

        // ── 信号区标签 ──────────────────────────────────────────

        /// <summary>信号区标签：第一个"全部"，其余按 ASDU 类型分组</summary>
        public ObservableCollection<PointTabViewModel> PointTabs { get; } = new();

        /// <summary>当前选中的标签</summary>
        public PointTabViewModel SelectedPointTab
        {
            get => _selectedPointTab;
            set
            {
                if (SetField(ref _selectedPointTab, value))
                {
                    SelectedPoint = null;
                    OnPropertyChanged(nameof(PointsInSelectedTab));
                }
            }
        }

        /// <summary>当前标签下的点位（表格真正绑定的集合）</summary>
        public ObservableCollection<PointViewModel> PointsInSelectedTab => _selectedPointTab?.Points;

        /// <summary>
        /// 点表标签的分组方式：false = 按 ASDU 类型（默认），true = 按电力行业四大类。
        ///
        /// 两种分法解决两种问法：
        ///   · "这条总召里回了几个 M_ME_NC_1？" —— 按类型分，和报文里的 TypeID 一一对得上；
        ///   · "这个站有多少遥测、多少遥信？"   —— 按大类分，和调度侧的口径对得上。
        /// 两种都有用，所以做成一个切换，而不是二选一写死。
        /// </summary>
        public bool GroupPointsByCategory
        {
            get => _groupPointsByCategory;
            private set
            {
                if (SetField(ref _groupPointsByCategory, value))
                {
                    OnPropertyChanged(nameof(PointGroupModeCaption));
                    OnPropertyChanged(nameof(PointGroupModeToolTip));
                    RebuildPointTabs();
                }
            }
        }

        /// <summary>
        /// 改分组方式。<paramref name="persist"/> = true 时写进设置——
        /// 主界面那颗切换按钮和设置 → 视图里的选项都传 true，
        /// 两边写的是同一个字段，用户在哪儿改都算数，下次启动也还是这个样子。
        /// </summary>
        public void SetPointsGroupByCategory(bool byCategory, bool persist)
        {
            GroupPointsByCategory = byCategory;

            if (!persist)
            {
                return;
            }

            AppSettingsStore.Current.PointsGroupByCategory = byCategory;
            AppSettingsStore.Save();
            AppSettingsStore.NotifyChanged();
        }

        /// <summary>切换按钮上显示"当前是哪一种"（按钮表示状态，不是动作）</summary>
        public string PointGroupModeCaption => _groupPointsByCategory ? "电力大类" : "ASDU 类型";

        /// <summary>切换按钮的悬停提示：说清当前是什么、点一下会变成什么</summary>
        public string PointGroupModeToolTip =>
            _groupPointsByCategory
                ? "标签当前按电力大类分组：遥测 / 遥信 / 遥控 / 遥脉。点击改为按 ASDU 类型分组。"
                : "标签当前按 ASDU 类型分组：单点信息 / 双点信息 / 测量值…。点击改为按电力大类分组。";

        public ICommand TogglePointGroupModeCommand { get; }

        /// <summary>
        /// 重建标签页。
        ///
        /// 做两件事：按类型分组生成标签；把"点表集合变化"接到重建上，
        /// 否则用批量新增铺完点后标签数量还是旧的。
        /// 重建时尽量保留用户当前所在的类型页，避免每次加个点就跳回"全部"。
        /// </summary>
        private void RebuildPointTabs()
        {
            string keepCode = _selectedPointTab?.IsAll == false ? _selectedPointTab.Code : null;

            if (_tabSourcePoints != null)
            {
                _tabSourcePoints.CollectionChanged -= OnPointCollectionChanged;
                _tabSourcePoints = null;
            }

            PointTabs.Clear();

            if (_selectedSlave == null)
            {
                SelectedPointTab = null;
                return;
            }

            ObservableCollection<PointViewModel> source = _selectedSlave.Points;
            PointTabs.Add(new PointTabViewModel("全部", string.Empty, "全部", source, isAll: true));

            if (_groupPointsByCategory)
            {
                BuildCategoryTabs(source);
            }
            else
            {
                BuildTypeTabs(source);
            }

            _tabSourcePoints = source;
            _tabSourcePoints.CollectionChanged += OnPointCollectionChanged;

            SelectedPointTab = keepCode == null
                ? PointTabs[0]
                : PointTabs.FirstOrDefault(t => t.Code == keepCode) ?? PointTabs[0];
        }

        /// <summary>
        /// 按 ASDU 类型出标签。
        /// 顺序按类型枚举值从小到大排（M_SP_NA_1=1、M_DP_NA_1=3、M_ME_NA_1=9…）。
        /// 早先按规约代号字符串排，M_DP/M_IT/M_ME 的字典序和规约编号对不上，
        /// 标签顺序看着是乱的——现场对照报文时"第几个类型"应当和 TypeID 一致。
        /// </summary>
        private void BuildTypeTabs(ObservableCollection<PointViewModel> source)
        {
            var groups = source
                .Where(p => p.Type != null)
                .GroupBy(p => p.Type.Type)
                .OrderBy(g => (int)g.Key)
                .ToList();

            foreach (var group in groups)
            {
                TypeDescriptor descriptor = TypeIdCatalog.Get(group.Key);
                PointTabs.Add(new PointTabViewModel(
                    descriptor.DisplayName,
                    descriptor.Code,
                    descriptor.Category,
                    new ObservableCollection<PointViewModel>(group),
                    typeValue: (int)group.Key));
            }
        }

        /// <summary>
        /// 按电力行业四大类出标签（遥测 / 遥信 / 遥控 / 遥脉）。
        ///
        /// 三个刻意的规矩：
        ///   ① 大类顺序固定，不跟着数据出现先后跑；
        ///   ② **四大类恒定出现**，哪怕某一类眼下是 0 个点——
        ///      这四个名字是行业口径，标签栏把它摆全了，用户一眼能确认"这套点表覆盖了哪几类"，
        ///      也免得今天有遥控点、明天没有，标签位置来回跳。
        ///      点表只存监视方向的信息对象，遥控类命令（C_SC/C_DC/C_SE…）由主站下发，
        ///      所以"遥控"这一栏通常只有档位（步位置）这类可监视的状态量，甚至是空的——
        ///      这是事实，界面照实摆出来，不藏。
        ///   ③ **同一标签内按点号升序**——按类型分组时同类型的点本来就是建站时顺序铺的，
        ///      按大类合并之后，遥测里会混进 4001、4003 和 9xx 的点，不排序就是乱的。
        /// "其他"是保留桶（万一将来加到没登记的类型），不属于行业口径，有才出现。
        /// </summary>
        private void BuildCategoryTabs(ObservableCollection<PointViewModel> source)
        {
            var buckets = source
                .Where(p => p.Type != null)
                .GroupBy(p => TypeIdCatalog.BroadCategoryOf(p.Type.Type))
                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.ObjectAddress).ToList());

            List<string> order =
                new(TypeIdCatalog.BroadCategories) { "其他" };

            // 一个点都没有的新工程：只留"全部"，不出四张空标签
            bool hasAnyPoint = buckets.Values.Any(items => items.Count > 0);

            foreach (string category in order)
            {
                bool isBroad = TypeIdCatalog.BroadCategories.Contains(category);
                if (!isBroad && !buckets.ContainsKey(category))
                {
                    continue;
                }

                if (!hasAnyPoint && !isBroad)
                {
                    continue;
                }

                if (!buckets.TryGetValue(category, out List<PointViewModel> items))
                {
                    items = new List<PointViewModel>();
                }

                string note = items.Count > 0
                    ? $"电力大类「{category}」· {items.Count} 个（按点号升序）· 含 "
                      + string.Join(" / ", items.Select(p => p.Type.Code).Distinct())
                    : $"电力大类「{category}」· 本点表暂时没有这一类。"
                      + (category == "遥控"
                          ? "这一类收的是档位（步位置）等可监视的状态量；遥控与设点命令由主站下发，不占点表。"
                          : string.Empty);

                PointTabs.Add(new PointTabViewModel(
                    category,
                    category,
                    category,
                    new ObservableCollection<PointViewModel>(items),
                    note: note));
            }
        }

        private void OnPointCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            // 点位增删了：类型分组可能变化，整体重建最省事（点位数量级通常不大）
            RebuildPointTabs();
        }

        private ObservableCollection<PointViewModel> _tabSourcePoints;
        private PointTabViewModel _selectedPointTab;
        private bool _groupPointsByCategory;

        /// <summary>true = 主界面显示设置界面（工作区被盖住但状态不变）</summary>
        private bool _isSettingsOpen;

        // ── 状态 ────────────────────────────────────────────────

        public string StatusText
        {
            get => _statusText;
            private set => SetField(ref _statusText, value);
        }

        /// <summary>窗口置顶（对应原程序 AlwaysOnTopCommand + AppSettings.AlwaysOnTop）</summary>
        public bool AlwaysOnTop
        {
            get => _alwaysOnTop;
            set
            {
                if (SetField(ref _alwaysOnTop, value))
                {
                    OnPropertyChanged(nameof(PinButtonToolTip));
                }
            }
        }

        /// <summary>
        /// 标题栏钉子按钮的悬停提示。
        /// 说清"现在是什么状态、点一下会变成什么"——这个按钮是开关型，
        /// 只写"窗口置顶"的话，用户看不出当前到底固定在顶层没有。
        /// </summary>
        public string PinButtonToolTip => _alwaysOnTop
            ? "已固定在最上层（再点一下取消）"
            : "固定窗口在最上层：切到别的程序时本窗口仍压在最前面";

        /// <summary>
        /// 底部监视面板是否展开。false = 只留一条标题带，把高度让给点表。
        ///
        /// 放在 ViewModel 而不是纯视图状态，是因为三个入口要写同一个值：
        /// 面板右上角的收起按钮、视图菜单里的勾选项、以及自检脚本。
        /// 三者各存一份就会互相打架（按钮收起了、菜单还是勾着）。
        /// </summary>
        public bool IsMonitorExpanded
        {
            get => _isMonitorExpanded;
            set
            {
                if (SetField(ref _isMonitorExpanded, value))
                {
                    OnPropertyChanged(nameof(MonitorToggleTip));
                }
            }
        }

        /// <summary>收起 / 展开按钮的悬停提示</summary>
        public string MonitorToggleTip => _isMonitorExpanded
            ? "收起监视面板，把高度让给点表（再次点击展开）"
            : "展开监视面板（报文监视 / 链路日志）";

        /// <summary>模拟数据变化周期（毫秒）</summary>
        public int ChangeIntervalMilliseconds
        {
            get => _changeIntervalMilliseconds;
            set
            {
                if (SetField(ref _changeIntervalMilliseconds, Math.Max(500, value)))
                {
                    AutoChange?.SetInterval(_changeIntervalMilliseconds);
                }
            }
        }

        public string RunningSummary
        {
            get
            {
                int open = Connections.Count(c => c.IsOpen);
                int total = Connections.Sum(c => c.Slaves.Count);
                return $"通道 {Connections.Count} 个（监听 {open}）· 从站 {total} 个";
            }
        }

        // ── 命令 ────────────────────────────────────────────────

        public ICommand CreateConnectionCommand { get; }
        public ICommand EditConnectionCommand { get; }
        public ICommand DeleteConnectionCommand { get; }
        public ICommand OpenConnectionCommand { get; }
        public ICommand OpenAllCommand { get; }
        public ICommand CloseAllCommand { get; }

        public ICommand ToggleSelectedConnectionCommand { get; }
        public ICommand ToggleAllConnectionsCommand { get; }

        /// <summary>选中的通道是否已启动（决定工具栏那颗按钮显示"启动"还是"停止"）</summary>
        public bool SelectedConnectionIsOpen => SelectedConnection?.IsOpen == true;

        public string SelectedConnectionToggleText => SelectedConnectionIsOpen ? "停止" : "启动";

        public string SelectedConnectionToggleTip => SelectedConnectionIsOpen ? "停止当前通道" : "启动当前通道";

        /// <summary>是否有任意通道在运行（决定"全部启停"按钮的方向）</summary>
        public bool AnyConnectionOpen => Connections.Any(c => c.IsOpen);

        public string AllConnectionToggleText => AnyConnectionOpen ? "全部停止" : "全部启动";

        public string AllConnectionToggleTip => AnyConnectionOpen ? "停止全部通道" : "启动全部通道";

        /// <summary>
        /// "启停合一"按钮依赖的所有派生属性。
        /// 这五个必须一起通知：只刷文本不刷样式类的话，按钮会显示"停止"却还是绿色的启动样式。
        /// </summary>
        private void NotifyToggleState()
        {
            OnPropertyChanged(nameof(SelectedConnectionIsOpen));
            OnPropertyChanged(nameof(SelectedConnectionToggleText));
            OnPropertyChanged(nameof(SelectedConnectionToggleTip));
            OnPropertyChanged(nameof(AnyConnectionOpen));
            OnPropertyChanged(nameof(AllConnectionToggleText));
            OnPropertyChanged(nameof(AllConnectionToggleTip));
            NotifyHealth();
        }

        // ── 运行健康度（状态栏那颗彩色胶囊）─────────────────────
        //
        // 状态栏原来全是灰字，用户"一眼判断不出当前服务好坏"。
        // 这里把"整机处于什么状态"归纳成四档，颜色沿用通道状态灯那一套语义：
        //   无通道     → 灰（什么都还没配）
        //   已停止     → 灰（配了但没跑，中性，不报警）
        //   监听中     → 橙（端口在听，但还没有主站接入 —— 联调时最常卡在这一步）
        //   主站已接入 → 绿（真的通了）
        // 只要有一条通道接入了主站就算绿：现场通常只关心"有没有通"。

        public string HealthText => Connections.Count == 0
            ? "无通道"
            : Connections.Any(c => c.State == ConnectionViewModel.ChannelState.Connected) ? "主站已接入"
            : Connections.Any(c => c.State == ConnectionViewModel.ChannelState.Listening) ? "监听中"
            : "已停止";

        /// <summary>圆点色</summary>
        public IBrush HealthBrush => HealthText switch
        {
            "主站已接入" => ThemePalette.StateConnected,
            "监听中" => ThemePalette.StateListening,
            _ => ThemePalette.StateStopped
        };

        /// <summary>胶囊底色（用浅色底，避免整条状态栏被大色块压住）</summary>
        public IBrush HealthSoftBrush => HealthText switch
        {
            "主站已接入" => ThemePalette.Get("SuccessSoftBrush"),
            "监听中" => ThemePalette.Get("WarningSoftBrush"),
            _ => ThemePalette.Get("DividerBrush")
        };

        /// <summary>胶囊里的文字色（与底色成对，保证对比度）</summary>
        public IBrush HealthForeground => HealthText switch
        {
            "主站已接入" => ThemePalette.Get("SuccessTextBrush"),
            "监听中" => ThemePalette.Get("WarningTextBrush"),
            _ => ThemePalette.Get("TextSecondaryBrush")
        };

        private void NotifyHealth()
        {
            OnPropertyChanged(nameof(HealthText));
            OnPropertyChanged(nameof(HealthBrush));
            OnPropertyChanged(nameof(HealthSoftBrush));
            OnPropertyChanged(nameof(HealthForeground));
        }

        // ── 主题 ────────────────────────────────────────────────

        public string CurrentThemeName => ThemeService.Current?.Name ?? string.Empty;

        public bool IsDarkTheme => ThemeService.Current?.IsDark == true;

        /// <summary>主题切换（界面上的浮层菜单调它）</summary>
        public void ApplyTheme(string themeId) => ThemeService.Apply(themeId);

        /// <summary>
        /// 主题变了之后，样式里的 DynamicResource 会自己重绘，
        /// 但 ViewModel 里按状态算出来的画刷（值着色、品质徽标、状态灯）不会，
        /// 得挨个通知一遍。点位数量可能上千，这里直接在 UI 线程同步跑完，
        /// 因为切主题本来就是低频操作。
        /// </summary>
        private void OnThemeChanged()
        {
            foreach (ConnectionViewModel connection in Connections)
            {
                connection.RefreshThemeVisuals();

                foreach (SlaveViewModel slave in connection.Slaves)
                {
                    foreach (PointViewModel point in slave.Points)
                    {
                        point.RefreshThemeVisuals();
                    }
                }
            }

            OnPropertyChanged(nameof(CurrentThemeName));
            OnPropertyChanged(nameof(IsDarkTheme));

            // 扁平列表的标签底色/描边也是按令牌取的缓存值
            foreach (SlaveRowViewModel row in SlaveRows)
            {
                row.RaiseVisuals();
            }

            // 健康度胶囊的三个画刷也是按令牌取的缓存值，换主题后要重取
            NotifyHealth();
        }

        public ICommand CreateSlaveCommand { get; }
        public ICommand EditSlaveCommand { get; }
        public ICommand CopySlaveCommand { get; }
        public ICommand DeleteSlaveCommand { get; }

        public ICommand AddPointCommand { get; }
        public ICommand EditPointCommand { get; }
        public ICommand DeletePointCommand { get; }
        public ICommand ExportPointsCommand { get; }

        public ICommand ClearRecordsCommand { get; }

        // ── 工程命令（1.10）────────────────────────────────────

        /// <summary>保存到当前工程文件（Ctrl+S 与工具条那颗按钮都走它）</summary>
        public ICommand SaveProjectCommand { get; }

        /// <summary>关闭工程（回欢迎页）。未保存的处理在视图层，见 ShowCloseProjectRequest</summary>
        public ICommand CloseProjectCommand { get; }

        /// <summary>按初始值复位当前从站（或选中的那个点）</summary>
        public ICommand ResetToInitValueCommand { get; }

        /// <summary>收起欢迎页（「继续编辑」）</summary>
        public ICommand DismissWelcomeCommand { get; }

        // ── 批量变化（1.10.2：会话上提到这里，关窗不停）──────────

        /// <summary>
        /// 批量变化会话。归主窗口所有，**不归批量变化窗口**——
        /// 关掉参数界面后数据要接着变（用户要求），所以驱动器不能跟着窗口释放。
        /// </summary>
        public BatchChangeSession BatchChange { get; }

        /// <summary>正在批量变化：工具条提示点、状态栏那一段、停止按钮都看这个</summary>
        public bool IsBatchChanging => BatchChange.IsRunning;

        /// <summary>停止批量变化（工具条停止按钮用；窗口里那颗按钮走它自己的命令）</summary>
        public ICommand StopBatchChangeCommand { get; }

        /// <summary>状态栏里那一段提示：谁在变、第几轮、节拍多少</summary>
        public string BatchChangeStatusText
        {
            get
            {
                if (!IsBatchChanging)
                {
                    return string.Empty;
                }

                string slave = string.IsNullOrEmpty(BatchChange.TargetSlaveName)
                    ? string.Empty
                    : BatchChange.TargetSlaveName + " · ";
                string size = BatchChange.AllPoints
                    ? "全部点"
                    : $"{BatchChange.BatchSize} 点";
                // 第一轮还没跑完时显示"刚启动"：写"第 0 轮"看着像没起来
                string round = BatchChange.RoundCount == 0
                    ? "刚启动"
                    : $"第 {BatchChange.RoundCount} 轮";
                return $"批量变化中 · {slave}{round} · {size}/{BatchChange.IntervalMs} ms";
            }
        }

        /// <summary>批量变化按钮的悬停提示：跑着的时候把参数和轮次说清</summary>
        public string BatchChangeTip => IsBatchChanging
            ? $"正在批量变化：每 {BatchChange.IntervalMs} ms 变 " +
              $"{(BatchChange.AllPoints ? "全部" : BatchChange.BatchSize.ToString())} 个点，" +
              $"已 {BatchChange.RoundCount} 轮、累计 {BatchChange.ChangedCount} 次。" +
              "参数窗口可以关掉，变化不会停；点右边的方块停止。"
            : "批量变化数据点（随机范围 / 固定步长）";


        // ── 欢迎页（1.10）──────────────────────────────────────

        /// <summary>最近使用的工程，最多 5 条，最新的在最前</summary>
        public ObservableCollection<RecentProjectEntry> RecentProjects { get; } = new();

        public bool HasRecentProjects => RecentProjects.Count > 0;

        public bool HasNoRecentProjects => RecentProjects.Count == 0;

        /// <summary>欢迎页的大标题：已经恢复过工程就说"当前工程"</summary>
        public string WelcomeTitle => HasProject ? "当前工程" : "还没有打开工程";

        /// <summary>欢迎页的说明文字，两种状态各一句</summary>
        public string WelcomeSubtitle => HasProject
            ? "上次的工程已经自动恢复，可以直接继续编辑。"
            : "新建一个工程，或者打开已有的工程开始。";

        /// <summary>欢迎页上显示的当前工程路径（没有工程时为空）</summary>
        public string WelcomeProjectPath => _currentProjectPath ?? string.Empty;

        /// <summary>
        /// 视图层实现"关闭工程"的完整流程（含未保存提示）。
        /// 放视图层是因为它要决定"弹不弹框、弹哪个"，VM 只负责发指令。
        /// </summary>

        public Action ShowCloseProjectRequest { get; set; }

        /// <summary>通用确认框（标题、正文、回调）。视图层注入，避免 VM 依赖 Window</summary>
        public Action<string, string, Action<bool>> ShowConfirmDialog { get; set; }

        /// <summary>批量创建从站（IP 递增/按步长）</summary>
        public ICommand BatchCreateSlaveCommand { get; }

        /// <summary>打开"批量创建从站"窗口（由视图层实现）</summary>
        public Action ShowBatchCreateSlaveDialog { get; set; }

        /// <summary>批量创建完成后刷新选中项与状态栏</summary>
        public void NotifyBatchCreated(int count)
        {
            if (count <= 0)
            {
                return;
            }

            // 选中最后一条新建的，用户能立刻看到结果
            ConnectionViewModel last = Connections.LastOrDefault();
            if (last != null)
            {
                SelectedConnection = last;
                SelectedSlave = last.Slaves.LastOrDefault();
            }

            StatusText = $"已批量创建 {count} 个从站（各自独占一条通道），点“全部启动”即可开始监听";
        }

        /// <summary>打开缓变（自动变化）配置窗口</summary>
        public ICommand ShowAutoChangeCommand { get; }

        // ── 设置 ────────────────────────────────────────────────

        /// <summary>
        /// 设置界面的视图模型（左侧标签栏 + 右侧内容）。
        ///
        /// 为什么放在主窗口里"就地切换"，而不是另开一个设置窗口：
        /// 设置里改的东西（ASDU 类型、报文缓冲上限）全都作用在主界面上，
        /// 开个模态窗改完再关，用户还得回来确认改对没有；就地切换可以随手切回工作区看效果。
        /// </summary>
        public SettingsViewModel Settings { get; } = new();

        /// <summary>true = 主界面正在显示设置（工作区被盖住）</summary>
        public bool IsSettingsOpen
        {
            get => _isSettingsOpen;
            private set
            {
                if (SetField(ref _isSettingsOpen, value))
                {
                    OnPropertyChanged(nameof(SettingsButtonToolTip));
                }
            }
        }

        /// <summary>标题栏那颗按钮：点一下进设置，再点一下回工作区</summary>
        public ICommand ToggleSettingsCommand { get; }

        public ICommand CloseSettingsCommand { get; }

        public string SettingsButtonToolTip => _isSettingsOpen
            ? "返回工作区（通道 / 从站 / 点表）"
            : "设置：ASDU 类型、报文监视、存储位置";

        /// <summary>
        /// 统计某个 ASDU 类型被多少个信息点使用。
        /// 设置页删除自定义类型前要问它——点还挂在那个类型上时删掉，
        /// 那些点会静默退化成"代号当名字、分类归到其他"。
        /// </summary>
        public int CountPointsUsing(TypeIdentifier type)
        {
            int count = 0;
            foreach (ConnectionViewModel connection in Connections)
            {
                foreach (SlaveViewModel slave in connection.Slaves)
                {
                    foreach (PointViewModel point in slave.Points)
                    {
                        if (point.Type != null && point.Type.Type == type)
                        {
                            count++;
                        }
                    }
                }
            }
            return count;
        }

        // ── 视图注入的交互委托 ────────────────────────────────
        // 模态对话框是异步的，无法同步返回结果，故这里用 "对象 + 完成回调" 的形式。
        // 好处：ViewModel 仍然不依赖 Avalonia 的 Window 类型。

        /// <summary>编辑连接：参数为草稿对象与完成回调（true = 确认）</summary>
        public Action<ConnectionViewModel, Action<bool>> EditConnectionRequested { get; set; }

        /// <summary>编辑从站</summary>
        public Action<SlaveViewModel, Action<bool>> EditSlaveRequested { get; set; }

        /// <summary>
        /// 本次"新建从站"的草稿模型（含通道选择）。
        /// 视图层在弹窗时优先拿它当 DataContext，这样用户能在对话框里决定
        /// "挂到哪个通道"甚至"现建一个通道"，不必先手工建通道再建从站。
        /// 编辑已有从站时为 null，视图层按老逻辑构造只读通道的模型。
        /// </summary>
        public SlaveEditModel PendingSlaveEdit => _pendingSlaveEdit;

        private SlaveEditModel _pendingSlaveEdit;

        /// <summary>编辑信息对象</summary>
        public Action<PointViewModel, SlaveViewModel, Action<bool>> EditPointRequested { get; set; }

        // 原来这里有 ShowAboutDialog（弹"关于"窗口）。
        // 1.9.1 起"关于"改成设置里的一页，不再有独立窗口，这个委托一并删掉——
        // 留着它就是一条永远没人调用的入口。

        /// <summary>显示报文解析窗口，参数为 "104" 或 "101"</summary>
        public Action<string> ShowFrameParserDialog { get; set; }

        /// <summary>显示冗余校验窗口</summary>
        public Action ShowRedundancyCheckDialog { get; set; }

        /// <summary>显示字符串编解码窗口</summary>
        public Action ShowStringCodecDialog { get; set; }

        /// <summary>显示提示消息（标题, 内容）</summary>
        public Action<string, string> ShowMessage { get; set; }

        /// <summary>显示缓变配置窗口</summary>
        public Action ShowAutoChangeDialog { get; set; }

        // ── 工程（1.10：原"工作区"）────────────────────────────

        /// <summary>当前工程文件路径；null = 还没打开工程（此时欢迎页显示 State A）</summary>
        public string CurrentProjectPath => _currentProjectPath;

        /// <summary>工程显示名。没有工程时返回应用名，让标题栏不至于空着</summary>
        public string CurrentProjectName => _currentProjectName;

        public bool HasProject => !string.IsNullOrEmpty(_currentProjectPath);

        /// <summary>结构脏：点表定义改了，值得打断用户</summary>
        public bool IsProjectDirty => _isProjectDirty;

        /// <summary>值脏：只有运行值变了，退出时静默保存</summary>
        public bool IsValueDirty => _isValueDirty;

        /// <summary>正在写盘（大工程会有几百毫秒），期间保存按钮禁用</summary>
        public bool IsSaving => _isSaving;

        /// <summary>欢迎页是否盖在主区域上</summary>
        public bool IsWelcomeVisible
        {
            get => _isWelcomeVisible;
            set
            {
                if (SetField(ref _isWelcomeVisible, value))
                {
                    OnPropertyChanged(nameof(IsEditorVisible));

                    // 每次露出欢迎页都重读一遍最近列表：用户可能刚从菜单里存过新文件
                    if (value)
                    {
                        RefreshRecentProjects();
                    }
                }
            }
        }

        /// <summary>编辑区可见 = 欢迎页没盖着</summary>
        public bool IsEditorVisible => !_isWelcomeVisible;

        /// <summary>
        /// 保存按钮能不能点。
        /// **值脏也算能点**：用户可能就想把当前值立刻落盘，
        /// 不该因为"结构没改"就灰着——那会让人觉得"我改了值为什么存不了"。
        /// </summary>
        /// 注意：**不要求已经有工程文件**。用户可以在欢迎页直接建从站、加点，
        /// 这时候还没有路径（未命名工程），但改动是真实存在的，
        /// Ctrl+S 应当直接转成"另存为"（见视图层 SaveProjectAsync），
        /// 按钮不该因为"还没选存放位置"就灰着——那会让用户以为改动不算数。
        public bool CanSaveProject => !_isSaving && (_isProjectDirty || _isValueDirty);

        /// <summary>
        /// "没有工程文件时另存为"的出口，由视图层注入（ViewModel 不能弹文件对话框）。
        ///
        /// 【为什么要有这个回调】1.10.1 之前工具条和菜单的「保存」都直接绑
        /// `SaveProjectCommand` → `SaveProject()`，而后者在 `!HasProject` 时
        /// **静默返回 false**：点了保存什么也不发生。偏偏 Ctrl+S 走的是另一条路
        /// （视图层 `SaveProjectAsync`，会转 `SaveProjectAsAsync` 弹框），
        /// 于是出现"Ctrl+S 能存、点按钮没反应"这种最难解释的现象。
        /// 现在两条路径合到一处：命令只管"有没有工程"，弹框交给视图。
        /// </summary>
        public Func<System.Threading.Tasks.Task<bool>> SaveAsFallback { get; set; }

        /// <summary>
        /// 「保存」命令的执行体（工具条按钮 / 文件菜单 / Ctrl+S 共用同一套语义）。
        /// </summary>
        private async void ExecuteSaveCommand()
        {
            if (HasProject)
            {
                try
                {
                    SaveProject();
                }
                catch (Exception exception)
                {
                    StatusText = $"保存工程失败：{exception.Message}";
                }
                return;
            }

            if (SaveAsFallback != null)
            {
                try
                {
                    await SaveAsFallback();
                }
                catch (Exception exception)
                {
                    StatusText = $"保存工程失败：{exception.Message}";
                }
                return;
            }

            // 没接上视图（理论上只有自检里才会发生）：至少说清为什么没存。
            StatusText = "当前还没有工程文件，请用「文件 → 另存为」指定保存位置";
        }

        /// <summary>保存按钮的悬停提示，带工程名（用户要能确认"存到哪个文件"）</summary>
        public string SaveProjectTip
        {
            get
            {
                if (!HasProject)
                {
                    return CanSaveProject
                        ? "保存到文件…（Ctrl+S，会先让你选存放位置）"
                        : "还没有打开工程";
                }

                if (_isSaving)
                {
                    return "正在保存…";
                }

                if (!CanSaveProject)
                {
                    return "没有需要保存的改动（Ctrl+S）";
                }

                return $"保存到 {_currentProjectName}{ProjectStore.Extension}（Ctrl+S）";
            }
        }

        /// <summary>
        /// 标题栏文字：有工程时带工程名，结构脏时前面加 ●。
        ///
        /// 为什么值脏不加 ●：仿真时值一直在变，标题上永远挂个点会让用户以为存不完。
        /// 值反正会自己落盘（退出时必写），不需要用户操心。
        /// </summary>
        public string WindowTitle
        {
            get
            {
                // 没有工程文件、但已经改了东西（未命名工程）：标题照样要挂 ●，
                // 否则用户看不出"这些改动还没有落脚的地方"
                string name = string.IsNullOrWhiteSpace(_currentProjectName)
                    ? ProjectStore.DefaultName
                    : _currentProjectName;

                // 值脏不进这个判断：和状态栏那颗指示器保持一致——只看结构脏。
                // 否则会出现"标题写着未命名工程、状态栏那格却不显示"的半截状态。
                if (!HasProject && !_isProjectDirty)
                {
                    return AppInfo.ProductName;
                }

                return _isProjectDirty
                    ? $"● {name} - {AppInfo.ProductName}"
                    : $"{name} - {AppInfo.ProductName}";
            }
        }

        /// <summary>
        /// 状态栏要不要显示"保存状态"。
        /// 有工程文件、或者结构上有改动（含"未命名工程"）都算——后者恰恰更需要提示，
        /// 因为它连落脚的文件都还没有。
        ///
        /// **值脏（只有信号值变了）不算**，这是刻意的：值脏每秒可能来几十次（40 万点跑起来时
        /// 一直有人在动），让它点亮这格等于让指示器全程闪"未保存"，反而没人看。
        /// 值本身是静默的——退出时必写，批次 C 的防抖也会中途落盘。
        /// 想知道"值有没有存"，看悬停提示。
        /// </summary>
        public bool ShowSaveState => HasProject || _isProjectDirty;

        /// <summary>状态栏右侧的保存状态文字</summary>
        public string SaveStateText
        {
            get
            {
                if (!ShowSaveState)
                {
                    return string.Empty;
                }

                if (_isSaving)
                {
                    return "正在保存…";
                }

                if (_isProjectDirty)
                {
                    return "未保存";
                }

                return _lastSavedAt.HasValue
                    ? $"已保存 {_lastSavedAt.Value:HH:mm}"
                    : "已保存";
            }
        }

        /// <summary>
        /// 「已保存」态：显示对勾。
        /// 写盘中不算（那一刻脏标记可能还没清，会同时亮出两个形状）。
        /// </summary>
        public bool IsSaveIdle => ShowSaveState && !_isSaving && !_isProjectDirty;

        /// <summary>状态栏那颗指示器的悬停提示：写明"存到哪个文件、什么时候存的"</summary>
        public string SaveStateTip
        {
            get
            {
                if (_isSaving)
                {
                    return "正在写入工程文件…";
                }

                if (!HasProject)
                {
                    return "这些改动还没有存成文件。按 Ctrl+S 会先让你选存放位置。";
                }

                if (_isProjectDirty)
                {
                    return $"点表有改动还没保存（Ctrl+S 保存到 {_currentProjectName}{ProjectStore.Extension}）";
                }

                // 值脏不点亮指示器（见 ShowSaveState 的说明），但也不能假装值已经存了。
                // 只在这句悬停提示里说清楚——不占视觉，问到才说。
                string suffix = _isValueDirty
                    ? $"\n信号值有变化，停手 {AppSettingsStore.Current.ValueSaveDelaySeconds} 秒后自动写入"
                      + "（退出时也会写一次）"
                    : string.Empty;

                return _lastSavedAt.HasValue
                    ? $"已保存到 {_currentProjectPath}\n{_lastSavedAt.Value:yyyy-MM-dd HH:mm:ss}{suffix}"
                    : $"已保存到 {_currentProjectPath}{suffix}";
            }
        }

        // ── 自动保存（1.10.1）──────────────────────────────────

        /// <summary>结构改动的防抖延迟：自检覆盖 > 设置 > 默认</summary>
        private int StructureDelayMs => DebounceOverrideMs
            ?? Math.Max(1, AppSettingsStore.Current.AutoSaveDelaySeconds) * 1000;

        /// <summary>信号值的防抖延迟</summary>
        private int ValueDelayMs => DebounceOverrideMs
            ?? Math.Max(1, AppSettingsStore.Current.ValueSaveDelaySeconds) * 1000;

        /// <summary>两次写盘之间的最小间隔</summary>
        private int MinGapMs => MinGapOverrideMs
            ?? Math.Max(0, AppSettingsStore.Current.MinSaveIntervalSeconds) * 1000;

        /// <summary>
        /// 结构改动了：能写就排队，不能写就什么都不做。
        ///
        /// 两种情况不排队：没有文件路径（写不了，排队只会排出一个失败）、
        /// 「自动保存」关着（用户明确不想在编辑过程中落盘，等他自己按 Ctrl+S）。
        /// </summary>
        private void ScheduleStructureSave()
        {
            if (!HasProject || !AppSettingsStore.Current.AutoSaveEnabled)
            {
                return;
            }

            _structureSave?.Bump();
        }

        /// <summary>
        /// 值变了：排队等停手。值这条没有开关——见字段上的说明。
        ///
        /// **刻意不设"最长 N 秒必写一次"的封顶**（用户 2026-10-29 定）：
        /// 缓变一直跑的时候值每秒都变，防抖永远等不到，那就全程不写，退出时写一次。
        /// 理由很直接——仿真过程中间那些值本来就是过程量，丢了不心疼；
        /// 为了它们每隔一分钟让界面卡 200 ms，反而更亏。
        /// 所以"变化过程中零写盘"是承诺，不是漏洞，别在这里加兜底。
        /// </summary>
        private void ScheduleValueSave()
        {
            if (!HasProject)
            {
                return;
            }

            _valueSave?.Bump();
        }

        /// <summary>
        /// 停掉两个调度器。打开 / 新建 / 关闭工程时用：
        /// 上一个工程欠着的那次写盘已经不属于新工程了，照写会把旧内容写进新文件。
        /// </summary>
        private void StopAutoSave()
        {
            _structureSave?.Stop();
            _valueSave?.Stop();
        }

        /// <summary>
        /// 批量变化会话状态变了：工具条提示点、状态栏那一段、停止按钮的可用性。
        ///
        /// 每轮都会调到这里（会话把每轮的完成事件转成 StateChanged），
        /// 所以状态栏的"第 N 轮"是活的——这也是关掉窗口之后，用户唯一能确认
        /// "它还在动"的地方，必须准。
        /// </summary>
        private void OnBatchChangeStateChanged()
        {
            OnPropertyChanged(nameof(IsBatchChanging));
            OnPropertyChanged(nameof(BatchChangeStatusText));
            OnPropertyChanged(nameof(BatchChangeTip));
            (StopBatchChangeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// 防抖到期后的静默写盘。
        ///
        /// **不碰状态栏左边那行操作日志**：值每几秒落一次盘，要是每次都写一句
        /// "工程已保存：xxx"，状态栏就成了刷屏日志，真正想看的提示全被冲走。
        /// 写盘这件事在右边那颗指示器上体现（"正在保存…" → "已保存 HH:mm"），够了。
        /// </summary>
        private bool AutoSaveQuietly()
        {
            if (!HasProject)
            {
                return false;
            }

            try
            {
                SaveProjectTo(_currentProjectPath, _currentProjectName, quiet: true);
                return true;
            }
            catch (Exception exception)
            {
                // 原因必须说出来：写不进去（磁盘满 / 只读 / 被别的进程占着）时，
                // 用户最需要知道的是"我改的东西现在还没落地"，而不是一个安静的界面。
                // 同时停表——每 5 秒往一个写不进去的磁盘上打一次，只会让界面定期卡一下。
                StatusText = $"自动保存失败：{exception.Message}";
                _structureSave?.Stop();
                _valueSave?.Stop();
                return false;
            }
        }

        /// <summary>
        /// 保存到当前工程文件。返回是否真的写了盘。
        /// 没有工程（或没有改动）时直接返回 false，让调用方决定要不要提示。
        /// </summary>
        public bool SaveProject()
        {
            if (!HasProject)
            {
                return false;
            }

            return SaveProjectTo(_currentProjectPath, _currentProjectName);
        }

        /// <summary>
        /// 保存到指定路径（另存为 / 新建工程都走这里）。
        /// 写盘是同步的：1.9.7 已经把序列化做成流式，100 万点约 258 ms，
        /// 这个量级放在 UI 线程上可接受，换来的是"写完就是写完"这个确定的行为。
        /// </summary>
        /// <param name="quiet">
        /// 静默写盘（自动保存用）：不往状态栏左边那行操作日志里写字。
        /// 用户手动按 Ctrl+S 要看到"工程已保存：xxx"，自动保存每几秒来一次则不能刷屏。
        /// 右边那颗指示器的"已保存 HH:mm"两种情况都会更新。
        /// </param>
        public bool SaveProjectTo(string path, string projectName, bool quiet = false)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            bool previous = _isSaving;
            _isSaving = true;
            OnPropertyChanged(nameof(IsSaving));
            OnPropertyChanged(nameof(CanSaveProject));
            OnPropertyChanged(nameof(SaveStateText));
            OnPropertyChanged(nameof(SaveStateTip));
            OnPropertyChanged(nameof(IsSaveIdle));
            OnPropertyChanged(nameof(SaveProjectTip));

            try
            {
                ProjectStore.Save(this, path, projectName);

                _currentProjectPath = path;
                _currentProjectName = string.IsNullOrWhiteSpace(projectName)
                    ? System.IO.Path.GetFileNameWithoutExtension(path)
                    : projectName;

                _isProjectDirty = false;
                _isValueDirty = false;
                _lastSavedAt = DateTime.Now;
                _isWelcomeVisible = false;

                AppSettingsStore.RememberProject(_currentProjectPath, _currentProjectName);

                // 写过盘了：两个调度器的待写标记都清掉、闸门基准挪到现在。
                // 不清的话，刚刚手动存完还会被防抖再写一次（同样的内容写两遍）。
                _structureSave?.MarkWritten();
                _valueSave?.MarkWritten();

                if (!quiet)
                {
                    _saveStatusMessage =
                        $"工程已保存：{_currentProjectName}{System.IO.Path.GetExtension(path)}";
                    StatusText = _saveStatusMessage;
                }

                return true;
            }
            finally
            {
                _isSaving = previous;
                NotifyProjectState();
            }
        }

        /// <summary>
        /// 打开工程文件。异常直接抛出，由视图层负责提示——VM 不该决定"用弹窗还是状态栏"。
        /// </summary>
        public int OpenProject(string path)
        {
            // 换工程前先停批量变化：变化目标是一批 PointViewModel 的引用，
            // 换掉点表之后那些对象就不在界面上了，还在写值等于往空气里改。
            BatchChange.Stop();

            string documentName;
            int pointCount;

            _suppressDirty = true;
            try
            {
                pointCount = ProjectStore.Load(this, path, out documentName);
            }
            finally
            {
                _suppressDirty = false;
            }

            _currentProjectPath = path;
            _currentProjectName = string.IsNullOrWhiteSpace(documentName)
                ? System.IO.Path.GetFileNameWithoutExtension(path)
                : documentName;

            _isProjectDirty = false;
            _isValueDirty = false;
            _lastSavedAt = null;
            _isWelcomeVisible = false;

            StopAutoSave();
            RefreshCommandStates();
            RebuildSlaveRows();
            AppSettingsStore.RememberProject(_currentProjectPath, _currentProjectName);

            StatusText = $"已打开工程：{_currentProjectName}（{Connections.Count} 个连接，{pointCount} 个信息对象）";
            NotifyProjectState();
            return pointCount;
        }

        /// <summary>
        /// 新建空工程并立刻落盘。
        ///
        /// 为什么要"立刻落盘"：否则会出现"新建完但还没保存"的中间状态，
        /// 用户关窗口时既没有文件可写、又不知道有没有东西丢了。先建文件再编辑，
        /// 之后每一次 Ctrl+S 都只是覆盖，语义最简单。
        /// </summary>
        public void CreateProject(string path, string projectName, bool withSamplePoints)
        {
            BatchChange.Stop();

            _suppressDirty = true;
            try
            {
                foreach (ConnectionViewModel existing in Connections.ToList())
                {
                    existing.Close();
                }
                Connections.Clear();

                if (withSamplePoints)
                {
                    LoadSampleData();
                }
            }
            finally
            {
                _suppressDirty = false;
            }

            _currentProjectPath = null;
            _currentProjectName = projectName;
            _isProjectDirty = false;
            _isValueDirty = false;
            _lastSavedAt = null;
            _isWelcomeVisible = false;

            StopAutoSave();
            SaveProjectTo(path, projectName);
            AppSettingsStore.RememberProject(_currentProjectPath, _currentProjectName);

            StatusText = $"已新建工程：{_currentProjectName}";
            NotifyProjectState();
        }

        /// <summary>
        /// 关闭工程：清空所有通道/从站/点表，回到欢迎页。
        /// 调用方负责先处理未保存的内容（见视图层的关闭保护流程）。
        /// </summary>
        public void CloseProject()
        {
            // 关工程 = 点表要没了，批量变化必须先停，否则定时器会继续往
            // 已经不在界面上的 PointViewModel 写值（状态栏却还显示"变化中"）。
            BatchChange.Stop();

            _suppressDirty = true;
            try
            {
                foreach (ConnectionViewModel existing in Connections.ToList())
                {
                    existing.Close();
                }
                Connections.Clear();
            }
            finally
            {
                _suppressDirty = false;
            }

            _currentProjectPath = null;
            _currentProjectName = null;
            _isProjectDirty = false;
            _isValueDirty = false;
            _lastSavedAt = null;
            IsWelcomeVisible = true;

            StopAutoSave();
            RefreshCommandStates();
            RebuildSlaveRows();
            AppSettingsStore.ForgetCurrentProject();

            StatusText = "已关闭工程";
            NotifyProjectState();
        }

        /// <summary>
        /// 自检专用：把累积的"脏"清掉，回到"没有改动"的基线。
        ///
        /// 为什么需要它：自检前几项会**真的**去改点表（验双击编辑、滚轮调值、建从站），
        /// 这些改动在工程视角下本来就是"未保存"。但后面的断言（主题刷新、标题栏、
        /// 保存按钮三态）要的是干净基线——不清就会把"该是灰的按钮"量成亮的、
        /// 把标题量成带 ● 的，那是自检自己造出来的噪声。
        /// </summary>
        internal void ClearDirtyState()
        {
            // 顺手把防抖也停掉：调度器里可能正欠着一次写盘，
            // 不清的话它会在后面的断言中间突然落一次盘，把 mtime 基线打乱。
            StopAutoSave();
            _isProjectDirty = false;
            _isValueDirty = false;
            NotifyProjectState();
        }

        /// <summary>
        /// 把过期的"工程已保存：xxx"从状态栏左侧撤掉，退回"就绪"。
        ///
        /// 为什么要有：左边这行是过去时的操作日志，右边那颗指示器说的是**现在**的状态。
        /// 存完再改一个点，两句话并排就成了"工程已保存 / 未保存"——自己打自己。
        /// 只认自己刚写进去的那一句：别的操作日志（"已按初始值复位 16 个信号"）不动。
        /// </summary>
        private void ClearStaleSaveMessage()
        {
            if (_saveStatusMessage != null && StatusText == _saveStatusMessage)
            {
                StatusText = "就绪";
            }

            _saveStatusMessage = null;
        }

        /// <summary>标结构脏。集合增删、参数编辑都会调它</summary>
        internal void MarkStructureDirty()
        {
            if (_suppressDirty)
            {
                return;
            }

            // 防抖要**每次改动都推后**，所以这句必须在下面的 early-return 之前：
            // 已经脏了就直接返回的话，连着编辑十分钟也只在第一次改动后写一次盘，
            // 而那一次多半正赶上用户还在敲。
            ScheduleStructureSave();

            if (_isProjectDirty)
            {
                return;
            }

            _isProjectDirty = true;

            // 一改结构就说明用户在编辑了，欢迎页没必要继续挡着点表（第三条退出路径）
            if (_isWelcomeVisible)
            {
                IsWelcomeVisible = false;
            }

            ClearStaleSaveMessage();

            NotifyProjectState();
        }

        /// <summary>
        /// 标值脏。由 SlaveStation 的值变化钩子冒泡上来，每次改值都会调——
        /// 所以这里必须**极轻**：只置一个 bool，不动集合、不刷新列表。
        /// 已经脏了就直接返回，避免每秒几十次属性通知。
        /// </summary>
        internal void MarkValueDirty()
        {
            if (_suppressDirty)
            {
                return;
            }

            // 和结构脏同理：每次变化都要把计时器推后，所以放在 early-return 之前。
            // 这条是全工具最热的路径（40 万点批量变化会调 40 万次），
            // 里面只有一次时间戳赋值 + 一个 if，代价可以忽略。
            ScheduleValueSave();

            if (_isValueDirty)
            {
                return;
            }

            _isValueDirty = true;

            // 这里**不**清"工程已保存：xxx"。值脏全程透明（决策 C）：指示器不亮、标题不挂 ●、
            // 左边那行日志也不动——文件里确实还是上次保存的那一版结构，没说过谎。
            // 值有没有落盘写在悬停提示里（见 SaveStateTip）。
            OnPropertyChanged(nameof(IsValueDirty));
            // ShowSaveState / SaveStateText / IsSaveIdle 都不依赖值脏，不用通知。
            // 只有悬停提示要说多一句（"信号值有变化，退出时自动写入"）。
            OnPropertyChanged(nameof(SaveStateTip));
            OnPropertyChanged(nameof(CanSaveProject));
            OnPropertyChanged(nameof(SaveProjectTip));
            (SaveProjectCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        /// <summary>工程状态相关的属性一次性全部通知（标题、按钮、状态栏都依赖它们）</summary>
        private void NotifyProjectState()
        {
            OnPropertyChanged(nameof(CurrentProjectPath));
            OnPropertyChanged(nameof(CurrentProjectName));
            OnPropertyChanged(nameof(HasProject));
            OnPropertyChanged(nameof(IsProjectDirty));
            OnPropertyChanged(nameof(IsValueDirty));
            // IsSaving 必须在这里也通知：SaveProjectTo 收尾走的是 NotifyProjectState，
            // 只在开始时通知一次的话，状态栏那颗指示器的 saving 类会一直挂着，
            // "正在保存…" 的淡灰样式就永久生效了（自检 statusbar-save-* 抓到的）。
            OnPropertyChanged(nameof(IsSaving));
            OnPropertyChanged(nameof(CanSaveProject));
            OnPropertyChanged(nameof(SaveProjectTip));
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(ShowSaveState));
            OnPropertyChanged(nameof(SaveStateText));
            OnPropertyChanged(nameof(SaveStateTip));
            OnPropertyChanged(nameof(IsSaveIdle));
            OnPropertyChanged(nameof(WelcomeTitle));
            OnPropertyChanged(nameof(WelcomeSubtitle));
            OnPropertyChanged(nameof(WelcomeProjectPath));

            RefreshRecentProjects();

            (SaveProjectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CloseProjectCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        // ── 欢迎页 ──────────────────────────────────────────────

        /// <summary>
        /// 重建欢迎页的「最近使用」列表。
        ///
        /// 每次都重新问一遍磁盘（File.Exists）：用户完全可能在上一分钟把文件挪走了，
        /// 列表却还显示着它——那样点下去只会得到一个打不开的框。
        /// 5 条记录的 File.Exists 是微秒级的，不值得为它做缓存。
        /// </summary>
        private void RefreshRecentProjects()
        {
            RecentProjects.Clear();

            List<RecentProjectDto> recent = AppSettingsStore.Current.RecentProjects;
            if (recent != null)
            {
                foreach (RecentProjectDto item in recent)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Path))
                    {
                        continue;
                    }

                    RecentProjects.Add(new RecentProjectEntry(
                        item.Path,
                        item.Name,
                        item.LastOpenedAt,
                        System.IO.File.Exists(item.Path),
                        OpenRecentProject));
                }
            }

            OnPropertyChanged(nameof(HasRecentProjects));
            OnPropertyChanged(nameof(HasNoRecentProjects));
        }

        /// <summary>从欢迎页的最近列表打开一个工程</summary>
        private void OpenRecentProject(RecentProjectEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Path))
            {
                return;
            }

            if (!System.IO.File.Exists(entry.Path))
            {
                AppSettingsStore.RemoveRecentProject(entry.Path);
                RefreshRecentProjects();
                ShowMessage?.Invoke("工程文件已不存在",
                    $"{entry.Path}\n\n这个文件已经被移动或删除，已从最近列表里移除。");
                return;
            }

            try
            {
                int count = OpenProject(entry.Path);
                AppendLog($"已打开工程 {entry.Path}（{count} 个信息对象）");
            }
            catch (Exception exception)
            {
                // 打开失败不动当前工程：用户手上的东西不能被一次失败的开操作清掉
                ShowMessage?.Invoke("打开工程失败", exception.Message);
            }
        }

        /// <summary>
        /// 复位前先问一句，并在确认框里写明范围——
        /// "复位 1240 个信号"和"复位 1 个信号"是完全不同量级的事，不说清楚就是坑。
        /// </summary>
        private void ConfirmResetToInitValue()
        {
            if (SelectedSlave == null)
            {
                return;
            }

            bool single = SelectedPoint != null && SelectedSlave.Points.Contains(SelectedPoint);
            string scope = single
                ? $"信号 {SelectedPoint.ObjectAddress} {SelectedPoint.Name}"
                : $"从站 {SelectedSlave.DisplayText} 的全部 {SelectedSlave.Points.Count} 个信号";

            if (ShowConfirmDialog == null)
            {
                ResetPointsToInitValue();
                return;
            }

            ShowConfirmDialog(
                "按初始值复位",
                $"将把{scope}的当前值恢复为初始值。\n\n这个操作会立即上送变位报文（COT=3），主站会收到值变化。",
                confirmed =>
                {
                    if (confirmed)
                    {
                        ResetPointsToInitValue();
                    }
                });
        }

        /// <summary>
        /// 按初始值复位。
        ///
        /// 范围规则：选中了某个点 → 只复位它；没选点 → 复位当前从站的全部点。
        /// 这两个范围对用户来说最好猜："选了的动、没选的全动"。
        /// </summary>
        public int ResetPointsToInitValue()
        {
            if (SelectedSlave == null)
            {
                return 0;
            }

            int count = 0;
            if (SelectedPoint != null && SelectedSlave.Points.Contains(SelectedPoint))
            {
                SelectedPoint.ResetToInitValue();
                count = 1;
            }
            else
            {
                foreach (PointViewModel point in SelectedSlave.Points)
                {
                    point.ResetToInitValue();
                    count++;
                }
            }

            if (count > 0)
            {
                StatusText = $"已按初始值复位 {count} 个信号";
                AppendLog($"按初始值复位：{SelectedSlave.DisplayText}，{count} 个信号");
            }

            return count;
        }

        // ── 连接操作 ────────────────────────────────────────────

        private void CreateConnection()
        {
            ConnectionViewModel connection = new ConnectionViewModel
            {
                Name = $"连接{Connections.Count + 1}",
                Port = 2404 + Connections.Count
            };

            if (EditConnectionRequested == null)
            {
                CommitNewConnection(connection);
                return;
            }

            EditConnectionRequested(connection, confirmed =>
            {
                if (confirmed)
                {
                    CommitNewConnection(connection);
                }
            });
        }

        private void CommitNewConnection(ConnectionViewModel connection)
        {
            Connections.Add(connection);
            SelectedConnection = connection;
            StatusText = $"已创建连接 {connection.Name}";
        }

        private void EditConnection()
        {
            if (SelectedConnection == null)
            {
                return;
            }
            EditConnectionRequested?.Invoke(SelectedConnection, _ => { });
        }

        private void DeleteConnection()
        {
            ConnectionViewModel target = SelectedConnection;
            if (target == null)
            {
                return;
            }

            target.Close();
            Connections.Remove(target);
            SelectedConnection = Connections.FirstOrDefault();
            StatusText = "已删除连接";
        }

        /// <summary>启动/停止一条连接</summary>
        private void ToggleConnection(ConnectionViewModel connection)
        {
            if (connection == null)
            {
                return;
            }

            try
            {
                if (connection.IsOpen)
                {
                    connection.Close();
                    StatusText = $"{connection.Name} 已停止";
                }
                else
                {
                    connection.Open();
                    StatusText = $"{connection.Name} 已启动，{connection.StatusMessage}";
                }
            }
            catch (Exception exception)
            {
                // 端口占用、参数非法等都要让用户看到原因，不能静默失败
                ShowMessage?.Invoke("启动连接失败", exception.Message);
                StatusText = "启动失败：" + exception.Message;
            }

            RefreshCommandStates();
        }

        private void OpenAll()
        {
            foreach (ConnectionViewModel connection in Connections.Where(c => !c.IsOpen).ToList())
            {
                ToggleConnection(connection);
            }
        }

        private void CloseAll()
        {
            foreach (ConnectionViewModel connection in Connections.Where(c => c.IsOpen).ToList())
            {
                ToggleConnection(connection);
            }
        }

        // ── 从站操作 ────────────────────────────────────────────

        private void CreateSlave()
        {
            // 注意：这里不再要求"必须先选中一个通道"。
            // 用户原话："如果每一个从站都是不一样的 IP，那我还需要先建立多个通道，
            // 然后再去每个通道下建立不同的从站"——这个两步流程就是在对话框里省掉的。
            ConnectionViewModel current = SelectedConnection
                ?? (Connections.Count > 0 ? Connections[0] : null);

            int nextAddress = current == null
                ? 1
                : (current.Slaves.Count == 0
                    ? 1
                    : Math.Min(65535, current.Slaves.Max(s => s.CommonAddress) + 1));

            SlaveViewModel slave = new()
            {
                Name = $"从站{nextAddress}",
                CommonAddress = nextAddress
            };

            // 把"通道决策"交给对话框：视图层拿 PendingSlaveEdit 当 DataContext，
            // 用户在里面选"已有通道"还是"新建通道（填 IP）"
            //
            // preferNewChannel：扁平视图下默认就是"一台装置一条通道"——
            // 用户点"新建从站"的意思本来就是"再加一台装置"，不该让他先想去挂哪儿
            _pendingSlaveEdit = new SlaveEditModel(slave, Connections, current, preferNewChannel: IsFlatView);

            if (EditSlaveRequested == null)
            {
                // 没有界面层（自检/无头场景）：没有通道就现建一条，
                // 语义和扁平视图点"新建从站"完全一致
                ConnectionViewModel target = current ?? CreateDefaultChannel();
                CommitNewSlave(target, slave);
                _pendingSlaveEdit = null;
                return;
            }

            EditSlaveRequested(slave, confirmed =>
            {
                ConnectionViewModel target = confirmed
                    ? (_pendingSlaveEdit?.ResolvedChannel ?? current)
                    : null;

                if (confirmed && target != null)
                {
                    CommitNewSlave(target, slave);
                }
                _pendingSlaveEdit = null;
            });
        }

        private void CommitNewSlave(ConnectionViewModel connection, SlaveViewModel slave)
        {
            // 公共地址在同一连接内必须唯一，否则主站报文无法路由
            if (connection.Slaves.Any(s => s.CommonAddress == slave.CommonAddress))
            {
                ShowMessage?.Invoke("无法创建从站", $"公共地址 {slave.CommonAddress} 已被占用。");
                return;
            }

            connection.Slaves.Add(slave);
            connection.HookSlave(slave);

            // 如果刚在对话框里现建了通道，这里顺手选中它，用户能立刻看到结果
            SelectedConnection = connection;
            SelectedSlave = slave;
            StatusText = $"已创建从站 {slave.DisplayText}（通道 {connection.Name}）";
        }

        /// <summary>
        /// 建一条默认 104 通道并加入列表。
        ///
        /// 存在的意义只有一个：让"新建从站"在一条通道都没有时也能一键完成。
        /// 端口从 2404 起找第一个没人用的，免得撞上已有通道（撞了两条都起不来，
        /// 报错还是 TcpListener 的原话，用户根本猜不到是自己点快了）。
        /// </summary>
        private ConnectionViewModel CreateDefaultChannel()
        {
            ConnectionViewModel channel = new()
            {
                Name = $"104通道{Connections.Count + 1}",
                Protocol = ConnectionProtocol.Iec104,
                Port = SlaveEditModel.FindFreePort(Connections),
                BindAddress = "0.0.0.0"
            };

            Connections.Add(channel);
            return channel;
        }

        private void EditSlave()
        {
            if (SelectedSlave == null || SelectedConnection == null)
            {
                return;
            }

            SlaveViewModel target = SelectedSlave;
            ConnectionViewModel owner = SelectedConnection;

            EditSlaveRequested?.Invoke(target, confirmed =>
            {
                if (!confirmed)
                {
                    return;
                }

                // 编辑后可能改了公共地址，需重新校验唯一性
                if (owner.Slaves.Count(s => s.CommonAddress == target.CommonAddress) > 1)
                {
                    ShowMessage?.Invoke("公共地址冲突",
                        $"公共地址 {target.CommonAddress} 已被同连接下的其它从站占用，请修改。");
                }
            });
        }

        private void CopySlave()
        {
            if (SelectedSlave == null || SelectedConnection == null)
            {
                return;
            }

            int nextAddress = Enumerable.Range(1, 255)
                .FirstOrDefault(a => SelectedConnection.Slaves.All(s => s.CommonAddress != a));
            if (nextAddress == 0)
            {
                ShowMessage?.Invoke("无法复制", "公共地址已用尽（1..255）。");
                return;
            }

            SlaveViewModel copy = new SlaveViewModel
            {
                Name = SelectedSlave.Name + "_副本",
                CommonAddress = nextAddress
            };

            foreach (PointViewModel point in SelectedSlave.Points)
            {
                copy.AddPoint(point.ObjectAddress, point.Type.Type, point.Name, point.Value);
            }

            SelectedConnection.Slaves.Add(copy);
            SelectedConnection.HookSlave(copy);
            SelectedSlave = copy;
            StatusText = $"已复制从站到 CA={nextAddress}";
        }

        private void DeleteSlave()
        {
            SlaveViewModel target = SelectedSlave;
            if (target == null || SelectedConnection == null)
            {
                return;
            }

            SelectedConnection.UnhookSlave(target);
            SelectedConnection.Slaves.Remove(target);
            SelectedSlave = SelectedConnection.Slaves.FirstOrDefault();
            StatusText = "已删除从站";
        }

        // ── 点位操作 ────────────────────────────────────────────

        private void AddPoint()
        {
            if (SelectedSlave == null)
            {
                return;
            }

            SlaveViewModel slave = SelectedSlave;

            // 新点号默认取当前最大点号 + 1，避免一新增就撞号
            int nextAddress = slave.Points.Count == 0
                ? 1
                : slave.Points.Max(p => p.ObjectAddress) + 1;

            PointViewModel draft = slave.AddPoint(
                nextAddress, TypeIdentifier.M_ME_NC_1, $"测点{nextAddress}", 0);

            if (EditPointRequested == null)
            {
                SelectedPoint = draft;
                return;
            }

            EditPointRequested(draft, slave, confirmed =>
            {
                if (!confirmed)
                {
                    // 取消则回滚，避免留下一个默认点位
                    slave.RemovePoint(draft);
                    return;
                }

                SelectedPoint = draft;
            });
        }

        private void EditPoint()
        {
            if (SelectedPoint == null || SelectedSlave == null)
            {
                return;
            }

            PointViewModel target = SelectedPoint;
            EditPointRequested?.Invoke(target, SelectedSlave, confirmed =>
            {
                if (confirmed)
                {
                }
            });
        }

        private void DeletePoint()
        {
            if (SelectedPoint == null || SelectedSlave == null)
            {
                return;
            }

            SelectedSlave.RemovePoint(SelectedPoint);
            SelectedPoint = null;
        }

        /// <summary>
        /// 外部（初始化数据窗口、工作区载入）批量改动点表后，刷新界面上的统计信息。
        /// 点表本身是 ObservableCollection，行会自动更新，但"16 个信息对象"这类
        /// 汇总文本是算出来的，必须显式通知。
        /// </summary>
        public void NotifyPointsChanged()
        {
            OnPropertyChanged(nameof(RunningSummary));
        }

        /// <summary>
        /// 点表就地编辑被拒（格式不对 / 点号重复 / 越界）时给个提示。
        /// 不提示的话用户会以为改成功了——输入框一消失，看不出区别。
        /// </summary>
        public void ReportEditRejected() =>
            StatusText = "修改未生效：请检查格式，点号需在 1..16777215 且不能与现有点号重复";

        /// <summary>
        /// 导出点表为 CSV。
        /// 复刻说明：原程序用 CsvHelper 实现 ExportInformationObjectsCommand，
        /// 此处手写 CSV 输出以免引入额外依赖（导出格式一致：点号,名称,类型,值）。
        /// </summary>
        private void ExportPoints()
        {
            if (SelectedSlave == null)
            {
                return;
            }

            ExportRequested?.Invoke(SelectedSlave);
        }

        /// <summary>视图层接管"导出到文件"（需要文件选择对话框）</summary>
        public Action<SlaveViewModel> ExportRequested { get; set; }

        // ── 报文记录 ────────────────────────────────────────────

        /// <summary>待刷新的报文缓冲（不直接进 Records，避免每条一次集合事件）</summary>
        private readonly List<AsduRecord> _pendingRecords = new(512);

        /// <summary>批量刷新定时器（有积压时才启动）</summary>
        private DispatcherTimer _recordFlushTimer;

        /// <summary>缓冲刷新间隔：150 ms 既能让"最新报文"看起来是实时的，又能把上万条压成几次刷新</summary>
        private const int RecordFlushMilliseconds = 150;

        /// <summary>
        /// 把一条记录写进报文监视。
        ///
        /// 注意是"进缓冲区"而不是"直接进列表"：调用方可能在网络线程上、
        /// 也可能一次来上万条（总召爆发），逐条插到列表头部会让 UI 线程跟不上。
        /// 真正的列表更新在 <see cref="FlushRecords"/> 里一次做完。
        /// </summary>
        public void AppendRecord(AsduRecord record)
        {
            if (record == null)
            {
                return;
            }

            _pendingRecords.Add(record);
            EnsureRecordFlushTimer();
        }

        private void EnsureRecordFlushTimer()
        {
            if (_recordFlushTimer != null)
            {
                if (!_recordFlushTimer.IsEnabled)
                {
                    _recordFlushTimer.Start();
                }
                return;
            }

            _recordFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RecordFlushMilliseconds) };
            _recordFlushTimer.Tick += (_, _) => FlushRecords();
            _recordFlushTimer.Start();
        }

        /// <summary>
        /// 把缓冲区合并进监视列表：新的在前、超出上限丢最旧的、只发一次 Reset。
        /// 自检与单元测试里可以手动调它，拿到"立刻生效"的结果。
        /// </summary>
        public void FlushRecords()
        {
            if (_pendingRecords.Count == 0)
            {
                // 没积压就停表：不让一个 150 ms 的定时器长期空转打扰 UI 线程
                _recordFlushTimer?.Stop();
                return;
            }

            int keep = Math.Min(MaxRecordCount, _pendingRecords.Count + Records.Count);
            List<AsduRecord> merged = new(keep);
            for (int index = _pendingRecords.Count - 1; index >= 0 && merged.Count < keep; index--)
            {
                merged.Add(_pendingRecords[index]);
            }
            for (int index = 0; index < Records.Count && merged.Count < keep; index++)
            {
                merged.Add(Records[index]);
            }

            _pendingRecords.Clear();
            Records.ReplaceAll(merged);
        }

        /// <summary>写入一条链路日志</summary>
        public void AppendLog(string message)
        {
            Logs.Insert(0, $"[{DateTime.Now:HH:mm:ss.fff}] {message}");
            while (Logs.Count > MaxRecordCount)
            {
                Logs.RemoveAt(Logs.Count - 1);
            }
        }

        // ── 内部 ────────────────────────────────────────────────

        private void OnConnectionsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (ConnectionViewModel connection in e.NewItems)
                {
                    connection.AsduRecorded += AppendRecord;
                    connection.LinkEvent += AppendLog;
                    // 通道可能是被协议层自己关掉的（比如监听失败、主站断开后清理），
                    // 不订阅 IsOpen 的话工具栏那颗按钮会一直停在"停止"上
                    connection.PropertyChanged += OnConnectionPropertyChanged;

                    // 扁平列表是所有通道下所有从站的摊平结果，任何一个通道的从站
                    // 增删都要重摊一次（重摊的成本是 N 行，通道数不会大到需要增量维护）
                    connection.Slaves.CollectionChanged += OnSlavesChanged;

                    // 已经有从站的通道（载入工程时一次性 Add 进来）也要挂上钩子
                    foreach (SlaveViewModel slave in connection.Slaves)
                    {
                        WatchSlave(slave);
                    }
                }
            }

            if (e.OldItems != null)
            {
                foreach (ConnectionViewModel connection in e.OldItems)
                {
                    connection.AsduRecorded -= AppendRecord;
                    connection.LinkEvent -= AppendLog;
                    connection.PropertyChanged -= OnConnectionPropertyChanged;
                    connection.Slaves.CollectionChanged -= OnSlavesChanged;
                }
            }

            // 集合变了就是结构变了（载入/新建期间被 _suppressDirty 拦住）
            MarkStructureDirty();

            RebuildSlaveRows();
            OnPropertyChanged(nameof(RunningSummary));
            RefreshCommandStates();
        }

        /// <summary>任一通道的从站集合变了：重摊扁平列表（选中按从站对象找回）</summary>
        private void OnSlavesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (SlaveViewModel slave in e.NewItems)
                {
                    WatchSlave(slave);
                }
            }

            // Reset（AddRange 批量铺点）时 NewItems 为 null，只能整表重扫
            if (e.Action == NotifyCollectionChangedAction.Reset && sender is IEnumerable<SlaveViewModel> slaves)
            {
                foreach (SlaveViewModel slave in slaves)
                {
                    WatchSlave(slave);
                }
            }

            MarkStructureDirty();
            RebuildSlaveRows();
        }

        /// <summary>
        /// 给一台从站挂上两个钩子：
        ///   ① 点表集合变化 → 结构脏（增删信号）
        ///   ② 任意点值变化 → 值脏（协议层冒泡上来，见 SlaveStation.ValueChangedHook）
        /// 重复调用是安全的：事件用 -= 再去重绑定，值钩子直接覆盖。
        /// </summary>
        private void WatchSlave(SlaveViewModel slave)
        {
            if (slave == null)
            {
                return;
            }

            slave.ValueTouched = MarkValueDirty;
            slave.Points.CollectionChanged -= OnPointsChanged;
            slave.Points.CollectionChanged += OnPointsChanged;
        }

        /// <summary>某台从站的点表增删了 → 结构脏</summary>
        private void OnPointsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            MarkStructureDirty();
        }

        /// <summary>
        /// 通道自身的 IsOpen 变化 → 工具栏"启停合一"按钮要换文案、换配色。
        /// 只关心 IsOpen：端口被占用之类的失败会走 ToggleConnection 的 catch，
        /// 那边已经会 RefreshCommandStates。
        /// </summary>
        // 这里用完全限定名：项目里有个 IEC104.App.Services.TypeDescriptor，
        // 直接 using System.ComponentModel 会和它撞名（CS0104）
        private void OnConnectionPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ConnectionViewModel.IsOpen))
            {
                NotifyToggleState();
                (ToggleAllConnectionsCommand as RelayCommand)?.RaiseCanExecuteChanged();
                return;
            }

            // 主站接入 / 断开时 IsOpen 不变，只有 ClientCount 变。
            // 状态栏那颗健康度胶囊要跟着从"监听中"跳到"主站已接入"，否则
            // 用户明明看到报文在跑、状态栏却还写着"监听中"。
            if (e.PropertyName == nameof(ConnectionViewModel.ClientCount))
            {
                NotifyHealth();
            }
        }

        private void RefreshCommandStates()
        {            (CreateConnectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (EditConnectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteConnectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenConnectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CloseAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CreateSlaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (EditSlaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CopySlaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteSlaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AddPointCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (EditPointCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeletePointCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ExportPointsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ToggleSelectedConnectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ToggleAllConnectionsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            NotifyToggleState();
            OnPropertyChanged(nameof(RunningSummary));
        }

        /// <summary>
        /// 载入演示数据。
        /// 目的：程序启动后立刻能看到一个可运行的从站，而不必先手动建点表。
        /// 原程序首次启动是空工程（需 OpenWorkspace 或手动创建）。
        /// </summary>
        private void LoadSampleData()
        {
            ConnectionViewModel connection = new ConnectionViewModel
            {
                Name = "本地104服务",
                Port = 2404
            };

            SlaveViewModel slave = new SlaveViewModel
            {
                Name = "1号从站",
                CommonAddress = 1
            };

            // 双点遥信（断路器位置）——用双点才能表达中间态
            for (int i = 0; i < 4; i++)
            {
                slave.AddPoint(1001 + i, TypeIdentifier.M_DP_NA_1, $"断路器{i + 1}位置", i % 2);
            }

            // 单点遥信 + CP56 时标（SOE 告警）
            for (int i = 0; i < 4; i++)
            {
                slave.AddPoint(2001 + i, TypeIdentifier.M_SP_TB_1, $"告警信号{i + 1}", 0);
            }

            // 短浮点遥测
            string[] names = { "1号主变有功", "1号主变无功", "110kV母线电压", "10kV母线电压" };
            for (int i = 0; i < names.Length; i++)
            {
                slave.AddPoint(4001 + i, TypeIdentifier.M_ME_NC_1, names[i], 100 + i * 10);
            }

            // 归一化遥测
            slave.AddPoint(4101, TypeIdentifier.M_ME_NA_1, "2号线路有功", 0.5);

            // 电度（累计量）
            for (int i = 0; i < 2; i++)
            {
                slave.AddPoint(6401 + i, TypeIdentifier.M_IT_NA_1, $"电度{i + 1}", 10000 + i * 100);
            }

            // 档位
            slave.AddPoint(10001, TypeIdentifier.M_ST_NA_1, "1号主变档位", 3);

            connection.Slaves.Add(slave);
            connection.HookSlave(slave);
            Connections.Add(connection);

            SelectedConnection = connection;
            SelectedSlave = slave;

            AppendLog("已载入演示点表（按需修改或删除）");
        }

        public void Dispose()
        {
            // 先停掉缓变定时器，避免关窗过程中还在往已释放的从站写值
            AutoChange?.Dispose();

            foreach (ConnectionViewModel connection in Connections)
            {
                connection.Dispose();
            }
        }
    }
}
