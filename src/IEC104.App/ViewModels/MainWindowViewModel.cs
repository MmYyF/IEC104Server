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

        public MainWindowViewModel()
        {
            // 视图偏好要在建任何列表之前定下来：LoadSampleData 会选中首个从站、
            // 进而重建点表标签，标签按哪种方式分组取决于这个值
            _sidebarTreeView = AppSettingsStore.Current.SidebarTreeView;
            _groupPointsByCategory = AppSettingsStore.Current.PointsGroupByCategory;

            Connections.CollectionChanged += OnConnectionsChanged;

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

            LoadSampleData();

            // 演示数据是直接塞进 Connections 的，扁平列表要自己再摊一次
            RebuildSlaveRows();

            // 主题切换后由这里统一刷新依赖状态的画刷
            ThemeService.Changed += OnThemeChanged;
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

        // ── 工作区（工程文件） ───────────────────────────────────

        /// <summary>
        /// 保存工作区到指定文件。
        /// 复刻说明：对应原程序 SaveWorkspaceCommand。
        /// 由视图层负责选路径（StorageProvider 属于视图职责），这里只做序列化。
        /// </summary>
        public void SaveWorkspace(string path)
        {
            WorkspaceStore.Save(this, path);
            StatusText = $"工作区已保存：{System.IO.Path.GetFileName(path)}";
        }

        /// <summary>
        /// 从文件载入工作区。
        /// 复刻说明：对应原程序 OpenWorkspaceCommand。
        /// 返回载入的点位总数，供界面提示。
        /// </summary>
        public int LoadWorkspace(string path)
        {
            int pointCount = WorkspaceStore.Load(this, path);
            RefreshCommandStates();
            StatusText = $"已打开工作区：{System.IO.Path.GetFileName(path)}（{Connections.Count} 个连接，{pointCount} 个信息对象）";
            return pointCount;
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

            RebuildSlaveRows();
            OnPropertyChanged(nameof(RunningSummary));
            RefreshCommandStates();
        }

        /// <summary>任一通道的从站集合变了：重摊扁平列表（选中按从站对象找回）</summary>
        private void OnSlavesChanged(object sender, NotifyCollectionChangedEventArgs e) => RebuildSlaveRows();

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
