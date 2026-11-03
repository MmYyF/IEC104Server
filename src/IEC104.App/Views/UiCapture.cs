using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Shapes = Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IEC104.App.Controls;
using IEC104.App.Infrastructure;
using IEC104.App.Services;
using IEC104.App.ViewModels;
using IEC104.Core.CS101;

namespace IEC104.App.Views
{
    /// <summary>
    /// UI 自检截图器（开发/验收用，正式交互不启用）。
    ///
    /// 用法： IEC104Simulator.exe --capture-ui D:\some\dir
    /// 作用： 依次打开每一个窗口，把它们渲染成 PNG 后退出。
    ///
    /// 为什么需要它：
    ///   1. 逆向复刻过程中要逐个核对 21 个窗口的排版，靠人工点鼠标太慢；
    ///   2. 在 CI / 远程桌面上不方便交互，但需要证据证明界面真的渲染出来了；
    ///   3. 直接对着 PNG 改 XAML 比“跑起来看一眼”迭代快得多。
    /// 实现要点：用 RenderTargetBitmap 渲染窗口视觉树，不依赖系统截屏，
    /// 因此窗口被别的程序遮住也能拿到正确画面。
    /// </summary>
    internal static class UiCapture
    {
        /// <summary>非空表示处于自检模式，值为输出目录</summary>
        public static string Directory { get; set; }

        public static bool Enabled => !string.IsNullOrWhiteSpace(Directory);

        public static async Task RunAsync(Window mainWindow, MainWindowViewModel viewModel)
        {
            string dir = Directory;
            System.IO.Directory.CreateDirectory(dir);
            // WinExe 没有控制台，Console 输出在重定向场景下不可靠，
            // 自检过程统一写文件，方便断言“到底跑到哪一步了”
            Log(dir, "start");

            // 等主窗口完成首帧布局，否则拿到的是空白位图
            await Task.Delay(1200);
            Save(mainWindow, Path.Combine(dir, "01-主窗口.png"));
            LogLayout(dir, mainWindow);
            await Task.Delay(400);
            LogLayout(dir, mainWindow);

            // 顺手截一张侧栏收起后的样子：折叠是纯视图行为，光看代码不能确认列宽真的归零
            await CaptureSidebarCollapsedAsync(dir, mainWindow);

            ConnectionViewModel connection = viewModel.SelectedConnection
                ?? (viewModel.Connections.Count > 0 ? viewModel.Connections[0] : null);
            SlaveViewModel slave = connection?.Slaves.Count > 0 ? connection.Slaves[0] : null;
            PointViewModel point = slave?.Points.Count > 0 ? slave.Points[0] : null;

            await CaptureAsync(dir, "02-连接编辑",
                () => new ConnectionWindow(),
                connection == null ? null : new ConnectionEditModel(connection));

            // 101 串口形态单独截一张：协议切到串口后应当出现 COM 口/波特率，而不是端口号
            ConnectionViewModel serialSample = new()
            {
                Name = "本地101串口",
                Protocol = ConnectionProtocol.Iec101,
                SerialPortName = "COM3",
                BaudRate = 9600
            };
            await CaptureAsync(dir, "02b-连接编辑-101",
                () => new ConnectionWindow(),
                new ConnectionEditModel(serialSample));

            await CaptureAsync(dir, "03-从站编辑",
                () => new SlaveWindow(),
                slave == null ? null : new SlaveEditModel(slave));

            // 扁平视图里双击标签打开的样子：多一块"监听信息"（改 IP / 端口）。
            // 和上面那张只是同一个窗口的两种形态，分开截是因为"少一块"这种问题
            // 只有并排看才看得出来。
            await CaptureAsync(dir, "03c-从站编辑-带监听端点",
                () => new SlaveWindow(),
                slave == null || connection == null
                    ? null
                    : new SlaveEditModel(slave, connection, viewModel.Connections));

            // 新建从站形态：多出"归属通道"区块（含"新建通道"填 IP）
            await CaptureAsync(dir, "03b-从站新建-选通道",
                () => new SlaveWindow(),
                viewModel == null
                    ? null
                    : new SlaveEditModel(
                        new SlaveViewModel { Name = "新从站", CommonAddress = 5 },
                        viewModel.Connections,
                        viewModel.SelectedConnection));

            await CaptureAsync(dir, "04-信息对象编辑",
                () => new PointWindow(),
                point == null || slave == null ? null : new PointEditModel(point, slave));

            await CaptureAsync(dir, "05-报文解析-104",
                () => new FrameParserWindow(),
                null);

            await CaptureAsync(dir, "06-缓变配置",
                () => new AutoChangeWindow(),
                slave == null ? null : new AutoChangeViewModel(viewModel.AutoChange, slave.Points));

            await CaptureAsync(dir, "07-初始化数据",
                () => new InitDataWindow(),
                new InitDataViewModel(slave));

            await CaptureAsync(dir, "08-冗余校验",
                () => new RedundancyCheckWindow(),
                null);

            await CaptureAsync(dir, "09-字符串编解码",
                () => new StringCodecWindow(),
                null);

            // 原来这里有 "10-关于"（独立关于窗口）。1.9.1 起"关于"是设置里的一页，
            // 截图改到设置那一段（27b-设置-关于）——窗口版的截图留着只会误导后来人。

            await CaptureAsync(dir, "11-实时曲线",
                () => new RealtimeChartWindow(),
                BuildChartModel(slave));

            await CaptureAsync(dir, "12-通信报文监视",
                () => new TrafficWindow(),
                BuildTrafficSampleModel());

            await CaptureAsync(dir, "13-批量变化",
                () => new BatchChangeWindow(),
                slave == null ? null : new BatchChangeViewModel(slave.Points));

            // 批量创建从站：参数 + 预览
            await CaptureAsync(dir, "14-批量创建从站",
                () => new BatchCreateSlaveWindow(),
                new BatchCreateSlaveViewModel(viewModel.Connections));

            // 跑一遍批量变化，验证"每秒 200 点"这条链路真的是通的，而不只是界面画出来了
            await RunBatchSelfCheckAsync(dir, mainWindow, viewModel);

            // 验证"新建从站时直接建通道"这条新链路
            RunSlaveChannelSelfCheck(dir, viewModel);

            // 验证联合建窗口里能选规约（104 TCP / 101 串口）
            RunSlaveChannelProtocolSelfCheck(dir, viewModel);

            // 验证三个报文长度的编解码：12 种宽度组合逐个往返
            await RunApplicationWidthSelfCheckAsync(dir, viewModel);

            // 验证批量创建从站（IP 递增 / 进位 / 端口递增 / 冲突跳过）
            RunBatchCreateSelfCheck(dir, viewModel);

            // 1.11：本机网卡与 IP（枚举 / 计划文案 / 添加与跳过 / 账本 / 10049 翻译 /
            // 回退 0.0.0.0 / 提权参数判定 / 管理窗口）。执行通道换成假的，绝不真动系统
            await RunIpAddressSelfCheckAsync(dir, viewModel);

            // 验证信号区分组：按 ASDU 类型 ⇄ 按电力大类，两种都要验
            RunPointTabSelfCheck(dir, viewModel, mainWindow);

            // 验证切换按钮在五套主题下都看得见，以及点号表头的进制提示
            await RunPointGroupToggleSelfCheckAsync(dir, mainWindow, viewModel);

            // 验证点表单元格的内联编辑与滚轮调整
            RunPointEditSelfCheck(dir);

            // 验证"滚轮改值"在真实可视树上真的被触发（XAML 接线）
            RunCellWheelSelfCheck(dir, mainWindow);

            // 验证第七轮的四项外观改动：启停合一 / 隔行底色 / 折叠按钮尺寸 / 页签字号
            RunToolbarAndGridPolishSelfCheck(dir, mainWindow, viewModel);

            // 验证第十七轮：工具条 11 颗图标是否真的"一个墨色、一个比例尺"
            RunToolbarIconSelfCheck(dir, mainWindow);

            // 前面的双击/滚轮检查会真的改点表，这里先把"脏"清成干净基线：
            // 主题刷新与保存按钮三态两条断言量的都是"没有改动"的样子
            viewModel.ClearDirtyState();

            // 验证第八轮的多主题：五套主题逐个切，对比度全部过 WCAG AA
            await RunThemeSelfCheckAsync(dir, mainWindow, viewModel);

            // 验证第九轮：标题栏合并、状态栏几何、单元格居中、健康度配色
            RunLayoutPolishSelfCheck(dir, mainWindow, viewModel);

            // 1.9.8：标题栏钉子按钮 + 监视面板的收起/清空（都放在布局稳定之后跑）
            await RunTitlebarMonitorSelfCheckAsync(dir, mainWindow, viewModel);

            // 验证第十轮：菜单圆角+图标、主题弹窗、标题栏仓库/更新按钮
            RunMenuPolishSelfCheck(dir, mainWindow);

            viewModel.ClearDirtyState();

            // 验证第十一轮：窗口图标、菜单图标清晰度/对齐/配色、关于窗口、标题栏垂直居中
            RunAppearanceSelfCheck(dir, mainWindow);

            // 验证第十二轮：报文监视的批量刷新链路（性能改造后最容易悄悄坏掉的地方）
            RunThroughputSelfCheck(dir, viewModel);

            // 验证第十二轮补丁：最大化时标题栏不能被窗口外扩挤出屏幕
            RunMaximizedSelfCheck(dir, mainWindow as MainWindow);

            // 弹层的截图：主窗口的位图里没有 Popup（它们在独立顶层窗口），
            // 所以直接渲染弹层自己的内容控件，把三处弹层各存一张。
            await CapturePopupsAsync(dir, mainWindow);

            // 验证报文监视窗口的"置顶"勾选框真的和 Topmost 双向绑定
            await RunTrafficWindowSelfCheckAsync(dir, viewModel);

            // 验证报文监视窗口是非模态的（开着也不挡主界面）
            await RunTrafficNonModalSelfCheckAsync(dir, mainWindow);

            // 验证通道状态灯三态（真开端口、真连一个主站上去）
            await RunChannelStateSelfCheckAsync(dir, viewModel);

            // 验证 1.9.0 的侧边栏双视图：默认扁平、标签带 IP/端口、颜色分三态、
            // 双击能改 IP、新建从站默认连通道一起建、工具条按钮随视图联动、设置项落盘
            await RunSidebarViewSelfCheckAsync(dir, mainWindow, viewModel);

            // 验证设置界面：入口按钮、左标签栏 + 右内容、自定义 ASDU 类型增删与落盘、点表显示中文名
            // 放最后：它会临时把主界面切成设置界面再切回来，前面那些要读工作区几何的断言不能被打断
            await RunSettingsSelfCheckAsync(dir, mainWindow, viewModel);

            viewModel.ClearDirtyState();

            // 验证 1.10 工程化：保存按钮三态、工程存读、初始值语义、欢迎页两态
            // （必须最后跑：它最后会关闭工程、清空通道与从站）
            await RunProjectSelfCheckAsync(dir, mainWindow, viewModel);

            // 1.10.1 自动保存：防抖 / 闸门 / 开关边界 / 设置页「工程」
            // （也放最后：它会建一份自己的工程，跑完再关掉）
            await RunAutoSaveSelfCheckAsync(dir, mainWindow, viewModel);

            Log(dir, "done");
            Console.WriteLine("[ui-capture] done -> " + dir);

            // 主窗口关闭会触发 OnMainWindowClose 退出整个应用
            mainWindow.Close();
        }

        /// <summary>
        /// 自检：单元格滚轮改值的**界面接线**是否通。
        ///
        /// 前面的 point-edit 验的是 ViewModel 行为，这里验的是
        /// "XAML 里挂的 PointerWheelChanged 真的被触发、Tag 真的被认出来"——
        /// 两件事分开测，否则把 Tag 写错、事件漏挂，前面那条照样 PASS。
        /// 做法：在真实的可视树上找到"当前值"单元格控件，直接抛 PointerWheelEventArgs。
        /// </summary>
        private static void RunCellWheelSelfCheck(string dir, Window mainWindow)
        {
            // DataGrid 是虚拟化的：行容器要到真正渲染时才生成。
            // 不先把布局跑一遍，可视树里只有表头，找不到任何单元格。
            WarmUpLayout(mainWindow);

            TextBlock valueCell = mainWindow.GetVisualDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(t => (t.Tag as string) == "value");

            // 品质的 Tag 挂在徽标 Border 上（不是里面那个 TextBlock）
            Border qualityCell = mainWindow.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(b => (b.Tag as string) == "quality");

            if (valueCell?.DataContext is not PointViewModel point)
            {
                var allText = mainWindow.GetVisualDescendants().OfType<TextBlock>().ToList();
                var tagged = allText.Where(t => t.Tag != null).Select(t => t.Tag?.ToString()).ToList();
                Log(dir, $"cell-wheel: 跳过（可视树里没找到当前值单元格）"
                    + $" 文本块={allText.Count} 带Tag={tagged.Count} tags=[{string.Join(",", tagged)}]");
                return;
            }

            // 遥测走"加减步长"，开关量走"轮换"，这里分两段验
            double before = point.Value;
            RaiseWheel(valueCell, mainWindow, 1);
            bool valueChanged = Math.Abs(point.Value - before) > 1e-9;

            // 品质：滚一格应该从"正常"变成 IV
            bool qualityBefore = point.QualityText == "正常";
            bool qualityChanged = false;
            if (qualityCell != null)
            {
                RaiseWheel(qualityCell, mainWindow, 1);
                qualityChanged = point.QualityText != "正常";
                RaiseWheel(qualityCell, mainWindow, -1);   // 滚回正常，别影响后面的截图
            }

            bool pass = valueChanged && qualityChanged;
            Log(dir, $"cell-wheel: 当前值滚轮生效={valueChanged}({before:0.###}→{point.Value:0.###}) "
                + $"品质滚轮生效={qualityChanged}(起始正常={qualityBefore}) => {(pass ? "PASS" : "FAIL")}");
        }

        /// <summary>
        /// 第七轮的四项外观改动，全部做成断言。
        ///
        /// 这一轮改的都是"看着别扭"的问题，最容易出现"改完自己觉得好了、下次重构又回去了"，
        /// 所以每项都要有可执行判据：
        ///   ① 启停合一 —— 按钮文案必须跟着通道 IsOpen 变（不能只改 XAML 不接状态）
        ///   ② 隔行底色 —— 相邻两行 Background 必须不同（否则等于没生效）
        ///   ③ 折叠按钮 —— 尺寸够不够大，按像素判
        ///   ④ 底部页签 —— 字号是否真的压下来了（TabItem 是主题模板，光设 Style 未必吃）
        /// </summary>
        private static void RunToolbarAndGridPolishSelfCheck(string dir, Window mainWindow, MainWindowViewModel viewModel)
        {
            // ① 启停合一
            ConnectionViewModel connection = viewModel.SelectedConnection;
            bool startedStopped = connection != null && !connection.IsOpen;
            string textBefore = viewModel.SelectedConnectionToggleText;
            string allBefore = viewModel.AllConnectionToggleText;

            viewModel.ToggleSelectedConnectionCommand?.Execute(null);
            bool openedNow = connection?.IsOpen == true;
            string textAfter = viewModel.SelectedConnectionToggleText;
            string allAfter = viewModel.AllConnectionToggleText;

            viewModel.ToggleSelectedConnectionCommand?.Execute(null);
            bool closedAgain = connection?.IsOpen == false;
            string textFinal = viewModel.SelectedConnectionToggleText;

            bool toggleOk = startedStopped && openedNow && closedAgain
                && textBefore == "启动" && textAfter == "停止" && textFinal == "启动"
                && allBefore == "全部启动" && allAfter == "全部停止";

            Log(dir, $"toggle-button: {textBefore}→{textAfter}→{textFinal} "
                + $"通道起/停={openedNow}/{closedAgain} 全部按钮={allBefore}→{allAfter} => "
                + (toggleOk ? "PASS" : "FAIL"));

            // ② 隔行底色：直接读行容器的 Background，不靠看图
            WarmUpLayout(mainWindow);

            System.Collections.Generic.List<DataGridRow> rows = mainWindow.GetVisualDescendants()
                .OfType<DataGridRow>()
                .OrderBy(row => row.Bounds.Y)
                .Take(3)
                .ToList();

            bool stripeOk = rows.Count >= 2
                && rows[0].Background is ISolidColorBrush first
                && rows[1].Background is ISolidColorBrush second
                && first.Color != second.Color;

            string stripeDetail = rows.Count >= 2
                ? $"{DescribeBrush(rows[0].Background)} / {DescribeBrush(rows[1].Background)}"
                : "行容器不足";
            Log(dir, $"row-stripes: 相邻行底色={stripeDetail} => {(stripeOk ? "PASS" : "FAIL")}");

            // ③ 折叠按钮尺寸：两个分组折叠箭头都要够大（原来 20×20、11px 字形，用户嫌小）
            //
            //    这里一定要按"有布局尺寸"过滤。设置界面的返回键也复用了 .chevron 样式，
            //    而它挂在初始 IsVisible=false 的 SettingsView 里，量出来恒为 0×0。
            //    不过滤的话，这条断言验的就不是"侧栏两个折叠箭头够不够大"，
            //    而是"全工程每一个 chevron 都得此刻可见"——两回事，后者随时会被新页面撞翻。
            System.Collections.Generic.List<Button> allChevrons = mainWindow.GetVisualDescendants()
                .OfType<Button>()
                .Where(button => button.Classes.Contains("chevron"))
                .ToList();

            System.Collections.Generic.List<Button> chevrons = allChevrons
                .Where(button => button.Bounds.Width > 0 && button.Bounds.Height > 0)
                .ToList();

            // 1.9.0：侧边栏改成两种视图后，扁平视图里只有一颗"收起侧栏"，
            // 分组折叠箭头只在"通道 / 从站"视图里出现（那边才有两组）。
            // 所以这里只要求"当前视图里但凡露出来的 chevron 都得够大"，
            // 树形视图那三颗在 RunSidebarViewSelfCheck 里单独验。
            bool chevronOk = chevrons.Count >= 1
                && chevrons.All(button => button.Bounds.Width >= 23 && button.Bounds.Height >= 23);

            string chevronSize = chevrons.Count == 0
                ? "无"
                : string.Join("/", chevrons.Select(button => $"{button.Bounds.Width:0}×{button.Bounds.Height:0}"));
            int hiddenChevrons = allChevrons.Count - chevrons.Count;
            Log(dir, $"collapse-button: 数量={chevrons.Count} 尺寸={chevronSize}"
                + (hiddenChevrons > 0 ? $"（另有 {hiddenChevrons} 个在隐藏容器内未计）" : string.Empty)
                + $" => {(chevronOk ? "PASS" : "FAIL")}");

            // ④ 底部页签字号：主题模板里 TabItem 默认字号偏大，压到 12 才算改到位
            System.Collections.Generic.List<TabItem> tabs = mainWindow.GetVisualDescendants()
                .OfType<TabItem>()
                .ToList();

            bool tabOk = tabs.Count >= 2
                && tabs.All(tab => tab.FontSize <= 12.5 && tab.Bounds.Height <= 30.5);

            string tabDetail = tabs.Count == 0
                ? "无"
                : string.Join("/", tabs.Select(tab => $"{tab.Header}:{tab.FontSize:0}px,{tab.Bounds.Height:0}高"));
            Log(dir, $"bottom-tabs: {tabDetail} => {(tabOk ? "PASS" : "FAIL")}");

            // ⑤ 底部面板"页签长进内容区"（第十三轮）。
            //    用户的原话是"点中的页签在视角上和下面的内容连起来，角上都是圆角"。
            //    "看着连上了"不能靠眼睛判，这里量三条硬指标：
            //      a) 选中页签的下沿 y 必须等于内容区顶边 y —— 差一点就是一道缝；
            //      b) 选中页签的底色必须与内容区**同色** —— 不同色的话即使贴死了也看得见接缝；
            //      c) 内容区必须挂了圆角 Clip —— Border 的 CornerRadius 只画自己的背景和描边、
            //         不裁子元素（第十轮踩过），没挂 Clip 时 DataGrid 的方角会盖住面板圆角，
            //         表现就是用户说的"下面这个输出信息的框还是直角的"。
            //    这三条任意一条不成立，"连成一体"就是没做成，只是截图上碰巧看着还行。
            TabControl bottomTabs = mainWindow.GetVisualDescendants()
                .OfType<TabControl>()
                .FirstOrDefault(tab => tab.Items.OfType<TabItem>()
                    .Any(item => (item.Header as string) == "ASDU 报文监视"));

            TabItem selectedTab = bottomTabs?.SelectedItem as TabItem;
            Border tabRoot = selectedTab?.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(border => border.Name == "root");
            Border contentCard = bottomTabs?.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(border => border.Name == "PART_ContentCard");
            DataGrid monitorGrid = mainWindow.GetVisualDescendants()
                .OfType<DataGrid>()
                .FirstOrDefault(grid => grid.Classes.Contains("monitorGrid"));

            bool seamAligned = false;
            bool seamSameColor = false;
            string seamDetail = "未找到页签或内容区";
            if (bottomTabs != null && tabRoot != null && contentCard != null)
            {
                Point? tabBottom = tabRoot.TranslatePoint(new Point(0, tabRoot.Bounds.Height), bottomTabs);
                Point? cardTop = contentCard.TranslatePoint(new Point(0, 0), bottomTabs);
                if (tabBottom.HasValue && cardTop.HasValue)
                {
                    seamAligned = Math.Abs(tabBottom.Value.Y - cardTop.Value.Y) <= 0.75;

                    Color? tabColor = (tabRoot.Background as ISolidColorBrush)?.Color;
                    Color? cardColor = (contentCard.Background as ISolidColorBrush)?.Color;
                    seamSameColor = tabColor.HasValue && cardColor.HasValue && tabColor.Value == cardColor.Value;

                    seamDetail = $"页签下沿y={tabBottom.Value.Y:0.##} 内容区顶y={cardTop.Value.Y:0.##} "
                        + $"页签底={DescribeBrush(tabRoot.Background)} 内容区底={DescribeBrush(contentCard.Background)}";
                }
            }

            bool gridClipped = monitorGrid?.Clip != null
                && monitorGrid.Clip.Bounds.Width >= monitorGrid.Bounds.Width - 1.5;
            string clipDetail = monitorGrid == null
                ? "未找到监控表格"
                : monitorGrid.Clip == null
                    ? $"未挂 Clip（控件 {monitorGrid.Bounds.Width:0}×{monitorGrid.Bounds.Height:0}）"
                    : $"Clip {monitorGrid.Clip.Bounds.Width:0}×{monitorGrid.Clip.Bounds.Height:0}"
                      + $" / 控件 {monitorGrid.Bounds.Width:0}×{monitorGrid.Bounds.Height:0}";
            Log(dir, $"bottom-card: {seamDetail} 内容区圆角={clipDetail} "
                + $"=> {(seamAligned && seamSameColor && gridClipped ? "PASS" : "FAIL")}");

            // ⑥ 第二个页签也要圆角。
            //    链路日志那页的内容在**没被选中时根本不在可视树上**（TabControl 只把选中项的
            //    Content 放进 PART_SelectedContentHost），所以它的 Clip 只能真正切过去、
            //    布好局之后才验得到。切回来是必须的，后面几张截图还指望停在报文监视页。
            if (bottomTabs != null && bottomTabs.ItemCount > 1)
            {
                bottomTabs.SelectedIndex = 1;
                WarmUpLayout(mainWindow);

                ListBox logList = mainWindow.GetVisualDescendants()
                    .OfType<ListBox>()
                    .FirstOrDefault(list => list.Name == "LogList");
                bool logClipped = logList?.Clip != null
                    && logList.Clip.Bounds.Width >= logList.Bounds.Width - 1.5;
                Log(dir, $"bottom-card-log: 链路日志列表 尺寸={logList?.Bounds.Width:0}×{logList?.Bounds.Height:0} "
                    + $"圆角裁剪={(logClipped ? "有" : "无")} => {(logClipped ? "PASS" : "FAIL")}");

                bottomTabs.SelectedIndex = 0;
                WarmUpLayout(mainWindow);
            }
        }

        /// <summary>
        /// 多主题自检。用户的原话是"不要换了主题有些字就看不清"，所以这里不靠肉眼：
        /// 五套主题逐个切过去，把**每一组会影响可读性的"前景/背景"组合**都算一遍
        /// WCAG 对比度，低于 4.5:1 就算失败。
        ///
        /// 为什么必须是 4.5:1：这是 WCAG AA 对正文字号（本工具 12px）的要求。
        /// 3:1 只适用于 18px 以上大字或粗体大标题，本工具界面全是 11~12px 小字，
        /// 按 3:1 放过去，现场在投运屏前就会看不清。
        ///
        /// 另外还验两件"结构正确性"，光看颜色值是验不出来的：
        ///   ① RequestedThemeVariant 有没有跟着切 —— 不切的话 SimpleTheme 自带的
        ///      滚动条、右键菜单、模态对话框仍然是浅色，深色主题里会出现一片白块；
        ///   ② 真实控件的 Foreground 有没有变 —— 样式里写成 StaticResource 的话
        ///      画刷在加载时就固定了，换字典不会重绘，表现为"切了主题只有一半区域变色"。
        /// </summary>
        private static async Task RunThemeSelfCheckAsync(string dir, Window mainWindow, MainWindowViewModel viewModel)
        {
            if (viewModel == null)
            {
                Log(dir, "theme-contrast: 跳过（无主视图模型）");
                return;
            }

            // 组合清单：每一项都是界面上真实存在的一对前景色/背景色。
            (string Fg, string Bg, string Label)[] pairs =
            {
                ("TextPrimaryBrush", "SurfaceBrush", "正文/面板"),
                ("TextPrimaryBrush", "AppBackgroundBrush", "正文/窗口底"),
                ("TextSecondaryBrush", "SurfaceBrush", "次要字/面板"),
                ("TextSecondaryBrush", "SurfaceAltBrush", "次要字/分组条"),
                ("TextSecondaryBrush", "ToolbarBrush", "次要字/工具条"),
                ("RowTextBrush", "SurfaceBrush", "行字/偶数行"),
                ("RowTextBrush", "GridRowAltBrush", "行字/奇数行"),
                ("RowTextBrush", "RowHoverBrush", "行字/悬停"),
                ("RowTextBrush", "RowSelectedBrush", "行字/选中"),
                ("PrimarySoftTextBrush", "PrimarySoftBrush", "主色浅底"),
                ("SelectionTextBrush", "SelectionBrush", "列表选中"),
                ("OnPrimaryBrush", "PrimaryBrush", "主色实心"),
                ("ValueOnBrush", "SurfaceBrush", "值-合"),
                ("ValueOffBrush", "SurfaceBrush", "值-分"),
                ("ValueMidBrush", "SurfaceBrush", "值-中间"),
                ("ValueBadBrush", "SurfaceBrush", "值-不确定"),
                ("ValueNeutralBrush", "SurfaceBrush", "值-模拟量"),
                ("QualityOkFgBrush", "QualityOkBgBrush", "品质-正常"),
                ("QualityWarnFgBrush", "QualityWarnBgBrush", "品质-警告"),
                ("QualityBadFgBrush", "QualityBadBgBrush", "品质-异常"),
                ("SuccessTextBrush", "SuccessSoftBrush", "正常态浅底"),
                ("WarningTextBrush", "WarningSoftBrush", "警告态浅底"),
                ("DangerTextBrush", "DangerSoftBrush", "异常态浅底"),
                ("DangerBrush", "SurfaceBrush", "删除红/面板")
            };

            const double MinimumContrast = 4.5;

            string originalId = ThemeService.Current?.Id ?? ThemeService.Options[0].Id;

            // 取一个真实的次要文字控件做"到底刷没刷新"的证据
            TextBlock hintSample = mainWindow.GetVisualDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(text => text.Classes.Contains("hint"));

            foreach (AppThemeOption option in ThemeService.Options)
            {
                ThemeService.Apply(option.Id, persist: false);
                Dispatcher.UIThread.RunJobs();

                string variantOk = (option.IsDark
                    ? Application.Current?.RequestedThemeVariant == ThemeVariant.Dark
                    : Application.Current?.RequestedThemeVariant == ThemeVariant.Light)
                    ? "OK" : "错";

                // 逐组算对比度，记下最差的一组，日志里直接给出数值方便调色
                double worst = double.MaxValue;
                string worstLabel = string.Empty;
                int bad = 0;

                foreach ((string fg, string bg, string label) in pairs)
                {
                    Color foreground = ResolveColor(fg);
                    Color background = ResolveColor(bg);
                    double ratio = ContrastRatio(foreground, background);
                    if (ratio < worst)
                    {
                        worst = ratio;
                        worstLabel = label;
                    }
                    if (ratio < MinimumContrast)
                    {
                        bad++;
                    }
                }

                Log(dir, $"theme-contrast[{option.Name}]: 变体={variantOk} "
                    + $"组合={pairs.Length} 不达标={bad} 最差={worst:0.00}:1({worstLabel}) => "
                    + (bad == 0 && variantOk == "OK" ? "PASS" : "FAIL"));
            }

            // 真实控件刷新：切到深色主题，hint 文字的前景色必须真的变成新主题的值
            string before = hintSample == null ? "<无控件>" : DescribeBrush(hintSample.Foreground);
            AppThemeOption dark = ThemeService.Options.First(option => option.IsDark);
            ThemeService.Apply(dark.Id, persist: false);

            // 等主题过渡真的走完，再量数值、再截图。
            // SimpleTheme 给按钮的底色挂了 Transition：切主题后底色是从旧值**渐变**到新值的，
            // 不等就会读到渐变途中的中间色（实测主题胶囊底读到 #D3D8DC，
            // 而目标值是 #263548），截图也是一帧"浅灰底压浅灰字"的假故障。
            await SettleThemeAsync(mainWindow);

            string after = hintSample == null ? "<无控件>" : DescribeBrush(hintSample.Foreground);
            string expected = DescribeBrush(ThemePalette.Get("TextSecondaryBrush"));
            bool liveRefresh = hintSample != null && after == expected && before != after;

            Log(dir, $"theme-live: 次要文字 {before}→{after} 期望={expected} "
                + $"已跟随刷新={liveRefresh} => " + (liveRefresh ? "PASS" : "FAIL"));

            // 主题胶囊单独验一遍：它是标题栏里唯一"有色底"的控件，
            // 底/字必须同时换成深色主题的那一套。只换字不换底 = 浅灰字压浅灰底，
            // 对比度 1.5:1，等于看不见——这种"半跟随"从代码里读是读不出来的
            // （两处都写的 DynamicResource），只能读真实控件。
            Button chip = mainWindow.FindControl<Button>("ThemeButton");
            string chipBg = DescribeBrush(chip?.Background);
            string chipFg = DescribeBrush(chip?.Foreground);
            string wantBg = DescribeBrush(ThemePalette.Get("SurfaceAltBrush"));
            string wantFg = DescribeBrush(ThemePalette.Get("TextSecondaryBrush"));
            bool chipThemed = chip != null && chipBg == wantBg && chipFg == wantFg;
            Log(dir, $"theme-chip-tokens: 底={chipBg}（期望 {wantBg}） 字={chipFg}（期望 {wantFg}） => "
                + (chipThemed ? "PASS" : "FAIL"));

            // 隔行底色也要跟着主题走（写死白色的话深色主题里会刺眼）
            WarmUpLayout(mainWindow);
            System.Collections.Generic.List<DataGridRow> rows = mainWindow.GetVisualDescendants()
                .OfType<DataGridRow>()
                .OrderBy(row => row.Bounds.Y)
                .Take(2)
                .ToList();
            bool rowsThemed = rows.Count >= 2
                && DescribeBrush(rows[0].Background) == DescribeBrush(ThemePalette.Get("SurfaceBrush"))
                && DescribeBrush(rows[1].Background) == DescribeBrush(ThemePalette.Get("GridRowAltBrush"));
            Log(dir, $"theme-rows: 偶数行={DescribeBrush(rows.ElementAtOrDefault(0)?.Background)} "
                + $"奇数行={DescribeBrush(rows.ElementAtOrDefault(1)?.Background)} => "
                + (rowsThemed ? "PASS" : "FAIL"));

            // 顺手留一张深色主题的整窗截图。
            // 主题自检全程只验数字（对比度、画刷有没有跟着换），
            // 数字全过但看着别扭的情况（比如新做的"页签连内容区"面板在深色下接缝是否还成立）
            // 只有图能看出来，所以这张图是给下一轮改样式的人留的证据。
            Save(mainWindow, Path.Combine(dir, "23-深色主题.png"));

            // 主题弹窗：按钮点开 → 项数与主题数一致、每项有色点、当前项带勾 → 点一项真的换主题。
            // 这几件事任意一件错了，用户就会看到"点了没反应"或"选中的和实际不符"，
            // 而这类问题在代码里读着完全正确，只能靠真跑一遍。
            if (mainWindow is MainWindow shell
                && mainWindow.FindControl<Button>("ThemeButton") is { } themeButton)
            {
                themeButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                StackPanel list = shell.ThemeMenuItems;
                System.Collections.Generic.List<Button> items =
                    list?.Children.OfType<Button>().ToList()
                    ?? new System.Collections.Generic.List<Button>();

                bool countOk = items.Count == ThemeService.Options.Count;

                // 每一项都必须有代表色圆点（这是这一轮改造的核心视觉），
                // 少一个就说明代码里漏了 swatch，界面又会退回"前面一个黑圆点"
                bool swatchOk = items.All(item =>
                    item.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Any());

                // 当前生效项：恰好一项 active，且恰好那一项带勾
                System.Collections.Generic.List<Button> activeItems =
                    items.Where(item => item.Classes.Contains("active")).ToList();
                bool oneActive = activeItems.Count == 1;
                bool checkMatchesActive = oneActive
                    && activeItems[0].GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
                        .Any(path => path.IsVisible);

                Button target = items.LastOrDefault();
                target?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                bool applied = target?.Tag is string pickedId
                    && ThemeService.Current?.Id == pickedId;

                shell.ThemeMenuPopup.IsOpen = false;
                Dispatcher.UIThread.RunJobs();

                bool menuOk = countOk && swatchOk && oneActive && checkMatchesActive && applied;
                Log(dir, $"theme-menu: 项数={items.Count}/{ThemeService.Options.Count} "
                    + $"每项有色点={swatchOk} 仅一项高亮={oneActive} "
                    + $"高亮项带勾={checkMatchesActive} 点选生效={applied} => "
                    + (menuOk ? "PASS" : "FAIL"));
            }
            else
            {
                Log(dir, "theme-menu: 跳过（找不到主题按钮）");
            }

            // 顺手留一张"纯白清新绿"（主题 F）的整窗截图。
            // 这套配色是用户给的设计稿，白底 + 工业绿，要看的恰恰是数字说不清的东西：
            // 大面积纯白会不会显得"空"、浅绿底的状态胶囊在纯白背景上还分不分得出来。
            AppThemeOption fresh = ThemeService.Options
                .FirstOrDefault(option => option.Id == "fresh-green");
            if (fresh != null)
            {
                ThemeService.Apply(fresh.Id, persist: false);
                await SettleThemeAsync(mainWindow);
                Save(mainWindow, Path.Combine(dir, "23b-主题-清新绿.png"));
            }

            // 恢复用户原来的主题，别让自检把界面留在深色上
            ThemeService.Apply(originalId);
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>
        /// 第十轮：菜单与标题栏的精致度改造。
        ///
        /// 这一轮全是"看着精不精致"的活，但有三件事是能客观量的，不该只靠眼睛：
        /// ① 圆角到底生效没有（读 ContextMenu / 弹窗卡片的 CornerRadius）；
        /// ② 每一行是不是真带了图标，而且图标真的占了左侧那 20 多像素
        ///    ——只判断 Icon 属性非空不够，SimpleTheme 的模板第一列是写死宽度的，
        ///    图标塞进去也可能被挤成 0；
        /// ③ 主题弹窗里每项都有代表色圆点、当前项唯一高亮且带勾。
        /// 图标画得好不好看，只能靠截图。
        /// </summary>
        private static void RunMenuPolishSelfCheck(string dir, Window mainWindow)
        {
            DataGrid grid = mainWindow.FindControl<DataGrid>("PointDataGrid");
            ListBox slaveList = mainWindow.FindControl<ListBox>("SlaveList");
            Menu titleMenu = mainWindow.FindControl<Menu>("TitleMenu");

            // 右键菜单在被打开之前还没挂到可视树上，Styles 也没跑过——
            // 这时候读 CornerRadius 只会读到默认值 0，会得出"圆角没生效"的假结论。
            // 所以先打开一次再量。
            ContextMenu pointMenu = grid?.ContextMenu;
            ContextMenu slaveMenu = slaveList?.ContextMenu;
            if (pointMenu != null && grid != null)
            {
                pointMenu.Open(grid);
                Dispatcher.UIThread.RunJobs();
            }

            if (slaveMenu != null && slaveList != null)
            {
                slaveMenu.Open(slaveList);
                Dispatcher.UIThread.RunJobs();
            }

            // ① 两个右键菜单都得是圆角卡片
            bool roundOk = pointMenu != null && slaveMenu != null
                && pointMenu.CornerRadius.TopLeft >= 8 && slaveMenu.CornerRadius.TopLeft >= 8
                && pointMenu.Padding.Left >= 4;
            Log(dir, $"menu-round: 点表右键圆角={pointMenu?.CornerRadius.TopLeft:0.#} "
                + $"从站右键圆角={slaveMenu?.CornerRadius.TopLeft:0.#} "
                + $"内边距={pointMenu?.Padding.Left:0.#} => "
                + (roundOk ? "PASS" : "FAIL"));

            slaveMenu?.Close();
            Dispatcher.UIThread.RunJobs();

            // ② 菜单栏顶级项 + 两个右键菜单的每一项，都要有图标
            var topItems = titleMenu?.Items.OfType<MenuItem>().ToList()
                ?? new System.Collections.Generic.List<MenuItem>();
            // 顶级项数量随版本变：1.9.1 起「帮助」被挪进设置，「视图」在 1.9.9 整栏删除，
            // 现在是 5 个（文件 / 从站 / 通道 / 信息对象 / 工具）。
            // 这里只卡下限、不写死等号：以后再加一栏不该顺手让这条断言变红。
            bool topIconOk = topItems.Count >= 5 && topItems.All(item => item.Icon != null);

            int ctxTotal = 0;
            int ctxWithIcon = 0;
            foreach (ContextMenu menu in new[] { pointMenu, slaveMenu })
            {
                if (menu == null)
                {
                    continue;
                }

                foreach (MenuItem item in menu.Items.OfType<MenuItem>())
                {
                    ctxTotal++;
                    if (item.Icon != null)
                    {
                        ctxWithIcon++;
                    }
                }
            }

            Log(dir, $"menu-icons: 顶级项 {topItems.Count} 个全带图标={topIconOk} "
                + $"右键项 {ctxWithIcon}/{ctxTotal} 带图标 => "
                + ((topIconOk && ctxTotal > 0 && ctxWithIcon == ctxTotal) ? "PASS" : "FAIL"));

            // ③ 把点表右键菜单真打开一次，量图标列有没有被用起来。
            //    只判断 Icon 非空是不够的：模板里那一列真要是 0 宽，
            //    图标会缩成一个点，肉眼看就是"前面还是空的"。
            double iconLeft = double.NaN;
            double textLeft = double.NaN;
            bool geometryOk = false;
            if (pointMenu != null && grid != null)
            {
                if (!pointMenu.IsOpen)
                {
                    pointMenu.Open(grid);
                    Dispatcher.UIThread.RunJobs();
                }

                MenuItem container = pointMenu.ContainerFromIndex(0) as MenuItem;
                MenuGlyph icon = container?.GetVisualDescendants()
                    .OfType<MenuGlyph>()
                    .FirstOrDefault(glyph => glyph.Classes.Contains("menuIcon"));
                // 量表头容器而不是文字控件：文字控件在不同版本里类型不一样，
                // 而 PART_HeaderPresenter 的位置才是"文字从哪一列开始"的真正答案
                ContentPresenter label = container?.GetVisualDescendants()
                    .OfType<ContentPresenter>()
                    .FirstOrDefault(presenter => presenter.Name == "PART_HeaderPresenter");

                if (container != null && icon != null && label != null)
                {
                    Point? iconPos = icon.TranslatePoint(new Point(0, 0), container);
                    Point? labelPos = label.TranslatePoint(new Point(0, 0), container);
                    if (iconPos.HasValue && labelPos.HasValue)
                    {
                        iconLeft = iconPos.Value.X;
                        textLeft = labelPos.Value.X;

                        // 图标离左边 4~12px（不贴边也不飘），文字在图标右侧 18px 开外
                        geometryOk = iconLeft >= 3 && iconLeft <= 12
                            && textLeft >= 18 && textLeft <= 34
                            && icon.Bounds.Width >= 10;
                    }
                }

                pointMenu.Close();
                Dispatcher.UIThread.RunJobs();
            }

            Log(dir, $"menu-item-geometry: 图标左距={iconLeft:0.#} 文字左距={textLeft:0.#} => "
                + (geometryOk ? "PASS" : "FAIL"));

            // ④ 主题弹窗：卡片圆角 + 不再是系统菜单
            Border card = null;
            if (mainWindow is MainWindow shell)
            {
                shell.BuildThemeMenu();
                card = shell.ThemeMenuCard;
            }

            bool cardOk = card != null
                && card.CornerRadius.TopLeft >= 8
                && card.Padding.Left >= 4
                && card.Classes.Contains("themeCard");
            Log(dir, $"theme-popup: 卡片圆角={card?.CornerRadius.TopLeft:0.#} "
                + $"内边距={card?.Padding.Left:0.#} => "
                + (cardOk ? "PASS" : "FAIL"));

            // ⑤ 标题栏右侧的两个新按钮：存在、可见、排在主题胶囊右边、且在标题栏之内
            Button github = mainWindow.FindControl<Button>("GitHubButton");
            Button update = mainWindow.FindControl<Button>("UpdateButton");
            Button chip = mainWindow.FindControl<Button>("ThemeButton");
            Border titleBar = mainWindow.FindControl<Border>("TitleBar");

            bool buttonsOk = false;
            double githubX = double.NaN;
            double updateX = double.NaN;
            if (github != null && update != null && chip != null && titleBar != null)
            {
                Point? g = github.TranslatePoint(new Point(0, 0), titleBar);
                Point? u = update.TranslatePoint(new Point(0, 0), titleBar);
                Point? c = chip.TranslatePoint(new Point(0, 0), titleBar);
                if (g.HasValue && u.HasValue && c.HasValue)
                {
                    githubX = g.Value.X;
                    updateX = u.Value.X;
                    buttonsOk = github.IsVisible && update.IsVisible
                        && githubX > c.Value.X + chip.Bounds.Width - 1
                        && updateX > githubX
                        && githubX + github.Bounds.Width <= titleBar.Bounds.Width
                        && github.Bounds.Width >= 26;
                }
            }

            Log(dir, $"titlebar-buttons: GitHub={github?.IsVisible}@x{githubX:0.#} "
                + $"更新={update?.IsVisible}@x{updateX:0.#} => "
                + (buttonsOk ? "PASS" : "FAIL"));
        }

        /// <summary>
        /// 把三处弹层各截一张：主题卡片、点表右键菜单、菜单栏下拉。
        ///
        /// 为什么不能直接用主窗口的位图：Popup / ContextMenu 是独立顶层窗口，
        /// RenderTargetBitmap 渲染主窗口时它们根本不在那棵树里。
        /// 但弹层里的内容控件本身是普通控件，有布局有尺寸，直接渲染它就行。
        /// </summary>
        private static async Task CapturePopupsAsync(string dir, Window mainWindow)
        {
            // ① 主题选择卡片
            if (mainWindow is MainWindow shell)
            {
                shell.BuildThemeMenu();
                shell.ThemeMenuPopup.IsOpen = true;
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(240);
                SaveVisual(shell.ThemeMenuCard, Path.Combine(dir, "20-主题弹窗.png"));
                shell.ThemeMenuPopup.IsOpen = false;
                Dispatcher.UIThread.RunJobs();
            }

            // ② 点表右键菜单
            DataGrid grid = mainWindow.FindControl<DataGrid>("PointDataGrid");
            if (grid?.ContextMenu is { } contextMenu)
            {
                contextMenu.Open(grid);
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(200);
                SaveVisual(contextMenu, Path.Combine(dir, "21-右键菜单.png"));
                contextMenu.Close();
                Dispatcher.UIThread.RunJobs();
            }

            // ③ 菜单栏下拉：顶层项的 Popup 内容要从模板里挖出来
            Menu titleMenu = mainWindow.FindControl<Menu>("TitleMenu");
            MenuItem top = titleMenu?.Items.OfType<MenuItem>().FirstOrDefault();
            if (top != null)
            {
                top.IsSubMenuOpen = true;
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(200);

                Avalonia.Controls.Primitives.Popup popup = top.GetVisualDescendants()
                    .OfType<Avalonia.Controls.Primitives.Popup>()
                    .FirstOrDefault(item => item.Name == "PART_Popup");
                if (popup?.Child is Control content)
                {
                    SaveVisual(content, Path.Combine(dir, "22-菜单下拉.png"));
                }
                else
                {
                    Log(dir, "22-菜单下拉: 跳过（拿不到 Popup 内容）");
                }

                top.IsSubMenuOpen = false;
                Dispatcher.UIThread.RunJobs();
            }
        }

        /// <summary>把任意控件按它自己的尺寸渲染成 PNG（用于 Popup 里的内容）</summary>
        private static void SaveVisual(Control control, string path)
        {
            if (control == null)
            {
                return;
            }

            Dispatcher.UIThread.RunJobs();

            double scaling = TopLevel.GetTopLevel(control)?.RenderScaling ?? 1.0;
            int width = (int)Math.Round(control.Bounds.Width * scaling);
            int height = (int)Math.Round(control.Bounds.Height * scaling);
            if (width <= 0 || height <= 0)
            {
                Log(Path.GetDirectoryName(path), $"skip {Path.GetFileName(path)}: zero size");
                return;
            }

            using RenderTargetBitmap bitmap = new RenderTargetBitmap(
                new PixelSize(width, height),
                new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(control);
            bitmap.Save(path);
            Log(Path.GetDirectoryName(path), $"saved {Path.GetFileName(path)} {width}x{height}");
        }

        /// <summary>从应用资源里取令牌的实际颜色</summary>
        private static Color ResolveColor(string key)
        {
            if (Application.Current != null
                && Application.Current.TryFindResource(key, out object resource)
                && resource is ISolidColorBrush solid)
            {
                return solid.Color;
            }
            return Colors.Transparent;
        }

        /// <summary>WCAG 相对亮度</summary>
        private static double RelativeLuminance(Color color)
        {
            static double Channel(byte value)
            {
                double normalized = value / 255.0;
                return normalized <= 0.03928
                    ? normalized / 12.92
                    : Math.Pow((normalized + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }

        /// <summary>WCAG 对比度（1:1 ~ 21:1）</summary>
        private static double ContrastRatio(Color foreground, Color background)
        {
            double a = RelativeLuminance(foreground);
            double b = RelativeLuminance(background);
            double lighter = Math.Max(a, b);
            double darker = Math.Min(a, b);
            return (lighter + 0.05) / (darker + 0.05);
        }

        /// <summary>
        /// 第十七轮：工具条图标"一套墨色 + 一个比例尺"的自检。
        ///
        /// 为什么这一轮要断言：这排图标的毛病（画法不一、颜色各配一种、线宽差三成）
        /// 全都是"看着才知道"的问题，而且极易在下次改样式时回潮——
        /// 谁往工具条里加一颗新图标，随手写个 Fill="{PrimaryBrush}" 就又花了。
        /// 所以这里不看图，直接量三个数：
        ///   ① 数量：11 个按钮对应的 13 个 MenuGlyph（启停、全部启停各两个互斥图标），
        ///      必须全部是 MenuGlyph —— 混进一个 Path，缩放比例尺就又不统一了；
        ///   ② 墨色：所有 Stroke / Fill 的颜色必须等于主题的 TextSecondaryBrush
        ///      （也就是标题栏那排图标的同一个令牌），一个都不能是主色或危险红；
        ///   ③ 线宽：按 MenuGlyph 的固定画布算出每颗图标的实际线宽
        ///      （StrokeThickness × Bounds.Width ÷ 20），最大与最小之差必须为 0。
        /// ③ 是这一轮的核心：改前用的是 Path + Stretch=Uniform，
        /// 同样写 1.95，插头实际 1.73、垃圾桶 2.10 —— 差三成，肉眼看就是"粗细不一"。
        /// </summary>
        private static void RunToolbarIconSelfCheck(string dir, Window mainWindow)
        {
            Border toolbar = mainWindow.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(border => border.Classes.Contains("toolbar"));

            List<MenuGlyph> allGlyphs = toolbar?.GetVisualDescendants()
                .OfType<MenuGlyph>()
                .Where(glyph => glyph.Classes.Contains("toolIcon"))
                .ToList() ?? new List<MenuGlyph>();

            // 1.10：工具条最左多了一颗**保存**图标按钮。它不是"整排一个墨色"里的成员——
            // 它是全场唯一会变状态的图标（无改动=TextDisabled / 有改动=主题主色 /
            // 写盘中=禁用）。混进下面三条断言里必然红，所以先剔出去，
            // 它自己那套断言在 RunProjectSelfCheck 里（toolbar-save-*）。
            MenuGlyph saveGlyph = mainWindow.FindControl<Button>("ToolbarSaveButton")?
                .GetVisualDescendants()
                .OfType<MenuGlyph>()
                .FirstOrDefault(glyph => glyph.Classes.Contains("toolIcon"));

            List<MenuGlyph> glyphs = allGlyphs.Where(glyph => !ReferenceEquals(glyph, saveGlyph)).ToList();

            // ① 数量与控件类型
            //
            // 期望值是**跟着视图走**的：1.9.0 起"新建通道"只在"通道 / 从站"视图里出现
            // （扁平视图下没有通道这个对象）。写死 12 或写死 13 都不行——
            // 自检跑在哪个视图取决于上一次留下的设置，写死就等于随机红一次绿一次。
            //
            // 但总数也不能按当前视图硬算。IsVisible=false 只是"不参与布局"，
            // 并不回收已展开的模板：「新建通道」这颗按钮若在本次进程里曾经可见过
            // （自检常常从树形视图起步，之后才切成扁平），切成扁平后按钮本身变
            // IsVisible=false，按钮模板和里面那颗 MenuGlyph 仍旧挂在可视树上。
            // 于是扁平视图下 Count 可能是 12，也可能还是 13——取决于这一轮怎么走过来。
            // 真正稳的判据是"有效可见"（自己可见 + 各级父级都可见）：
            // 扁平 9 颗、树形 10 颗（启停 / 全部启停各藏一颗互斥图标）。
            //
            // 1.9.9：实时曲线从工具条撤回「工具」菜单，整排少一颗，
            // 所以这里从 10/11 各减一。数量写死在这个方法里是有意的：
            // 工具栏"加一颗/减一颗"是设计决定，值得让自检停下来提醒一次。
            bool toolbarTreeView = mainWindow.DataContext is MainWindowViewModel { IsTreeView: true };
            int expectedVisible = toolbarTreeView ? 10 : 9;

            int solidCount = glyphs.Count(glyph => glyph.Classes.Contains("solid"));
            int visibleCount = glyphs.Count(glyph => glyph.IsEffectivelyVisible);
            // 实心剪影从 5 变 6：1.10.2 加了"停止批量变化"那颗方形图标。
            // 它平时 IsVisible=false（不参与布局，也不会有禁用色混进来），
            // 所以有效可见数仍是 9/10。加这颗是为了让用户关掉参数窗口之后
            // 还能在工具条上停下变化——属于设计决定，不是随手加图标。
            bool countOk = glyphs.Count >= 11
                && glyphs.Count <= 13
                && visibleCount == expectedVisible
                && solidCount == 6;

            Log(dir, $"toolbar-icon-set: 图标控件={glyphs.Count}(有效可见 {visibleCount}) "
                + $"实心={solidCount} 预期 总数11~12/可见{expectedVisible} => "
                + (countOk ? "PASS" : "FAIL"));

            // ② 墨色：全部等于主题 TextSecondaryBrush
            Color expectedInk = ReadThemeColor("TextSecondaryBrush");
            Color expectedPrimary = ReadThemeColor("PrimaryBrush");
            Color expectedDanger = ReadThemeColor("DangerBrush");

            List<string> offInk = new List<string>();
            foreach (MenuGlyph glyph in glyphs)
            {
                if (glyph.Stroke is not ISolidColorBrush stroke || stroke.Color != expectedInk)
                {
                    offInk.Add($"描边={(glyph.Stroke as ISolidColorBrush)?.Color}");
                    continue;
                }

                // 实心的那几颗还要检查 Fill 同色：只要 Fill 留空，
                // 出来的就是一个空心三角形，"实心剪影"这件事就没做到
                if (!glyph.Classes.Contains("solid"))
                {
                    continue;
                }

                if (glyph.Fill is not ISolidColorBrush fill || fill.Color != expectedInk)
                {
                    offInk.Add($"填充={(glyph.Fill as ISolidColorBrush)?.Color}");
                }
            }

            bool inkOk = offInk.Count == 0;
            Log(dir, $"toolbar-icon-ink: 期望=TextSecondary {expectedInk} "
                + $"（主色 {expectedPrimary} / 危险 {expectedDanger} 都不该出现）"
                + $" 偏离 {offInk.Count} 颗{(offInk.Count == 0 ? string.Empty : "[" + string.Join(";", offInk) + "]")} => "
                + (inkOk ? "PASS" : "FAIL"));

            // ③ 线宽：固定 20 单位画布下的实际线宽必须一致
            //    （MenuGlyph.CanvasWindow 是私有常量，这里按它文档里的 20 复算，
            //      同时校验 Bounds 真的是 16 —— 换尺寸就等于换了比例尺）
            const double canvasWindow = 20.0;
            // 必须同时过滤"有效可见"：不可见的控件不参与布局，但 Bounds 不会清零，
            // 会保留上一次布局的 16——单看 Bounds.Width > 0 会把已经藏起来的图标算进来。
            List<double> widths = glyphs
                .Where(glyph => glyph.IsEffectivelyVisible && glyph.Bounds.Width > 0)
                .Select(glyph => glyph.StrokeThickness * glyph.Bounds.Width / canvasWindow)
                .ToList();

            double minWidth = widths.Count > 0 ? widths.Min() : 0;
            double maxWidth = widths.Count > 0 ? widths.Max() : 0;
            double boxSize = glyphs.Count > 0 ? glyphs[0].Bounds.Width : 0;
            bool weightOk = widths.Count == expectedVisible && boxSize >= 15.5 && boxSize <= 16.5
                && Math.Abs(maxWidth - minWidth) < 0.001;

            Log(dir, $"toolbar-icon-weight: 框={boxSize:0.#} 实际线宽={minWidth:0.###}~{maxWidth:0.###} "
                + $"（量了 {widths.Count} 颗可见图标，极差必须为 0） => "
                + (weightOk ? "PASS" : "FAIL"));
        }

        /// <summary>
        /// 第九轮：顶部合并成一行、状态栏重排之后的几何与状态自检。
        ///
        /// 这几项的共同点是"看着不对但断言不会报"——之前的自检里，
        /// 状态栏那一条是怎么摆的都过得去，结果文字整个掉到栏外面去了
        /// （实测状态栏底色只有 12px 高，文字画在它下面）。所以这里全部按坐标量：
        ///   ① 状态栏必须贴住窗口底边、高度就是样式里的 30；
        ///   ② 栏里的文字中心必须落在状态栏的垂直中心上（±2px）；
        ///   ③ 侧栏开关必须有底色（不能再是透明的一根箭头）；
        ///   ④ 标题栏里不能再有软件名，菜单必须落在标题栏那一行内；
        ///   ⑤ 主题按钮上写的就是当前主题名；
        ///   ⑥ 点表单元格必须垂直居中（用户反馈"字都贴着行上沿"）。
        /// </summary>
        private static void RunLayoutPolishSelfCheck(string dir, Window mainWindow, MainWindowViewModel viewModel)
        {
            Border statusBar = mainWindow.FindControl<Border>("StatusBar");
            Button toggle = mainWindow.FindControl<Button>("SidebarToggleButton");
            Border titleBar = mainWindow.FindControl<Border>("TitleBar");
            Button themeButton = mainWindow.FindControl<Button>("ThemeButton");
            Menu menu = mainWindow.FindControl<Menu>("TitleMenu");

            if (statusBar == null || toggle == null || titleBar == null)
            {
                Log(dir, "status-bar: FAIL（找不到状态栏 / 开关 / 标题栏）");
                return;
            }

            // ① 贴底 + 高度
            Point? barPos = statusBar.TranslatePoint(new Point(0, 0), mainWindow);
            double barTop = barPos?.Y ?? -1;
            double barBottom = barTop + statusBar.Bounds.Height;
            double windowHeight = mainWindow.ClientSize.Height;
            bool heightOk = Math.Abs(statusBar.Bounds.Height - 30) <= 0.6;
            bool flushBottom = Math.Abs(barBottom - windowHeight) <= 0.6;

            Log(dir, $"status-bar-geometry: 高度={statusBar.Bounds.Height:0.#}(应 30) 贴底={flushBottom}"
                + $"(底边={barBottom:0.#} 窗高={windowHeight:0.#}) => "
                + ((heightOk && flushBottom) ? "PASS" : "FAIL"));

            // ② 栏内文字垂直居中：拿"就绪"那一条过程信息来量
            TextBlock statusText = statusBar.GetVisualDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(text => text.Classes.Contains("statusText"));
            double offset = double.NaN;
            bool centered = false;
            if (statusText != null)
            {
                Point? textPos = statusText.TranslatePoint(new Point(0, 0), mainWindow);
                if (textPos.HasValue)
                {
                    double textCenter = textPos.Value.Y + statusText.Bounds.Height / 2;
                    offset = textCenter - (barTop + statusBar.Bounds.Height / 2);
                    centered = Math.Abs(offset) <= 2.0;
                }
            }
            Log(dir, $"status-bar-center: 文字中心对栏中心偏差={offset:0.##}px => "
                + (centered ? "PASS" : "FAIL"));

            // ③ 侧栏开关：必须有可见底色 + 描边，且不超出状态栏高度
            string toggleBackground = DescribeBrush(toggle.Background);
            bool hasFill = toggle.Background is ISolidColorBrush fill
                && fill.Color.A > 0x10
                && !string.Equals(toggleBackground, "#00FFFFFF", StringComparison.OrdinalIgnoreCase);
            bool fitsBar = toggle.Bounds.Height <= statusBar.Bounds.Height - 2
                && toggle.Bounds.Width >= 20 && toggle.Bounds.Width <= 26;
            Log(dir, $"status-toggle-look: 底色={toggleBackground} 有底色={hasFill} "
                + $"尺寸={toggle.Bounds.Width:0}x{toggle.Bounds.Height:0} 放得下={fitsBar} => "
                + ((hasFill && fitsBar) ? "PASS" : "FAIL"));

            // ④ 标题栏：不能再有软件名；菜单要落在标题栏这一行里
            bool hasAppName = titleBar.GetVisualDescendants()
                .OfType<TextBlock>()
                .Any(text => (text.Text ?? string.Empty).Contains("IEC104-Server"));
            double menuCenterOffset = double.NaN;
            bool menuInBar = false;
            Menu titleMenu = menu
                ?? titleBar.GetVisualDescendants().OfType<Menu>().FirstOrDefault();
            if (titleMenu != null)
            {
                Point? menuPos = titleMenu.TranslatePoint(new Point(0, 0), titleBar);
                if (menuPos.HasValue)
                {
                    menuCenterOffset = menuPos.Value.Y + titleMenu.Bounds.Height / 2
                        - titleBar.Bounds.Height / 2;
                    menuInBar = Math.Abs(menuCenterOffset) <= 2.0
                        && titleMenu.Bounds.Height <= titleBar.Bounds.Height;
                }
            }
            Log(dir, $"titlebar-layout: 软件名已移除={!hasAppName} 菜单在标题栏内={menuInBar}"
                + $"(中心偏差={menuCenterOffset:0.##}px) => "
                + ((!hasAppName && menuInBar) ? "PASS" : "FAIL"));

            // ⑤ 主题按钮上显示的就是当前主题名
            bool chipShowsTheme = themeButton != null
                && themeButton.GetVisualDescendants().OfType<TextBlock>()
                    .Any(text => text.Text == viewModel?.CurrentThemeName);
            Log(dir, $"theme-chip: 显示当前主题名={chipShowsTheme}({viewModel?.CurrentThemeName}) => "
                + (chipShowsTheme ? "PASS" : "FAIL"));

            // ⑥ 点表单元格垂直居中：拿第一行的"当前值"单元格和它所在行的中心比
            WarmUpLayout(mainWindow);
            DataGridRow firstRow = mainWindow.GetVisualDescendants()
                .OfType<DataGridRow>()
                .OrderBy(row => row.Bounds.Y)
                .FirstOrDefault();
            TextBlock valueCell = firstRow?.GetVisualDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(text => (text.Tag as string) == "value");

            double cellOffset = double.NaN;
            bool cellCentered = false;
            if (firstRow != null && valueCell != null)
            {
                Point? cellPos = valueCell.TranslatePoint(new Point(0, 0), firstRow);
                if (cellPos.HasValue)
                {
                    cellOffset = cellPos.Value.Y + valueCell.Bounds.Height / 2
                        - firstRow.Bounds.Height / 2;
                    cellCentered = Math.Abs(cellOffset) <= 1.5;
                }
            }
            Log(dir, $"cell-center: 当前值单元格中心对行中心偏差={cellOffset:0.##}px => "
                + (cellCentered ? "PASS" : "FAIL"));

            // ⑥b 水平方向：只有"名称"列居左，其余五列（点号/类型/值/品质/时间）全部居中。
            // 只验垂直是不够的——之前的版本垂直也没居中、水平也没居中，
            // 但"看着像那么回事"，只有把坐标量出来才知道差了 8、9 个像素。
            // 另外必须校验"居中列一个都不少"：只判断已采到的格子，
            // 某列忘了打 Tag 时会静默通过，等于没测。
            var leftAligned = new System.Collections.Generic.List<string>();
            var centerAligned = new System.Collections.Generic.List<string>();
            var expectedCentered = new[] { "address", "asdu", "value", "quality", "time" };
            bool horizontalOk = firstRow != null;

            if (firstRow != null)
            {
                foreach (DataGridCell cell in firstRow.GetVisualDescendants().OfType<DataGridCell>())
                {
                    Control tagged = cell.GetVisualDescendants()
                        .OfType<Control>()
                        .FirstOrDefault(item => item.Tag is string);
                    if (tagged?.Tag is not string tag)
                    {
                        continue;
                    }

                    Point? cellPos = cell.TranslatePoint(new Point(0, 0), firstRow);
                    Point? textPos = tagged.TranslatePoint(new Point(0, 0), firstRow);
                    if (!cellPos.HasValue || !textPos.HasValue)
                    {
                        continue;
                    }

                    double cellCenterX = cellPos.Value.X + cell.Bounds.Width / 2;
                    double textCenterX = textPos.Value.X + tagged.Bounds.Width / 2;
                    double leftGap = textPos.Value.X - cellPos.Value.X;

                    // 名称：贴左（只要求左边距很小，不参与居中判断）
                    if (tag == "name")
                    {
                        bool ok = leftGap <= 16;
                        if (!ok)
                        {
                            horizontalOk = false;
                        }
                        leftAligned.Add($"{tag}:左距={leftGap:0.#}{(ok ? "" : "✗")}");
                    }
                    else
                    {
                        double drift = textCenterX - cellCenterX;
                        bool ok = Math.Abs(drift) <= 2.5;
                        if (!ok)
                        {
                            horizontalOk = false;
                        }
                        centerAligned.Add($"{tag}:偏移={drift:0.#}{(ok ? "" : "✗")}");
                    }
                }

                // 居中列必须集齐，缺一个就判失败（对应 Tag 漏打 / 列被删掉）
                foreach (string expected in expectedCentered)
                {
                    if (!centerAligned.Exists(item => item.StartsWith(expected + ":", StringComparison.Ordinal)))
                    {
                        centerAligned.Add($"{expected}:缺席✗");
                        horizontalOk = false;
                    }
                }
            }

            Log(dir, $"cell-align: 居左[{string.Join(",", leftAligned)}] "
                + $"居中[{string.Join(",", centerAligned)}] => "
                + (horizontalOk ? "PASS" : "FAIL"));

            // ⑥c 表头也必须跟内容对齐（名称居左、其余居中）。
            // 这条是补出来的教训：单元格全绿、表头却整体左偏 30~150px，
            // 因为 DataGridColumnHeader 的 ContentPresenter 默认按内容宽度排，
            // 里面 TextBlock 的 TextAlignment=Center 根本没有可居中的宽度。
            var headerNotes = new System.Collections.Generic.List<string>();
            bool headerOk = true;
            DataGrid grid = mainWindow.FindControl<DataGrid>("PointDataGrid");
            if (grid != null)
            {
                foreach (DataGridColumnHeader header in grid.GetVisualDescendants().OfType<DataGridColumnHeader>())
                {
                    TextBlock caption = header.GetVisualDescendants().OfType<TextBlock>()
                        .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text.Text));
                    if (caption == null)
                    {
                        continue;
                    }

                    Point? headerPos = header.TranslatePoint(new Point(0, 0), grid);
                    Point? captionPos = caption.TranslatePoint(new Point(0, 0), grid);
                    if (!headerPos.HasValue || !captionPos.HasValue)
                    {
                        continue;
                    }

                    double headerCenter = headerPos.Value.X + header.Bounds.Width / 2;
                    double captionCenter = captionPos.Value.X + caption.Bounds.Width / 2;
                    double captionGap = captionPos.Value.X - headerPos.Value.X;

                    if (caption.Text == "名称")
                    {
                        bool ok = captionGap <= 16;
                        headerOk &= ok;
                        headerNotes.Add($"{caption.Text}:左距={captionGap:0.#}{(ok ? "" : "✗")}");
                    }
                    else
                    {
                        double drift = captionCenter - headerCenter;
                        bool ok = Math.Abs(drift) <= 2.5;
                        headerOk &= ok;
                        headerNotes.Add($"{caption.Text}:偏移={drift:0.#}{(ok ? "" : "✗")}");
                    }
                }

                // 六列都得测到，漏一列说明表头文本没取到（等于没测）
                headerOk &= headerNotes.Count == 6;
            }
            else
            {
                headerOk = false;
            }
            Log(dir, $"header-align: 共{headerNotes.Count}列[{string.Join(",", headerNotes)}] => "
                + (headerOk ? "PASS" : "FAIL"));

            // ⑦ 健康度：文案与颜色必须成对，且三档颜色互不相同
            bool healthOk = viewModel != null
                && !string.IsNullOrWhiteSpace(viewModel.HealthText)
                && !SameBrush(viewModel.HealthBrush, viewModel.HealthSoftBrush)
                && viewModel.HealthForeground != null;
            Log(dir, $"status-health: 文案=\"{viewModel?.HealthText}\" "
                + $"圆点={DescribeBrush(viewModel?.HealthBrush)} "
                + $"底={DescribeBrush(viewModel?.HealthSoftBrush)} "
                + $"字={DescribeBrush(viewModel?.HealthForeground)} => "
                + (healthOk ? "PASS" : "FAIL"));
        }

        /// <summary>把画刷打成 #RRGGBB，日志里一眼能看出两行到底是不是同一色</summary>
        private static string DescribeBrush(IBrush brush) =>
            brush is ISolidColorBrush solid ? solid.Color.ToString() : (brush?.ToString() ?? "null");

        /// <summary>
        /// 取 TextBlock 实际显示的整段文本，包括 Inlines 里的 Run。
        ///
        /// 为什么需要这个：TextBlock 一旦用 &lt;Run&gt; 拼内容，<see cref="TextBlock.Text"/> 就不再
        /// 反映真实字符串（它只对应"直接设 Text"那种写法，走 Inlines 时为 null）。
        /// 用 Text 去比对的断言会全部落空，表现成"明明画出来了却判定为没渲染"——
        /// 从站标签的第二行改成 Run 拼接之后，`slave-tag-endpoint` 就是这么红的。
        /// </summary>
        private static string InlineText(TextBlock block)
        {
            if (block == null)
            {
                return string.Empty;
            }

            if (block.Inlines == null || block.Inlines.Count == 0)
            {
                return block.Text ?? string.Empty;
            }

            StringBuilder builder = new();
            foreach (Inline inline in block.Inlines)
            {
                if (inline is Run run)
                {
                    builder.Append(run.Text);
                }
            }

            return builder.Length == 0 ? block.Text ?? string.Empty : builder.ToString();
        }

        /// <summary>在指定控件上抛一个垂直滚轮事件（+1 = 向上/加大）</summary>
        private static void RaiseWheel(Control target, Visual root, int delta)        {
            Pointer pointer = new(1, PointerType.Mouse, true);
            PointerPointProperties properties = new();
            Point position = target.TranslatePoint(new Point(0, 0), root) ?? new Point(0, 0);

            target.RaiseEvent(new PointerWheelEventArgs(
                target, pointer, root, position, 0, properties, KeyModifiers.None, new Vector(0, delta)));
        }

        /// <summary>
        /// 跑一遍布局 + 渲染，让虚拟化列表把行容器生成出来。
        /// 只调 RunJobs 不够：DataGrid 的行是在渲染阶段才实例化的。
        /// </summary>
        private static void WarmUpLayout(Window window)
        {
            Dispatcher.UIThread.RunJobs();

            double scaling = window.RenderScaling <= 0 ? 1.0 : window.RenderScaling;
            int width = (int)Math.Round(window.ClientSize.Width * scaling);
            int height = (int)Math.Round(window.ClientSize.Height * scaling);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            using RenderTargetBitmap bitmap = new(
                new PixelSize(width, height),
                new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(window);
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>
        /// 等主题切换的过渡动画落地。
        ///
        /// 为什么需要：SimpleTheme 给按钮的 Background 挂了 Transition，
        /// 换主题时底色是从旧值**渐变**到新值的。切完立刻读，
        /// 读到的是渐变途中的中间色；立刻截图，截到的是"浅灰底压浅灰字"的一帧，
        /// 看着像严重故障，其实只是没等它画完。
        /// 光 RunJobs 不够 —— 动画时钟要跟着真实渲染帧走，
        /// 所以这里"让出 UI 线程 + 主动渲染"重复几轮。
        /// </summary>
        private static async Task SettleThemeAsync(Window window)
        {
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(60);
                WarmUpLayout(window);
            }
        }

        /// <summary>自检过程日志，直接落盘（不经 Console）</summary>
        private static void Log(string dir, string message)
        {
            try
            {
                File.AppendAllText(Path.Combine(dir, "_capture.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
                // 自检日志写不进去也不该影响主流程
            }
        }

        /// <summary>
        /// 记一行布局尺寸。
        /// 截图只能看出"不对劲"，看不出到底哪个容器多宽——
        /// 窗口逻辑宽度、缩放比、点表实际宽度写下来，一眼能定位是布局溢出还是 DPI 变了。
        /// </summary>
        private static void LogLayout(string dir, Window mainWindow)
        {
            Grid grid = mainWindow.FindControl<Grid>("SidebarGrid");
            DataGrid pointGrid = mainWindow.FindControl<DataGrid>("PointDataGrid");

            Log(dir, $"layout: 窗口={mainWindow.Bounds.Width:0}x{mainWindow.Bounds.Height:0} "
                + $"缩放={mainWindow.RenderScaling:0.##} "
                + $"主体={grid?.Bounds.Width:0}x{grid?.Bounds.Height:0} "
                + $"点表={pointGrid?.Bounds.Width:0}x{pointGrid?.Bounds.Height:0} "
                + $"列合计={(pointGrid?.Columns.Sum(c => c.ActualWidth) ?? 0):0} "
                + $"各列={string.Join("/", pointGrid?.Columns.Select(c => $"{c.Header}={c.ActualWidth:0}") ?? Array.Empty<string>())}");
        }

        /// <summary>
        /// 给曲线窗口灌一批演示采样值。
        /// 自检时没有真实报文，采样缓冲是空的，图里只会显示"空态"提示，
        /// 看不出坐标系、网格、图例是否正常，所以这里手工造一段正弦。
        /// </summary>
        private static RealtimeChartViewModel BuildChartModel(SlaveViewModel slave)
        {
            if (slave == null)
            {
                return null;
            }

            RealtimeChartViewModel model = new(slave.Points);
            for (int step = 0; step < 70; step++)
            {
                foreach (ChartPointItem item in model.Items)
                {
                    if (!item.IsChecked)
                    {
                        continue;
                    }
                    double amplitude = 20 + item.Point.ObjectAddress % 30;
                    item.Append(item.Point.Value + amplitude * Math.Sin(step / 6.0));
                }
            }
            model.RefreshSeries();
            return model;
        }

        /// <summary>
        /// 标题栏钉子按钮 + 底部监视面板的收起/清空（1.9.8）。
        ///
        /// 这一轮全是"把已有功能挪个地方"，最怕的是**挪过去之后看着对、点了不动**：
        ///   · 钉子按钮只画了图标没接 Topmost（窗口其实没置顶）；
        ///   · 清空按钮放到面板上了，但 Command 没绑上（点了没反应）；
        ///   · 收起按钮把行高压扁了，可内容还在画、或者展开回不到原来的高度。
        /// 所以每条都要"点一下 + 读实际值"，不验属性设没设。
        /// </summary>
        private static async Task RunTitlebarMonitorSelfCheckAsync(
            string dir, Window mainWindow, MainWindowViewModel viewModel)
        {
            Button pin = mainWindow.FindControl<Button>("PinButton");
            Button settings = mainWindow.FindControl<Button>("SettingsButton");
            Button clear = mainWindow.FindControl<Button>("MonitorClearButton");
            Button toggle = mainWindow.FindControl<Button>("MonitorToggleButton");
            Border panel = mainWindow.FindControl<Border>("MonitorPanel");
            TabControl tabs = mainWindow.FindControl<TabControl>("MonitorTabs");
            Grid host = mainWindow.FindControl<Grid>("MonitorHostGrid");

            if (pin == null || settings == null || clear == null || toggle == null
                || panel == null || tabs == null || host == null)
            {
                Log(dir, "titlebar-pin: FAIL（找不到钉子 / 清空 / 收起 / 监视面板）");
                return;
            }

            // ① 位置：钉子在设置右侧、同一行、同尺寸
            Point? pinPos = pin.TranslatePoint(new Point(0, 0), mainWindow);
            Point? setPos = settings.TranslatePoint(new Point(0, 0), mainWindow);
            bool rightOfSettings = pinPos.HasValue && setPos.HasValue
                && pinPos.Value.X > setPos.Value.X + 8;
            bool sameRow = pinPos.HasValue && setPos.HasValue
                && Math.Abs(pinPos.Value.Y - setPos.Value.Y) <= 1.5;
            bool sameSize = Math.Abs(pin.Bounds.Width - settings.Bounds.Width) <= 0.6
                && Math.Abs(pin.Bounds.Height - settings.Bounds.Height) <= 0.6;
            // Path 在这里要写全名：本文件 using System.IO，裸 Path 会被解析成 Path 类
            bool hasIcon = pin.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any()
                || pin.Content is Avalonia.Controls.Shapes.Path;

            Log(dir, $"titlebar-pin-order: 钉子X={pinPos?.X:0.#} 设置X={setPos?.X:0.#} "
                + $"右侧={rightOfSettings} 同行={sameRow} "
                + $"尺寸={pin.Bounds.Width:0.#}×{pin.Bounds.Height:0.#}(设置 {settings.Bounds.Width:0.#}×{settings.Bounds.Height:0.#}) "
                + $"有图标={hasIcon} => "
                + ((rightOfSettings && sameRow && sameSize && hasIcon) ? "PASS" : "FAIL"));

            // ② 点击真的改 Topmost（不是只改图标）
            bool before = mainWindow.Topmost;
            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(120);
            bool afterOn = mainWindow.Topmost;
            bool activeOn = pin.Classes.Contains("active");

            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(120);
            bool afterOff = mainWindow.Topmost;
            bool activeOff = pin.Classes.Contains("active");

            Log(dir, $"titlebar-pin-toggle: 初始置顶={before} →点一下={afterOn}(active={activeOn}) "
                + $"→再点={afterOff}(active={activeOff}) => "
                + ((afterOn && !afterOff && activeOn && !activeOff) ? "PASS" : "FAIL"));

            // ③ 1.9.9：「视图」整栏已删除，菜单栏只剩 5 项。
            //    删除前必须确认原来那三项都另有入口，否则就是把功能一起删了。
            Menu titleMenu = mainWindow.FindControl<Menu>("TitleMenu");
            List<string> topHeaders = titleMenu?.Items.OfType<MenuItem>()
                .Select(item => (item.Header as string) ?? string.Empty)
                .ToList() ?? new List<string>();
            bool viewMenuGone = !topHeaders.Any(text => text.StartsWith("视图", StringComparison.Ordinal));

            // 三个被删项的替代入口：钉子（置顶）/ 面板按钮（收起展开）/ 点表右下角分组按钮
            Button groupButton = mainWindow.FindControl<Button>("PointGroupModeButton");
            bool alternativesAlive = pin.IsEffectivelyVisible
                && toggle.IsEffectivelyVisible
                && (groupButton == null || groupButton.IsEffectivelyVisible);

            Log(dir, $"view-menu-removed: 顶级项={topHeaders.Count} [{string.Join("/", topHeaders)}] "
                + $"视图已删={viewMenuGone} 替代入口(钉子={pin.IsEffectivelyVisible}/面板={toggle.IsEffectivelyVisible}"
                + $"/分组按钮={groupButton?.IsEffectivelyVisible}) => "
                + ((viewMenuGone && alternativesAlive && topHeaders.Count == 5) ? "PASS" : "FAIL"));

            // ④ 工具条：实时曲线撤回菜单，「清空监视」也早已移走
            Border toolbar = mainWindow.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(border => border.Classes.Contains("toolbar"));
            List<string> toolTexts = toolbar?.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible)
                .Select(text => text.Text ?? string.Empty)
                .ToList() ?? new List<string>();
            bool noChartOnBar = !toolTexts.Contains("实时曲线");
            bool hasTraffic = toolTexts.Contains("报文监视");
            bool noOldClear = !toolTexts.Contains("清空监视");

            // 「工具」菜单里两项都在
            MenuItem toolsMenu = titleMenu?.Items.OfType<MenuItem>()
                .LastOrDefault(item => (item.Header as string)?.StartsWith("工具", StringComparison.Ordinal) == true);
            List<string> toolsItems = toolsMenu?.Items.OfType<MenuItem>()
                .Select(item => item.Header as string)
                .Where(text => text != null)
                .ToList() ?? new List<string>();
            bool toolsHasChart = toolsItems.Contains("实时曲线…");
            bool toolsHasTraffic = toolsItems.Contains("通信报文监视…");

            Log(dir, $"toolbar-relayout: 条上实时曲线={!noChartOnBar}(应无) 报文监视={hasTraffic} "
                + $"清空监视已移走={noOldClear} 工具菜单[曲线={toolsHasChart}/监视={toolsHasTraffic}] => "
                + ((noChartOnBar && hasTraffic && noOldClear && toolsHasChart && toolsHasTraffic)
                    ? "PASS" : "FAIL"));

            // ④b 「工具」菜单展开的样子留一张：功能搬进菜单最怕"看着有、点了没反应"，
            //     这张图同时用来核对两条新项在列表里的位置
            if (toolsMenu != null)
            {
                toolsMenu.IsSubMenuOpen = true;
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(220);
                Avalonia.Controls.Primitives.Popup toolsPopup = toolsMenu.GetVisualDescendants()
                    .OfType<Avalonia.Controls.Primitives.Popup>()
                    .FirstOrDefault(item => item.Name == "PART_Popup");
                if (toolsPopup?.Child is Control toolsPopupContent)
                {
                    SaveVisual(toolsPopupContent, Path.Combine(dir, "40-工具菜单.png"));
                }
                toolsMenu.IsSubMenuOpen = false;
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(120);
            }

            // ⑤ 清空按钮在面板右上角（不是"随便放在面板里"）
            Point? clearPos = clear.TranslatePoint(new Point(0, 0), panel);
            Point? togglePos = toggle.TranslatePoint(new Point(0, 0), panel);
            double panelWidth = panel.Bounds.Width;
            bool clearTopRight = clearPos.HasValue
                && clearPos.Value.X > panelWidth * 0.6
                && clearPos.Value.Y < 40
                && clearPos.Value.Y >= 0;
            bool clearLeftOfToggle = clearPos.HasValue && togglePos.HasValue
                && clearPos.Value.X < togglePos.Value.X;
            Log(dir, $"monitor-clear-position: 面板宽={panelWidth:0.#} "
                + $"清空按钮=({clearPos?.X:0.#},{clearPos?.Y:0.#}) 收起按钮=({togglePos?.X:0.#},{togglePos?.Y:0.#}) "
                + $"右上角={clearTopRight} 在收起左侧={clearLeftOfToggle} => "
                + ((clearTopRight && clearLeftOfToggle) ? "PASS" : "FAIL"));

            // ⑤b 两个按钮必须在这条标题带里垂直居中，而且彼此对齐。
            //     只验"都靠上"不够：1.9.8 用 Margin 硬凑的 3px，两者中心都在 15，
            //     而标题带中心在 16.5——差 1.5px，高分屏上肉眼就能看出（用户看到的就是这个）。
            //     所以量三件事：① 两钮中心 Y 相同；② 中心对齐标题带中心；③ 不越出带子。
            double clearCenterY = (clearPos?.Y ?? 0) + clear.Bounds.Height / 2;
            double toggleCenterY = (togglePos?.Y ?? 0) + toggle.Bounds.Height / 2;
            double bandCenterY = 1 + 33 / 2.0;   // 面板 1px 边框 + 标题带 33 高
            bool centersMatch = Math.Abs(clearCenterY - toggleCenterY) <= 0.6;
            bool centeredInBand = Math.Abs(clearCenterY - bandCenterY) <= 1.2
                && Math.Abs(toggleCenterY - bandCenterY) <= 1.2;

            Log(dir, $"monitor-action-align: 清空中心Y={clearCenterY:0.##} 收起中心Y={toggleCenterY:0.##} "
                + $"带中心Y={bandCenterY:0.##} 互相齐={centersMatch} 居中={centeredInBand} => "
                + ((centersMatch && centeredInBand) ? "PASS" : "FAIL"));

            // ⑥ 清空真的清掉两块内容（报文 + 日志），不是只清一个
            viewModel.Records.Add(AsduRecord.Note("自检占位报文 1"));
            viewModel.Records.Add(AsduRecord.Note("自检占位报文 2"));
            viewModel.Logs.Add("自检占位日志 1");
            int beforeRecords = viewModel.Records.Count;
            int beforeLogs = viewModel.Logs.Count;

            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(150);
            int afterRecords = viewModel.Records.Count;
            int afterLogs = viewModel.Logs.Count;

            Log(dir, $"monitor-clear-action: 点前 报文={beforeRecords} 日志={beforeLogs} "
                + $"点后 报文={afterRecords} 日志={afterLogs} => "
                + ((beforeRecords > 0 && beforeLogs > 0 && afterRecords == 0 && afterLogs == 0)
                    ? "PASS" : "FAIL"));

            // ⑦ 收起 / 展开：高度、内容可见性、按钮可点三件事一起验
            double expandedHeight = panel.Bounds.Height;

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(160);
            mainWindow.UpdateLayout();
            await Task.Delay(80);

            double collapsedHeight = panel.Bounds.Height;
            bool contentHidden = !tabs.IsEffectivelyVisible;
            bool splitterHidden = host.RowDefinitions[1].Height.Value < 1;
            bool toggleStillVisible = toggle.IsEffectivelyVisible
                && toggle.TranslatePoint(new Point(0, 0), panel) is Point collapsedPos
                && collapsedPos.Y + toggle.Bounds.Height <= collapsedHeight + 0.5;

            Log(dir, $"monitor-collapse: 展开={expandedHeight:0.#} → 收起={collapsedHeight:0.#} "
                + $"(应 ≤40) 内容隐藏={contentHidden} 分隔条归零={splitterHidden} "
                + $"按钮仍可点={toggleStillVisible} => "
                + ((collapsedHeight <= 40 && contentHidden && splitterHidden && toggleStillVisible)
                    ? "PASS" : "FAIL"));

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(160);
            mainWindow.UpdateLayout();
            await Task.Delay(80);

            double restoredHeight = panel.Bounds.Height;
            bool contentBack = tabs.IsEffectivelyVisible;
            bool heightRestored = Math.Abs(restoredHeight - expandedHeight) <= 1.0;

            Log(dir, $"monitor-expand: 还原高度={restoredHeight:0.#}（收起前 {expandedHeight:0.#}） "
                + $"内容回来={contentBack} 高度复原={heightRestored} => "
                + ((contentBack && heightRestored) ? "PASS" : "FAIL"));

            // ⑧ 用户拖过之后还能不能记住高度。
            // 只验默认 240 是验不出东西的——"记住上次高度"和"写死 240"结果一模一样。
            // 这里先把行高改成 300 模拟拖过分隔条，再收起、再展开，看是不是回到 300。
            host.RowDefinitions[2].Height = new GridLength(300);
            mainWindow.UpdateLayout();
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(140);
            mainWindow.UpdateLayout();
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(140);
            mainWindow.UpdateLayout();
            double remembered = host.RowDefinitions[2].Height.Value;
            Log(dir, $"monitor-remember-height: 拖动后 300 → 收起 → 展开 = {remembered:0.#} => "
                + ((Math.Abs(remembered - 300) <= 1.0) ? "PASS" : "FAIL"));

            // 把高度还原成默认值再截图：300 是刚才为了测"记住上次高度"造的，
            // 留着它会让截图里的点表只剩五六行，看不出正常比例。
            host.RowDefinitions[2].Height = new GridLength(240);
            mainWindow.UpdateLayout();
            await Task.Delay(100);

            // ⑨ 两态各截一张。截图前灌几条报文/日志：空表格看不出
            //   "监视面板展开后到底长什么样"，也看不出清空按钮到底在哪一行的右端。
            viewModel.Records.Add(AsduRecord.Note("自检占位报文 A"));
            viewModel.Records.Add(AsduRecord.Note("自检占位报文 B"));
            viewModel.Logs.Add("自检占位日志 A");
            await Task.Delay(120);
            Save(mainWindow, Path.Combine(dir, "38-监视面板-展开.png"));

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(200);
            mainWindow.UpdateLayout();
            Save(mainWindow, Path.Combine(dir, "39-监视面板-收起.png"));

            // 收尾：恢复成展开态 + 清掉自检塞的占位行，免得后面的截图带着假数据
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(200);
            mainWindow.UpdateLayout();
            viewModel.ClearRecordsCommand?.Execute(null);
        }

        /// <summary>
        /// 批量变化自检。
        ///
        /// 目的：证明"1 秒变化 200 个点"这条链路真的在跑，而不是只验证窗口画得出来。
        /// 做法：
        ///   1. 造一个 300 点的从站（200 遥测 + 100 遥信）——演示从站只有十几个点，
        ///      点位数少于每轮点数时"稀释"逻辑会退化成全量，测不出东西；
        ///   2. 随机范围模式下节拍 1 秒 × 每轮 200 点跑 3 秒，
        ///      校验轮数、改值数、值域、以及遥信是否被限制在合法状态；
        ///   3. 再用固定步长模式跑一轮，校验增量确实按步长递增。
        /// 任何一项不符都写 FAIL，方便不盯着界面也能判断功能是否正常。
        /// </summary>
        private static async Task RunBatchSelfCheckAsync(
            string dir, Window mainWindow, MainWindowViewModel viewModel)
        {
            // 造点：1..200 遥测（短浮点），201..300 遥信（单点）
            SlaveViewModel sandbox = new() { Name = "自检从站", CommonAddress = 99 };
            for (int i = 1; i <= 200; i++)
            {
                sandbox.AddPoint(i, TypeIdentifier.M_ME_NC_1, "自检遥测" + i, 50);
            }
            for (int i = 1; i <= 100; i++)
            {
                sandbox.AddPoint(200 + i, TypeIdentifier.M_SP_NA_1, "自检遥信" + i, 0);
            }

            // ---------- 第 1 组：随机范围，1 秒 200 点，跑 3 秒 ----------
            BatchChangeViewModel randomModel = new(sandbox.Points)
            {
                Pattern = ChangePattern.RandomInRange,
                IntervalMs = 1000,
                BatchSize = 200,
                Min = 0,
                Max = 100
            };
            randomModel.StartCommand.Execute(null);
            await Task.Delay(3400);
            long rounds = randomModel.Rounds;
            long changed = randomModel.ChangedPoints;
            randomModel.StopCommand.Execute(null);
            randomModel.Detach();

            // 值域与状态校验：遥测必须落在 [0,100]，遥信只能是 0 或 1
            int outOfRange = 0;
            int badSwitch = 0;
            foreach (PointViewModel item in sandbox.Points)
            {
                bool isSwitch = item.Type?.Type == TypeIdentifier.M_SP_NA_1;
                if (isSwitch)
                {
                    if (item.Value != 0 && item.Value != 1)
                    {
                        badSwitch++;
                    }
                }
                else if (item.Value < 0 || item.Value > 100)
                {
                    outOfRange++;
                }
            }

            // 期望 3 轮（3.4s / 1s），给调度抖动留 1 轮余量
            bool roundsOk = rounds is >= 3 and <= 4;
            // 每轮固定挑 200 个点；遥信翻转、遥测随机，理论上每次都会变
            long expectedMin = rounds * 200;
            bool changedOk = changed >= expectedMin;
            bool rangeOk = outOfRange == 0 && badSwitch == 0;

            Log(dir, $"batch-random: rounds={rounds} changed={changed} " +
                     $"(期望 ≈{expectedMin}) 值域异常={outOfRange} 遥信非法={badSwitch} " +
                     $"=> {(roundsOk && changedOk && rangeOk ? "PASS" : "FAIL")}");

            // ---------- 第 2 组：固定步长，校验增量 ----------
            // 先把 20 个遥测样本压到基线 10，避免随机组留下的 99.x 加步长后撞上限回绕，
            // 让"每轮 +1"这个断言变成确定性的
            const int StepSampleCount = 20;
            const double StepBaseline = 10;
            double[] before = new double[StepSampleCount];
            for (int i = 0; i < StepSampleCount; i++)
            {
                sandbox.Points[i].Value = StepBaseline;
                before[i] = StepBaseline;
            }

            BatchChangeViewModel stepModel = new(sandbox.Points)
            {
                Pattern = ChangePattern.FixedStep,
                IntervalMs = 400,
                AllPoints = false,
                BatchSize = StepSampleCount,
                Step = 1,
                Min = 0,
                Max = 100
            };
            // 只勾选前 20 个遥测，避免其他点干扰观察
            foreach (BatchPointItem item in stepModel.Items)
            {
                item.IsChecked = sandbox.Points.IndexOf(item.Point) < StepSampleCount;
            }
            stepModel.StartCommand.Execute(null);
            await Task.Delay(1000);
            long stepRounds = stepModel.Rounds;
            stepModel.StopCommand.Execute(null);
            stepModel.Detach();

            double totalDelta = 0;
            int increased = 0;
            for (int i = 0; i < StepSampleCount; i++)
            {
                double delta = sandbox.Points[i].Value - before[i];
                totalDelta += delta;
                if (Math.Abs(delta - stepRounds) < 1e-6)
                {
                    increased++;    // 每轮 +1，跑 N 轮就该 +N
                }
            }
            bool stepOk = stepRounds >= 2 && increased == StepSampleCount;

            Log(dir, $"batch-step: rounds={stepRounds} 按步长精确递增={increased}/{StepSampleCount} " +
                     $"总增量={totalDelta} => {(stepOk ? "PASS" : "FAIL")}");

            // ── ⑤ 关掉参数窗口，变化必须继续（1.10.2 用户报的核心问题）──
            //    这一条只能靠"关掉之后再等一会儿看轮数还在不在涨"来验：
            //    截图拍不到"定时器被释放了"这件事。
            BatchChangeSession session = new();
            BatchChangeViewModel windowModel = new(session, sandbox.Points)
            {
                IntervalMs = 200,
                BatchSize = 50
            };
            windowModel.StartCommand.Execute(null);
            await Task.Delay(500);

            long roundsBeforeDetach = session.RoundCount;
            windowModel.Detach();                       // 等价于用户点窗口的 ×
            await Task.Delay(700);

            bool keepsRunning = session.IsRunning && session.RoundCount > roundsBeforeDetach;
            long roundsAfterDetach = session.RoundCount;

            session.Stop();
            bool sessionStopped = !session.IsRunning;

            Log(dir, $"batch-close-keeps-running: 关窗时轮次={roundsBeforeDetach} "
                + $"等待后轮次={roundsAfterDetach} 关窗后仍在变化={keepsRunning} "
                + $"停止生效={sessionStopped} => "
                + ((keepsRunning && sessionStopped) ? "PASS" : "FAIL"));

            session.Dispose();

            // ── ⑥ 工具条提示点 / 停止按钮 / 状态栏那一段 ──
            if (viewModel == null)
            {
                Log(dir, "batch-indicator: 跳过（无主视图模型）");
                return;
            }

            bool idleQuiet = !viewModel.IsBatchChanging
                && viewModel.BatchChangeStatusText.Length == 0
                && !viewModel.StopBatchChangeCommand.CanExecute(null);

            viewModel.BatchChange.Start(
                sandbox.Points,
                new BatchChangeSettings { IntervalMs = 200, BatchSize = 20 },
                "自检从站");

            string runningText = viewModel.BatchChangeStatusText;
            // 这三个值必须在停止**之前**取下来：日志是最后才拼的，
            // 那时候 Stop 已经执行过，直接读控件会把"停止后"的状态写进"运行中"那一栏
            // （踩过一次：日志写"停止按钮可用=False"，断言却是 PASS，看日志会以为错了）。
            bool runningFlag = viewModel.IsBatchChanging;
            bool stopUsable = viewModel.StopBatchChangeCommand.CanExecute(null);
            bool runningShown = runningFlag
                && runningText.Contains("批量变化中")
                && stopUsable
                && viewModel.BatchChangeTip.Contains("停止");

            // 跑着的时候截一张：状态栏那段提示 + 工具条上的圆点和停止按钮都要在图上
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            Save(mainWindow, Path.Combine(dir, "45-批量变化中-工具条与状态栏.png"));

            viewModel.StopBatchChangeCommand.Execute(null);

            bool stoppedQuiet = !viewModel.IsBatchChanging
                && viewModel.BatchChangeStatusText.Length == 0
                && !viewModel.StopBatchChangeCommand.CanExecute(null);

            Log(dir, $"batch-indicator: 空闲时安静={idleQuiet} 运行中提示=\"{runningText}\" "
                + $"运行标志={runningFlag} 停止按钮可用={stopUsable} "
                + $"停止后回到安静={stoppedQuiet} => "
                + ((idleQuiet && runningShown && stoppedQuiet) ? "PASS" : "FAIL"));
        }

        /// <summary>
        /// 自检：新建从站时"现建通道"这条链路。
        ///
        /// 背景：原版（以及本复刻的早期版本）要求先建通道、再在通道下建从站。
        /// 现场每台装置是不同 IP 时，这个两步流程要重复 N 遍。改造后建从站
        /// 可以顺手建通道，这里就验证它真的落到数据模型上了。
        /// </summary>
        private static void RunSlaveChannelSelfCheck(string dir, MainWindowViewModel viewModel)
        {
            if (viewModel == null)
            {
                Log(dir, "slave-channel-selfcheck: 跳过（无主视图模型）");
                return;
            }

            int before = viewModel.Connections.Count;

            // 场景零：默认端口要避开已被占用的 2404，否则用户勾了"新建通道"直接点确定就报错
            bool port2404Taken = viewModel.Connections.Any(c =>
                c.Protocol == ConnectionProtocol.Iec104 && c.Port == 2404);
            SlaveEditModel probe = new(new SlaveViewModel { Name = "探测", CommonAddress = 1 },
                viewModel.Connections, null);
            bool portAvoided = !port2404Taken || probe.NewPort != 2404;

            Log(dir, $"slave-channel-defaultport: 已有2404={port2404Taken} 默认端口={probe.NewPort} " +
                     $"=> {(portAvoided ? "PASS" : "FAIL")}");

            // 场景一：新建从站 + 现建通道（指定 IP 与端口）
            SlaveViewModel fresh = new() { Name = "自检从站A", CommonAddress = 7 };
            SlaveEditModel model = new(fresh, viewModel.Connections, viewModel.SelectedConnection)
            {
                CreateNewChannel = true,
                NewChannelName = "自检通道A",
                NewBindAddress = "192.168.1.50",
                NewPort = 2405
            };

            string error = model.Validate();
            bool createOk = error == null;
            if (createOk)
            {
                model.Apply();
            }

            ConnectionViewModel created = model.ResolvedChannel;
            bool landed = created != null
                && viewModel.Connections.Count == before + 1
                && viewModel.Connections.Contains(created)
                && created.Port == 2405
                && created.BindAddress == "192.168.1.50";

            // 从站挂到新通道上（CommitNewSlave 的动作，这里直接做等价操作）
            if (landed)
            {
                created.Slaves.Add(fresh);
                created.HookSlave(fresh);
            }
            bool slaveLanded = landed && created.Slaves.Count == 1
                && ReferenceEquals(created.Slaves[0], fresh);

            Log(dir, $"slave-channel-create: err={(error ?? "无")} 通道落地={landed} 从站挂载={slaveLanded} " +
                     $"端口={(created?.Port.ToString() ?? "-")} IP={(created?.BindAddress ?? "-")} " +
                     $"通道数{viewModel.Connections.Count} => {(createOk && slaveLanded ? "PASS" : "FAIL")}");

            // 场景二：同一 IP:端口 再建一个通道，必须被拦下（两个 TcpListener 绑同端点必有一个失败）
            SlaveEditModel clash = new(new SlaveViewModel { Name = "自检从站B", CommonAddress = 8 },
                viewModel.Connections, null)
            {
                CreateNewChannel = true,
                NewBindAddress = "192.168.1.50",
                NewPort = 2405
            };
            string clashError = clash.Validate();
            bool clashBlocked = clashError != null && clashError.Contains("占用");

            Log(dir, $"slave-channel-clash: 重复端口被拦={clashBlocked} 提示={(clashError ?? "无")} " +
                     $"=> {(clashBlocked ? "PASS" : "FAIL")}");

            // 场景三：用已有通道时不新建（不应增加通道数）
            int afterCreate = viewModel.Connections.Count;
            SlaveEditModel reuse = new(new SlaveViewModel { Name = "自检从站C", CommonAddress = 9 },
                viewModel.Connections, created)
            {
                CreateNewChannel = false,
                SelectedConnection = created
            };
            string reuseError = reuse.Validate();
            if (reuseError == null)
            {
                reuse.Apply();
            }
            bool reuseOk = reuseError == null
                && ReferenceEquals(reuse.ResolvedChannel, created)
                && viewModel.Connections.Count == afterCreate;

            Log(dir, $"slave-channel-reuse: err={(reuseError ?? "无")} 复用已有={reuseOk} " +
                     $"通道数保持{viewModel.Connections.Count} => {(reuseOk ? "PASS" : "FAIL")}");
        }

        /// <summary>
        /// 自检：联合建窗口的"通信规约"选择。
        ///
        /// 用户要求在"直接创建从站与通道"里也能选规约（104 TCP / 101 串口），
        /// 并且这个窗口只放"必须当场决定"的端点参数，链路细节留给通道自己配。
        /// </summary>
        private static void RunSlaveChannelProtocolSelfCheck(string dir, MainWindowViewModel viewModel)
        {
            if (viewModel == null)
            {
                Log(dir, "slave-channel-protocol: 跳过（无主视图模型）");
                return;
            }

            int before = viewModel.Connections.Count;

            // 场景一：选 101 → 落地的是串口通道，默认名也跟着改成 "101通道N"
            SlaveViewModel serialSlave = new() { Name = "自检串口从站", CommonAddress = 3 };
            SlaveEditModel serial = new(serialSlave, viewModel.Connections, null)
            {
                CreateNewChannel = true,
                NewChannelProtocolIndex = 1,
                NewSerialPortName = "COM7",
                NewBaudRate = 19200
            };

            // 换规约时默认名要跟着翻（选 101 还叫 "104通道5" 会误导人）
            bool nameFollowed = serial.NewChannelName.StartsWith("101通道", StringComparison.Ordinal);

            bool serialControlVisible = serial.NewChannelIsSerial && !serial.ShowNewChannelTcp;

            string serialError = serial.Validate();
            if (serialError == null)
            {
                serial.Apply();
            }

            ConnectionViewModel serialChannel = serial.ResolvedChannel;
            bool serialLanded = serialChannel != null
                && serialChannel.Protocol == ConnectionProtocol.Iec101
                && serialChannel.SerialPortName == "COM7"
                && serialChannel.BaudRate == 19200
                && viewModel.Connections.Count == before + 1;

            Log(dir, $"slave-channel-protocol-101: err={(serialError ?? "无")} 名称随规约={nameFollowed} "
                + $"串口控件显示={serialControlVisible} 落地协议={(serialChannel?.ProtocolText ?? "-")} "
                + $"COM={(serialChannel?.SerialPortName ?? "-")} 波特率={(serialChannel?.BaudRate.ToString() ?? "-")} "
                + $"=> {((nameFollowed && serialControlVisible && serialLanded) ? "PASS" : "FAIL")}");

            // 场景二：同一串口再建一条必须被拦（串口是独占资源，两条通道抢一个口必然互相踩）
            SlaveEditModel serialClash = new(new SlaveViewModel { Name = "撞串口", CommonAddress = 4 },
                viewModel.Connections, null)
            {
                CreateNewChannel = true,
                NewChannelProtocolIndex = 1,
                NewSerialPortName = "COM7"
            };
            string clashError = serialClash.Validate();
            bool clashBlocked = clashError != null && clashError.Contains("占用");

            Log(dir, $"slave-channel-protocol-clash: 重复串口被拦={clashBlocked} 提示={(clashError ?? "无")} "
                + $"=> {(clashBlocked ? "PASS" : "FAIL")}");

            // 场景三：切回 104 → 端口/地址重新回到校验范围，且默认名翻回 104
            SlaveEditModel back = new(new SlaveViewModel { Name = "切回104", CommonAddress = 5 },
                viewModel.Connections, null)
            {
                CreateNewChannel = true,
                NewChannelProtocolIndex = 0
            };
            bool backOk = back.NewChannelIsTcp && back.ShowNewChannelTcp
                && back.NewChannelName.StartsWith("104通道", StringComparison.Ordinal);

            Log(dir, $"slave-channel-protocol-back: 切回104={backOk} 名称={back.NewChannelName} "
                + $"=> {(backOk ? "PASS" : "FAIL")}");
        }

        /// <summary>
        /// 自检：三个报文长度（传送原因 / 公共地址 / 信息体地址）的编解码正确性。
        ///
        /// 【为什么这条必须逐组合验】
        /// 用户原话："改动增加这三个配置时要特别注意处理报文时不要拼错和解析错，
        /// 导致后面连接不上主站。" 这三种宽度组合起来有 12 种，任何一种少写一个字节、
        /// 或多写一个字节，报文都"看起来合法"，主站却会整段错位——症状是连上了但没数据。
        /// 所以这里对 12 种组合逐个做"编码 → 解码"往返，既比字段值，也比总字节数。
        /// </summary>
        private static async Task RunApplicationWidthSelfCheckAsync(string dir, MainWindowViewModel viewModel)
        {
            // ── ① 12 种宽度组合的编解码往返 ────────────────────────
            int comboCount = 0;
            int comboOk = 0;
            List<string> comboFailures = new();

            for (int cot = 1; cot <= 2; cot++)
            {
                for (int ca = 1; ca <= 2; ca++)
                {
                    for (int ioa = 1; ioa <= 3; ioa++)
                    {
                        comboCount++;

                        ApplicationLayerParameters widths = new()
                        {
                            SizeOfCauseOfTransmission = cot,
                            SizeOfCommonAddress = ca,
                            SizeOfInformationObjectAddress = ioa
                        };

                        // 每个组合都取该宽度的最大值：最容易被漏写的正是最高位那个字节
                        int commonAddress = widths.MaxCommonAddress;
                        int objectAddress = widths.MaxInformationObjectAddress;

                        string failure = RoundTripProbe(widths, commonAddress, objectAddress);
                        if (failure == null)
                        {
                            comboOk++;
                        }
                        else
                        {
                            comboFailures.Add($"COT{cot}/CA{ca}/IOA{ioa}:{failure}");
                        }
                    }
                }
            }

            Log(dir, $"app-widths-roundtrip: {comboOk}/{comboCount} 种宽度组合编解码一致"
                + (comboFailures.Count == 0 ? string.Empty : " 失败[" + string.Join(" | ", comboFailures) + "]")
                + $" => {(comboOk == comboCount ? "PASS" : "FAIL")}");

            // ── ② 传送原因 1 字节时不能多写源发地址 ────────────────
            // 这是最容易"多写一个字节"的地方：写死在 COT 后面总跟 OA，
            // 结果主站把公共地址低字节当成 OA 读，整帧错位。
            byte[] cot1 = BuildProbeAsdu(
                new ApplicationLayerParameters { SizeOfCauseOfTransmission = 1, SizeOfCommonAddress = 2, SizeOfInformationObjectAddress = 2 },
                0x1234, 0x0102).Encode();

            byte[] cot2 = BuildProbeAsdu(
                new ApplicationLayerParameters { SizeOfCauseOfTransmission = 2, SizeOfCommonAddress = 2, SizeOfInformationObjectAddress = 2 },
                0x1234, 0x0102).Encode();

            // 布局：TypeId VSQ COT [OA] CAlo CAhi IOAlo IOAhi SIQ
            bool cot1NoOriginator = cot1.Length == 8 && cot1[3] == 0x34 && cot1[4] == 0x12;
            bool cot2HasOriginator = cot2.Length == 9 && cot2[3] == 0x00 && cot2[4] == 0x34;
            bool cotDiffByOne = cot2.Length - cot1.Length == 1;

            Log(dir, $"app-widths-cot: COT1字节={cot1.Length}B CA低字节@{3}=0x{cot1[3]:X2} "
                + $"COT2字节={cot2.Length}B OA@{3}=0x{cot2[3]:X2} CA低字节@{4}=0x{cot2[4]:X2} "
                + $"=> {((cot1NoOriginator && cot2HasOriginator && cotDiffByOne) ? "PASS" : "FAIL")}");

            // ── ③ 信息体地址 1/2 字节时不能写第三个字节 ────────────
            int ioa1Length = BuildProbeAsdu(
                new ApplicationLayerParameters { SizeOfCauseOfTransmission = 2, SizeOfCommonAddress = 2, SizeOfInformationObjectAddress = 1 },
                1, 0xFF).Encode().Length;
            int ioa2Length = BuildProbeAsdu(
                new ApplicationLayerParameters { SizeOfCauseOfTransmission = 2, SizeOfCommonAddress = 2, SizeOfInformationObjectAddress = 2 },
                1, 0xFFFF).Encode().Length;
            int ioa3Length = BuildProbeAsdu(
                new ApplicationLayerParameters { SizeOfCauseOfTransmission = 2, SizeOfCommonAddress = 2, SizeOfInformationObjectAddress = 3 },
                1, 0xFFFFFF).Encode().Length;

            // 头固定 6 字节（TypeId VSQ COT OA CAlo CAhi）+ IOA 宽度 + 1 字节 SIQ
            bool ioaOk = ioa1Length == 8 && ioa2Length == 9 && ioa3Length == 10;

            Log(dir, $"app-widths-ioa: 1字节={ioa1Length}B 2字节={ioa2Length}B 3字节={ioa3Length}B "
                + $"=> {(ioaOk ? "PASS" : "FAIL")}");

            // ── ④ 装不下的地址必须报错，而不是截断后照发 ────────────
            // 截断是最坏的一种"成功"：报文格式完全合法，主站按错的站地址/点号去找，
            // 永远找不到，现场只能看到"链路正常但没数据"。
            bool caGuard = ThrowsOnEncode(
                new ApplicationLayerParameters { SizeOfCauseOfTransmission = 2, SizeOfCommonAddress = 1, SizeOfInformationObjectAddress = 2 },
                300, 1);
            bool ioaGuard = ThrowsOnEncode(
                new ApplicationLayerParameters { SizeOfCauseOfTransmission = 2, SizeOfCommonAddress = 2, SizeOfInformationObjectAddress = 1 },
                1, 300);
            bool fitsNoThrow = !ThrowsOnEncode(
                new ApplicationLayerParameters { SizeOfCauseOfTransmission = 2, SizeOfCommonAddress = 2, SizeOfInformationObjectAddress = 3 },
                300, 300);

            Log(dir, $"app-widths-guard: 公共地址超宽被拒={caGuard} 点号超宽被拒={ioaGuard} 装得下不误报={fitsNoThrow} "
                + $"=> {((caGuard && ioaGuard && fitsNoThrow) ? "PASS" : "FAIL")}");

            // ── ⑤ 通道启动前就把"装不下"拦掉 ───────────────────────
            // 只到编码层才报错是不够的：那时通道已经在监听、主站已经接上，
            // 用户看到的只是"没有数据"。必须在启动那一刻给出可操作的提示。
            ConnectionViewModel probeChannel = new() { Name = "自检宽度通道", Port = 26104 };
            SlaveViewModel wideSlave = new() { Name = "宽地址从站", CommonAddress = 1000 };
            probeChannel.Slaves.Add(wideSlave);
            probeChannel.HookSlave(wideSlave);
            probeChannel.CommonAddressLength = 1;   // 1 字节装不下 1000

            string openError = null;
            try
            {
                probeChannel.Open();
            }
            catch (InvalidOperationException exception)
            {
                openError = exception.Message;
            }
            catch (Exception exception)
            {
                openError = "非预期异常：" + exception.Message;
            }
            finally
            {
                if (probeChannel.IsOpen)
                {
                    probeChannel.Close();
                }
            }

            bool openBlocked = openError != null && openError.Contains("公共地址");
            Log(dir, $"app-widths-open-guard: 被拦={openError != null} 提示={(openError ?? "无")} "
                + $"=> {(openBlocked ? "PASS" : "FAIL")}");

            // ── ⑥ 参数从通道贯通到从站（同一个实例，不是复制值）──────
            // 若从站拿到的是复制品，通道改宽度后从站还按旧宽度编码，
            // 收和发两个方向就会用不同宽度——最隐蔽的一类错位。
            int freePort = FindFreeTcpPort();
            ConnectionViewModel live = new() { Name = "自检贯通通道", Port = freePort };
            live.CauseOfTransmissionLength = 1;
            live.CommonAddressLength = 1;
            live.InformationObjectAddressLength = 2;

            SlaveViewModel fitted = new() { Name = "窄地址从站", CommonAddress = 100 };
            live.Slaves.Add(fitted);
            live.HookSlave(fitted);

            bool sharedInstance = ReferenceEquals(live.AppParameters, fitted.Station.Parameters);

            bool opened = false;
            string liveError = null;
            try
            {
                live.Open();
                opened = live.IsOpen;
            }
            catch (Exception exception)
            {
                liveError = exception.Message;
            }

            // 启动后按从站自己的参数编一帧：长度必须是 1/1/2 的那套
            int liveEncodedLength = -1;
            if (opened)
            {
                try
                {
                    liveEncodedLength = BuildProbeAsdu(
                        fitted.Station.Parameters, 100, 1000).Encode().Length;
                }
                catch (Exception exception)
                {
                    liveError = exception.Message;
                }
                live.Close();
            }

            // TypeId VSQ COT CAlo IOAlo IOAhi SIQ = 7 字节
            bool widthsPropagated = opened
                && sharedInstance
                && ReferenceEquals(live.AppParameters, fitted.Station.Parameters)
                && liveEncodedLength == 7;

            Log(dir, $"app-widths-propagation: 共享同一实例={sharedInstance} 启动={opened} "
                + $"从站编出={liveEncodedLength}B（期望7） err={(liveError ?? "无")} "
                + $"=> {(widthsPropagated ? "PASS" : "FAIL")}");

            // ── ⑦ 界面：通道窗口的三个下拉 ─────────────────────────
            ConnectionViewModel uiSample = new() { Name = "自检宽度界面", Port = 26204 };
            uiSample.CauseOfTransmissionLength = 1;
            uiSample.CommonAddressLength = 1;
            uiSample.InformationObjectAddressLength = 2;

            ConnectionWindow widthWindow = new()
            {
                DataContext = new ConnectionEditModel(uiSample),
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            widthWindow.Show();
            await Task.Delay(400);

            // 三个下拉现在住在「链路参数 + 报文长度」面板里（四个窗口共用），
            // 它们在面板自己的命名作用域内，窗口的 FindControl 找不到，要从面板上取。
            LinkParametersPanel widthLinkPanel = widthWindow.FindControl<LinkParametersPanel>("LinkPanel");
            ComboBox cotBox = widthLinkPanel?.CauseOfTransmissionBox;
            ComboBox caBox = widthLinkPanel?.CommonAddressBox;
            ComboBox ioaBox = widthLinkPanel?.InformationObjectAddressBox;

            bool boxesExist = cotBox != null && caBox != null && ioaBox != null;
            bool itemCounts = boxesExist && cotBox.ItemCount == 2 && caBox.ItemCount == 2 && ioaBox.ItemCount == 3;
            // 1/1/2 对应的索引是 0/0/1 —— 绑定没接上的话这里会停在默认的 1/1/2
            bool indexesOk = boxesExist
                && cotBox.SelectedIndex == 0
                && caBox.SelectedIndex == 0
                && ioaBox.SelectedIndex == 1;

            // 反向也验一次：改下拉要真的写回模型，否则用户点了等于没点
            bool writesBack = false;
            if (boxesExist && widthWindow.DataContext is ConnectionEditModel editModel)
            {
                ioaBox.SelectedIndex = 2;
                cotBox.SelectedIndex = 1;
                writesBack = editModel.InformationObjectAddressLength == 3
                    && editModel.CauseOfTransmissionLength == 2;
            }

            Save(widthWindow, Path.Combine(dir, "28-通道配置-报文长度.png"));
            widthWindow.Close();
            await Task.Delay(150);

            Log(dir, $"app-widths-ui: 三个下拉={boxesExist} 项数2/2/3={itemCounts} "
                + $"默认索引0/0/1={indexesOk} 反向写回={writesBack} "
                + $"=> {((boxesExist && itemCounts && indexesOk && writesBack) ? "PASS" : "FAIL")}");

            // ── ⑧ 界面：从站窗口的规约下拉真的能切 ──────────────────
            SlaveEditModel slaveModel = new(new SlaveViewModel { Name = "自检规约从站", CommonAddress = 6 },
                viewModel?.Connections ?? (IList<ConnectionViewModel>)new List<ConnectionViewModel>(), null)
            {
                CreateNewChannel = true
            };

            SlaveWindow protocolWindow = new()
            {
                DataContext = slaveModel,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            protocolWindow.Show();
            await Task.Delay(400);

            ComboBox protocolBox = protocolWindow.FindControl<ComboBox>("NewChannelProtocolBox");
            bool protocolBoxExists = protocolBox != null && protocolBox.ItemCount == 2;

            bool protocolSwitches = false;
            if (protocolBox != null)
            {
                protocolBox.SelectedIndex = 1;
                protocolSwitches = slaveModel.NewChannelProtocolIndex == 1
                    && slaveModel.NewChannelIsSerial
                    && !slaveModel.ShowNewChannelTcp;
                protocolBox.SelectedIndex = 0;
            }

            Save(protocolWindow, Path.Combine(dir, "29-从站新建-可选规约.png"));
            protocolWindow.Close();
            await Task.Delay(150);

            Log(dir, $"slave-window-protocol-ui: 规约下拉={protocolBoxExists} 切换到101生效={protocolSwitches} "
                + $"=> {((protocolBoxExists && protocolSwitches) ? "PASS" : "FAIL")}");

            // ── ⑨ 报文解析器：宽度下拉真的参与解析 ──────────────────
            // 编一帧 1/1/2 宽度的报文（公共地址 100），让解析器按默认 2/2/3 解一次、
            // 再按 1/1/2 解一次——前者必然解错，后者必须解出 公共地址 100。
            // 注意要套上 APCI：解析器是按 104 帧（0x68 + 长度 + 控制域 4 字节 + ASDU）分帧的，
            // 光喂一个裸 ASDU 会直接被"启动字符不是 0x68"挡下来，那验的就不是宽度了。
            byte[] narrowAsdu = BuildProbeAsdu(
                new ApplicationLayerParameters { SizeOfCauseOfTransmission = 1, SizeOfCommonAddress = 1, SizeOfInformationObjectAddress = 2 },
                100, 1000).Encode();

            byte[] narrowFrame = new byte[6 + narrowAsdu.Length];
            narrowFrame[0] = 0x68;
            narrowFrame[1] = (byte)(4 + narrowAsdu.Length);
            // 控制域 4 字节全 0 = I 帧，N(S)=0、N(R)=0
            Array.Copy(narrowAsdu, 0, narrowFrame, 6, narrowAsdu.Length);

            FrameParserWindow parser = new() { WindowStartupLocation = WindowStartupLocation.Manual };
            parser.Show();
            await Task.Delay(400);

            string hex = BitConverter.ToString(narrowFrame).Replace('-', ' ');
            string parsedDefault = parser.ParseToText(narrowFrame);

            ComboBox parserCot = parser.FindControl<ComboBox>("CotLengthBox");
            ComboBox parserCa = parser.FindControl<ComboBox>("CaLengthBox");
            ComboBox parserIoa = parser.FindControl<ComboBox>("IoaLengthBox");
            bool parserBoxes = parserCot != null && parserCa != null && parserIoa != null;

            if (parserBoxes)
            {
                parserCot.SelectedIndex = 0;
                parserCa.SelectedIndex = 0;
                parserIoa.SelectedIndex = 1;
            }
            string parsedNarrow = parser.ParseToText(narrowFrame);

            // 把输入与结果也填进去，截图才看得出"这套宽度认出了站 100 / 点 1000"
            TextBox parserInput = parser.FindControl<TextBox>("InputBox");
            TextBox parserOutput = parser.FindControl<TextBox>("OutputBox");
            if (parserInput != null)
            {
                parserInput.Text = hex;
            }
            if (parserOutput != null)
            {
                parserOutput.Text = parsedNarrow;
            }

            Save(parser, Path.Combine(dir, "30-报文解析-按宽度.png"));
            parser.Close();
            await Task.Delay(150);

            bool narrowDecoded = parsedNarrow.Contains("公共地址 100", StringComparison.Ordinal)
                && parsedNarrow.Contains("IOA=1000", StringComparison.Ordinal);
            // 按默认宽度解同一段字节必须解出不一样的东西，否则说明"宽度下拉根本没参与"
            bool defaultDiffers = !parsedDefault.Contains("公共地址 100", StringComparison.Ordinal);

            Log(dir, $"frame-parser-widths: 三个下拉={parserBoxes} 报文={hex} "
                + $"按1/1/2解出站100点1000={narrowDecoded} 按默认宽度结果不同={defaultDiffers} "
                + $"=> {((parserBoxes && narrowDecoded && defaultDiffers) ? "PASS" : "FAIL")}");

            // ── ⑩ 非默认宽度下的真实链路 ────────────────────────────
            await RunNarrowLinkSelfCheckAsync(dir);

            // ── ⑪ 四个入口的"链路参数 + 报文长度"面板 ───────────────
            await RunLinkPanelSelfCheckAsync(dir, viewModel);

            // ── ⑫ 所有弹窗的小屏适配、输入控件圆角、提示精简 ─────────
            await RunDialogFitSelfCheckAsync(dir);

            // ── ⑬ 下拉列表的弹出层也要圆角 ──────────────────────────
            await RunComboPopupSelfCheckAsync(dir);
        }

        /// <summary>
        /// 自检：ComboBox 展开后的列表（Popup 里的那个 Border）也是圆角。
        ///
        /// 只给 ComboBox 本体设 CornerRadius 管不到弹层 —— 它是独立 Popup 里的另一个 Border，
        /// 模板自带圆角 0，于是"框是圆的、弹出来是尖的"。
        ///
        /// 弹层不在主窗口的可视树里，截 Window 也截不到它，所以这里直接读 Popup.Child
        /// 的实际圆角，并把边框的 Name 一起打出来：SimpleTheme 升级若改了部件名，
        /// 样式会静默失效（选择器不匹配不报错），靠这条日志能立刻看出来。
        /// </summary>
        private static async Task RunComboPopupSelfCheckAsync(string dir)
        {
            FrameParserWindow window = new() { WindowStartupLocation = WindowStartupLocation.Manual };
            try
            {
                window.Show();
                await Task.Delay(250);

                ComboBox box = window.FindControl<ComboBox>("CotLengthBox");
                if (box == null)
                {
                    Log(dir, "dialog-combo-popup: 找不到下拉（CotLengthBox）=> FAIL");
                    return;
                }

                box.IsDropDownOpen = true;
                await Task.Delay(300);
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

                Popup popup = box.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
                Control content = popup?.Child;
                if (content == null)
                {
                    Log(dir, "dialog-combo-popup: 打开后拿不到弹层内容 => FAIL");
                    return;
                }

                List<Border> borders = new List<Border>();
                if (content is Border self)
                {
                    borders.Add(self);
                }

                borders.AddRange(content.GetVisualDescendants().OfType<Border>());

                string names = string.Join("、", borders
                    .Select(border => $"{border.Name ?? "(无名)"}:{border.CornerRadius.TopLeft:0.#}"));
                double radius = borders.Count == 0
                    ? 0
                    : borders.Max(border => border.CornerRadius.TopLeft);

                // 弹层能截就截一张，正面回应"下拉是尖的"
                if (popup?.Host is Control host)
                {
                    SaveVisual(host, Path.Combine(dir, "36-下拉弹层-圆角.png"));
                }

                Log(dir, $"dialog-combo-popup: 弹层内 Border=[{names}] 最大圆角={radius:0.#} "
                    + $"=> {(radius >= 4 ? "PASS" : "FAIL")}");

                box.IsDropDownOpen = false;
                await Task.Delay(120);
            }
            catch (Exception ex)
            {
                Log(dir, $"dialog-combo-popup: 异常 {ex.GetType().Name} => FAIL");
            }
            finally
            {
                window.Close();
                await Task.Delay(100);
            }
        }

        /// <summary>
        /// 自检：所有弹窗在小屏上要"按钮点得到"。
        ///
        /// 用户反馈：小屏笔记本上配置窗口的下半截跑到屏幕外，"确定"按钮点不到，也没法滚。
        /// 根因不是内容太多，而是窗口本身写死了 Height —— 900 高的批量窗口在逻辑高 720 的屏上
        /// 一出场就比屏幕高，此时加不加 ScrollViewer 都没用，因为用户连窗口底部都看不到。
        ///
        /// 所以这条不能用"看一眼截图"验收：必须把窗口压到小屏高度，再量动作按钮的底边落在哪。
        ///
        /// 三段判据：
        ///   ① 自然打开后，窗口高度不超过工作区的 93%（DialogWindow 基类有没有真的生效）；
        ///   ② 压到 520 高后，底部动作按钮仍完整落在客户区内；
        ///   ③ 输入控件圆角 &gt; 0，且界面上不再有超过 110 字的成段提示（那类文字该进 ToolTip）。
        /// </summary>
        private static async Task RunDialogFitSelfCheckAsync(string dir)
        {
            (string Name, Func<DialogWindow> Create)[] dialogs = new (string, Func<DialogWindow>)[]
            {
                ("从站配置", () => new SlaveWindow()),
                ("通道配置", () => new ConnectionWindow()),
                ("批量创建从站", () => new BatchCreateSlaveWindow()),
                ("报文类型", () => new AsduTypeWindow()),
                ("信息对象", () => new PointWindow()),
                ("缓变配置", () => new AutoChangeWindow()),
                ("批量变化", () => new BatchChangeWindow()),
                ("报文解析", () => new FrameParserWindow()),
                ("初始化数据", () => new InitDataWindow()),
                ("实时曲线", () => new RealtimeChartWindow()),
                ("冗余校验", () => new RedundancyCheckWindow()),
                ("字符串编解码", () => new StringCodecWindow()),
                ("报文监视", () => new TrafficWindow())
            };

            List<string> failures = new List<string>();
            List<string> noScroll = new List<string>();
            List<string> unrounded = new List<string>();
            List<string> longHints = new List<string>();
            double workHeight = 0;
            double worstRatio = 0;
            string worstName = "-";
            int inputTotal = 0;

            foreach ((string name, Func<DialogWindow> create) in dialogs)
            {
                DialogWindow dialog;
                try
                {
                    dialog = create();
                }
                catch (Exception ex)
                {
                    failures.Add($"{name}: 构造失败 {ex.GetType().Name}");
                    continue;
                }

                try
                {
                    dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                    dialog.Show();
                    await Task.Delay(160);

                    if (workHeight <= 0)
                    {
                        Screen screen = dialog.Screens?.ScreenFromWindow(dialog) ?? dialog.Screens?.Primary;
                        if (screen != null)
                        {
                            double scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
                            workHeight = screen.WorkingArea.Height / scaling;
                        }
                    }

                    // ① 自然尺寸不得超出工作区
                    double natural = dialog.ClientSize.Height;
                    if (workHeight > 0 && natural > workHeight * 0.93 + 1)
                    {
                        failures.Add($"{name}: 自然高 {natural:0} 超过工作区 {workHeight:0} 的 93%");
                    }

                    // ①b 窗口整体要落在工作区内。
                    // 尺寸夹对了还不够：CenterOwner 在母窗口偏上时会把位置算成负坐标，
                    // 标题栏跑到屏幕外，用户看到的就是"窗口顶部缺一截"。
                    Screen current = dialog.Screens?.ScreenFromWindow(dialog) ?? dialog.Screens?.Primary;
                    if (current != null)
                    {
                        double sc = current.Scaling <= 0 ? 1.0 : current.Scaling;
                        PixelRect area = current.WorkingArea;
                        int pixelWidth = (int)Math.Round(dialog.ClientSize.Width * sc);
                        int pixelHeight = (int)Math.Round(dialog.ClientSize.Height * sc);
                        PixelPoint at = dialog.Position;

                        bool inside = at.X >= area.X - 2
                            && at.Y >= area.Y - 2
                            && at.X + pixelWidth <= area.Right + 2
                            && at.Y + pixelHeight <= area.Bottom + 2;

                        if (!inside)
                        {
                            failures.Add($"{name}: 窗口 @({at.X},{at.Y}) 尺寸 {pixelWidth}×{pixelHeight} "
                                + $"超出工作区 {area.X},{area.Y} {area.Width}×{area.Height}");
                        }
                    }

                    // ② 压到小屏高度（1366×768 的本子去掉任务栏约 700，再给标题栏和边距留量）
                    dialog.SizeToContent = SizeToContent.Manual;
                    dialog.Height = 520;
                    dialog.InvalidateMeasure();
                    Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
                    await Task.Delay(60);

                    // 留一张"小屏上是什么样"的证据图：窗口 520 高，内容区出滚动条，按钮仍在底部
                    if (name == "从站配置")
                    {
                        Save(dialog, Path.Combine(dir, "35-从站配置-小屏滚动.png"));

                        // 顺手把 ? 图标的实际尺寸/配色打出来。
                        // 之前改过两轮样式但截图 md5 一直没变，靠眼睛看不出来到底生效没有，
                        // 直接读控件与它内部那个圆点 Border 才能定性。
                        List<string> icons = dialog.GetVisualDescendants()
                            .OfType<HintIcon>()
                            .Select(icon =>
                            {
                                Border dot = icon.FindControl<Border>("HintDot");
                                string color = dot?.Background is ISolidColorBrush brush
                                    ? brush.Color.ToString()
                                    : "无";

                                // Tip 必须是一个会换行的 TextBlock，不能是裸字符串 ——
                                // 裸字符串由 ToolTip 内部默认 TextBlock 显示，不换行，长说明会被截成一行。
                                object tip = ToolTip.GetTip(icon);
                                string tipInfo = tip switch
                                {
                                    TextBlock block => $"换行={block.TextWrapping}/宽{block.MaxWidth:0}/"
                                        + $"{block.Text?.Length ?? 0}字/实占{MeasureTipLines(block)}行",
                                    null => "无",
                                    _ => $"{tip.GetType().Name}（不是 TextBlock）"
                                };

                                return $"{icon.Bounds.Width:0}×{icon.Bounds.Height:0} "
                                    + $"圆点{dot?.Width:0}×{dot?.Height:0}/圆角{dot?.CornerRadius.TopLeft:0} "
                                    + $"底色{color} Tip[{tipInfo}]";
                            })
                            .ToList();
                        Log(dir, $"hint-icon: 从站窗口内 {icons.Count} 个 "
                            + $"=> {(icons.Count == 0 ? "（没有实例）" : icons[0])}");
                    }

                    Button action = FindActionButton(dialog);
                    double clientHeight = dialog.ClientSize.Height;

                    if (action != null)
                    {
                        Point? topPoint = action.TranslatePoint(new Point(0, 0), dialog);
                        Point? bottomPoint = action.TranslatePoint(new Point(0, action.Bounds.Height), dialog);
                        double top = topPoint?.Y ?? double.MaxValue;
                        double bottom = bottomPoint?.Y ?? double.MaxValue;

                        if (bottom > clientHeight + 0.5)
                        {
                            failures.Add($"{name}: 520 高下按钮底 {bottom:0} 超出客户区 {clientHeight:0}");
                        }

                        if (top < -0.5)
                        {
                            failures.Add($"{name}: 按钮顶 {top:0} 被顶部裁掉");
                        }

                        double ratio = bottom / Math.Max(1, clientHeight);
                        if (ratio > worstRatio)
                        {
                            worstRatio = ratio;
                            worstName = name;
                        }
                    }
                    else
                    {
                        // 没有底部按钮的窗口（报文解析、实时曲线、报文监视这类）靠标题栏关闭，
                        // 内容由 * 行自适应，压到 520 也不会溢出。
                        // 这里只拦一种真问题：内容比视口高，却把纵向滚动条关了 —— 那就是看不全还没法滚。
                        foreach (ScrollViewer viewer in dialog.GetVisualDescendants().OfType<ScrollViewer>())
                        {
                            if (viewer.Extent.Height > viewer.Viewport.Height + 1
                                && viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled)
                            {
                                failures.Add($"{name}: 内容 {viewer.Extent.Height:0} 高于视口 "
                                    + $"{viewer.Viewport.Height:0} 却没有纵向滚动条");
                                break;
                            }
                        }
                    }

                    // ③ 输入控件圆角 + 成段提示
                    int badHere = 0;
                    foreach (Control control in dialog.GetVisualDescendants().OfType<Control>())
                    {
                        double radius = control switch
                        {
                            TextBox box => box.CornerRadius.TopLeft,
                            ComboBox combo => combo.CornerRadius.TopLeft,
                            NumericUpDown number => number.CornerRadius.TopLeft,
                            _ => -1
                        };

                        if (radius < 0)
                        {
                            continue;
                        }

                        inputTotal++;
                        if (radius <= 0)
                        {
                            badHere++;
                        }
                    }

                    if (badHere > 0)
                    {
                        unrounded.Add($"{name}({badHere})");
                    }

                    foreach (TextBlock block in dialog.GetVisualDescendants().OfType<TextBlock>())
                    {
                        if (!block.IsEffectivelyVisible || !block.Classes.Contains("hint"))
                        {
                            continue;
                        }

                        if ((block.Text?.Length ?? 0) > 110)
                        {
                            longHints.Add(name);
                            break;
                        }
                    }

                    bool scrollable = dialog.GetVisualDescendants().OfType<ScrollViewer>()
                        .Any(viewer => viewer.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled);
                    if (!scrollable)
                    {
                        noScroll.Add(name);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{name}: 检查异常 {ex.GetType().Name}");
                }
                finally
                {
                    dialog.Close();
                    await Task.Delay(90);
                }
            }

            Log(dir, $"dialog-small-screen: {dialogs.Length} 个弹窗压到 520 高，"
                + $"最差按钮底/客户区={worstRatio:0.00}（{worstName}） 超界 {failures.Count} 个 "
                + $"=> {(failures.Count == 0 ? "PASS" : "FAIL")}");
            foreach (string failure in failures)
            {
                Log(dir, "dialog-small-screen 明细: " + failure);
            }

            Log(dir, $"dialog-input-rounding: 输入控件 {inputTotal} 个，圆角为 0 的窗口 "
                + $"{(unrounded.Count == 0 ? "无" : string.Join("、", unrounded))} "
                + $"=> {(unrounded.Count == 0 ? "PASS" : "FAIL")}");

            Log(dir, $"dialog-hint-slim: 界面上超过 110 字的成段提示 "
                + $"{(longHints.Count == 0 ? "无" : string.Join("、", longHints))} "
                + $"=> {(longHints.Count == 0 ? "PASS" : "FAIL")}");

            Log(dir, $"dialog-scrollable: 无纵向滚动条的弹窗 "
                + $"{(noScroll.Count == 0 ? "无" : string.Join("、", noScroll))}");
        }

        /// <summary>
        /// 把提示文本按它自己的宽度量一遍，折算成行数。
        ///
        /// 只断言"TextWrapping=Wrap"不够 —— 换行属性设了、但宽度没限制时照样一行铺到屏幕外。
        /// 量出真实高度才能说明"这段说明确实是多行、右边不会丢字"。
        /// </summary>
        private static string MeasureTipLines(TextBlock block)
        {
            block.Measure(new Size(block.MaxWidth, double.PositiveInfinity));
            double lineHeight = block.LineHeight > 0 ? block.LineHeight : 19;
            double lines = Math.Max(1, Math.Round(block.DesiredSize.Height / lineHeight));
            return lines.ToString("0");
        }

        /// <summary>底部动作按钮（确定 / 保存 / 取消 / 创建 / 关闭 / 生成）。取最靠下的那个。</summary>
        private static Button FindActionButton(Window window)
        {
            string[] actionWords = { "确定", "保存", "取消", "创建", "关闭", "生成" };
            Button found = null;
            double lowest = double.MinValue;

            foreach (Button button in window.GetVisualDescendants().OfType<Button>())
            {
                if (!button.IsEffectivelyVisible || button.Content is not string text)
                {
                    continue;
                }

                if (!actionWords.Any(word => text.Contains(word, StringComparison.Ordinal)))
                {
                    continue;
                }

                Point? bottom = button.TranslatePoint(new Point(0, button.Bounds.Height), window);
                if (bottom == null)
                {
                    continue;
                }

                if (bottom.Value.Y > lowest)
                {
                    lowest = bottom.Value.Y;
                    found = button;
                }
            }

            return found;
        }

        /// <summary>
        /// 自检：链路参数 + 报文长度面板在**四个入口**都要有，且都能改。
        ///
        /// 用户诉求：单独建通道能改的，联合建从站、双击从站标签、批量建从站也必须能改。
        /// 这类"某个窗口少了一块"的问题，代码上看不出来（每处 XAML 都独立），
        /// 只能逐个窗口打开、逐个下拉去点。
        /// 顺带验"右边显示不全"：面板里最右边那个下拉的右边界必须落在窗口内，
        /// 且宽度足够显示"2 字节"这种三四个字的选项。
        /// </summary>
        private static async Task RunLinkPanelSelfCheckAsync(string dir, MainWindowViewModel viewModel)
        {
            IList<ConnectionViewModel> connections = viewModel?.Connections
                ?? (IList<ConnectionViewModel>)new List<ConnectionViewModel>();
            int beforeCount = connections.Count;

            try
            {
                // ── ① 入口一：双击通道（通道配置窗口）──────────────
                ConnectionViewModel channelSample = new() { Name = "面板自检通道", Port = 26504 };
                ConnectionEditModel channelEdit = new(channelSample);

                ConnectionWindow channelWindow = new()
                {
                    DataContext = channelEdit,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                channelWindow.Show();
                await Task.Delay(400);

                LinkParametersPanel channelPanel = channelWindow.FindControl<LinkParametersPanel>("LinkPanel");
                bool channelPanelOk = VerifyLinkPanel(channelPanel, channelEdit.Link, out string channelDetail);
                bool channelFits = VerifyPanelFits(channelWindow, channelPanel, out string channelFitDetail);

                // VerifyLinkPanel 刚连着改了好几档，ComboBox 的显示文本要等下一轮渲染才刷新。
                // 不让它落定就截图，会拍到"模型已经是 3 字节、下拉还写着 1 字节"的中间帧。
                await Task.Delay(200);

                Save(channelWindow, Path.Combine(dir, "31-通道配置-面板.png"));
                string channelShotState = SnapshotBoxes(channelPanel);
                channelWindow.Close();
                await Task.Delay(120);

                Log(dir, $"link-panel-channel: 面板={channelPanelOk}（{channelDetail}） "
                    + $"不被裁={channelFits}（{channelFitDetail}） "
                    + $"截图后复读={channelShotState} "
                    + $"=> {((channelPanelOk && channelFits) ? "PASS" : "FAIL")}");

                // ── ② 入口二：新建从站 + 现建通道 ───────────────────
                SlaveViewModel freshSlave = new() { Name = "面板自检从站", CommonAddress = 21 };
                SlaveEditModel createModel = new(freshSlave, connections, null)
                {
                    CreateNewChannel = true,
                    NewChannelName = "面板自检新通道",
                    NewBindAddress = "127.0.0.1",
                    NewPort = 26505
                };

                SlaveWindow createWindow = new()
                {
                    DataContext = createModel,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                createWindow.Show();
                await Task.Delay(400);

                LinkParametersPanel createPanel = createWindow.FindControl<LinkParametersPanel>("NewChannelLinkPanel");
                bool createPanelOk = VerifyLinkPanel(createPanel, createModel.NewChannelLink, out string createDetail);
                bool createFits = VerifyPanelFits(createWindow, createPanel, out string createFitDetail);

                Save(createWindow, Path.Combine(dir, "32-从站新建-带链路参数.png"));

                // 小屏：压到 662（1080p@150% 时工作区的 92% 就在这个量级），
                // 留证"新建通道模式内容最多，也能滚、按钮也在"。
                createWindow.SizeToContent = SizeToContent.Manual;
                createWindow.Height = 662;
                createWindow.InvalidateMeasure();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
                await Task.Delay(150);
                Save(createWindow, Path.Combine(dir, "37-从站新建-小屏.png"));

                createWindow.Close();
                await Task.Delay(120);

                // 面板改的值必须真的落到新建出来的通道上
                createModel.NewChannelLink.K = 9;
                createModel.NewChannelLink.W = 4;
                createModel.NewChannelLink.T3Seconds = 33;
                createModel.NewChannelLink.CauseOfTransmissionLength = 1;
                createModel.NewChannelLink.CommonAddressLength = 2;
                createModel.NewChannelLink.InformationObjectAddressLength = 2;

                string createError = createModel.Validate();
                if (createError == null)
                {
                    createModel.Apply();
                }
                ConnectionViewModel createdChannel = createModel.ResolvedChannel;
                bool createApplied = createdChannel != null
                    && createdChannel.K == 9
                    && createdChannel.T3Seconds == 33
                    && createdChannel.CauseOfTransmissionLength == 1
                    && createdChannel.InformationObjectAddressLength == 2;

                Log(dir, $"link-panel-slave-create: 面板={createPanelOk}（{createDetail}） "
                    + $"不被裁={createFits}（{createFitDetail}） err={(createError ?? "无")} "
                    + $"参数落到新通道={createApplied}"
                    + $"(k={createdChannel?.K} t3={createdChannel?.T3Seconds} "
                    + $"cot={createdChannel?.CauseOfTransmissionLength} ioa={createdChannel?.InformationObjectAddressLength}) "
                    + $"=> {((createPanelOk && createFits && createApplied) ? "PASS" : "FAIL")}");

                // ── ③ 入口三：双击从站标签（改所属通道）──────────────
                // 用刚建出来的通道 + 从站，走"编辑模式 + 能改通道端点"那条构造
                if (createdChannel != null)
                {
                    createdChannel.Slaves.Add(freshSlave);
                    createdChannel.HookSlave(freshSlave);
                }

                SlaveEditModel editModel = new(freshSlave, createdChannel, connections);
                SlaveWindow editWindow = new()
                {
                    DataContext = editModel,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                editWindow.Show();
                await Task.Delay(400);

                // 注意顺序：VerifyLinkPanel 会来回改模型（验双向绑定），
                // 所以"初值是否等于通道现值"必须在它之前取，否则测到的已被覆盖。
                bool editLoaded = editModel.OwnerChannelLink != null
                    && editModel.OwnerChannelLink.K == 9
                    && editModel.OwnerChannelLink.T3Seconds == 33
                    && editModel.OwnerChannelLink.CauseOfTransmissionLength == 1;

                LinkParametersPanel editPanel = editWindow.FindControl<LinkParametersPanel>("OwnerLinkPanel");
                bool editPanelOk = VerifyLinkPanel(editPanel, editModel.OwnerChannelLink, out string editDetail);
                bool editFits = VerifyPanelFits(editWindow, editPanel, out string editFitDetail);

                Save(editWindow, Path.Combine(dir, "33-从站编辑-带链路参数.png"));
                editWindow.Close();
                await Task.Delay(120);

                bool editApplied = false;
                string editError = editModel.OwnerChannelLink == null ? "所属通道参数未创建" : null;
                if (editModel.OwnerChannelLink != null)
                {
                    editModel.OwnerChannelLink.K = 10;
                    editModel.OwnerChannelLink.W = 4;
                    editModel.OwnerChannelLink.InformationObjectAddressLength = 3;

                    editError = editModel.Validate();
                    if (editError == null)
                    {
                        editModel.Apply();
                    }
                    editApplied = createdChannel != null
                        && createdChannel.K == 10
                        && createdChannel.W == 4
                        && createdChannel.InformationObjectAddressLength == 3;
                }

                Log(dir, $"link-panel-slave-edit: 面板={editPanelOk}（{editDetail}） "
                    + $"不被裁={editFits}（{editFitDetail}） 初值取通道现值={editLoaded} "
                    + $"err={(editError ?? "无")} 改回通道={editApplied} "
                    + $"=> {((editPanelOk && editFits && editLoaded && editApplied) ? "PASS" : "FAIL")}");

                // ── ④ 入口四：批量建从站 ─────────────────────────────
                BatchCreateSlaveViewModel batchModel = new(connections)
                {
                    StartIp = "127.0.0.2",
                    Count = 2,
                    StartPort = 26520,
                    PortStep = 1,
                    StartCommonAddress = 31,
                    AddressStep = 1,
                    ChannelNamePattern = "面板批量通道{n}",
                    SlaveNamePattern = "面板批量从站{n}"
                };

                BatchCreateSlaveWindow batchWindow = new()
                {
                    DataContext = batchModel,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                batchWindow.Show();
                await Task.Delay(400);

                LinkParametersPanel batchPanel = batchWindow.FindControl<LinkParametersPanel>("BatchLinkPanel");
                bool batchPanelOk = VerifyLinkPanel(batchPanel, batchModel.Link, out string batchDetail);
                bool batchFits = VerifyPanelFits(batchWindow, batchPanel, out string batchFitDetail);

                Save(batchWindow, Path.Combine(dir, "34-批量创建-带链路参数.png"));
                batchWindow.Close();
                await Task.Delay(120);

                // 统一套用：这一批每条通道都带同一份参数
                batchModel.Link.K = 6;
                batchModel.Link.W = 4;
                batchModel.Link.T2Seconds = 7;
                batchModel.Link.CauseOfTransmissionLength = 1;
                batchModel.Link.CommonAddressLength = 1;
                batchModel.Link.InformationObjectAddressLength = 2;

                int made = batchModel.Create(connections);
                List<ConnectionViewModel> batchChannels = connections
                    .Where(c => c.Name.StartsWith("面板批量通道", StringComparison.Ordinal))
                    .ToList();
                bool batchApplied = made == 2
                    && batchChannels.Count == 2
                    && batchChannels.All(c => c.K == 6
                        && c.T2Seconds == 7
                        && c.CauseOfTransmissionLength == 1
                        && c.CommonAddressLength == 1
                        && c.InformationObjectAddressLength == 2);

                // 公共地址 1 字节只到 255：起始地址填 300 必须逐行报出来，不能默默建
                batchModel.StartCommonAddress = 300;
                string widthWarning = batchModel.Preview.Count > 0 ? batchModel.Preview[0].Warning : null;
                bool widthGuard = widthWarning != null && widthWarning.Contains("255");

                Log(dir, $"link-panel-batch: 面板={batchPanelOk}（{batchDetail}） "
                    + $"不被裁={batchFits}（{batchFitDetail}） 建出={made} 统一套用={batchApplied} "
                    + $"地址超宽告警={widthGuard}（{widthWarning ?? "无"}） "
                    + $"=> {((batchPanelOk && batchFits && batchApplied && widthGuard) ? "PASS" : "FAIL")}");
            }
            finally
            {
                // 把自己造出来的通道清掉，别影响后面的侧边栏/几何断言
                for (int index = connections.Count - 1; index >= 0; index--)
                {
                    ConnectionViewModel connection = connections[index];
                    if (connection.Name.StartsWith("面板", StringComparison.Ordinal))
                    {
                        connections.RemoveAt(index);
                        connection.Dispose();
                    }
                }

                if (connections.Count != beforeCount)
                {
                    Log(dir, $"link-panel-cleanup: 通道数 {beforeCount} → {connections.Count}（应相等）");
                }
            }
        }

        /// <summary>
        /// 验一个面板：三个下拉齐全、项数正确、**双向**都通（模型→界面、界面→模型）。
        /// 只验单向是不够的——绑定写成 OneWay 时模型改了界面会变，用户点下拉却写不回去。
        /// </summary>
        private static bool VerifyLinkPanel(
            LinkParametersPanel panel, LinkParametersModel model, out string detail)
        {
            detail = "面板为空";
            if (panel == null || model == null)
            {
                return false;
            }

            ComboBox cot = panel.CauseOfTransmissionBox;
            ComboBox ca = panel.CommonAddressBox;
            ComboBox ioa = panel.InformationObjectAddressBox;
            if (cot == null || ca == null || ioa == null)
            {
                detail = $"缺下拉 cot={cot != null} ca={ca != null} ioa={ioa != null}";
                return false;
            }

            bool itemCounts = cot.ItemCount == 2 && ca.ItemCount == 2 && ioa.ItemCount == 3;

            // 正向：改模型，界面要跟着动
            model.CauseOfTransmissionLength = 1;
            model.CommonAddressLength = 1;
            model.InformationObjectAddressLength = 2;
            bool forward = cot.SelectedIndex == 0 && ca.SelectedIndex == 0 && ioa.SelectedIndex == 1;

            // 反向：点下拉，模型要写回去
            cot.SelectedIndex = 1;
            ioa.SelectedIndex = 2;
            bool backward = model.CauseOfTransmissionLength == 2
                && model.InformationObjectAddressLength == 3;

            // 记下截图当下三个下拉**实际**停在哪一档。
            // 前面出现过"日志 PASS、截图里下拉却还是旧值"的情况 ——
            // 光看 forward/backward 布尔值看不出渲染有没有跟上，把索引和选中项文本都打出来才查得动。
            detail = $"项数{cot.ItemCount}/{ca.ItemCount}/{ioa.ItemCount} 模型→界面={forward} 界面→模型={backward} "
                + $"停档={cot.SelectedIndex}/{ca.SelectedIndex}/{ioa.SelectedIndex} "
                + $"选中={SelectedBoxText(cot)}/{SelectedBoxText(ca)}/{SelectedBoxText(ioa)}";
            return itemCounts && forward && backward;
        }

        /// <summary>下拉当前选中项的显示文字。日志里用它比对"界面到底写的是哪一档"。</summary>
        private static string SelectedBoxText(ComboBox box)
        {
            if (box == null)
            {
                return "?";
            }

            return box.SelectedItem is ComboBoxItem item
                ? item.Content?.ToString() ?? "?"
                : box.SelectedItem?.ToString() ?? "null";
        }

        /// <summary>
        /// 截图之后再读一遍下拉状态。
        ///
        /// 截图走的是离屏的 RenderTargetBitmap，和窗口真实呈现不是一条路径：
        /// 数据已经改对、但离屏帧还停在旧值时，截图上会看到"下拉写着旧值"。
        /// 所以要在这里再读一次 —— 读到的还是新值，说明数据没问题，问题只在离屏帧；
        /// 读回旧值才是真有回调把值改回去了。
        /// </summary>
        private static string SnapshotBoxes(LinkParametersPanel panel)
        {
            if (panel == null)
            {
                return "面板为空";
            }

            ComboBox cot = panel.CauseOfTransmissionBox;
            ComboBox ca = panel.CommonAddressBox;
            ComboBox ioa = panel.InformationObjectAddressBox;
            return $"{SelectedBoxText(cot)}/{SelectedBoxText(ca)}/{SelectedBoxText(ioa)}"
                + $"（停档 {cot?.SelectedIndex}/{ca?.SelectedIndex}/{ioa?.SelectedIndex}）"
                + $" 视觉文本={VisualTexts(cot)}/{VisualTexts(ca)}/{VisualTexts(ioa)}";
        }

        /// <summary>下拉内部真正画出来的文字（可视树里的 TextBlock）。</summary>
        private static string VisualTexts(ComboBox box)
        {
            if (box == null)
            {
                return "?";
            }

            IEnumerable<string> texts = box.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(block => block.Text)
                .Where(text => !string.IsNullOrEmpty(text))!;
            return string.Join("|", texts);
        }

        /// <summary>
        /// 验面板有没有被窗口右边界裁掉。
        ///
        /// 两个判据：
        ///   ① 面板里最右边那个下拉，其右边界要落在窗口可视宽度内（这就是"右边显示不全"）；
        ///   ② 它的实际宽度要够放"2 字节"这种选项 —— 上一版正是被挤到 90px 出头，
        ///      "2 字节（默认）"直接显示成"2 字节（默认"。
        /// </summary>
        private static bool VerifyPanelFits(Window window, LinkParametersPanel panel, out string detail)
        {
            detail = "面板为空";
            if (window == null || panel == null)
            {
                return false;
            }

            List<ComboBox> boxes = panel.GetVisualDescendants().OfType<ComboBox>().ToList();
            if (boxes.Count == 0)
            {
                detail = "面板里没有下拉";
                return false;
            }

            ComboBox rightmost = boxes.OrderByDescending(box => box.Bounds.X).First();
            Point? rightEdge = rightmost.TranslatePoint(new Point(rightmost.Bounds.Width, 0), window);

            double windowWidth = window.Bounds.Width;
            double edgeX = rightEdge?.X ?? double.NaN;
            bool inside = rightEdge.HasValue && edgeX <= windowWidth - 4;
            bool wideEnough = rightmost.Bounds.Width >= 96;

            detail = $"最右下拉右边界={edgeX:0.#} 窗口宽={windowWidth:0.#} 下拉宽={rightmost.Bounds.Width:0.#}";
            return inside && wideEnough;
        }

        /// <summary>
        /// 自检：非默认报文宽度下的**真实链路**联通性。
        ///
        /// 前面的往返验的是"同一个参数进来出去对得上"，还不足以证明真能连主站——
        /// 真的主站会按它自己的宽度去切字节流。所以这里用 1/1/2 的窄宽度真开一个端口，
        /// 扮演主站走完 STARTDT → 总召 → 数据上送 → 总召结束，每一步都按窄宽度解析回来的报文。
        /// 这条挂了就是"主站连不上"，比任何字段级断言都更接近用户报的现象。
        /// </summary>
        private static async Task RunNarrowLinkSelfCheckAsync(string dir)
        {
            int port = FindFreeTcpPort();
            ConnectionViewModel channel = new()
            {
                Name = "窄宽度链路自检",
                BindAddress = "127.0.0.1",
                Port = port,
                CauseOfTransmissionLength = 1,
                CommonAddressLength = 1,
                InformationObjectAddressLength = 2
            };

            // CA=100 装得进 1 字节；点号 1000 装得进 2 字节
            SlaveViewModel slave = new() { Name = "窄宽度从站", CommonAddress = 100 };
            slave.AddPoints(new[] { new PointDraft(1000, TypeIdentifier.M_SP_NA_1, "点1000", 1) });
            channel.Slaves.Add(slave);
            channel.HookSlave(slave);

            ApplicationLayerParameters widths = channel.BuildConfiguredParameters();

            bool startConfirm = false;
            bool interrogationConfirm = false;
            bool pointReported = false;
            bool intervalTermination = false;
            string failure = null;
            List<string> seen = new();

            try
            {
                channel.Open();

                using System.Net.Sockets.TcpClient client = new();
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
                await Task.Delay(200);

                using System.Net.Sockets.NetworkStream stream = client.GetStream();

                // ── STARTDT 激活（U 帧，固定 6 字节）──
                await stream.WriteAsync(new byte[] { 0x68, 0x04, 0x07, 0x00, 0x00, 0x00 });
                byte[] uConfirm = await ReadApduAsync(stream);
                startConfirm = uConfirm != null && uConfirm.Length == 6
                    && uConfirm[2] == 0x0B;

                // ── 总召激活：C_IC_NA_1，COT=6，CA=100，IOA=0，QOI=20 ──
                // 按 1/1/2 组装：64 01 06 64 | 00 00 | 14
                byte[] interrogationAsdu = new byte[]
                {
                    0x64, 0x01, 0x06, 0x64, 0x00, 0x00, 0x14
                };
                await stream.WriteAsync(BuildInformationFrame(interrogationAsdu));
                await Task.Delay(300);

                // ── 读回到总召结束（COT=10）或超时 ──
                for (int guard = 0; guard < 40; guard++)
                {
                    byte[] apdu;
                    try
                    {
                        apdu = await ReadApduAsync(stream);
                    }
                    catch (TimeoutException)
                    {
                        break;
                    }
                    catch (System.IO.EndOfStreamException)
                    {
                        break;
                    }

                    if (apdu == null || apdu.Length <= 6)
                    {
                        continue;
                    }

                    Asdu asdu = Asdu.Decode(apdu, 6, widths, out _);

                    seen.Add($"{asdu.TypeId}/COT{(int)asdu.Cot}/CA{asdu.CommonAddress}"
                        + $"/IOA{(asdu.InformationObjects.Count > 0 ? asdu.InformationObjects[0].ObjectAddress.ToString() : "-")}");

                    if (asdu.TypeId == TypeIdentifier.C_IC_NA_1
                        && asdu.Cot == CauseOfTransmission.ActivationCon)
                    {
                        interrogationConfirm = true;
                    }

                    if (asdu.CommonAddress == 100
                        && asdu.InformationObjects.Count > 0
                        && asdu.InformationObjects[0].ObjectAddress == 1000)
                    {
                        pointReported = true;
                    }

                    if (asdu.TypeId == TypeIdentifier.C_IC_NA_1
                        && asdu.Cot == CauseOfTransmission.ActivationTermination)
                    {
                        intervalTermination = true;
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                failure = $"{exception.GetType().Name}: {exception.Message}";
            }
            finally
            {
                try { channel.Close(); } catch { /* 已关闭 */ }
                channel.Dispose();
            }

            bool pass = startConfirm && interrogationConfirm && pointReported && intervalTermination;
            Log(dir, $"app-widths-live-link: 起始确认={startConfirm} 总召确认={interrogationConfirm} "
                + $"点1000上送={pointReported} 总召结束={intervalTermination} "
                + $"宽度={widths} 收到=[{string.Join(" ", seen)}] err={(failure ?? "无")} "
                + $"=> {(pass ? "PASS" : "FAIL")}");
        }

        /// <summary>从流里读一个完整 APDU（0x68 + 长度 + 其余）</summary>
        private static async Task<byte[]> ReadApduAsync(Stream stream, int timeoutMs = 1200)
        {
            using CancellationTokenSource cts = new(timeoutMs);

            byte[] header = new byte[2];
            await stream.ReadExactlyAsync(header, cts.Token);

            int total = header[1] + 2;
            byte[] apdu = new byte[total];
            apdu[0] = header[0];
            apdu[1] = header[1];

            if (total > 2)
            {
                await stream.ReadExactlyAsync(apdu.AsMemory(2), cts.Token);
            }
            return apdu;
        }

        /// <summary>把一个裸 ASDU 包成 104 I 帧（控制域全 0）</summary>
        private static byte[] BuildInformationFrame(byte[] asdu)
        {
            byte[] frame = new byte[6 + asdu.Length];
            frame[0] = 0x68;
            frame[1] = (byte)(4 + asdu.Length);
            Array.Copy(asdu, 0, frame, 6, asdu.Length);
            return frame;
        }

        /// <summary>
        /// 一组宽度参数的编解码往返探针。返回 null 表示一致，否则返回差异说明。
        /// </summary>
        private static string RoundTripProbe(
            ApplicationLayerParameters widths, int commonAddress, int objectAddress)
        {
            byte[] bytes;
            try
            {
                bytes = BuildProbeAsdu(widths, commonAddress, objectAddress).Encode();
            }
            catch (Exception exception)
            {
                return "编码抛错(" + exception.Message + ")";
            }

            // 头 = TypeId(1) + VSQ(1) + 传送原因(含源发地址) + 公共地址 + 信息体地址，尾 = SIQ(1)。
            // 注意：传送原因字段"宽度 2 字节"的意思就是"1 字节原因 + 1 字节源发地址"，
            // 所以这里只能加一次 SizeOfCauseOfTransmission，不能再补一个 OA 字节。
            int expectedLength = 2
                + widths.SizeOfCauseOfTransmission
                + widths.SizeOfCommonAddress
                + widths.SizeOfInformationObjectAddress
                + 1;

            if (bytes.Length != expectedLength)
            {
                return $"长度{bytes.Length}≠{expectedLength}";
            }

            Asdu decoded;
            int consumed;
            try
            {
                decoded = Asdu.Decode(bytes, 0, widths, out consumed);
            }
            catch (Exception exception)
            {
                return "解码抛错(" + exception.Message + ")";
            }

            if (consumed != bytes.Length)
            {
                return $"解码消费{consumed}≠{bytes.Length}";
            }
            if (decoded.CommonAddress != commonAddress)
            {
                return $"公共地址{decoded.CommonAddress}≠{commonAddress}";
            }
            if (decoded.InformationObjects.Count != 1)
            {
                return $"对象数{decoded.InformationObjects.Count}≠1";
            }
            if (decoded.InformationObjects[0].ObjectAddress != objectAddress)
            {
                return $"点号{decoded.InformationObjects[0].ObjectAddress}≠{objectAddress}";
            }
            if (decoded.Cot != CauseOfTransmission.Spontaneous)
            {
                return $"传送原因{(int)decoded.Cot}≠{(int)CauseOfTransmission.Spontaneous}";
            }
            return null;
        }

        /// <summary>构造一帧"单点遥信自发上送"探针（TypeId + VSQ + COT + CA + 1 个对象）</summary>
        private static Asdu BuildProbeAsdu(
            ApplicationLayerParameters widths, int commonAddress, int objectAddress)
        {
            Asdu asdu = new(widths)
            {
                TypeId = TypeIdentifier.M_SP_NA_1,
                Cot = CauseOfTransmission.Spontaneous,
                CommonAddress = commonAddress
            };
            asdu.AddInformationObject(new SinglePointInformation(objectAddress, true));
            return asdu;
        }

        /// <summary>编码时会不会因为地址超宽而抛错</summary>
        private static bool ThrowsOnEncode(
            ApplicationLayerParameters widths, int commonAddress, int objectAddress)
        {
            try
            {
                BuildProbeAsdu(widths, commonAddress, objectAddress).Encode();
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 找一个当前空闲的 TCP 端口。
        /// 绑 0 端口让系统分配，读回来再释放——比写死 2404 之类安全得多，
        /// 免得自检和正在跑的通道抢端口导致假失败。
        /// </summary>
        private static int FindFreeTcpPort()
        {
            try
            {
                System.Net.Sockets.TcpListener probe =
                    new(System.Net.IPAddress.Loopback, 0);
                probe.Start();
                int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                return port;
            }
            catch
            {
                return 26304;
            }
        }

        /// <summary>
        /// 自检：批量创建从站。
        /// 校验三件事：IP 按步长递增、末段超 255 自动进位、端口递增与冲突跳过。
        /// </summary>
        private static void RunBatchCreateSelfCheck(string dir, MainWindowViewModel viewModel)
        {
            if (viewModel == null)
            {
                Log(dir, "batch-create-selfcheck: 跳过（无主视图模型）");
                return;
            }

            // 场景一：步长 2，数量 4 → .10 .12 .14 .16
            BatchCreateSlaveViewModel model = new(viewModel.Connections)
            {
                StartIp = "192.168.1.10",
                IpStep = 2,
                Count = 4,
                StartPort = 2500,
                PortStep = 1,
                StartCommonAddress = 10,
                AddressStep = 1,
                ChannelNamePattern = "自检通道{n}",
                SlaveNamePattern = "自检从站{n}"
            };

            string ipList = string.Join(",", model.Preview.Select(p => p.Ip));
            bool stepOk = model.Preview.Count == 4
                && model.Preview.Select(p => p.Ip).SequenceEqual(
                    new[] { "192.168.1.10", "192.168.1.12", "192.168.1.14", "192.168.1.16" })
                && model.Preview.Select(p => p.Port).SequenceEqual(new[] { 2500, 2501, 2502, 2503 })
                && model.Preview.Select(p => p.CommonAddress).SequenceEqual(new[] { 10, 11, 12, 13 });

            Log(dir, $"batch-create-step: {ipList} 端口/地址递增={stepOk} => {(stepOk ? "PASS" : "FAIL")}");

            // 场景二：末段进位 —— 从 .250 起、步长 3、取 4 个 → 250 253 256(→.1.0) 259(→.1.3)
            model.StartIp = "192.168.1.250";
            model.IpStep = 3;
            model.Count = 4;
            string carryList = string.Join(",", model.Preview.Select(p => p.Ip));
            bool carryOk = model.Preview.Select(p => p.Ip).SequenceEqual(
                new[] { "192.168.1.250", "192.168.1.253", "192.168.2.0", "192.168.2.3" });

            Log(dir, $"batch-create-carry: {carryList} 末段进位={carryOk} => {(carryOk ? "PASS" : "FAIL")}");

            // 场景三：真建一批，校验通道与从站数量
            model.StartIp = "10.20.30.40";
            model.IpStep = 1;
            model.Count = 3;
            model.StartPort = 2600;
            model.PortStep = 0;
            model.StartCommonAddress = 20;

            int beforeChannels = viewModel.Connections.Count;
            int created = model.Create(viewModel.Connections);
            int added = viewModel.Connections.Count - beforeChannels;
            bool createOk = created == 3 && added == 3
                && viewModel.Connections.Last().Slaves.Count == 1
                && viewModel.Connections.Last().BindAddress == "10.20.30.42";

            Log(dir, $"batch-create-apply: 创建={created} 新增通道={added} 末条={viewModel.Connections.Last().Name}" +
                     $"/{viewModel.Connections.Last().BindAddress}:{viewModel.Connections.Last().Port} " +
                     $"=> {(createOk ? "PASS" : "FAIL")}");

            // 场景四：同 IP:端口 已存在时，勾了"跳过"就不应重复创建
            int beforeSkip = viewModel.Connections.Count;
            int createdAgain = model.Create(viewModel.Connections);
            bool skipOk = createdAgain == 0 && viewModel.Connections.Count == beforeSkip;
            Log(dir, $"batch-create-skip: 重复创建={createdAgain} 通道数不变={viewModel.Connections.Count == beforeSkip} " +
                     $"=> {(skipOk ? "PASS" : "FAIL")}");
        }

        /// <summary>自检：信号区标签（第一个"全部"，其余按 ASDU 类型）</summary>
        /// <summary>两种分组方式的中文名，日志里直接用</summary>
        private static string DescribeMode(bool byCategory) => byCategory ? "电力大类" : "ASDU 类型";

        /// <summary>
        /// 自检：信号区的两种分组方式。
        ///
        /// 同一个从站有两种问法，"这条总召回了几个 M_ME_NC_1"要看类型，
        /// "这个站多少遥测多少遥信"要看电力大类，所以标签栏可以切。
        /// 两条路径都得验，而且验的东西不一样：
        ///   · 类型模式：标签按 TypeID 升序（和报文里的顺序一致）、标签文字不含规约代号；
        ///   · 大类模式：大类按 遥测/遥信/遥控/遥脉 固定顺序、**同一标签内按点号升序**、
        ///               每个点确实落在自己那一类里（不是"随手 group by 了个字段"）。
        /// 最后再切回类型模式，确认往返之后标签和第一次一模一样 —— 切换是纯视图行为，
        /// 不该有残留状态（点表、选中项、标签内容都不受影响）。
        /// </summary>
        private static void RunPointTabSelfCheck(string dir, MainWindowViewModel viewModel, Window mainWindow)
        {
            if (viewModel == null)
            {
                Log(dir, "point-tab-selfcheck: 跳过（无主视图模型）");
                return;
            }

            // 找一个真的有点位的从站
            SlaveViewModel target = viewModel.Connections
                .SelectMany(c => c.Slaves)
                .FirstOrDefault(s => s.Points.Count > 0);

            if (target == null)
            {
                Log(dir, "point-tab-selfcheck: 跳过（没有带点位的从站）");
                return;
            }

            viewModel.SelectedSlave = target;
            Dispatcher.UIThread.RunJobs();

            // 起点必须先摆回"按 ASDU 类型"。分组方式是存进 settings.json 的用户偏好，
            // 上一轮要是选了"电力大类"，这一整段（5 条断言）就全部反着来：
            // 标签数、四类齐全、按钮文字、往返切换，条条报 FAIL，看着像分组功能坏了。
            // persist: false —— 自检只借这个状态跑一遍，不能顺手改掉用户的选择。
            if (viewModel.GroupPointsByCategory)
            {
                viewModel.SetPointsGroupByCategory(false, persist: false);
                Dispatcher.UIThread.RunJobs();
                WarmUpLayout(mainWindow);
            }

            // 先把入口状态记一笔。上一次 point-tabs 偶发 FAIL（标签只剩遥测/遥信/遥脉 4 个纯中文页），
            // 连跑三次都复现不出来 —— 把模式本身打进日志，下次才能分清是"状态被别处改过"
            // 还是"分组算错了"。断言失败时最贵的成本从来不是修，而是不知道当时到底什么状态。
            bool enteredByType = !viewModel.GroupPointsByCategory;
            Log(dir, $"point-tabs-mode: 入口={DescribeMode(viewModel.GroupPointsByCategory)} "
                + $"按钮文字=\"{viewModel.PointGroupModeCaption}\" => "
                + (enteredByType ? "PASS" : "FAIL"));

            // ───────────────── ① 按 ASDU 类型 ─────────────────
            int allCount = viewModel.PointTabs.Count == 0 ? 0 : viewModel.PointTabs[0].Points.Count;
            bool firstIsAll = viewModel.PointTabs.Count > 0 && viewModel.PointTabs[0].IsAll;

            // 每个类型标签的点数之和应等于"全部"（不重不漏）
            int sumOfTypes = viewModel.PointTabs.Skip(1).Sum(t => t.Points.Count);
            bool sumOk = sumOfTypes == allCount;

            string detail = string.Join(" | ", viewModel.PointTabs.Select(t => $"{t.Code}{(t.IsAll ? "全部" : "")}={t.Points.Count}"));

            // 切到第一个类型标签，验证表格跟随
            if (viewModel.PointTabs.Count > 1)
            {
                viewModel.SelectedPointTab = viewModel.PointTabs[1];
            }
            bool followOk = viewModel.PointsInSelectedTab != null
                && viewModel.PointTabs.Count > 1
                && viewModel.PointsInSelectedTab.Count == viewModel.PointTabs[1].Points.Count;

            // 类型标签必须按 TypeID 升序（M_DP_NA_1=3 在 M_ME_NA_1=9 前面）
            var typeTabs = viewModel.PointTabs.Skip(1).ToList();
            bool orderOk = true;
            for (int i = 1; i < typeTabs.Count; i++)
            {
                if (typeTabs[i - 1].TypeValue > typeTabs[i].TypeValue)
                {
                    orderOk = false;
                    break;
                }
            }
            string orderDetail = string.Join(" < ", typeTabs.Select(t => $"{t.TypeValue}"));

            // 标签文字只留中文，不能再混进 M_SP_NA_1 这类规约代号。
            // 注意"CP56时标"里的 CP56 是中文名的一部分（时标类型），要放行，
            // 所以判据是"不含规约代号、不含下划线"，而不是"不含任何 ASCII 字母"。
            bool captionOk = typeTabs.All(t =>
                !t.Caption.Contains(t.Code, StringComparison.Ordinal) && !t.Caption.Contains('_'));

            bool pass = firstIsAll && sumOk && followOk && viewModel.PointTabs.Count > 1
                && orderOk && captionOk;
            Log(dir, $"point-tabs: 标签数={viewModel.PointTabs.Count} 首个为全部={firstIsAll} " +
                     $"分组合计={sumOfTypes}/{allCount} 表格跟随={followOk} " +
                     $"按枚举升序={orderOk} 标签纯中文={captionOk} => {(pass ? "PASS" : "FAIL")}");
            Log(dir, $"point-tabs-detail: {detail}");
            Log(dir, $"point-tabs-order: {orderDetail}");

            // 记下这一版标签，切回来之后要比对
            List<string> typeSnapshot = viewModel.PointTabs
                .Select(t => $"{t.Caption}:{t.Points.Count}").ToList();

            // ───────────────── ② 按电力大类 ─────────────────
            // 走命令而不是直接改属性：和用户点那个按钮完全同一条代码路径。
            // 直接 set 属性就测不到"按钮是否真的接在命令上"。
            viewModel.TogglePointGroupModeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var categoryTabs = viewModel.PointTabs.Skip(1).ToList();
            bool categoryFirstIsAll = viewModel.PointTabs.Count > 0 && viewModel.PointTabs[0].IsAll;
            int sumOfCategories = categoryTabs.Sum(t => t.Points.Count);
            string categoryDetail = string.Join(" | ", viewModel.PointTabs.Select(t => $"{t.Code}{(t.IsAll ? "全部" : "")}={t.Points.Count}"));

            // 大类必须**四类齐全**、顺序固定：遥测 → 遥信 → 遥控 → 遥脉（"其他"有才出，永远排最后）。
            //
            // 以前空大类不出标签，比对的是"这一串是不是那个固定序列的子序列"；
            // 现在改成固定四类——"遥控"这一栏在本工具里常态是 0 个（点表只存监视方向的信息对象，
            // 遥控与设点命令由主站下发、不占点表），但它是行业口径的一部分，位置必须留着，
            // 不能因为"眼下没数据"就消失。所以这里从"子序列"升级成"前缀必须逐项相等"。
            List<string> expectedOrder = new(TypeIdCatalog.BroadCategories) { "其他" };
            List<string> actualOrder = categoryTabs.Select(tab => tab.Caption).ToList();

            bool fourPresent = TypeIdCatalog.BroadCategories.All(c => actualOrder.Contains(c));
            bool prefixExact = actualOrder.Count >= TypeIdCatalog.BroadCategories.Count
                && actualOrder.Take(TypeIdCatalog.BroadCategories.Count)
                    .SequenceEqual(TypeIdCatalog.BroadCategories);
            bool tailOk = actualOrder.Count == TypeIdCatalog.BroadCategories.Count
                || (actualOrder.Count == TypeIdCatalog.BroadCategories.Count + 1
                    && actualOrder[^1] == "其他");
            bool categoryOrderOk = fourPresent && prefixExact && tailOk;

            string categoryOrderDetail = string.Join(" → ", categoryTabs.Select(t => $"{t.Caption}={t.Points.Count}"));

            // 空标签不该画"0"角标（HasPoints=false）：那是按口径必须存在、但眼下没内容的一栏，
            // 挂个 0 只是噪音。
            List<PointTabViewModel> emptyTabs = categoryTabs.Where(tab => tab.Points.Count == 0).ToList();
            bool emptyBadgeOk = emptyTabs.All(tab => !tab.HasPoints && tab.CountText == "0");

            // "遥控"这一栏如果为空，提示语要说清为什么（而不是让人以为点丢了）
            PointTabViewModel remoteTab = categoryTabs.FirstOrDefault(tab => tab.Caption == "遥控");
            bool remoteNoteOk = remoteTab == null
                || remoteTab.Points.Count > 0
                || remoteTab.ToolTip.Contains("主站下发");

            // 同一标签内按点号升序 —— 这是用户明确要求的，也是按大类合并之后最容易被遗忘的一步
            bool ascendingOk = categoryTabs.All(tab =>
            {
                for (int i = 1; i < tab.Points.Count; i++)
                {
                    if (tab.Points[i - 1].ObjectAddress > tab.Points[i].ObjectAddress)
                    {
                        return false;
                    }
                }
                return true;
            });

            // 每个点确实落在自己那一类里。只比数量的话，"把遥测和遥信对调"这种错照样能过。
            bool bucketOk = categoryTabs.All(tab => tab.Points.All(point =>
                point.Type != null && TypeIdCatalog.BroadCategoryOf(point.Type.Type) == tab.Caption));

            // 大类标签必须是纯中文（不再带 M_XX_NA_1 的规约代号）
            bool categoryCaptionOk = categoryTabs.All(tab =>
                !tab.Caption.Contains('_') && !tab.Caption.Any(ch => ch < 128));

            bool categoryPass = categoryFirstIsAll && sumOfCategories == allCount
                && categoryOrderOk && ascendingOk && bucketOk && categoryCaptionOk
                && emptyBadgeOk && remoteNoteOk
                && categoryTabs.Count >= TypeIdCatalog.BroadCategories.Count;

            Log(dir, $"point-tabs-category: 标签数={viewModel.PointTabs.Count} 首个为全部={categoryFirstIsAll} "
                + $"分组合计={sumOfCategories}/{allCount} 四类齐全={fourPresent} 顺序精确={prefixExact} "
                + $"同标签点号升序={ascendingOk} 归类正确={bucketOk} 标签纯中文={categoryCaptionOk} "
                + $"空标签无角标={emptyBadgeOk}（{emptyTabs.Count} 个空） 遥控提示语={remoteNoteOk} => "
                + (categoryPass ? "PASS" : "FAIL"));
            Log(dir, $"point-tabs-category-detail: {categoryDetail}");
            Log(dir, $"point-tabs-category-order: {categoryOrderDetail}");
            Log(dir, $"point-tabs-category-button: 按钮文字=\"{viewModel.PointGroupModeCaption}\" "
                + $"（期望\"电力大类\"） => {(viewModel.PointGroupModeCaption == "电力大类" ? "PASS" : "FAIL")}");

            // 留一张"按电力大类"的整窗图。断言只能证明数量和顺序对，
            // 看不出"同一个遥测标签里 9xx 和 4001 混排之后读着顺不顺"，
            // 这种只有图能给下一轮改样式的人。选遥测页而不是"全部"页，
            // 因为要看的正是"合并之后排序对不对"。
            if (mainWindow != null)
            {
                PointTabViewModel telemetry = viewModel.PointTabs
                    .Skip(1)
                    .FirstOrDefault(tab => tab.Caption == "遥测");
                if (telemetry != null)
                {
                    viewModel.SelectedPointTab = telemetry;
                    Dispatcher.UIThread.RunJobs();
                }
                WarmUpLayout(mainWindow);
                Save(mainWindow, Path.Combine(dir, "01b-主窗口-电力大类.png"));

                // ───── "遥控"大类页：1.8.0 起它装的是档位（步位置） ─────
                // 分类口径改过之后，这一页不再"常态为空"——档位（M_ST_NA_1）归遥控，
                // 所以这里要验的是"档位点确实落进遥控页"，而不再是"空页有提示语"。
                // 空页那条路径仍然保留（下面分支），因为换个点表就可能某一类真的是空的。
                PointTabViewModel remoteCategoryTab = viewModel.PointTabs
                    .Skip(1)
                    .FirstOrDefault(tab => tab.Caption == "遥控");

                if (remoteCategoryTab != null)
                {
                    viewModel.SelectedPointTab = remoteCategoryTab;
                    Dispatcher.UIThread.RunJobs();
                    WarmUpLayout(mainWindow);

                    // 提示语只该在"这一类一个点都没有"时出现
                    TextBlock hintBlock = mainWindow.GetVisualDescendants().OfType<TextBlock>()
                        .FirstOrDefault(text => text.Text != null && text.Text.Contains("主站下发"));

                    DataGrid pointGrid = mainWindow.GetVisualDescendants().OfType<DataGrid>()
                        .FirstOrDefault(grid => grid.Name == "PointDataGrid");

                    if (remoteCategoryTab.Points.Count > 0)
                    {
                        bool remoteOk = remoteCategoryTab.HasPoints
                            && remoteCategoryTab.Points.All(point => point.Type != null
                                && TypeIdCatalog.BroadCategoryOf(point.Type.Type) == "遥控")
                            && pointGrid?.ItemsSource is System.Collections.ICollection shown
                            && shown.Count == remoteCategoryTab.Points.Count
                            && (hintBlock == null || !hintBlock.IsVisible);

                        Log(dir, $"point-tabs-remote: 遥控点数={remoteCategoryTab.Points.Count} "
                            + $"HasPoints={remoteCategoryTab.HasPoints} "
                            + $"全部归遥控={remoteCategoryTab.Points.All(point => point.Type != null && TypeIdCatalog.BroadCategoryOf(point.Type.Type) == "遥控")} "
                            + $"含类型={string.Join("/", remoteCategoryTab.Points.Select(point => point.Type?.Code).Distinct())} "
                            + $"空页提示已隐藏={(hintBlock == null || !hintBlock.IsVisible)} => "
                            + (remoteOk ? "PASS" : "FAIL"));

                        Save(mainWindow, Path.Combine(dir, "01c-主窗口-遥控页.png"));
                    }
                    else
                    {
                        // 这一栏真的是 0 个：提示必须**真的画在可视树上**，
                        // 不是只写了个属性；而且要在点表正中，不是缩在左下角。
                        Point? hintPos = hintBlock?.TranslatePoint(new Point(0, 0), mainWindow);
                        Point? gridPos = pointGrid?.TranslatePoint(new Point(0, 0), mainWindow);
                        double hintCenterX = hintPos.HasValue ? hintPos.Value.X + hintBlock.Bounds.Width / 2 : double.NaN;
                        double gridCenterX = gridPos.HasValue ? gridPos.Value.X + pointGrid.Bounds.Width / 2 : double.NaN;
                        double centerOffset = Math.Abs(hintCenterX - gridCenterX);
                        double hintCenterY = hintPos.HasValue ? hintPos.Value.Y + hintBlock.Bounds.Height / 2 : double.NaN;
                        double gridCenterY = gridPos.HasValue ? gridPos.Value.Y + pointGrid.Bounds.Height / 2 : double.NaN;
                        double centerOffsetY = Math.Abs(hintCenterY - gridCenterY);

                        bool emptyHintOk = !remoteCategoryTab.HasPoints
                            && hintBlock != null
                            && hintBlock.IsVisible
                            && hintBlock.Bounds.Width > 60
                            && pointGrid?.ItemsSource is System.Collections.ICollection rows
                            && rows.Count == 0
                            && centerOffset <= 2.5
                            && centerOffsetY <= 2.5;

                        Log(dir, $"point-tabs-empty: 遥控行数={remoteCategoryTab.Points.Count} "
                            + $"HasPoints={remoteCategoryTab.HasPoints} 提示已渲染={hintBlock?.IsVisible} "
                            + $"宽={hintBlock?.Bounds.Width:0.#} 居中偏差=({centerOffset:0.#},{centerOffsetY:0.#})px "
                            + $"文案=\"{remoteCategoryTab.EmptyHint}\" => {(emptyHintOk ? "PASS" : "FAIL")}");

                        Save(mainWindow, Path.Combine(dir, "01c-主窗口-遥控空页.png"));
                    }
                }
            }

            // ───────────────── ③ 切回来必须和第一次完全一致 ─────────────────
            viewModel.TogglePointGroupModeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            List<string> backSnapshot = viewModel.PointTabs
                .Select(t => $"{t.Caption}:{t.Points.Count}").ToList();
            bool roundTripOk = !viewModel.GroupPointsByCategory
                && backSnapshot.SequenceEqual(typeSnapshot)
                && viewModel.PointGroupModeCaption == "ASDU 类型";
            Log(dir, $"point-group-toggle: 切回={DescribeMode(viewModel.GroupPointsByCategory)} "
                + $"标签往返一致={backSnapshot.SequenceEqual(typeSnapshot)} "
                + $"按钮文字=\"{viewModel.PointGroupModeCaption}\" "
                + $"（{string.Join(" | ", backSnapshot)}） => {(roundTripOk ? "PASS" : "FAIL")}");

            // 复位到"全部"，截图才是全量视图（分组方式此时已回到 ASDU 类型）
            if (viewModel.PointTabs.Count > 0)
            {
                viewModel.SelectedPointTab = viewModel.PointTabs[0];
            }
        }

        /// <summary>
        /// 自检：分组切换按钮的可见性（五套主题）+ 点号表头的进制提示。
        ///
        /// 按钮为什么值得单独立一条断言：
        /// 它放在标签栏右端，背后那条栏的底色就是面板色。如果按钮底色也取面板色
        /// （或者取了一个和面板近到分不出的令牌），在浅色主题里就是"一整块白上叠一块白"，
        /// 代码仍然是 DynamicResource、XAML 里怎么看都对，只有量数值才抓得住。
        /// 所以这里量三件事：底色/字色确实是那两个令牌、字和底对比度 ≥ 4.5、
        /// 底和栏底（面板色）对比度 > 1.1（能看出这是一颗按钮，不是一段文字）。
        /// 五套主题逐个量一遍——只验当前主题等于只验了五分之一。
        /// </summary>
        private static async Task RunPointGroupToggleSelfCheckAsync(
            string dir, Window mainWindow, MainWindowViewModel viewModel)
        {
            Button toggle = mainWindow.FindControl<Button>("PointGroupModeButton");
            ListBox tabBar = mainWindow.FindControl<ListBox>("PointTabBar");

            // ① 位置：必须在标签栏右侧、同一行、真的可见（不是被布局压成 0 宽）
            bool placed = false;
            double edgeGap = double.NaN;
            if (toggle != null && tabBar != null)
            {
                Point? barPos = tabBar.TranslatePoint(new Point(0, 0), mainWindow);
                Point? buttonPos = toggle.TranslatePoint(new Point(0, 0), mainWindow);
                if (barPos.HasValue && buttonPos.HasValue)
                {
                    edgeGap = buttonPos.Value.X - (barPos.Value.X + tabBar.Bounds.Width);
                    bool sameRow = Math.Abs(buttonPos.Value.Y - barPos.Value.Y) < 8;
                    placed = toggle.IsVisible && toggle.Bounds.Width >= 56
                        && toggle.Bounds.Height >= 18 && sameRow && edgeGap >= -1;
                }
            }

            // ② 配色：真实控件上的底/字必须就是那两个令牌
            Color actualBg = (toggle?.Background as ISolidColorBrush)?.Color ?? Colors.Transparent;
            Color actualFg = (toggle?.Foreground as ISolidColorBrush)?.Color ?? Colors.Transparent;
            Color tokenBg = ResolveColor("PrimarySoftBrush");
            Color tokenFg = ResolveColor("PrimarySoftTextBrush");
            Color barSurface = ResolveColor("SurfaceBrush");

            double textRatio = ContrastRatio(actualFg, actualBg);
            double pillRatio = ContrastRatio(tokenBg, barSurface);
            bool lookOk = placed
                && actualBg == tokenBg && actualFg == tokenFg
                && textRatio >= 4.5 && pillRatio > 1.1;

            Log(dir, $"mode-toggle-look: 可见={toggle?.IsVisible} 尺寸={toggle?.Bounds.Width:0}x{toggle?.Bounds.Height:0} "
                + $"距标签栏右端={edgeGap:0.#} 底={DescribeBrush(toggle?.Background)}(期望 {DescribeBrush(new SolidColorBrush(tokenBg))}) "
                + $"字={DescribeBrush(toggle?.Foreground)}(期望 {DescribeBrush(new SolidColorBrush(tokenFg))}) "
                + $"字/底={textRatio:0.00}:1 底/栏底={pillRatio:0.00}:1 => {(lookOk ? "PASS" : "FAIL")}");

            // ③ 五套主题逐个量令牌（只读资源，不读控件，所以不用等过渡动画）
            string originalId = ThemeService.Current?.Id ?? ThemeService.Options[0].Id;
            foreach (AppThemeOption option in ThemeService.Options)
            {
                ThemeService.Apply(option.Id, persist: false);
                Dispatcher.UIThread.RunJobs();

                Color background = ResolveColor("PrimarySoftBrush");
                Color foreground = ResolveColor("PrimarySoftTextBrush");
                Color surface = ResolveColor("SurfaceBrush");
                double fgRatio = ContrastRatio(foreground, background);
                double contrastWithBar = ContrastRatio(background, surface);
                bool ok = fgRatio >= 4.5 && contrastWithBar > 1.1;

                Log(dir, $"mode-toggle-themes[{option.Name}]: 字/底={fgRatio:0.00}:1 "
                    + $"底/栏底={contrastWithBar:0.00}:1 => {(ok ? "PASS" : "FAIL")}");
            }

            // 切回原来的主题并等过渡走完，否则后面量到的控件底色是渐变中间值
            ThemeService.Apply(originalId, persist: false);
            await SettleThemeAsync(mainWindow);

            // ④ 点号表头：两行（"点号" + "10 进制"）、提示语里讲清协议帧是 3 字节
            DataGrid grid = mainWindow.FindControl<DataGrid>("PointDataGrid");
            StackPanel addressHeader = grid != null && grid.Columns.Count > 0
                ? grid.Columns[0].Header as StackPanel
                : null;
            List<TextBlock> headerLines = addressHeader?.Children.OfType<TextBlock>().ToList()
                ?? new List<TextBlock>();
            string headerTip = addressHeader == null ? string.Empty : ToolTip.GetTip(addressHeader) as string ?? string.Empty;

            bool twoLines = headerLines.Count == 2
                && headerLines[0].Text == "点号"
                && headerLines[1].Text != null && headerLines[1].Text.Contains("进制");
            bool smaller = headerLines.Count == 2
                && headerLines[1].FontSize > 0
                && headerLines[1].FontSize < headerLines[0].FontSize;
            bool tipOk = headerTip.Contains("十六进制") && headerTip.Contains("十进制");

            // 光读声明不算数：从可视树上确认表头里真的画出了"进制"这两个字
            bool rendered = grid != null && grid.GetVisualDescendants().OfType<TextBlock>()
                .Any(text => text.Text != null && text.Text.Contains("进制"));

            bool headerOk = twoLines && smaller && tipOk && rendered;
            Log(dir, $"header-hint: 行数={headerLines.Count} "
                + $"首行=\"{headerLines.ElementAtOrDefault(0)?.Text}\" "
                + $"次行=\"{headerLines.ElementAtOrDefault(1)?.Text}\" "
                + $"次行字号={headerLines.ElementAtOrDefault(1)?.FontSize:0.#}(主 {headerLines.ElementAtOrDefault(0)?.FontSize:0.#}) "
                + $"提示含进制度说明={tipOk} 已渲染={rendered} => {(headerOk ? "PASS" : "FAIL")}");
        }

        /// <summary>
        /// 自检：侧边栏的双视图（1.9.0）。
        ///
        /// 这一轮改的东西有个共同点——"看起来对"很容易，"其实对"要量：
        ///   ① 扁平列表里标签上的 IP / 端口是**跨对象**取来的（属于通道，不属于从站），
        ///      光看 XAML 发现不了取错对象，所以既量 ViewModel 的值，也量可视树上真的画出来了；
        ///   ② 颜色分三态，要验的是"通道状态变了、行跟不跟着变"，
        ///      只验初始色等于灰毫无意义（新建的通道本来就是灰的）；
        ///   ③ "新建从站顺手把通道建了"这条语义只在 ViewModel 里，界面没有痕迹，
        ///      只能直接构造草稿模型问它的默认值；
        ///   ④ 设置项要真改一次再从盘上重读——"写进 JSON"和"读得回来"是两回事，
        ///      恒写 false 也能让前一条过。
        /// </summary>
        private static async Task RunSidebarViewSelfCheckAsync(
            string dir, Window mainWindow, MainWindowViewModel viewModel)
        {
            if (viewModel == null)
            {
                Log(dir, "sidebar-view: 跳过（无主视图模型）");
                return;
            }

            bool wasTree = viewModel.IsTreeView;

            // ── ① 扁平视图：摊平、面板对调、工具条联动 ──────────────
            viewModel.SetSidebarView(false, persist: false);
            await Task.Delay(350);
            WarmUpLayout(mainWindow);

            int slaveTotal = viewModel.Connections.Sum(connection => connection.Slaves.Count);
            bool rowsOk = viewModel.IsFlatView
                && viewModel.SlaveRows.Count == slaveTotal
                && viewModel.SlaveRows.All(row => viewModel.Connections.Contains(row.Channel)
                    && row.Channel.Slaves.Contains(row.Slave));

            Grid flatPanel = mainWindow.FindControl<Grid>("FlatSlavePanel");
            Grid treePanel = mainWindow.FindControl<Grid>("ChannelTreePanel");
            bool panelsOk = flatPanel?.IsVisible == true && treePanel?.IsVisible == false;

            Button newChannelButton = mainWindow.FindControl<Button>("NewChannelButton");
            bool hideNewChannel = newChannelButton?.IsVisible == false;

            Log(dir, $"sidebar-flat: 行数={viewModel.SlaveRows.Count}/{slaveTotal} "
                + $"摊平且归属正确={rowsOk} 面板对调={panelsOk} 新建通道按钮已隐藏={hideNewChannel} => "
                + (rowsOk && panelsOk && hideNewChannel ? "PASS" : "FAIL"));

            Save(mainWindow, Path.Combine(dir, "01d-主窗口-从站列表视图.png"));

            // ── ② 标签上真的有 IP / 端口 ────────────────────────────
            SlaveRowViewModel first = viewModel.SlaveRows.FirstOrDefault();
            string endpoint = first?.EndPointText;

            bool endpointValueOk = first != null
                && endpoint == first.Channel.EndPointText
                && endpoint.Contains(':');

            // 可视树上真画出来了：光有值不算，得有人把它显示出来
            // （用 InlineText 取整段——第二行现在是 Run 拼接，TextBlock.Text 为 null）
            bool endpointDrawn = first == null || mainWindow.GetVisualDescendants()
                .OfType<TextBlock>()
                .Any(text => InlineText(text).Contains(endpoint, StringComparison.Ordinal)
                    && text.Bounds.Width > 0);

            Log(dir, $"slave-tag-endpoint: 标签=\"{endpoint}\" 取自所属通道={endpointValueOk} "
                + $"可视树上已渲染={endpointDrawn} => "
                + (endpointValueOk && endpointDrawn ? "PASS" : "FAIL"));

            // ── ②a 标签内部的两行布局 ─────────────────────────────
            //
            // 两件事：灯与状态胶囊相对**整张标签**垂直居中；
            // IP 与点数必须在**同一条基线**上。
            //
            // 后半句不能靠"量两个 TextBlock 的中心 Y"来验了——上一版正是两个并排的 TextBlock，
            // 即使字号/LineHeight/VerticalAlignment 全一致，文字在各自盒子里仍按**各自字体的
            // ascent/descent** 摆（IP 是 Consolas、点数是中文字体），基线天生差一两个像素，
            // 肉眼看就是"一高一低"。现在改成同一个 TextBlock 里的多个 Run，基线由排版引擎统一。
            // 所以断言改成验这个前提：第二行是一个 TextBlock 同时装着两段文本，
            // 且不存在"只放 IP"的独立 TextBlock（拆开 = 退回老路）。
            // 必须按 DataContext 精确匹配到"就是上面那个从站"的那张标签。
            // 早先直接取可视树上第一个 slaveTag，等于假设"树里的顺序 = 列表顺序"，
            // 一旦前面新增过通道/从站，可视树里先出现的可能是别的从站（比如自检临时造的那台），
            // 于是拿 A 的标签去比 B 的期望值，断言就莫名其妙地红了。
            Border tagBorder = mainWindow.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(border => border.Classes.Contains("slaveTag")
                    && border.Bounds.Width > 0
                    && (first == null || ReferenceEquals(border.DataContext, first)));

            if (tagBorder == null)
            {
                tagBorder = mainWindow.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(border => border.Classes.Contains("slaveTag") && border.Bounds.Width > 0);
            }

            double tagCenterY = double.NaN;
            double dotCenterY = double.NaN;
            double pillCenterY = double.NaN;
            bool sameBaselineLine = false;
            bool noSplitLine = false;
            string tagDiagnostic = string.Empty;

            if (tagBorder != null)
            {
                tagCenterY = tagBorder.Bounds.Height / 2;

                Avalonia.Controls.Shapes.Ellipse dot = tagBorder.GetVisualDescendants()
                    .OfType<Avalonia.Controls.Shapes.Ellipse>()
                    .FirstOrDefault(shape => !shape.Classes.Contains("halo") && shape.Bounds.Width > 0);
                Point? dotPos = dot?.TranslatePoint(new Point(0, 0), tagBorder);
                if (dot != null && dotPos != null)
                {
                    dotCenterY = dotPos.Value.Y + dot.Bounds.Height / 2;
                }

                Border pill = tagBorder.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(border => border.Classes.Contains("statePill"));
                Point? pillPos = pill?.TranslatePoint(new Point(0, 0), tagBorder);
                if (pill != null && pillPos != null)
                {
                    pillCenterY = pillPos.Value.Y + pill.Bounds.Height / 2;
                }

                List<TextBlock> texts = tagBorder.GetVisualDescendants().OfType<TextBlock>().ToList();
                string countText = first?.PointCountText ?? string.Empty;

                // 第二行现在是单个 TextBlock 里用 Run 拼出来的（为了统一基线），
                // 于是 TextBlock.Text 变成 null，必须用 InlineText 取实际显示文本。
                sameBaselineLine = !string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(countText)
                    && texts.Any(text =>
                    {
                        string shown = InlineText(text);
                        return shown.Contains(endpoint, StringComparison.Ordinal)
                            && shown.Contains(countText, StringComparison.Ordinal);
                    });

                noSplitLine = !texts.Any(text => InlineText(text) == endpoint);

                // 失败时把实际看到的文本都带出来，否则只能靠猜
                // （"IP 与点数不在同一个 TextBlock"有好几种成因，日志不写清楚等于没日志）
                if (!sameBaselineLine || !noSplitLine)
                {
                    tagDiagnostic = $"首个标签={first?.DisplayText ?? "-"}"
                        + $" 期望点数值=\"{countText}\""
                        + $" 标签内文本=[{string.Join(" | ", texts.Select(InlineText))}]";
                }
            }

            bool tagLayoutOk = tagBorder != null
                && Math.Abs(dotCenterY - tagCenterY) <= 1.5
                && Math.Abs(pillCenterY - tagCenterY) <= 1.5
                && sameBaselineLine
                && noSplitLine;

            Log(dir, $"slave-tag-layout: 标签中心Y={tagCenterY:0.0} 灯={dotCenterY:0.0} "
                + $"状态胶囊={pillCenterY:0.0}（±1.5） IP与点数同一个TextBlock={sameBaselineLine} "
                + $"没有拆成两个={noSplitLine}"
                + (tagDiagnostic.Length == 0 ? string.Empty : " " + tagDiagnostic)
                + $" => {(tagLayoutOk ? "PASS" : "FAIL")}");

            // ── ②a-2 侧栏底部那颗切换按钮：只有一个图标 ──────────────
            // 用户明确要求去掉右边那颗 chevron：箭头已经表达"来回切"，
            // 再挂一个 ">" 会让人以为点进去还有一层。这条盯住别再长回来。
            Button viewSwitchButton = mainWindow.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => button.Classes.Contains("viewSwitch"));
            int switchGlyphs = viewSwitchButton?.GetVisualDescendants()
                .OfType<Avalonia.Controls.Shapes.Path>().Count() ?? 0;

            Log(dir, $"sidebar-switch-button: 按钮已找到={viewSwitchButton != null} "
                + $"图形={switchGlyphs} 个（只留切换图标，不要 chevron）=> "
                + (viewSwitchButton != null && switchGlyphs == 1 ? "PASS" : "FAIL"));

            // ②b 顺手截一张"监听中"的标签：橙色这一态在静态图里最好认，
            //     人一眼能看出"标签颜色真的分了状态"。（这一步只截图，不算断言——
            //     端口可能被别人占着，开不起来不该算功能错。）
            if (first != null && !first.Channel.IsOpen)
            {
                try
                {
                    first.Channel.Open();
                    await Task.Delay(300);
                    WarmUpLayout(mainWindow);
                    Save(mainWindow, Path.Combine(dir, "01f-主窗口-从站标签-监听中.png"));
                    Log(dir, $"slave-tag-listening-shot: 通道={first.Channel.Name} "
                        + $"状态=\"{first.Channel.StateText}\"（仅截图）");
                }
                catch (Exception exception)
                {
                    Log(dir, $"slave-tag-listening-shot: 跳过（{exception.Message}）");
                }
                finally
                {
                    try { first.Channel.Close(); } catch { /* 已关闭 */ }
                }
            }

            // ── ③ 标签配色跟着通道三态走 ────────────────────────────
            const int ProbePort = 2421;
            ConnectionViewModel probe = new()
            {
                Name = "视图自检",
                BindAddress = "127.0.0.1",
                Port = ProbePort
            };
            SlaveViewModel probeSlave = new() { Name = "装置", CommonAddress = 9 };
            SlaveRowViewModel probeRow = new(probe, probeSlave);

            int visualRaises = 0;
            probeRow.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SlaveRowViewModel.TagBorder))
                {
                    visualRaises++;
                }
            };

            bool stoppedOk = SameBrush(probeRow.TagBorder, ThemePalette.Get("TextDisabledBrush"))
                && SameBrush(probeRow.TagBackground, ThemePalette.Get("SurfaceAltBrush"))
                && probeRow.StateShortText == "已停止";

            bool listeningOk = false;
            bool connectedOk = false;
            try
            {
                probe.Open();
                await Task.Delay(250);
                listeningOk = SameBrush(probeRow.TagBorder, ThemePalette.Get("WarningBrush"))
                    && SameBrush(probeRow.TagForeground, ThemePalette.Get("WarningTextBrush"))
                    && probeRow.StateShortText == "监听中";

                using System.Net.Sockets.TcpClient client = new();
                await client.ConnectAsync(System.Net.IPAddress.Loopback, ProbePort);
                await Task.Delay(500);
                connectedOk = SameBrush(probeRow.TagBorder, ThemePalette.Get("SuccessBrush"))
                    && SameBrush(probeRow.TagForeground, ThemePalette.Get("SuccessTextBrush"))
                    && probeRow.StateShortText == "已连接";

                client.Close();
            }
            catch (Exception exception)
            {
                Log(dir, $"slave-tag-state: 异常 {exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                try { probe.Close(); } catch { /* 已关闭 */ }
                probeRow.Dispose();
                probe.Dispose();
            }

            bool stateOk = stoppedOk && listeningOk && connectedOk && visualRaises >= 2;
            Log(dir, $"slave-tag-state: 停止灰={stoppedOk} 监听橙={listeningOk} 主站接入绿={connectedOk} "
                + $"颜色变更通知={visualRaises}次 => {(stateOk ? "PASS" : "FAIL")}");

            // ── ④ 双击标签 = 同一个对话框里改 IP / 端口 ─────────────
            ConnectionViewModel editProbe = new()
            {
                Name = "自检通道",
                BindAddress = "127.0.0.1",
                Port = 2441
            };
            SlaveViewModel editSlave = new() { Name = "自检装置", CommonAddress = 1 };

            SlaveEditModel editModel = new(editSlave, editProbe, new List<ConnectionViewModel> { editProbe });
            bool editShowsEndpoint = editModel.CanEditChannel
                && editModel.OwnerPort == 2441
                && editModel.OwnerBindAddress == "127.0.0.1";

            editModel.OwnerPort = 2442;
            editModel.OwnerBindAddress = "0.0.0.0";
            string editError = editModel.Validate();
            editModel.Apply();
            bool editApplied = editError == null
                && editProbe.Port == 2442
                && editProbe.BindAddress == "0.0.0.0";

            // 树形视图下不显示这块（那儿通道自己有一行，各改各的职责清楚）
            bool treeHidesEndpoint = !new SlaveEditModel(editSlave).CanEditChannel;

            bool editOk = editShowsEndpoint && editApplied && treeHidesEndpoint;
            Log(dir, $"slave-double-click-edit: 带出端点={editShowsEndpoint} 写回通道={editApplied}"
                + $"（{editError ?? "无错误"}） 树形视图不显示={treeHidesEndpoint} => "
                + (editOk ? "PASS" : "FAIL"));

            // ── ⑤ 新建从站默认"连通道一起建" ────────────────────────
            SlaveEditModel flatCreate = new(
                new SlaveViewModel { Name = "新装置", CommonAddress = 7 },
                viewModel.Connections,
                null,
                preferNewChannel: true);

            Log(dir, $"slave-create-flat: 默认新建通道={flatCreate.CreateNewChannel} "
                + $"默认端点={flatCreate.NewBindAddress}:{flatCreate.NewPort} => "
                + (flatCreate.CreateNewChannel ? "PASS" : "FAIL"));

            // ── ⑥ 切到树形视图：面板对调、新建通道按钮回来 ──────────
            // persist: true —— 和用户点那颗胶囊按钮走的是同一条路（按钮就是持久化的），
            // 下面 ⑦ 要验"设置页能不能把已经落盘的视图改回来"，先得让它真的落成树形
            viewModel.SetSidebarView(true, persist: true);
            await Task.Delay(350);
            WarmUpLayout(mainWindow);

            bool treePanels = flatPanel?.IsVisible == false && treePanel?.IsVisible == true;
            bool showNewChannel = newChannelButton?.IsVisible == true;
            Log(dir, $"sidebar-tree: 面板对调={treePanels} 新建通道按钮已恢复={showNewChannel} => "
                + (treePanels && showNewChannel ? "PASS" : "FAIL"));

            Save(mainWindow, Path.Combine(dir, "01e-主窗口-通道树视图.png"));

            // ── ⑦ 设置 → 视图：立刻生效 + 真的落盘 ──────────────────
            ViewPageViewModel page = viewModel.Settings.ViewPage;

            page.SidebarListView = true;
            bool pageToFlat = viewModel.IsFlatView && !AppSettingsStore.Current.SidebarTreeView;

            page.SidebarTableView = true;
            bool pageToTree = viewModel.IsTreeView && AppSettingsStore.Current.SidebarTreeView;

            page.GroupByCategory = true;
            bool pageGroup = viewModel.GroupPointsByCategory
                && AppSettingsStore.Current.PointsGroupByCategory;

            // 从盘上重读：只验"内存里的 Current 变了"是查不出"根本没写文件"的
            AppSettingsStore.Load();
            bool persisted = AppSettingsStore.Current.SidebarTreeView
                && AppSettingsStore.Current.PointsGroupByCategory;

            // 收尾：改回默认（扁平视图 + 按类型分组）并落盘，别把自检的状态留给下次
            page.GroupByAsduType = true;
            page.SidebarListView = true;
            AppSettingsStore.Load();
            bool restored = !AppSettingsStore.Current.SidebarTreeView
                && !AppSettingsStore.Current.PointsGroupByCategory;

            bool settingsOk = pageToFlat && pageToTree && pageGroup && persisted && restored;
            Log(dir, $"settings-view-page: 设置页→扁平={pageToFlat} →树形={pageToTree} "
                + $"分组方式={pageGroup} 已落盘={persisted} 收尾复位={restored} => "
                + (settingsOk ? "PASS" : "FAIL"));

            // 恢复进入本段之前的样子，后面还有断言要读工作区几何
            viewModel.SetSidebarView(wasTree, persist: false);
            await Task.Delay(200);
        }

        /// <summary>
        /// 自检：设置界面 + 自定义 ASDU 类型 + 点表显示中文名。
        ///
        /// 这一套里最值得验证的不是"界面画出来了"，而是三件容易"看着对、实际错"的事：
        ///   ① 点表那一列真的显示中文名（而不是把代号改了列宽又露出来）；
        ///   ② 新增一个自定义类型之后，它的**编码格式**真的进了 Core 的注册表——
        ///      只改界面不改注册表的话，发出去的帧仍是旧的兜底格式，界面上完全看不出来；
        ///   ③ 类型被点位占用时删不掉，且设置文件里真的没留下垃圾。
        ///
        /// 全程自己收拾干净：加进去的类型最后会删掉、探针点也会删掉，
        /// 不给下一次运行留一个"莫名其妙多出来的自定义类型"。
        /// </summary>
        private static async Task RunSettingsSelfCheckAsync(
            string dir, Window mainWindow, MainWindowViewModel viewModel)
        {
            if (viewModel == null)
            {
                Log(dir, "settings-selfcheck: 跳过（无主视图模型）");
                return;
            }

            DataGrid grid = mainWindow.FindControl<DataGrid>("PointDataGrid");
            SlaveViewModel slave = viewModel.Connections
                .SelectMany(c => c.Slaves)
                .FirstOrDefault(s => s.Points.Count > 0);

            // ───────── ① 点表 ASDU 列显示中文名 ─────────
            // 原来写的是"模型里第一个点 vs 渲染出来的第一格"。这条会偶发红：
            // 切完 SelectedSlave 之后 DataGrid 的行容器不一定已经重新实体化，
            // 量到的第一格还是上一张点表的（2026-10-29 实跑撞到过一次：期望"双点信息"、
            // 量到"步位置信息"，原样重跑又全绿——不是功能坏了，是断言在跟渲染赛跑）。
            // 现在改成**逐行自证**：每个已实体化的行，格子上的中文名必须等于它自己
            // 悬停代号查出来的名字。与行序无关，顺带覆盖到不止一行。
            int asduRows = 0;
            int asduBad = 0;
            string asduBadDetail = string.Empty;

            if (slave != null && grid != null)
            {
                viewModel.SelectedSlave = slave;
                if (viewModel.PointTabs.Count > 0)
                {
                    viewModel.SelectedPointTab = viewModel.PointTabs[0];
                }
                Dispatcher.UIThread.RunJobs();
                WarmUpLayout(mainWindow);

                foreach (TextBlock cell in grid.GetVisualDescendants()
                             .OfType<TextBlock>()
                             .Where(text => (text.Tag as string) == "asdu"))
                {
                    // 悬停提示形如 "M_DP_NA_1 · 遥信"，前半截就是规约代号
                    string tip = ToolTip.GetTip(cell) as string ?? string.Empty;
                    string code = tip.Split('·')[0].Trim();
                    TypeDescriptor descriptor = TypeIdCatalog.AllTypes
                        .FirstOrDefault(item => item.Code == code);
                    if (descriptor == null)
                    {
                        continue;
                    }

                    asduRows++;

                    // 两件事一起验：得是中文名（不是代号本身），而且和代号对得上
                    if (cell.Text != descriptor.DisplayName || cell.Text == code)
                    {
                        asduBad++;
                        if (asduBadDetail.Length == 0)
                        {
                            asduBadDetail = $"{code}→\"{cell.Text}\"（期望 {descriptor.DisplayName}）";
                        }
                    }
                }
            }

            bool columnOk = asduRows >= 2 && asduBad == 0;
            Log(dir, $"asdu-column-chinese: 逐行核对={asduRows} 行 不符={asduBad} "
                + (asduBadDetail.Length > 0
                    ? $"首例 {asduBadDetail} "
                    : "全部为中文名、悬停带代号 ")
                + $"=> {(columnOk ? "PASS" : "FAIL")}");

            // ───────── ② 设置按钮：位置在"检查更新"右边 ─────────
            Button settingsButton = mainWindow.FindControl<Button>("SettingsButton");
            Button updateButton = mainWindow.FindControl<Button>("UpdateButton");
            Border titleBar = mainWindow.FindControl<Border>("TitleBar");

            bool buttonOk = false;
            double buttonX = double.NaN;
            double updateX = double.NaN;
            if (settingsButton != null && updateButton != null && titleBar != null)
            {
                Point? s = settingsButton.TranslatePoint(new Point(0, 0), titleBar);
                Point? u = updateButton.TranslatePoint(new Point(0, 0), titleBar);
                if (s.HasValue && u.HasValue)
                {
                    buttonX = s.Value.X;
                    updateX = u.Value.X;
                    buttonOk = settingsButton.IsVisible
                        && buttonX > updateX
                        && buttonX + settingsButton.Bounds.Width <= titleBar.Bounds.Width
                        && settingsButton.Bounds.Width >= 26;
                }
            }

            Log(dir, $"settings-button: 检查更新@x{updateX:0.#} 设置@x{buttonX:0.#} "
                + $"可见={settingsButton?.IsVisible} 宽={settingsButton?.Bounds.Width:0.#} => "
                + (buttonOk ? "PASS" : "FAIL"));

            // ───────── ③ 打开设置：主界面切成"左标签栏 + 右内容" ─────────
            //
            // 先归位再验。Toggle 是个开关：如果进这段之前设置页已经是开着的（别的断言、
            // 或者上一轮残留的状态），这一下就是"关"，后面整段断言全反着来——
            // 表现为 settings-open 说"切到设置=False"、点表单元格读成空串，
            // 看着像功能坏了，其实是断言没从确定的状态起步。（本轮就撞过一次。）
            bool settingsWasOpen = viewModel.IsSettingsOpen;
            if (settingsWasOpen)
            {
                viewModel.CloseSettingsCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                WarmUpLayout(mainWindow);
            }

            viewModel.ToggleSettingsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            WarmUpLayout(mainWindow);

            Grid sidebarGrid = mainWindow.FindControl<Grid>("SidebarGrid");
            Grid settingsView = mainWindow.FindControl<Grid>("SettingsView");
            ListBox nav = mainWindow.FindControl<ListBox>("SettingsNav");
            Border contentPanel = mainWindow.FindControl<Border>("SettingsContentPanel");

            // 工具条是按样式类找的（它没有 x:Name，早先也不需要在代码里引用）
            Border toolbar = mainWindow.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(border => border.Classes.Contains("toolbar"));

            Point? navPos = nav?.TranslatePoint(new Point(0, 0), mainWindow);
            Point? contentPos = contentPanel?.TranslatePoint(new Point(0, 0), mainWindow);
            bool sideBySide = navPos.HasValue && contentPos.HasValue
                && nav.Bounds.Width >= 150
                && contentPos.Value.X > navPos.Value.X + nav.Bounds.Width - 1;

            bool openOk = viewModel.IsSettingsOpen
                && settingsView?.IsVisible == true
                && sidebarGrid?.IsVisible == false
                && toolbar?.IsVisible == false
                && nav?.IsVisible == true
                && sideBySide;

            Log(dir, $"settings-open: 切到设置={viewModel.IsSettingsOpen} 工作区隐藏={sidebarGrid?.IsVisible == false} "
                + $"工具条隐藏={toolbar?.IsVisible == false} 设置可见={settingsView?.IsVisible} "
                + $"左侧栏宽={nav?.Bounds.Width:0.#} 内容区在右侧={sideBySide} "
                + $"（开始时已开着={settingsWasOpen}） => {(openOk ? "PASS" : "FAIL")}");

            // 左侧标签栏：五页（1.10.1 加了「工程」），「视图」排第一（1.9.1 按用户要求提前），
            // 「关于」不在 Pages 里（它钉在左栏最下面单独一格，下面单独验）。
            // 打开设置时选中的是第一页 —— 所以这里期望「视图」，不是「ASDU 类型」；
            // 后面那套 ASDU 断言靠紧跟的显式切页把当前页换成 ASDU 类型。
            bool navOk = nav != null
                && viewModel.Settings.Pages.Count == 5
                && viewModel.Settings.Pages[0].Title == "视图"
                && viewModel.Settings.Pages[1].Title == "工程"
                && viewModel.Settings.Pages[2].Title == "ASDU 类型"
                && viewModel.Settings.SelectedPage?.Title == "视图"
                && !viewModel.Settings.IsAboutPageSelected;
            string navDetail = string.Join(" / ", viewModel.Settings.Pages.Select(page => page.Title));
            Log(dir, $"settings-nav: 页数={viewModel.Settings.Pages.Count} [{navDetail}] "
                + $"关于不在列表里={!viewModel.Settings.Pages.Any(p => p.Title == "关于")} "
                + $"当前={viewModel.Settings.SelectedPage?.Title} => {(navOk ? "PASS" : "FAIL")}");

            // 右侧内容确实换成了当前页的模板（切页后 ContentControl 里应当是另一类 VM）
            //
            // 必须按名字取。原来用"谁的 Content 是 SettingsPageViewModel"去找，
            // 会先命中左侧导航的 ListBoxItem（它也是 ContentControl，Content 就是页面 VM），
            // 于是量到的永远是第一页，切页断言恒 FAIL——断言的错，不是功能的错。
            ContentControl host = mainWindow.FindControl<ContentControl>("SettingsPageHost")
                ?? mainWindow.GetVisualDescendants().OfType<ContentControl>()
                    .FirstOrDefault(control => control is not ListBoxItem
                        && control.Content is SettingsPageViewModel);

            // 打开设置落在第一页（视图），右侧内容就得是视图页那张模板。
            // 顺带钉住 host 与 SelectedPage 是同一个实例——只比对类型的话，
            // 哪天两页共用一个模板类，这条就形同虚设。
            bool templateOk = host?.Content is ViewPageViewModel
                && ReferenceEquals(host.Content, viewModel.Settings.SelectedPage);
            Log(dir, $"settings-template: 内容控件={host?.GetType().Name} "
                + $"当前页类型={host?.Content?.GetType().Name} => {(templateOk ? "PASS" : "FAIL")}");

            // 后面从 ③ 开始是一整套 ASDU 类型页的断言（按钮图标、表格列、右键菜单…），
            // 全部依赖右侧内容真的显示着那张 ASDU 表格。视图页排到第一页之后，
            // 若不显式切页，typeGrid 取到 null、整段 if 被静默跳过——
            // 不会报 FAIL，只会"少验一堆"，比红一条更危险。
            viewModel.Settings.SelectedPage = viewModel.Settings.AsduTypesPage;
            Dispatcher.UIThread.RunJobs();

            WarmUpLayout(mainWindow);

            // ③ 按钮里的图标与文字不能叠在一起。
            //    第一次截图就是这个毛病：看着像"＋新增类型"糊成一坨。
            //    根因是 Path 里放的是 16px 设计尺寸的几何，却给了 12px 的布局框，
            //    而 Shape.Stretch 默认是 None —— 框变小了图形不跟着缩，笔画直接溢到旁边的文字上。
            //    这里量的是几何关系而不是"有没有写 Stretch"，以后换图标、改字号都照样管用。
            System.Collections.Generic.List<Button> settingButtons = (contentPanel == null
                    ? new System.Collections.Generic.List<Button>()
                    : contentPanel.GetVisualDescendants().OfType<Button>()
                        .Where(button => button.Classes.Contains("subtle") && button.Bounds.Width > 0)
                        .ToList());

            bool glyphOk = settingButtons.Count >= 3;
            System.Collections.Generic.List<string> glyphDetail = new();

            foreach (Button button in settingButtons)
            {
                TextBlock label = button.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
                Avalonia.Controls.Shapes.Path glyph = button.GetVisualDescendants()
                    .OfType<Avalonia.Controls.Shapes.Path>()
                    .FirstOrDefault();

                if (label == null || glyph == null)
                {
                    glyphOk = false;
                    glyphDetail.Add("缺图标或文字");
                    continue;
                }

                Point? labelPos = label.TranslatePoint(new Point(0, 0), button);
                Point? glyphPos = glyph.TranslatePoint(new Point(0, 0), button);
                if (labelPos == null || glyphPos == null)
                {
                    glyphOk = false;
                    glyphDetail.Add("位置取不到");
                    continue;
                }

                // 图标右沿到文字左沿的净间距
                double gap = labelPos.Value.X - (glyphPos.Value.X + glyph.Bounds.Width);

                // 图标还得给按钮左边留出内边距；文字不能高过按钮（高了就是被压扁/裁掉）
                bool insetOk = glyphPos.Value.X >= 4;
                bool fitsOk = label.Bounds.Height <= button.Bounds.Height + 0.5;

                glyphDetail.Add($"{label.Text}:间距{gap:0.#}/左内边{labelPos.Value.X - glyphPos.Value.X:0.#}"
                    + $"/字高{label.Bounds.Height:0.#}≤{button.Bounds.Height:0.#}");

                if (gap < 3 || !insetOk || !fitsOk)
                {
                    glyphOk = false;
                }
            }

            Log(dir, $"settings-button-glyphs: 按钮={settingButtons.Count} "
                + $"[{string.Join(" | ", glyphDetail)}] => {(glyphOk ? "PASS" : "FAIL")}");

            // ③b "编码格式"这一列必须真的说清字节数。
            //     原来所有内置类型一律显示"专用实现"——在一列叫编码格式的格子里等于没说。
            //     现在走"注册表 → 专用类说明表 → 专用实现"三级回落，
            //     这条断言盯的就是别哪天表被删了又退回去。
            AsduTypeRowViewModel spRow = viewModel.Settings.AsduTypesPage.Rows
                .FirstOrDefault(row => row.Code == "M_SP_NA_1");
            AsduTypeRowViewModel ncRow = viewModel.Settings.AsduTypesPage.Rows
                .FirstOrDefault(row => row.Code == "M_ME_NC_1");
            AsduTypeRowViewModel scRow = viewModel.Settings.AsduTypesPage.Rows
                .FirstOrDefault(row => row.Code == "C_SC_NA_1");
            AsduTypeRowViewModel nbRow = viewModel.Settings.AsduTypesPage.Rows
                .FirstOrDefault(row => row.Code == "M_ME_NB_1");

            bool formatOk = spRow?.FormatText == "单点 SIQ（1 字节）"
                && ncRow?.FormatText == "短浮点 R32（4+1 字节）"
                && scRow?.FormatText == "单点命令 SCO（1 字节）"
                && nbRow?.FormatText == "标度化 SVA（2+1 字节）"
                && spRow?.TimeTagText == "无时标";

            Log(dir, $"settings-format-column: M_SP_NA_1=<{spRow?.FormatText}/{spRow?.TimeTagText}> "
                + $"M_ME_NC_1=<{ncRow?.FormatText}> C_SC_NA_1=<{scRow?.FormatText}> "
                + $"M_ME_NB_1=<{nbRow?.FormatText}> => {(formatOk ? "PASS" : "FAIL")}");

            // ③b-2 全覆盖：目录里每一个"监视方向"类型都得说得出字节布局。
            //      只要有一行还在显示"专用实现"，这一列就还有人在看天书——
            //      逐行眼查是查不出来的（32 行里混着 3 行），交给断言。
            List<string> noFormat = TypeIdCatalog.AllTypes
                .Where(type => !type.IsCommand)
                .Where(type => !InformationObjectRegistry.TryDescribe(type.Type, out _, out _))
                .Select(type => type.Code)
                .ToList();

            Log(dir, $"settings-format-coverage: 监视类型 {TypeIdCatalog.AllTypes.Count(t => !t.IsCommand)} 项 "
                + $"说不清格式的 {noFormat.Count} 项[{string.Join(" / ", noFormat)}] => "
                + (noFormat.Count == 0 ? "PASS" : "FAIL"));

            // ③b-3 分类口径：电网上只有四种"遥"再加一个"其他"，不许再冒出"电度""档位"这种自造名。
            //
            //      1.8.0 之前：M_IT_* 叫"电度"、M_ST_*/M_BO_*/M_PS_*/M_EI_* 叫"档位"。
            //      现场看到这两个词要再翻译一次（电度=遥脉、档位=遥控），而翻译错一次
            //      就会把点分到错的页签里。这里逐条钉住新的对应关系。
            bool categoryVocabularyOk =
                TypeIdCatalog.Categories.Count == 5
                && !TypeIdCatalog.Categories.Contains("电度")
                && !TypeIdCatalog.Categories.Contains("档位")
                && TypeIdCatalog.Get(TypeIdentifier.M_ST_NA_1).Category == "遥控"
                && TypeIdCatalog.Get(TypeIdentifier.M_ST_TA_1).Category == "遥控"
                && TypeIdCatalog.Get(TypeIdentifier.M_ST_TB_1).Category == "遥控"
                && TypeIdCatalog.Get(TypeIdentifier.M_IT_NA_1).Category == "遥脉"
                && TypeIdCatalog.Get(TypeIdentifier.M_IT_TA_1).Category == "遥脉"
                && TypeIdCatalog.Get(TypeIdentifier.M_IT_TB_1).Category == "遥脉"
                && TypeIdCatalog.Get(TypeIdentifier.M_BO_NA_1).Category == "其他"
                && TypeIdCatalog.Get(TypeIdentifier.M_PS_NA_1).Category == "其他"
                && TypeIdCatalog.Get(TypeIdentifier.M_EI_NA_1).Category == "其他"
                && TypeIdCatalog.BroadCategoryOf(TypeIdentifier.M_ST_NA_1) == "遥控"
                && TypeIdCatalog.BroadCategoryOf(TypeIdentifier.M_IT_NA_1) == "遥脉"
                && TypeIdCatalog.BroadCategoryOf(TypeIdentifier.M_BO_NA_1) == "其他"
                // 旧设置文件里的废弃名要能折算过来，否则编辑时会被静默改成"遥信"
                && TypeIdCatalog.NormalizeCategory("电度") == "遥脉"
                && TypeIdCatalog.NormalizeCategory("档位") == "遥控"
                && TypeIdCatalog.NormalizeCategory("命令") == "遥控"
                // 内置表里不该再有自造分类残留
                && TypeIdCatalog.AllTypes.All(type => !type.IsCustom
                    || !string.Equals(type.Category, "电度", StringComparison.Ordinal)
                    && !string.Equals(type.Category, "档位", StringComparison.Ordinal));

            Log(dir, $"settings-category-vocabulary: 可选分类=[{string.Join("/", TypeIdCatalog.Categories)}] "
                + $"M_ST_NA_1={TypeIdCatalog.Get(TypeIdentifier.M_ST_NA_1).Category} "
                + $"M_IT_NA_1={TypeIdCatalog.Get(TypeIdentifier.M_IT_NA_1).Category} "
                + $"M_BO_NA_1={TypeIdCatalog.Get(TypeIdentifier.M_BO_NA_1).Category} "
                + $"折算(电度→{TypeIdCatalog.NormalizeCategory("电度")}, 档位→{TypeIdCatalog.NormalizeCategory("档位")}) => "
                + (categoryVocabularyOk ? "PASS" : "FAIL"));

            // ③b-4 类型号可手填：这是 1.8.0 的新口子，光看界面看不出三种分支对不对。
            //      · 规约里现成的号（8 = M_BO_TA_1）→ 代号/格式/时标自动带出；
            //      · 0x 十六进制写法（0xC8 = 200）→ 要能解析；
            //      · 越界（300）与已占用（13 = M_IT_NA_1）→ 必须被挡在确定之前。
            //      "34" 这个号也顺带验一下：它在规约里是 M_ME_TD_1（归一化 + CP56），
            //      说明"号 → 类型"是查枚举得来的，不是按顺序猜的。
            AsduTypeEditModel numericDraft = new();
            int standardProbe = (int)TypeIdentifier.M_BO_TA_1;
            numericDraft.TypeNumberText = standardProbe.ToString();
            string standardDetail = $"{numericDraft.TypeNumber}/{numericDraft.Code}";
            bool standardFilled = numericDraft.TypeNumber == standardProbe
                && numericDraft.Code == "M_BO_TA_1"
                && numericDraft.Format == ElementFormat.Bitstring32
                && numericDraft.TimeTag == TimeTagKind.Cp24Time2a;

            numericDraft.TypeNumberText = "34";
            bool enumLookupOk = numericDraft.Code == "M_ME_TD_1"
                && numericDraft.Format == ElementFormat.Normalized
                && numericDraft.TimeTag == TimeTagKind.Cp56Time2a;

            numericDraft.TypeNumberText = "0xC8";
            string hexDetail = $"{numericDraft.TypeNumber}/{numericDraft.Code}";
            bool hexParsed = numericDraft.TypeNumber == 200
                && numericDraft.Code == "T_200"
                && numericDraft.Validate() == null
                && (int)numericDraft.ToDescriptor().Type == 200;

            numericDraft.TypeNumberText = "300";
            string rangeError = numericDraft.Validate();
            bool rangeRejected = rangeError != null;

            numericDraft.TypeNumberText = "13";
            string builtInError = numericDraft.Validate();
            bool builtInRejected = builtInError != null && builtInError.Contains("内置");

            Log(dir, $"settings-type-number: {standardProbe}→<{standardDetail}> 规约号自动带出={standardFilled} "
                + $"34→M_ME_TD_1 查表正确={enumLookupOk} "
                + $"0xC8→<{hexDetail}> 十六进制={hexParsed} "
                + $"300 被拒={rangeRejected}（{rangeError}） "
                + $"13(内置)被拒={builtInRejected}（{builtInError}） => "
                + ((standardFilled && enumLookupOk && hexParsed && rangeRejected && builtInRejected)
                    ? "PASS" : "FAIL"));

            // ③c 分类名：命令方向统一叫"遥控"（电网口径里遥控与设点都归遥控）。
            //
            //     这条断言盯的不是"字符串改成遥控了没有"，而是**改了显示名之后行为没变**：
            //     命令类类型仍然 IsCommand=true（所以点表的下拉里仍然选不到它们），
            //     大类折算仍然落到"遥控"这一栏。显示名是最容易被随手改的东西，
            //     一旦有人把逻辑判据也挂在分类名上，改个称呼就会悄悄放开"命令类能进点表"。
            List<TypeDescriptor> remoteTypes = TypeIdCatalog.AllTypes
                .Where(type => type.Code.StartsWith("C_", StringComparison.Ordinal)).ToList();

            bool remoteCategoryOk = remoteTypes.Count >= 7
                && remoteTypes.All(type => type.Category == "遥控")
                && remoteTypes.All(type => type.IsCommand)
                && remoteTypes.All(type => TypeIdCatalog.BroadCategoryOf(type.Type) == "遥控")
                && TypeIdCatalog.MonitorTypes.All(type => !type.IsCommand && !type.Code.StartsWith("C_"))
                && TypeIdCatalog.MonitorTypes.Count == TypeIdCatalog.AllTypes.Count - remoteTypes.Count;

            Log(dir, $"settings-remote-category: 命令类 {remoteTypes.Count} 项 "
                + $"分类全为遥控={remoteTypes.All(type => type.Category == "遥控")} "
                + $"IsCommand 仍为真={remoteTypes.All(type => type.IsCommand)} "
                + $"大类折算=遥控 {remoteTypes.All(type => TypeIdCatalog.BroadCategoryOf(type.Type) == "遥控")} "
                + $"点表可选 {TypeIdCatalog.MonitorTypes.Count}/{TypeIdCatalog.AllTypes.Count}（命令类仍不进点表） => "
                + (remoteCategoryOk ? "PASS" : "FAIL"));

            // 遥控这一段在表格最底下（TypeID 45..51），首屏看不见。
            // 用户要确认的正是这一列的字变成"遥控"了，所以滚过去单留一张图。
            // 选中间那一行（C_SE_NA_1）而不是第一行：表格高度装不下 7 行命令类型，
            // 选中首行会把它顶到视口最下沿，只能看见一条；选中间行，45..51 才基本都在画面里。
            AsduTypeRowViewModel remoteRow = viewModel.Settings.AsduTypesPage.Rows
                .FirstOrDefault(row => row.Code == "C_SE_NA_1");

            // 注意：这张表在 DataTemplate 里，名字不在窗口的命名域，
            // FindControl 取不到——必须从可视树上找。
            DataGrid typeGrid = contentPanel?.GetVisualDescendants().OfType<DataGrid>()
                .FirstOrDefault(grid => grid.Name == "AsduTypeGrid");

            if (remoteRow != null && typeGrid != null)
            {
                viewModel.Settings.AsduTypesPage.SelectedRow = remoteRow;
                Dispatcher.UIThread.RunJobs();

                typeGrid.ScrollIntoView(remoteRow, null);
                Dispatcher.UIThread.RunJobs();
                WarmUpLayout(mainWindow);

                Save(mainWindow, Path.Combine(dir, "24b-设置-ASDU类型-遥控.png"));

                viewModel.Settings.AsduTypesPage.SelectedRow = null;
                typeGrid.ScrollIntoView(viewModel.Settings.AsduTypesPage.Rows.FirstOrDefault(), null);
                Dispatcher.UIThread.RunJobs();
            }

            Save(mainWindow, Path.Combine(dir, "24-设置-ASDU类型.png"));

            // ② 页签：切到"报文监视"再截一张（证明左标签栏真的驱动右侧内容）
            viewModel.Settings.SelectedPage = viewModel.Settings.TrafficPage;
            Dispatcher.UIThread.RunJobs();
            WarmUpLayout(mainWindow);

            // 类型名要在"此刻"就抓下来。日志挪到后面写，那时已经切回 ASDU 类型页了，
            // 若在 Log 里现取 host.Content，打出来永远是 AsduTypesPageViewModel——
            // 结论 PASS、日志看着像 FAIL，下次排查会白查半小时。
            bool switchOk = host?.Content is TrafficPageViewModel;
            string switchedTo = host?.Content?.GetType().Name ?? "null";

            Save(mainWindow, Path.Combine(dir, "25-设置-报文监视.png"));

            // ①b "视图"页（1.9.1 改成卡片式、并排到第一页）：
            //     侧边栏 + 信号表格标签各一组卡片，卡片上只有标题和一句话。
            //     截图用来核对"没有多余的解释段落"——这一页曾经顶着一整段导语。
            viewModel.Settings.SelectedPage = viewModel.Settings.ViewPage;
            Dispatcher.UIThread.RunJobs();
            WarmUpLayout(mainWindow);
            Save(mainWindow, Path.Combine(dir, "25b-设置-视图.png"));

            // 数一下"可见的文字块里有多长的句子"：整段说明会以 > 40 字的 TextBlock 出现，
            // 卡片描述都在 25 字以内。这条比"我看了一眼觉得简洁"能落地。
            int longestOnViewPage = mainWindow.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsVisible && text.Classes.Contains("hint")
                    || text.IsVisible && text.Classes.Contains("cardDesc"))
                .Select(text => (text.Text ?? string.Empty).Length)
                .DefaultIfEmpty(0)
                .Max();

            bool viewPageOk = host?.Content is ViewPageViewModel && longestOnViewPage <= 40;
            Log(dir, $"settings-view-page-capture: 内容={host?.Content?.GetType().Name ?? "null"} "
                + $"最长说明文字={longestOnViewPage}字（≤40 才算简洁）=> {(viewPageOk ? "PASS" : "FAIL")}");

            // ①c "关于"：从主界面菜单栏挪到左栏最下面那一格。
            //     要验三件事：点了能进去、左栏那一格高亮、页面里版本/构建/更新说明真的渲染出来了。
            Button aboutNavButton = mainWindow.FindControl<Button>("AboutNavButton");
            viewModel.Settings.OpenAboutCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            WarmUpLayout(mainWindow);

            bool aboutOpened = viewModel.Settings.IsAboutPageSelected
                && host?.Content is AboutPageViewModel
                && aboutNavButton?.Classes.Contains("active") == true;

            // 信息行真的铺出来了（ItemsControl 生成的是 SelectableTextBlock）
            int infoRows = mainWindow.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Count(block => block.IsVisible && !string.IsNullOrWhiteSpace(block.Text));
            bool infoOk = infoRows >= 7;

            // 更新说明真的铺出来了，而且"当前版本"那条默认是展开的（能看到条目文字）
            string firstItemText = viewModel.Settings.AboutPage.Releases
                .FirstOrDefault(release => release.IsCurrent)?.Items.FirstOrDefault();
            bool releaseRendered = mainWindow.GetVisualDescendants().OfType<TextBlock>()
                .Any(text => text.IsVisible && text.Text == firstItemText);

            int releaseRows = viewModel.Settings.AboutPage.Releases.Count;
            bool aboutPageOk = aboutOpened && infoOk && releaseRendered && releaseRows >= 5;

            Save(mainWindow, Path.Combine(dir, "27-设置-关于.png"));

            Log(dir, $"settings-about-page: 打开={aboutOpened} 左栏高亮={aboutNavButton?.Classes.Contains("active")} "
                + $"信息行={infoRows} 更新说明={releaseRows}条 当前版条目已渲染={releaseRendered} => "
                + (aboutPageOk ? "PASS" : "FAIL"));

            // 切回普通页：左栏那一格要跟着取消高亮，否则"关于"会一直亮着
            viewModel.Settings.SelectedPage = viewModel.Settings.AsduTypesPage;
            Dispatcher.UIThread.RunJobs();
            bool aboutDeselected = !viewModel.Settings.IsAboutPageSelected
                && mainWindow.FindControl<Button>("AboutNavButton")?.Classes.Contains("active") == false;
            Log(dir, $"settings-about-deselect: 切走后取消高亮={aboutDeselected} => "
                + (aboutDeselected ? "PASS" : "FAIL"));

            Dispatcher.UIThread.RunJobs();

            Log(dir, $"settings-page-switch: 切到报文监视后内容={switchedTo} "
                + $"切回后内容={host?.Content?.GetType().Name ?? "null"} => "
                + (switchOk ? "PASS" : "FAIL"));

            // ②b 存储页 + 深色主题下的设置界面，各留一张图。
            //     存储页是"设置文件到底存在哪、为什么不用 SQLite"的唯一说明处，
            //     用户要核对文件时得能一眼找到路径；深色那张是因为设置界面新加了三种自绘样式
            //     （settingsNav / subtle / settingsGrid），这几个在浅色下对了不代表深色下也对。
            viewModel.Settings.SelectedPage = viewModel.Settings.StoragePage;
            Dispatcher.UIThread.RunJobs();
            WarmUpLayout(mainWindow);
            Save(mainWindow, Path.Combine(dir, "26-设置-存储.png"));

            string themeBefore = ThemeService.Current.Id;
            ThemeService.Apply("graphite-dark", persist: false);

            // 换主题之后要多推一轮布局再截图。DynamicResource 的重新解析落在下一帧，
            // 只 RunJobs 一次就截，图中会出现"底还是浅色、字已经变白"的中间态——
            // 那是截图抢跑，不是界面真的白底白字。
            for (int pass = 0; pass < 2; pass++)
            {
                Dispatcher.UIThread.RunJobs();
                WarmUpLayout(mainWindow);
                mainWindow.InvalidateVisual();
                Dispatcher.UIThread.RunJobs();
            }

            // Button.subtle 上挂了 140ms 的 BrushTransition（悬停时底色渐变过去，别"啪"一下闪出来）。
            // 换主题时这个过渡同样会跑：只推布局不放过时间，底色就永远停在上一套主题的那个值上，
            // 而字色（没挂过渡）已经换好了——截图出来就是"白底白字"，看着像个真 bug，其实是拍早了。
            await Task.Delay(320);
            Dispatcher.UIThread.RunJobs();
            WarmUpLayout(mainWindow);

            // 顺手把"按钮底色实际解析成了什么"也打进日志：
            // 图上看着不对时，先分清是渲染没跟上，还是令牌真的错了。
            Button darkProbe = contentPanel?.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => button.Classes.Contains("subtle"));
            string probeBg = DescribeBrush(darkProbe?.Background);
            string probeFg = DescribeBrush(darkProbe?.Foreground);

            Save(mainWindow, Path.Combine(dir, "27-设置-深色.png"));

            // 设置页在深色下也必须用主题令牌，不能有写死的浅色底。
            //
            // 这里不能拿"标签栏的底 != 内容区的底"当判据——ListBox 的 Background 本来就是
            // 透明（null），这条恒成立，等于什么都没验。真正会翻车的是选中项：
            // 它挂的是 settingsNav + :selected 两段自绘样式，一旦写成死色，
            // 深色下就是一块浅底的蓝条配白字（或者更糟：白底白字）。
            ListBoxItem navSelected = nav?.ContainerFromItem(nav.SelectedItem) as ListBoxItem;
            ListBoxItem navOther = nav?.ContainerFromIndex(1) as ListBoxItem;

            // 选中项的底色是画在模板里的 ContentPresenter 上的（ListBoxItem 自己的
            // Background 属性始终是透明），量 ListBoxItem.Background 会恒读成 Transparent
            ContentPresenter navSelPresenter = navSelected?.GetVisualDescendants()
                .OfType<ContentPresenter>().FirstOrDefault();
            ContentPresenter navOtherPresenter = navOther?.GetVisualDescendants()
                .OfType<ContentPresenter>().FirstOrDefault();

            string selBg = DescribeBrush(navSelPresenter?.Background);
            string selFg = DescribeBrush(navSelected?.GetVisualDescendants()
                .OfType<TextBlock>().FirstOrDefault()?.Foreground);
            string darkPanelBg = DescribeBrush(contentPanel?.Background);

            bool darkOk = ThemeService.Current.IsDark
                && selBg == DescribeBrush(ThemePalette.Get("PrimarySoftBrush"))
                && selFg == DescribeBrush(ThemePalette.Get("PrimarySoftTextBrush"))
                && darkPanelBg == DescribeBrush(ThemePalette.Get("SurfaceBrush"))
                && navOtherPresenter?.Background is not ISolidColorBrush { Color.A: > 0 }
                && probeBg == DescribeBrush(ThemePalette.Get("SurfaceAltBrush"))
                && probeFg == DescribeBrush(ThemePalette.Get("TextPrimaryBrush"));

            Log(dir, $"settings-dark: 主题={ThemeService.Current.Name} "
                + $"选中项底={selBg}（期望 {DescribeBrush(ThemePalette.Get("PrimarySoftBrush"))}） "
                + $"字={selFg}（期望 {DescribeBrush(ThemePalette.Get("PrimarySoftTextBrush"))}） "
                + $"内容区底={darkPanelBg} 未选中项底={DescribeBrush(navOtherPresenter?.Background)} "
                + $"按钮底={probeBg}（期望 {DescribeBrush(ThemePalette.Get("SurfaceAltBrush"))}） "
                + $"按钮字={probeFg}（期望 {DescribeBrush(ThemePalette.Get("TextPrimaryBrush"))}） => "
                + (darkOk ? "PASS" : "FAIL"));

            // 卡片式选项是 1.9.1 新加的视觉件，浅色下对不代表深色下也对：
            // 选中卡片挂的是 PrimarySoft 底 + TextPrimary 标题，未选中是 Surface 底 +
            // BorderLine 描边，这两组在深色主题里最容易翻车成"深底深字"。
            // 换回浅色之前先把视图页切出来，留一张图 + 量一次对比度。
            viewModel.Settings.SelectedPage = viewModel.Settings.ViewPage;
            Dispatcher.UIThread.RunJobs();
            WarmUpLayout(mainWindow);
            Save(mainWindow, Path.Combine(dir, "27b-设置-深色-视图.png"));

            Border darkCard = mainWindow.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(border => border.Name == "CardBorder");
            TextBlock darkCardTitle = darkCard?.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(text => text.Classes.Contains("cardTitle"));
            Color darkCardBg = (darkCard?.Background as ISolidColorBrush)?.Color ?? default;
            Color darkCardFg = (darkCardTitle?.Foreground as ISolidColorBrush)?.Color ?? default;
            double darkCardContrast = ContrastRatio(darkCardFg, darkCardBg);

            bool darkCardOk = ThemeService.Current.IsDark
                && darkCardBg == ReadThemeColor("PrimarySoftBrush")
                && darkCardFg == ReadThemeColor("TextPrimaryBrush")
                && darkCardContrast >= 4.5;

            Log(dir, $"settings-dark-card: 选中卡片底={darkCardBg}（期望 PrimarySoft） "
                + $"标题字={darkCardFg}（期望 TextPrimary） 对比度={darkCardContrast:0.0}（≥4.5） => "
                + (darkCardOk ? "PASS" : "FAIL"));

            ThemeService.Apply(themeBefore, persist: false);
            viewModel.Settings.SelectedPage = viewModel.Settings.AsduTypesPage;
            Dispatcher.UIThread.RunJobs();
            // ②c "新增自定义类型"对话框单独留一张。
            //     这是整套扩展能力的门面：类型号可手填 1..255（十进制或 0x 十六进制），
            //     规约里现成的号会自动带出代号并猜格式与时标，底部实时算字节数。
            //     光看断言看不出这些提示在不在，所以整窗拍一张。
            await CaptureAsync(dir, "28-自定义ASDU类型",
                () => new AsduTypeWindow(),
                new AsduTypeEditModel());

            WarmUpLayout(mainWindow);
            WarmUpLayout(mainWindow);

            // ───────── ④ 自定义类型：新增（含编码格式落进注册表） ─────────
            AsduTypesPageViewModel page = viewModel.Settings.AsduTypesPage;
            Action<AsduTypeEditModel, Action<bool>> originalEdit = viewModel.Settings.EditAsduTypeDialog;
            Action<string, string, Action<bool>> originalConfirm = viewModel.Settings.ConfirmDialog;

            // 选一个"规约里有、目录里还没有"的类型：M_BO_TA_1（32 位比特串 + CP24 时标）
            TypeIdentifier probeType = TypeIdentifier.M_BO_TA_1;
            const string probeName = "自检-32位比特串(CP24)";

            bool addableBefore = TypeIdCatalog.AddableTypes.Contains(probeType);
            int customBefore = TypeIdCatalog.CustomTypes.Count;
            int rowsBefore = page.Rows.Count;

            // 模拟用户填表：手填类型号（34）→ 自动带出代号/格式 → 填中文名 → 确定
            viewModel.Settings.EditAsduTypeDialog = (draft, done) =>
            {
                draft.TypeNumberText = ((int)probeType).ToString();
                draft.DisplayName = probeName;
                done(true);
            };

            page.AddCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            TypeDescriptor added = TypeIdCatalog.CustomTypes.FirstOrDefault(item => item.Type == probeType);
            bool addedOk = added != null
                && added.DisplayName == probeName
                && added.IsCustom
                && added.Format == ElementFormat.Bitstring32
                && added.TimeTag == TimeTagKind.Cp24Time2a;

            // 关键一条：编码格式必须同时登记进 Core 的注册表。
            // 只改界面不改注册表的话，从站仍会走"按单点发"的兜底分支，帧是错的。
            bool registryOk = InformationObjectRegistry.TryGet(probeType,
                    out ElementFormat format, out TimeTagKind timeTag)
                && format == ElementFormat.Bitstring32
                && timeTag == TimeTagKind.Cp24Time2a;

            // 真正编一次码，量字节数：BSI 4 + QDS 1 + CP24 3 = 8
            int encodedLength = -1;
            if (registryOk)
            {
                GenericInformationObject probe = new(1, format, timeTag)
                {
                    Value = 0x01020304,
                    Timestamp = new DateTime(2026, 10, 27, 19, 30, 15, 500)
                };
                List<byte> buffer = new();
                probe.Encode(buffer, ApplicationLayerParameters.CreateForCs104());
                encodedLength = buffer.Count;
            }

            bool lengthOk = encodedLength == 8;

            // 新增的类型必须出现在"新增信息对象"的下拉里，否则加了个摆设
            bool inDropdown = false;
            if (slave != null && slave.Points.Count > 0)
            {
                PointEditModel pointModel = new(slave.Points[0], slave);
                inDropdown = pointModel.AvailableTypes.Any(type => type.Type == probeType);
            }

            Log(dir, $"settings-add-custom: 可新增={addableBefore} 表格行 {rowsBefore}→{page.Rows.Count} "
                + $"自定义 {customBefore}→{TypeIdCatalog.CustomTypes.Count} 条目正确={addedOk} "
                + $"注册表={registryOk} 编码字节={encodedLength}(应 8) 进下拉={inDropdown} => "
                + ((addableBefore && addedOk && registryOk && lengthOk && inDropdown) ? "PASS" : "FAIL"));

            // ───────── ⑤ 落盘：再读一遍文件，类型还在 ─────────
            AppSettingsStore.Load();
            bool persisted = AppSettingsStore.Current.CustomAsduTypes
                .Any(dto => dto.TypeId == (byte)probeType && dto.DisplayName == probeName);
            AppSettingsStore.ApplyToCatalog();
            Dispatcher.UIThread.RunJobs();

            Log(dir, $"settings-persist: 设置文件={AppSettingsStore.FilePath} "
                + $"重读后含自定义类型={persisted} 重建后目录仍有={TypeIdCatalog.CustomTypes.Any(t => t.Type == probeType)} => "
                + (persisted ? "PASS" : "FAIL"));

            // ───────── ⑤a 便携 + 单一设置文件 ─────────
            // 1.8.1 把用户数据从 %APPDATA% 搬到了 exe 旁边；1.8.2 又把主题从独立的 theme.txt
            // 并进了 settings.json。这条断言同时盯这两件事，量的是"路径与文件数量"而不是"能不能读到"：
            // 文件读得到只说明它存在，不说明它在哪儿——旧的在 AppData、新的在 exe 目录时 File.Exists 一样为真。
            string exeFolder = (AppContext.BaseDirectory ?? string.Empty)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string settingsFolder = (Path.GetDirectoryName(AppSettingsStore.FilePath) ?? string.Empty)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            bool settingsByExe = string.Equals(settingsFolder, exeFolder, StringComparison.OrdinalIgnoreCase);

            // 换一次主题，验它真的落到 settings.json 里（而不是又冒出个 theme.txt）。
            // 必须真换：只查"文件里有没有 Theme 字段"，一个恒写 null 的实现也能过。
            string originalThemeId = ThemeService.Current?.Id ?? ThemeService.Options[0].Id;
            AppThemeOption otherTheme = ThemeService.Options
                .FirstOrDefault(option => option.Id != originalThemeId) ?? ThemeService.Options[0];

            ThemeService.Apply(otherTheme.Id, persist: true);
            AppSettingsStore.Load();                       // 从盘上重读，排除"只改了内存"的假象
            bool themeInSettings = string.Equals(AppSettingsStore.Current.Theme, otherTheme.Id,
                StringComparison.Ordinal);

            ThemeService.Apply(originalThemeId, persist: true);   // 还原用户原来的选择

            bool noThemeFile = !File.Exists(Path.Combine(settingsFolder, "theme.txt"))
                && !File.Exists(Path.Combine(exeFolder, "theme.txt"));

            bool portableOk = settingsByExe && PortableFolder.IsPortable
                && File.Exists(AppSettingsStore.FilePath)
                && themeInSettings && noThemeFile;

            Log(dir, $"settings-portable: 设置目录={settingsFolder} exe 目录={exeFolder} "
                + $"同目录={settingsByExe} 便携模式={PortableFolder.IsPortable} "
                + $"主题写入 settings={themeInSettings}（试填 {otherTheme.Id}） "
                + $"无独立 theme.txt={noThemeFile} 说明=\"{PortableFolder.Note}\" => "
                + (portableOk ? "PASS" : "FAIL"));

            // ───────── ⑤b 任意类型号（200）走一遍完整链路 ─────────
            // 1.8.0 放开"类型号可手填 1..255"之后，最有价值的验证不是"输入框能打字"，
            // 而是**规约里根本没有这个号时，帧还发得出去、还被编成了登记的格式**。
            // 这条链路经过：草稿 → 目录 → Core 注册表 → ASDU 编码（首字节=200）。
            const int customNumber = 200;
            const string customCode = "T_200";
            const string customName = "自检-项目私有类型";

            int rowsBeforePrivate = page.Rows.Count;
            viewModel.Settings.EditAsduTypeDialog = (draft, done) =>
            {
                draft.TypeNumberText = customNumber.ToString();
                draft.Code = customCode;
                draft.DisplayName = customName;
                // 分类选"遥测"，格式按"主站厂家私有类型"的常见约定显式选成 短浮点 + 不带时标。
                // 必须显式选：草稿打开时那三项是按"建议类型号"推的，不指定就等于在测猜得准不准，
                // 而不是在测"用户选什么就发什么"。
                draft.CategoryIndex = TypeIdCatalog.Categories.ToList().IndexOf("遥测");
                draft.FormatIndex = draft.FormatOptions.ToList()
                    .FindIndex(text => text.Contains("短浮点"));
                draft.TimeTagIndex = draft.TimeTagOptions.ToList()
                    .FindIndex(text => text.Contains("不带时标"));
                done(true);
            };

            page.AddCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            TypeDescriptor privateType = TypeIdCatalog.CustomTypes
                .FirstOrDefault(item => (int)item.Type == customNumber);
            bool privateAdded = privateType != null
                && privateType.Code == customCode
                && privateType.IsCustom
                && privateType.Category == "遥测";

            bool privateRegistered = InformationObjectRegistry.TryGet((TypeIdentifier)customNumber,
                out ElementFormat privateFormat, out TimeTagKind privateTag);

            // 编一条真实 ASDU：首字节必须是 200，载荷长度必须与登记的格式一致
            byte[] privateFrame = Array.Empty<byte>();
            bool privateEncoded = false;
            int privatePayload = -1;
            if (privateRegistered)
            {
                GenericInformationObject privateObject =
                    new GenericInformationObject(5001, privateFormat, privateTag)
                    {
                        Value = 12.5,
                        Timestamp = new DateTime(2026, 10, 28, 9, 30, 0)
                    };
                privatePayload = privateObject.EncodedLength;

                Asdu privateAsdu = new Asdu(ApplicationLayerParameters.CreateForCs104())
                {
                    TypeId = (TypeIdentifier)customNumber,
                    Cot = CauseOfTransmission.Spontaneous,
                    CommonAddress = 1
                };
                privateAsdu.AddInformationObject(privateObject);
                privateFrame = privateAsdu.Encode();
                privateEncoded = privateFrame.Length > 0 && privateFrame[0] == customNumber;
            }

            bool privateOk = privateAdded && privateRegistered && privateEncoded
                && privateFormat == ElementFormat.ShortFloat
                && privateTag == TimeTagKind.None
                // R32 4 字节 + QDS 1 字节 = 5；整帧 = 6 字节头 + 3 字节点号 + 5 = 14
                && privatePayload == 5
                && privateFrame.Length == 14;
            Log(dir, $"settings-custom-number: 类型号={customNumber} 代号={privateType?.Code} "
                + $"分类={privateType?.Category} 已进目录={privateAdded} 已进注册表={privateRegistered} "
                + $"登记格式={privateFormat}/{privateTag} 载荷={privatePayload}字节 "
                + $"帧首字节={((privateFrame.Length > 0) ? privateFrame[0] : -1)}(应 {customNumber}) "
                + $"帧长={privateFrame.Length} => {(privateOk ? "PASS" : "FAIL")}");

            // 收尾：删掉这条私有类型，再删掉 ④ 的探针类型必须在它之前完成清理，
            // 否则 ⑦ 里比对的"表格行数回到原值"会多出一行。
            // 确认框在这里先换成"永远同意"的桩：视图层接的是真对话框，
            // 自检里点不到它，RemoveCommand 会停在等待确认上（现象就是"删不掉"）。
            viewModel.Settings.ConfirmDialog = (title, message, done) => done(true);
            page.SelectedRow = page.Rows.FirstOrDefault(row => (int)row.Type == customNumber);
            page.RemoveCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            bool privateRemoved = !TypeIdCatalog.CustomTypes.Any(item => (int)item.Type == customNumber);
            Log(dir, $"settings-custom-number-cleanup: 已移除={privateRemoved} "
                + $"表格行={page.Rows.Count}(期望 {rowsBeforePrivate}) => "
                + ((privateRemoved && page.Rows.Count == rowsBeforePrivate) ? "PASS" : "FAIL"));

            // ───────── ⑥ 被点位占用时不能删 ─────────
            string confirmTitle = string.Empty;
            viewModel.Settings.ConfirmDialog = (title, message, done) =>
            {
                confirmTitle = title;
                done(true);
            };

            PointViewModel probePoint = slave?.AddPoint(16777215, probeType, "自检-自定义类型点", 0);
            Dispatcher.UIThread.RunJobs();

            page.SelectedRow = page.Rows.FirstOrDefault(row => row.Type == probeType);
            page.RemoveCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            bool blocked = confirmTitle == "无法删除"
                && TypeIdCatalog.CustomTypes.Any(item => item.Type == probeType);

            Log(dir, $"settings-remove-guard: 提示标题=\"{confirmTitle}\" 仍存在={TypeIdCatalog.CustomTypes.Any(t => t.Type == probeType)} => "
                + (blocked ? "PASS" : "FAIL"));

            // ───────── ⑦ 清掉探针点之后可以删，且文件里不留痕 ─────────
            if (probePoint != null)
            {
                slave.RemovePoint(probePoint);
                Dispatcher.UIThread.RunJobs();
            }

            confirmTitle = string.Empty;
            page.SelectedRow = page.Rows.FirstOrDefault(row => row.Type == probeType);
            page.RemoveCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            AppSettingsStore.Load();
            bool removed = !TypeIdCatalog.CustomTypes.Any(item => item.Type == probeType);
            bool fileClean = !AppSettingsStore.Current.CustomAsduTypes.Any(dto => dto.TypeId == (byte)probeType);

            Log(dir, $"settings-remove-custom: 目录已移除={removed} 文件无残留={fileClean} "
                + $"确认标题=\"{confirmTitle}\" 表格行={page.Rows.Count}(原 {rowsBefore}) => "
                + ((removed && fileClean && page.Rows.Count == rowsBefore) ? "PASS" : "FAIL"));

            // 恢复视图层注入的真实回调，别把自检用的桩留在运行中的实例上
            viewModel.Settings.EditAsduTypeDialog = originalEdit;
            viewModel.Settings.ConfirmDialog = originalConfirm;

            // ───────── ⑧ 退出设置：工作区原样回来 ─────────
            viewModel.CloseSettingsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            WarmUpLayout(mainWindow);

            bool closedOk = !viewModel.IsSettingsOpen
                && settingsView?.IsVisible == false
                && sidebarGrid?.IsVisible == true
                && toolbar?.IsVisible == true;
            Log(dir, $"settings-close: 回工作区={!viewModel.IsSettingsOpen} "
                + $"设置隐藏={settingsView?.IsVisible == false} 工作区可见={sidebarGrid?.IsVisible} "
                + $"工具条可见={toolbar?.IsVisible} => {(closedOk ? "PASS" : "FAIL")}");

            await Task.Delay(60);
        }

        /// <summary>
        /// 自检：点表单元格的内联编辑与滚轮调整。
        ///
        /// 用一个临时从站跑，不碰用户工作区。逐条验的是"行为"而不是"字段"：
        /// 滚轮转一圈值要真的按类型轮换，回车要真的落到协议层（SlaveStation），
        /// 撞号要真的被拒——这些都不是看一眼界面能确认的。
        /// </summary>
        private static void RunPointEditSelfCheck(string dir)
        {
            SlaveViewModel slave = new() { Name = "内联编辑自检", CommonAddress = 99 };
            PointViewModel sp = slave.AddPoint(1, TypeIdentifier.M_SP_NA_1, "单点", 0);
            PointViewModel dp = slave.AddPoint(2, TypeIdentifier.M_DP_NA_1, "双点", 0);
            PointViewModel me = slave.AddPoint(3, TypeIdentifier.M_ME_NC_1, "遥测", 0);

            // 单点：滚轮在 0/1 之间轮换
            sp.NudgeValue(1);
            bool spOn = sp.Value == 1;
            sp.NudgeValue(1);
            bool spOff = sp.Value == 0;

            // 双点：四态轮换 0 中间态 → 1 分 → 2 合 → 3 不确定 → 回到 0
            double[] dpCycle = new double[5];
            for (int i = 0; i < dpCycle.Length; i++)
            {
                dp.NudgeValue(1);
                dpCycle[i] = dp.Value;
            }
            bool dpOk = dpCycle.SequenceEqual(new double[] { 1, 2, 3, 0, 1 });

            // 遥测：普通滚轮步长 1，Shift 加速 ×10
            me.Value = 5;
            me.NudgeValue(1);
            bool meStep = Math.Abs(me.Value - 6) < 1e-9;
            me.NudgeValue(1, coarse: true);
            bool meCoarse = Math.Abs(me.Value - 16) < 1e-9;

            // 名称内联编辑
            dp.BeginEdit(PointField.Name);
            dp.EditText = "改名后的双点";
            bool nameCommitted = dp.CommitEdit();
            bool nameOk = nameCommitted && dp.Name == "改名后的双点" && !dp.IsNameEditing;

            // 值内联编辑：中文"合"要能解析（双点=2），而不是只认数字
            dp.BeginEdit(PointField.Value);
            dp.EditText = "合";
            bool valueCommitted = dp.CommitEdit();
            bool valueOk = valueCommitted && Math.Abs(dp.Value - 2) < 1e-9 && !dp.IsValueEditing;

            // 点号撞号：必须被拒，且原号不变
            dp.BeginEdit(PointField.Address);
            dp.EditText = "1";
            bool dupRejected = !dp.CommitEdit();
            bool addressUnchanged = dp.ObjectAddress == 2;

            // 点号改成空闲号：要成功，并且协议层能按新号查到
            dp.BeginEdit(PointField.Address);
            dp.EditText = "22";
            bool movedOk = dp.CommitEdit();
            bool movedFound = dp.ObjectAddress == 22 && slave.FindPoint(22) != null;

            // 品质：滚轮在 8 种组合里轮换，反向一格回到正常
            sp.NudgeQuality(1);
            bool qualityIv = sp.IsInvalid && sp.QualityText.Contains("IV");
            sp.NudgeQuality(-1);
            bool qualityNormal = !sp.IsInvalid && !sp.IsBlocked && !sp.IsSubstituted
                && sp.QualityText == "正常";

            bool pass = spOn && spOff && dpOk && meStep && meCoarse && nameOk && valueOk
                && dupRejected && addressUnchanged && movedOk && movedFound
                && qualityIv && qualityNormal;

            Log(dir, $"point-edit: 单点轮换={spOn && spOff} "
                + $"双点四态={string.Join(">", dpCycle)} "
                + $"遥测步进={meStep} Shift加速={meCoarse} "
                + $"名称内联={nameOk} 中文值输入={valueOk} "
                + $"撞号被拒={dupRejected && addressUnchanged} 改号成功={movedOk && movedFound} "
                + $"品质轮换={qualityIv && qualityNormal} => {(pass ? "PASS" : "FAIL")}");
        }

        /// <summary>
        /// 截一张"侧栏收起"的主窗口，并验证第八轮把开关挪到状态栏之后的三件事：
        /// ① 收起后侧栏真的不见、列宽归零；
        /// ② 状态栏那颗开关**很小**（用户嫌之前 24px 的竖条太大太占地方）；
        /// ③ 收起状态下按钮图标翻转成 »、提示语变成"展开侧栏"，
        ///    否则用户收起后看到还写着"收起侧栏"，根本不知道去哪还原。
        /// </summary>
        private static async Task CaptureSidebarCollapsedAsync(string dir, Window mainWindow)
        {
            if (mainWindow is not MainWindow shell)
            {
                return;
            }

            Button toggle = mainWindow.FindControl<Button>("SidebarToggleButton");
            Grid shellGrid = mainWindow.FindControl<Grid>("SidebarGrid");
            DataGrid pointGrid = mainWindow.FindControl<DataGrid>("PointDataGrid");
            Border panel = mainWindow.FindControl<Border>("SidebarPanel");

            if (toggle == null || shellGrid == null || pointGrid == null || panel == null)
            {
                Log(dir, "sidebar-collapse: FAIL（找不到侧栏开关 / 主体网格 / 点表）");
                return;
            }

            // 走真实路径：RaiseEvent 触发 Click，和用户鼠标点下去完全同一条代码
            string stateBefore = toggle.Tag as string ?? string.Empty;
            toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(350);

            bool collapsed = shell.IsSidebarCollapsed;
            bool panelHidden = !panel.IsVisible;
            // 图标翻转与否读 Tag（见 MainWindow.SetSidebarCollapsed 的说明）：
            // Geometry.ToString() 不是可比较的值，两个不同路径打出来是一样的类型名
            string stateCollapsed = toggle.Tag as string ?? string.Empty;
            bool iconFlipped = stateBefore == "collapse" && stateCollapsed == "expand";
            string tip = ToolTip.GetTip(toggle) as string ?? string.Empty;
            bool tipFlipped = tip.Contains("展开");

            // 关键：不能再有"让出来的车道"。收起后点表的左边缘必须和主体网格左边缘重合，
            // 也就是信号区真的吃满了整行宽度，一像素都不留给按钮。
            double gridLeft = 0;
            Point? gridPos = pointGrid.TranslatePoint(new Point(0, 0), shellGrid);
            if (gridPos.HasValue)
            {
                gridLeft = gridPos.Value.X;
            }
            // 留 1px：点表是包在 Border.panel 里的，那个面板有 1px 描边，
            // 所以点表左边缘本来就在 1 个布局单位处（125% 缩放下量出来是 0.8）。
            // 关键是不能再多出"让给按钮的车道"。
            bool fullWidth = gridLeft <= 1.05;

            // 开关本身必须小：宽 <= 24、高 <= 22（状态栏一行就这么高）
            bool compact = toggle.Bounds.Width <= 24.5 && toggle.Bounds.Height <= 22.5;

            // 必须落在状态栏里（Y 坐标在主体区域下方），不能又跑回内容区
            Window root = mainWindow;
            Point? togglePos = toggle.TranslatePoint(new Point(0, 0), root);
            bool inStatusBar = togglePos.HasValue && togglePos.Value.Y >= shellGrid.Bounds.Height
                && shellGrid.TranslatePoint(new Point(0, 0), root) is { } bodyPos
                && togglePos.Value.Y >= bodyPos.Y + shellGrid.Bounds.Height - 0.5;

            Save(mainWindow, Path.Combine(dir, "01b-主窗口-侧栏收起.png"));

            Log(dir, $"sidebar-collapse: 列宽归零={collapsed} 侧栏隐藏={panelHidden} "
                + $"图标翻转={iconFlipped} 提示语翻转={tipFlipped} => "
                + ((collapsed && panelHidden && iconFlipped && tipFlipped) ? "PASS" : "FAIL"));

            Log(dir, $"sidebar-geometry: 信号区吃满宽度(左边距={gridLeft:0.#}px)={fullWidth} "
                + $"开关尺寸={toggle.Bounds.Width:0.#}x{toggle.Bounds.Height:0.#} 够小={compact} "
                + $"在状态栏内={inStatusBar} => "
                + ((fullWidth && compact && inStatusBar) ? "PASS" : "FAIL"));

            toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(250);

            string stateRestored = toggle.Tag as string ?? string.Empty;
            bool restored = !shell.IsSidebarCollapsed && panel.IsVisible && stateRestored == stateBefore;
            Log(dir, $"sidebar-expand: 列宽恢复={!shell.IsSidebarCollapsed} 面板回来={panel.IsVisible} "
                + $"图标复位={stateRestored == stateBefore}({stateRestored}) => "
                + (restored ? "PASS" : "FAIL"));
        }


        /// <summary>
        /// 自检：标题栏那颗"钉子"是否真的能钉住 / 拔掉。
        ///
        /// 直接对钉子按钮 RaiseEvent(Click)，走的是真实的 OnPinClick，
        /// 不是绕过界面调 SetPinned——否则把点击处理写错也能过。
        /// 同时校验按钮的视觉状态跟着变（钉住时高亮），
        /// 因为用户是靠颜色判断"现在是不是钉着的"。
        /// </summary>
        private static async Task RunTrafficWindowSelfCheckAsync(string dir, MainWindowViewModel viewModel)
        {
            if (viewModel == null)
            {
                Log(dir, "traffic-pin: 跳过（无主视图模型）");
                return;
            }

            TrafficWindow window = new() { DataContext = new TrafficViewModel(viewModel.Records) };
            Button pin = window.FindControl<Button>("PinButton");
            if (pin == null)
            {
                Log(dir, "traffic-pin: FAIL（找不到钉子按钮）");
                return;
            }

            bool initiallyUnpinned = !window.IsPinned && !window.IsPinHighlighted;

            pin.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(30);
            bool pinned = window.IsPinned && window.IsPinHighlighted;

            pin.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(30);
            bool unpinned = !window.IsPinned && !window.IsPinHighlighted;

            Log(dir, $"traffic-pin: 初始未钉住={initiallyUnpinned} 点击后置顶={pinned} "
                + $"再点取消={unpinned} => "
                + ((initiallyUnpinned && pinned && unpinned) ? "PASS" : "FAIL"));

            window.SetPinned(false);
            // 窗口没 Show 过就不 Close：Avalonia 对未显示的窗口调 Close 没有意义，
            // 自检进程马上就退出了，留着不管更省事
        }

        /// <summary>
        /// 自检：报文监视窗口必须是非模态的——开着它的时候主窗口还能操作。
        ///
        /// 判定手段：Avalonia 用 ShowDialog 弹模态窗口时会把 owner 置为 IsEnabled=false。
        /// 所以"监视窗口开着 &amp;&amp; 主窗口 IsEnabled 仍为 true"就等价于非模态。
        /// 这一条正是用户提的"不能阻止对主界面的操作"。
        /// </summary>
        private static async Task RunTrafficNonModalSelfCheckAsync(string dir, Window mainWindow)
        {
            if (mainWindow is not MainWindow shell)
            {
                return;
            }

            shell.OpenTrafficWindow();
            await Task.Delay(400);

            bool opened = shell.IsTrafficWindowOpen;
            bool mainStillEnabled = mainWindow.IsEnabled;

            // 重复点按钮不应该开出第二个窗口
            shell.OpenTrafficWindow();
            await Task.Delay(200);
            bool stillOpen = shell.IsTrafficWindowOpen;

            shell.CloseTrafficWindow();
            await Task.Delay(200);
            bool closedOk = !shell.IsTrafficWindowOpen;

            Log(dir, $"traffic-nonmodal: 窗口打开={opened} 主界面仍可操作={mainStillEnabled} "
                + $"重复打开仍单实例={stillOpen} 关闭成功={closedOk} => "
                + ((opened && mainStillEnabled && stillOpen && closedOk) ? "PASS" : "FAIL"));
        }

        /// <summary>
        /// 自检：通道状态灯三态。
        ///
        /// 用真实 TCP 连接来验证，而不是直接改字段——状态灯的意义就在于
        /// "主站真的连上来了没有"，绕过 socket 测等于没测。
        /// 流程：停止(灰) → 启动(橙监听中) → 主站连上(绿) → 断开(退回橙) → 停止(灰)。
        /// </summary>
        private static async Task RunChannelStateSelfCheckAsync(string dir, MainWindowViewModel viewModel)
        {
            if (viewModel == null)
            {
                Log(dir, "channel-state-selfcheck: 跳过（无主视图模型）");
                return;
            }

            const int ProbePort = 2419;

            ConnectionViewModel channel = new()
            {
                Name = "状态灯自检",
                BindAddress = "127.0.0.1",
                Port = ProbePort
            };
            viewModel.Connections.Add(channel);

            bool stoppedOk = channel.State == ConnectionViewModel.ChannelState.Stopped
                && SameBrush(channel.StateBrush, ThemePalette.Get("TextDisabledBrush"));

            try
            {
                channel.Open();
                await Task.Delay(250);
                bool listeningOk = channel.State == ConnectionViewModel.ChannelState.Listening
                    && SameBrush(channel.StateBrush, ThemePalette.Get("WarningBrush"));

                using System.Net.Sockets.TcpClient client = new();
                await client.ConnectAsync(System.Net.IPAddress.Loopback, ProbePort);
                await Task.Delay(500);
                bool connectedOk = channel.State == ConnectionViewModel.ChannelState.Connected
                    && channel.ClientCount == 1
                    && SameBrush(channel.StateBrush, ThemePalette.Get("SuccessBrush"));

                client.Close();
                await Task.Delay(800);
                bool backOk = channel.State == ConnectionViewModel.ChannelState.Listening
                    && SameBrush(channel.StateBrush, ThemePalette.Get("WarningBrush"));

                channel.Close();
                await Task.Delay(150);
                bool closeOk = channel.State == ConnectionViewModel.ChannelState.Stopped;

                bool pass = stoppedOk && listeningOk && connectedOk && backOk && closeOk;
                Log(dir, $"channel-state: 停止灰={stoppedOk} 监听橙={listeningOk} " +
                         $"主站接入绿={connectedOk} 断开退回橙={backOk} 停止复位={closeOk} " +
                         $"状态文本=\"{channel.StateText}\" => {(pass ? "PASS" : "FAIL")}");
            }
            catch (Exception exception)
            {
                Log(dir, $"channel-state: 异常 {exception.GetType().Name}: {exception.Message} => FAIL");
            }
            finally
            {
                try { channel.Close(); } catch { /* 已关闭 */ }
                viewModel.Connections.Remove(channel);
                channel.Dispose();
            }
        }

        /// <summary>
        /// 自检（1.10 工程化）：工具条保存按钮三态、工程存读往返、初始值语义、欢迎页两态。
        ///
        /// 为什么排在最后跑：它会真的建一个工程、改结构、最后再关闭工程（清空通道/从站），
        /// 前面那些依赖演示点表的断言不能被它打乱。
        /// </summary>
        private static async Task RunProjectSelfCheckAsync(string dir, Window mainWindow, MainWindowViewModel viewModel)
        {
            string workDir = Path.Combine(dir, "project");
            System.IO.Directory.CreateDirectory(workDir);
            string projectPath = Path.Combine(workDir, "自检工程.iec104proj");

            try
            {
                // ── ① 保存按钮：必须独占工具条最左 ──
                Button saveButton = mainWindow.FindControl<Button>("ToolbarSaveButton");
                Border toolbar = mainWindow.GetVisualDescendants()
                    .OfType<Border>()
                    .FirstOrDefault(border => border.Classes.Contains("toolbar"));
                StackPanel strip = toolbar?.Child as StackPanel;
                bool leftmost = strip != null && strip.Children.Count > 0
                    && ReferenceEquals(strip.Children[0], saveButton);

                MenuGlyph saveGlyph = saveButton?.GetVisualDescendants()
                    .OfType<MenuGlyph>()
                    .FirstOrDefault();

                bool sizeOk = saveButton != null
                    && Math.Abs(saveButton.Bounds.Width - 28) <= 0.6
                    && Math.Abs(saveButton.Bounds.Height - 28) <= 0.6
                    && saveGlyph != null
                    && Math.Abs(saveGlyph.Bounds.Width - 16) <= 0.6;

                Log(dir, $"toolbar-save-button: 最左={leftmost} "
                    + $"按钮={saveButton?.Bounds.Width:0.#}×{saveButton?.Bounds.Height:0.#} "
                    + $"图标={saveGlyph?.Bounds.Width:0.#}（期望 28×28 / 图标 16） => "
                    + (leftmost && sizeOk ? "PASS" : "FAIL"));

                // ── ①b 状态栏那颗保存指示器：形状 + 颜色 + 胶囊底 ──
                //     它不是一段裸文字，三态各有自己的形状（圆点 / 对勾 / 双箭头），
                //     色弱也能分辨；所以不能只量文字，得把形状和底也一起量。
                Border saveBadge = mainWindow.FindControl<Border>("SaveStateIndicator");
                List<MenuGlyph> badgeGlyphs = saveBadge?
                    .GetVisualDescendants()
                    .OfType<MenuGlyph>()
                    .ToList() ?? new List<MenuGlyph>();
                MenuGlyph checkGlyph = badgeGlyphs.FirstOrDefault();
                MenuGlyph syncGlyph = badgeGlyphs.Skip(1).FirstOrDefault();
                Shapes.Ellipse badgeDot = saveBadge?
                    .GetVisualDescendants()
                    .OfType<Shapes.Ellipse>()
                    .FirstOrDefault(ellipse => ellipse.Classes.Contains("saveStateDot"));
                TextBlock badgeText = saveBadge?
                    .GetVisualDescendants()
                    .OfType<TextBlock>()
                    .FirstOrDefault(text => text.Classes.Contains("saveStateText"));

                Color badgeBackground() =>
                    (saveBadge?.Background as ISolidColorBrush)?.Color ?? Colors.Transparent;
                Color badgeForeground() =>
                    (badgeText?.Foreground as ISolidColorBrush)?.Color ?? Colors.Transparent;

                bool hiddenWhenClean = saveBadge != null && !saveBadge.IsEffectivelyVisible;

                // 落一个工程出来，进入"已保存"态
                viewModel.SaveProjectTo(projectPath, "自检工程");
                Dispatcher.UIThread.RunJobs();

                bool idleBadge = saveBadge?.IsEffectivelyVisible == true
                    && checkGlyph?.IsEffectivelyVisible == true
                    && badgeDot?.IsEffectivelyVisible != true
                    && syncGlyph?.IsEffectivelyVisible != true
                    && badgeBackground().A == 0
                    && badgeForeground() == ReadThemeColor("TextSecondaryBrush")
                    && !string.IsNullOrEmpty(badgeText?.Text);

                Log(dir, $"statusbar-save-idle: 无工程时隐藏={hiddenWhenClean} "
                    + $"已保存可见={saveBadge?.IsEffectivelyVisible} 对勾={checkGlyph?.IsEffectivelyVisible} "
                    + $"圆点={badgeDot?.IsEffectivelyVisible} 双箭头={syncGlyph?.IsEffectivelyVisible} "
                    + $"底=透明({badgeBackground().A == 0}) 字=\"{badgeText?.Text}\" 字色={badgeForeground()} => "
                    + (hiddenWhenClean && idleBadge ? "PASS" : "FAIL"));

                // 改结构 → 未保存态：主色胶囊 + 实心圆点
                Color wantSoft = ReadThemeColor("PrimarySoftBrush");
                Color wantSoftText = ReadThemeColor("PrimarySoftTextBrush");
                // ── ② 主色必须跟着主题走：六套主题两两不同，且与工具条底色对比度 ≥ 3.0 ──
                //    非文字元素按 WCAG 的图形标准 3:1，不是文字的 4.5:1
                string restoreId = ThemeService.Current?.Id;
                List<string> primarySet = new List<string>();
                double worstPrimary = double.MaxValue;
                foreach (AppThemeOption option in ThemeService.Options)
                {
                    ThemeService.Apply(option.Id, persist: false);
                    Dispatcher.UIThread.RunJobs();

                    Color primary = ReadThemeColor("PrimaryBrush");
                    if (!primarySet.Contains(primary.ToString()))
                    {
                        primarySet.Add(primary.ToString());
                    }

                    double ratio = ContrastRatio(primary, ReadThemeColor("SurfaceAltBrush"));
                    if (ratio < worstPrimary)
                    {
                        worstPrimary = ratio;
                    }
                }

                if (!string.IsNullOrEmpty(restoreId))
                {
                    ThemeService.Apply(restoreId, persist: false);
                    Dispatcher.UIThread.RunJobs();
                }

                bool themeOk = primarySet.Count == ThemeService.Options.Count && worstPrimary >= 3.0;
                Log(dir, $"toolbar-save-theme-color: 不同主色={primarySet.Count}/{ThemeService.Options.Count} "
                    + $"最差对比度={worstPrimary:0.00}:1（≥3.0） => " + (themeOk ? "PASS" : "FAIL"));

                // ── ③ 三态：无工程=TextDisabled / 有改动=主色 / 保存后回到 TextDisabled ──
                Color wantIdle = ReadThemeColor("TextDisabledBrush");
                Color wantDirty = ReadThemeColor("PrimaryBrush");

                Color idleInk = (saveGlyph?.Stroke as ISolidColorBrush)?.Color ?? Colors.Transparent;
                bool idleOk = idleInk == wantIdle && !viewModel.CanSaveProject;

                // 工程文件在 ①b 已经落过盘（那里要验"已保存"态，本来就得先存一次），
                // 这里只确认它真的在磁盘上、且路径正确
                bool fileOk = System.IO.File.Exists(projectPath)
                    && viewModel.HasProject
                    && System.IO.Path.GetFileName(viewModel.CurrentProjectPath) == "自检工程.iec104proj";

                // 改结构 → 按钮亮主色
                viewModel.MarkStructureDirty();
                Dispatcher.UIThread.RunJobs();
                Color dirtyInk = (saveGlyph?.Stroke as ISolidColorBrush)?.Color ?? Colors.Transparent;
                bool dirtyOk = viewModel.IsProjectDirty && viewModel.CanSaveProject && dirtyInk == wantDirty;

                // 未保存态的指示器：浅主色胶囊底 + 实心圆点 + 主色半粗字。
                // 先取快照再往下走：后面的 SaveProject 会把状态改回"已保存"，
                // 如果等到打日志时再去读控件，日志里写的全是存完后的样子
                // （第一版就是这样，看着像断言坏了，其实是日志在说谎）。
                bool dirtyDotOn = badgeDot?.IsEffectivelyVisible == true;
                bool dirtyCheckOn = checkGlyph?.IsEffectivelyVisible == true;
                bool dirtySyncOn = syncGlyph?.IsEffectivelyVisible == true;
                Color dirtyBackground = badgeBackground();
                Color dirtyForeground = badgeForeground();
                FontWeight dirtyWeight = badgeText?.FontWeight ?? FontWeight.Normal;
                string dirtyCaption = badgeText?.Text;

                bool dirtyBadge = saveBadge?.IsEffectivelyVisible == true
                    && dirtyDotOn
                    && !dirtyCheckOn
                    && !dirtySyncOn
                    && dirtyBackground == wantSoft
                    && dirtyForeground == wantSoftText
                    && dirtyWeight == FontWeight.SemiBold
                    && dirtyCaption == "未保存";

                // 左边那行"工程已保存：xxx"是过去时，右边这颗说的是现在。
                // 存完再改一个点，两句话并排就成了"工程已保存 / 未保存"——自己打自己。
                bool noStaleSave = viewModel.StatusText != null
                    && !viewModel.StatusText.StartsWith("工程已保存", StringComparison.Ordinal);
                Log(dir, $"statusbar-no-stale-save: 保存后又改结构，左邻文字=\"{viewModel.StatusText}\""
                    + "（不能还挂着「工程已保存：…」） => " + (noStaleSave ? "PASS" : "FAIL"));

                // 留一张"未保存"态的实图：这三态是给人看的，光靠断言看不出胶囊底抢不抢眼。
                // Save 走离屏渲染，不会改动窗口状态，存完照常往下走。
                Save(mainWindow, Path.Combine(dir, "43-状态栏-未保存.png"));

                bool saved = viewModel.SaveProject();
                Dispatcher.UIThread.RunJobs();
                Color afterInk = (saveGlyph?.Stroke as ISolidColorBrush)?.Color ?? Colors.Transparent;
                bool idleBack = saved && !viewModel.IsProjectDirty && afterInk == wantIdle;

                // 存完，指示器回到"对勾 + 无底"
                bool badgeBack = checkGlyph?.IsEffectivelyVisible == true
                    && badgeDot?.IsEffectivelyVisible != true
                    && badgeBackground().A == 0;

                Log(dir, $"toolbar-save-states: 无工程={idleOk}({idleInk}) 落盘={fileOk} "
                    + $"改动={dirtyOk}({dirtyInk} 期望 {wantDirty}) 存后={idleBack}({afterInk}) => "
                    + (idleOk && fileOk && dirtyOk && idleBack ? "PASS" : "FAIL"));

                Log(dir, $"statusbar-save-dirty: 圆点={dirtyDotOn} 对勾={dirtyCheckOn} 双箭头={dirtySyncOn} "
                    + $"胶囊底={dirtyBackground}(期望 {wantSoft}) 字色={dirtyForeground}(期望 {wantSoftText}) "
                    + $"半粗={dirtyWeight} 字=\"{dirtyCaption}\" 存后回到对勾={badgeBack} => "
                    + (dirtyBadge && badgeBack ? "PASS" : "FAIL"));

                // ── ③c 值脏不点名（决策 C）：只改了信号值，指示器必须一动不动 ──
                //     40 万点跑起来时值每秒变几十次，让这格跟着闪"未保存"就没人看它了。
                //     值只在悬停提示里说一句"停手 N 秒后自动写入"。
                viewModel.MarkValueDirty();
                Dispatcher.UIThread.RunJobs();

                bool valueDotOff = badgeDot?.IsEffectivelyVisible != true;
                bool valueCheckOn = checkGlyph?.IsEffectivelyVisible == true;
                string valueCaption = badgeText?.Text;
                string valueStatus = viewModel.StatusText;
                bool valueTip = (viewModel.SaveStateTip ?? string.Empty).Contains("自动写入");

                bool valueSilent = viewModel.IsValueDirty
                    && !viewModel.IsProjectDirty
                    && valueDotOff
                    && valueCheckOn
                    && valueCaption != null
                    && valueCaption.StartsWith("已保存", StringComparison.Ordinal)
                    && valueStatus != null
                    && valueStatus.StartsWith("工程已保存", StringComparison.Ordinal)
                    && valueTip;

                Log(dir, $"statusbar-value-dirty-silent: 值脏={viewModel.IsValueDirty} "
                    + $"圆点={!valueDotOff} 对勾={valueCheckOn} 字=\"{valueCaption}\" "
                    + $"左邻文字=\"{valueStatus}\" 提示含「自动写入」={valueTip} => "
                    + (valueSilent ? "PASS" : "FAIL"));

                viewModel.ClearDirtyState();
                Dispatcher.UIThread.RunJobs();

                // ── ④ 初始值语义：SetValue 不许污染 InitValue；复位回到初始值 ──
                SlaveViewModel slave = viewModel.Connections
                    .SelectMany(connection => connection.Slaves)
                    .FirstOrDefault(candidate => candidate.Points.Count > 0);
                PointViewModel point = slave?.Points.FirstOrDefault();

                bool initKept = false;
                bool resetOk = false;
                bool persisted = false;

                if (point != null)
                {
                    point.InitValue = 42;
                    point.Value = 7.5;
                    initKept = point.InitValue.Equals(42.0) && point.Value.Equals(7.5);

                    // 存一次，让"初始值 + 最后值"两个字段都落盘
                    viewModel.SaveProject();
                    string json = System.IO.File.ReadAllText(projectPath);
                    persisted = json.Contains(ProjectStore.FormatTag)
                        && json.Contains("\"InitValue\": 42")
                        && json.Contains("\"Value\": 7.5");

                    // 复位：选中该从站、不选点 → 整站复位
                    viewModel.SelectedSlave = slave;
                    viewModel.SelectedPoint = null;
                    int reset = viewModel.ResetPointsToInitValue();
                    resetOk = reset == slave.Points.Count
                        && slave.Points.All(item => item.Value.Equals(item.InitValue));
                }

                Log(dir, $"project-init-value: 改值不动初始值={initKept} "
                    + $"双字段落盘={persisted} 整站复位={resetOk}({slave?.Points.Count} 点) => "
                    + (initKept && persisted && resetOk ? "PASS" : "FAIL"));

                // ── ④b 老格式 .iec104ws：能直接打开，存一次静默升级标识、文件不改名 ──
                //    现场手里一堆 1.9.x 存的"工作区"，迁移口径必须是"打开就能用"：
                //    不弹转换对话框、也不另存成新文件（用户会找不到自己的东西）。
                //    做法是把刚存的新格式文件里的标识改回旧值，当成老文件喂进去。
                viewModel.SaveProject();
                string modernJson = System.IO.File.ReadAllText(projectPath);
                string legacyPath = System.IO.Path.Combine(workDir, "旧工作区.iec104ws");
                System.IO.File.WriteAllText(
                    legacyPath,
                    modernJson.Replace(ProjectStore.FormatTag, ProjectStore.LegacyFormatTag,
                        StringComparison.Ordinal));

                int legacyPoints = viewModel.OpenProject(legacyPath);
                bool legacyOpened = legacyPoints > 0
                    && viewModel.HasProject
                    && string.Equals(viewModel.CurrentProjectPath, legacyPath,
                        StringComparison.OrdinalIgnoreCase);

                // 存一次：文件内容升级成新标识，文件名保持 .iec104ws，路径不变（没走另存为）
                viewModel.SaveProject();
                string upgradedJson = System.IO.File.ReadAllText(legacyPath);
                bool tagUpgraded = upgradedJson.Contains(ProjectStore.FormatTag, StringComparison.Ordinal)
                    && !upgradedJson.Contains(ProjectStore.LegacyFormatTag, StringComparison.Ordinal);
                bool nameKept = System.IO.File.Exists(legacyPath)
                    && string.Equals(viewModel.CurrentProjectPath, legacyPath,
                        StringComparison.OrdinalIgnoreCase);

                Log(dir, $"project-legacy-open: 打开 {legacyPoints:N0} 点={legacyOpened} "
                    + $"标识升级={tagUpgraded} 文件名与路径不变={nameKept} => "
                    + (legacyOpened && tagUpgraded && nameKept ? "PASS" : "FAIL"));

                // ── ⑤ 欢迎页两态（工程已恢复 / 没有工程） ──
                bool stateBTitle = viewModel.WelcomeTitle == "当前工程";
                viewModel.IsWelcomeVisible = true;
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(350);
                Save(mainWindow, Path.Combine(dir, "41-欢迎页-已恢复工程.png"));

                bool recentOk = viewModel.HasRecentProjects
                    && viewModel.RecentProjects.Any(item => item.Path == projectPath);
                bool editorHidden = !viewModel.IsEditorVisible;

                Log(dir, $"welcome-state-b: 标题=\"{viewModel.WelcomeTitle}\" 编辑区隐藏={editorHidden} "
                    + $"最近={viewModel.RecentProjects.Count} 当前工程在列表里={recentOk} => "
                    + (stateBTitle && editorHidden && recentOk ? "PASS" : "FAIL"));

                // 退出路径 1：继续编辑
                viewModel.DismissWelcomeCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                bool dismissed = !viewModel.IsWelcomeVisible;

                // 关闭工程 → 回到 State A
                viewModel.CloseProject();
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(350);
                Save(mainWindow, Path.Combine(dir, "42-欢迎页-没有工程.png"));

                bool stateA = !viewModel.HasProject
                    && viewModel.IsWelcomeVisible
                    && viewModel.WelcomeTitle == "还没有打开工程";

                Log(dir, $"welcome-state-a: 继续编辑可收起={dismissed} 关工程后标题=\"{viewModel.WelcomeTitle}\" "
                    + $"有工程={viewModel.HasProject} 欢迎页可见={viewModel.IsWelcomeVisible} => "
                    + (dismissed && stateA ? "PASS" : "FAIL"));

                // ── ⑥ 未命名工程：没有工程文件时改结构，也必须算脏、也能存 ──
                //    这条防的是"欢迎页上直接建从站，改完发现存不了"——按钮灰着、
                //    状态栏不提示、关窗口也不问，改动会静悄悄丢掉。
                viewModel.IsWelcomeVisible = true;
                Dispatcher.UIThread.RunJobs();
                viewModel.MarkStructureDirty();
                Dispatcher.UIThread.RunJobs();

                bool unnamed = !viewModel.HasProject
                    && viewModel.IsProjectDirty
                    && viewModel.CanSaveProject
                    && viewModel.ShowSaveState
                    && !viewModel.IsWelcomeVisible
                    && viewModel.WindowTitle.StartsWith("●")
                    && (saveGlyph?.Stroke as ISolidColorBrush)?.Color == wantDirty;

                Log(dir, $"project-unnamed: 无文件也算脏={viewModel.IsProjectDirty} 可保存={viewModel.CanSaveProject} "
                    + $"状态栏提示={viewModel.ShowSaveState} 欢迎页收起={!viewModel.IsWelcomeVisible} "
                    + $"标题=\"{viewModel.WindowTitle}\" 按钮亮主色={((saveGlyph?.Stroke as ISolidColorBrush)?.Color == wantDirty)} => "
                    + (unnamed ? "PASS" : "FAIL"));

                // ── ⑦ 无工程时点「保存」必须转成另存为（1.10.2 修的 bug）──
                //    之前工具条和菜单绑的是 SaveProjectCommand → SaveProject()，
                //    而它在 !HasProject 时静默 return false：点保存毫无反应；
                //    偏偏 Ctrl+S 走的另一条路会弹框。同一个动作两种结果，
                //    用户的原话是"点击保存按钮，不能弹出提示框去保存成工程"。
                //    这里注入一个假的另存为回调，只验"命令有没有走到它"——
                //    自检里不能真弹文件对话框（没人点）。
                int saveAsAsked = 0;
                Func<Task<bool>> originalFallback = viewModel.SaveAsFallback;
                viewModel.SaveAsFallback = () =>
                {
                    saveAsAsked++;
                    return Task.FromResult(false);   // 模拟用户在对话框点了"取消"
                };

                viewModel.SaveProjectCommand.Execute(null);
                await Task.Delay(120);               // 命令体是 async void，给它一点时间跑到回调

                viewModel.SaveAsFallback = originalFallback;
                bool saveAsOk = saveAsAsked == 1;

                Log(dir, $"project-save-unnamed: 无工程点保存→另存为回调命中={saveAsAsked} 次 => "
                    + (saveAsOk ? "PASS" : "FAIL"));

                // 收尾：把"未命名工程 + 脏"的状态清掉，别把现场留给后续/退出流程
                viewModel.CloseProject();
                Dispatcher.UIThread.RunJobs();
            }
            catch (Exception exception)
            {
                Log(dir, $"project-self-check: 异常 {exception.GetType().Name}: {exception.Message} => FAIL");
            }
        }

        /// <summary>
        /// 自检（1.10.1）：自动保存的防抖、闸门、开关边界。
        ///
        /// 为什么这些断言不能靠看截图：防抖是"时间上的行为"，
        /// 界面上完全看不出来——文件到底写没写、写了几次，只能读 mtime 和计数器。
        ///
        /// 延迟不能真按 5 秒等（一轮会白等十几秒），所以把 DebounceOverrideMs 压到 300 ms；
        /// 这也是延迟值从设置读、而不是写死在代码里的原因之一。
        /// </summary>
        private static async Task RunAutoSaveSelfCheckAsync(
            string dir, Window mainWindow, MainWindowViewModel viewModel)
        {
            string workDir = Path.Combine(dir, "autosave");
            System.IO.Directory.CreateDirectory(workDir);
            string projectPath = Path.Combine(workDir, "自动保存自检.iec104proj");

            bool originalAutoSave = AppSettingsStore.Current.AutoSaveEnabled;
            int originalValueDelay = AppSettingsStore.Current.ValueSaveDelaySeconds;

            try
            {
                // 起一份带示例信号的工程（24 个点），立刻落一次盘当基线
                viewModel.CreateProject(projectPath, "自动保存自检", withSamplePoints: true);
                Dispatcher.UIThread.RunJobs();

                viewModel.DebounceOverrideMs = 300;
                viewModel.MinGapOverrideMs = 0;

                SlaveViewModel slave = viewModel.Connections
                    .SelectMany(connection => connection.Slaves)
                    .FirstOrDefault(candidate => candidate.Points.Count > 0);

                // 挑一个测量值：双点/单点这类开关量的取值域只有 0/1，
                // 拿来验"最后值落盘"容易和取整撞上
                PointViewModel point = slave?.Points
                    .FirstOrDefault(candidate => candidate.TypeCode != null
                        && candidate.TypeCode.Contains("M_ME", StringComparison.Ordinal))
                    ?? slave?.Points.FirstOrDefault();

                if (point == null)
                {
                    Log(dir, "autosave-self-check: 没有可用的信号 => FAIL");
                    return;
                }

                DateTime before = System.IO.File.GetLastWriteTime(projectPath);
                int writesBefore = viewModel.ValueSaver.WriteCount;

                // ── ① 变化过程中零写盘：20 次改值铺满 2 秒，延迟 300 ms 永远等不到"停手" ──
                for (int index = 0; index < 20; index++)
                {
                    point.Value = 20 + index * 0.5;
                    await Task.Delay(100);
                }

                bool quietWhileChanging = System.IO.File.GetLastWriteTime(projectPath) == before
                    && viewModel.ValueSaver.WriteCount == writesBefore;

                Log(dir, $"project-value-no-write-while-changing: 2 秒内改值 20 次 "
                    + $"mtime 未变={System.IO.File.GetLastWriteTime(projectPath) == before} "
                    + $"写盘次数={viewModel.ValueSaver.WriteCount}（期望 {writesBefore}） => "
                    + (quietWhileChanging ? "PASS" : "FAIL"));

                // ── ② 停手之后写一次，而且写的是最后那个值 ──
                await Task.Delay(800);

                string expected = point.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                string json = System.IO.File.ReadAllText(projectPath);
                bool wroteOnIdle = viewModel.ValueSaver.WriteCount == writesBefore + 1
                    && !viewModel.IsValueDirty
                    && System.IO.File.GetLastWriteTime(projectPath) > before;
                bool lastValuePersisted = json.Contains("\"Value\": " + expected, StringComparison.Ordinal);

                Log(dir, $"project-value-write-after-idle: 写盘次数 {writesBefore}→{viewModel.ValueSaver.WriteCount} "
                    + $"值脏已清={!viewModel.IsValueDirty} 最后值={expected} 已落盘={lastValuePersisted} => "
                    + (wroteOnIdle && lastValuePersisted ? "PASS" : "FAIL"));

                // ── ③ 最小间隔闸门：刚写完盘，即使防抖到期也要先憋住 ──
                //    防的是"连续微调十几个值，每次停手就写 200 ms"的手感问题。
                //    这里先手动存一次把"上次写盘"的时刻钉死在现在：
                //    否则上一段那次自动保存落在 800 ms 窗口里的哪一刻是浮动的，
                //    闸门到底该挡不该挡就说不清了（第一版就是这么假红的）。
                viewModel.SaveProject();
                Dispatcher.UIThread.RunJobs();

                int gateBase = viewModel.ValueSaver.WriteCount;
                viewModel.MinGapOverrideMs = 1200;
                point.Value = 31.5;

                await Task.Delay(500);   // > 防抖 300，< 闸门 1200
                bool heldByGate = viewModel.ValueSaver.WriteCount == gateBase;

                await Task.Delay(900);   // 距上次写盘 1400ms > 闸门
                bool releasedAfterGate = viewModel.ValueSaver.WriteCount == gateBase + 1;

                Log(dir, $"project-min-save-interval: 闸门 1200ms 时 500ms 处写盘={!heldByGate} "
                    + $"1400ms 处写盘={releasedAfterGate} 次数={viewModel.ValueSaver.WriteCount} => "
                    + (heldByGate && releasedAfterGate ? "PASS" : "FAIL"));

                // ── ④ 开关边界：关着时结构脏不写盘，打开后才写 ──
                //    这条还顺带验"If 已经脏了再改一次，计时器要重新算"——
                //    MarkStructureDirty 里的 Bump 必须在 early-return 之前，否则永远等不到这一次。
                viewModel.MinGapOverrideMs = 0;
                AppSettingsStore.Current.AutoSaveEnabled = false;

                DateTime offBefore = System.IO.File.GetLastWriteTime(projectPath);
                viewModel.MarkStructureDirty();
                await Task.Delay(700);

                bool offKeepsDirty = viewModel.IsProjectDirty
                    && System.IO.File.GetLastWriteTime(projectPath) == offBefore;

                AppSettingsStore.Current.AutoSaveEnabled = true;
                viewModel.MarkStructureDirty();
                await Task.Delay(700);

                bool onWrites = !viewModel.IsProjectDirty
                    && System.IO.File.GetLastWriteTime(projectPath) > offBefore;

                Log(dir, $"project-auto-save-switch: 关闭时仍脏={offKeepsDirty} 打开后自动落盘={onWrites} => "
                    + (offKeepsDirty && onWrites ? "PASS" : "FAIL"));

                // ── ⑤ 设置页「工程」：页签在、改完落盘、可选档位齐全 ──
                ProjectPageViewModel page = viewModel.Settings.ProjectPage;
                List<int> options = page.DelayOptions.Select(option => option.Seconds).ToList();
                bool optionSet = options.SequenceEqual(new[] { 3, 5, 10, 30 });

                page.SelectedValueSaveDelay = page.DelayOptions.First(option => option.Seconds == 10);
                bool delayInMemory = AppSettingsStore.Current.ValueSaveDelaySeconds == 10;
                string settingsJson = System.IO.File.ReadAllText(AppSettingsStore.FilePath);
                bool delayOnDisk = settingsJson.Contains("\"ValueSaveDelaySeconds\": 10", StringComparison.Ordinal);

                // 顺手留一张设置页的图（这一页只有真切过去才渲染得出来）
                viewModel.ToggleSettingsCommand.Execute(null);
                viewModel.Settings.SelectedPage = page;
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(350);
                Save(mainWindow, Path.Combine(dir, "44-设置-工程.png"));
                viewModel.ToggleSettingsCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();

                bool pageOk = page != null
                    && page.Title == "工程"
                    && viewModel.Settings.Pages.Contains(page)
                    && optionSet
                    && delayInMemory
                    && delayOnDisk;

                Log(dir, $"project-page-options: 页签=工程/在列表里={viewModel.Settings.Pages.Contains(page)} "
                    + $"档位=[{string.Join(" / ", options)}] 内存={AppSettingsStore.Current.ValueSaveDelaySeconds}秒 "
                    + $"落盘={delayOnDisk} => " + (pageOk ? "PASS" : "FAIL"));
            }
            catch (Exception exception)
            {
                Log(dir, $"autosave-self-check: 异常 {exception.GetType().Name}: {exception.Message} => FAIL");
            }
            finally
            {
                // 恢复现场：override 归零、开关与档位回到自检前的值，别把污染留给下一次运行
                viewModel.DebounceOverrideMs = null;
                viewModel.MinGapOverrideMs = null;
                AppSettingsStore.Current.AutoSaveEnabled = originalAutoSave;
                AppSettingsStore.Current.ValueSaveDelaySeconds = originalValueDelay;
                AppSettingsStore.Save();

                viewModel.ClearDirtyState();
                viewModel.CloseProject();
                Dispatcher.UIThread.RunJobs();
            }
        }

        /// <summary>
        /// 比较两个画刷的实际颜色是否一致。
        ///
        /// 早期这里写的是"颜色字符串后缀匹配某个硬编码 hex"，主题化之后当场就挂了：
        /// 状态灯的颜色现在来自主题令牌（Success / Warning / TextDisabled），
        /// 写死 hex 等于把断言和某一套配色绑死，换主题必红。改成和令牌比。
        /// </summary>
        private static bool SameBrush(IBrush actual, IBrush expected) =>
            actual is ISolidColorBrush a && expected is ISolidColorBrush e && a.Color == e.Color;

        private static async Task CaptureAsync(string dir, string name,
            Func<Window> factory, object dataContext)
        {
            Window window = factory();

            // 顺手统计窗口图标：用户反馈"很多弹窗里还是 Windows 原始图标"，
            // 这类问题不会报错、截图也不明显，只能逐个窗口问一句 Icon 有没有值。
            CapturedWindows++;
            if (window.Icon != null)
            {
                CapturedWindowsWithIcon++;
            }
            else
            {
                Log(dir, $"win-icon-missing: {window.GetType().Name}");
            }

            if (dataContext != null)
            {
                window.DataContext = dataContext;
            }
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Show();
            await Task.Delay(700);
            Save(window, Path.Combine(dir, name + ".png"));
            window.Close();
            await Task.Delay(200);
        }

        /// <summary>自检过程中真正实例化过的窗口数（见 CaptureAsync）。</summary>
        private static int CapturedWindows;

        /// <summary>其中 Icon 非空的个数。</summary>
        private static int CapturedWindowsWithIcon;

        /// <summary>
        /// 自检（第十一轮）：外观类四个问题。
        ///
        /// ① 弹窗图标：13 个非主窗口全都带 Icon，任务栏/标题栏不再显示 Windows 默认图标。
        /// ② 菜单图标：描边够粗、颜色走主题令牌、与文字基线居中对齐。
        /// ③ 关于窗口：背景是主题渐变（不再是 bk.png 位图）、文案收敛、功能条目齐全。
        /// ④ 标题栏：logo / 菜单 / 主题胶囊 / 右侧按钮的垂直中心都落在标题栏中线上。
        /// </summary>
        private static void RunAppearanceSelfCheck(string dir, Window mainWindow)
        {
            // ── ① 窗口图标 ──
            Log(dir, $"win-icons: {CapturedWindowsWithIcon}/{CapturedWindows} 个窗口带图标 => "
                + (CapturedWindows > 0 && CapturedWindowsWithIcon == CapturedWindows ? "PASS" : "FAIL"));

            // 代码里 new 出来的提示框不在 CaptureAsync 的范围内，单独验一次资源能加载
            Log(dir, $"win-icons-code: 代码提示框图标={(AppIcons.Main != null ? "有" : "无")} => "
                + (AppIcons.Main != null ? "PASS" : "FAIL"));

            // ── ② 菜单图标：粗细、配色、尺寸 ──
            Menu titleMenu = mainWindow.FindControl<Menu>("TitleMenu");
            MenuItem topItem = titleMenu?.Items.OfType<MenuItem>().FirstOrDefault();
            MenuGlyph topIcon = topItem?.GetVisualDescendants()
                .OfType<MenuGlyph>()
                .FirstOrDefault(glyph => glyph.Classes.Contains("menuIconTop"));
            ContentControl topIconHost = topItem?.GetVisualDescendants()
                .OfType<ContentControl>()
                .FirstOrDefault(presenter => presenter.Name == "PART_IconPresenter");

            double topThickness = topIcon?.StrokeThickness ?? 0;
            double hostSize = topIconHost?.Bounds.Width ?? 0;
            // 第十七轮：顶级菜单图标不再用主色，改成和标题栏右侧那排（调色板/GitHub/云下载/齿轮）
            // 同一个墨色。判据因此从"等于 PrimaryBrush"改成"等于 TextSecondaryBrush"，
            // 并且下面还要和右侧图标实际渲染出来的颜色逐颗比对 —— 光比令牌不算统一，
            // 万一右边哪天换了令牌，这条断言就该炸。
            Color expectedTop = ReadThemeColor("TextSecondaryBrush");
            bool topColorOk = topIcon?.Stroke is ISolidColorBrush topBrush
                && topBrush.Color == expectedTop;

            Log(dir, $"menu-icon-weight: 顶级图标描边={topThickness:0.##} 框={hostSize:0.#} "
                + $"颜色=次要色{topColorOk} => "
                + ((topThickness >= 2.0 && hostSize >= 15 && topColorOk) ? "PASS" : "FAIL"));

            // 顶级菜单（左边七颗）与标题栏右侧那排（调色板/GitHub/云下载/齿轮）必须同墨。
            // 取右侧"云下载"那颗 Path 的实际描边色做基准：它是线框图标里最普通的一颗，
            // 齿轮的描边特意加重过（2.4），拿它当基准会被那条例外带偏。
            Button updateButton = mainWindow.FindControl<Button>("UpdateButton");
            Avalonia.Controls.Shapes.Path sideIcon = updateButton?.GetVisualDescendants()
                .OfType<Avalonia.Controls.Shapes.Path>()
                .FirstOrDefault();
            Color? sideInk = (sideIcon?.Stroke as ISolidColorBrush)?.Color;
            bool inkMatched = sideInk.HasValue && topColorOk && sideInk.Value == expectedTop;
            Log(dir, $"menu-icon-ink-match: 菜单图标墨色={(topIcon?.Stroke as ISolidColorBrush)?.Color} "
                + $"标题栏图标墨色={sideInk} 同一色={inkMatched} => "
                + (inkMatched ? "PASS" : "FAIL"));

            // 下拉项图标：颜色必须取 MenuIconBrush（次要色压向主色那一档），
            // 直接拿 TextSecondary 画细线在浅底上会发灰、缩放到位图像素后更糊
            ContextMenu pointMenu = mainWindow.FindControl<DataGrid>("PointDataGrid")?.ContextMenu;
            DataGrid grid = mainWindow.FindControl<DataGrid>("PointDataGrid");
            if (pointMenu != null && grid != null && !pointMenu.IsOpen)
            {
                pointMenu.Open(grid);
                Dispatcher.UIThread.RunJobs();
            }

            MenuItem ctxItem = pointMenu?.ContainerFromIndex(0) as MenuItem;
            MenuGlyph ctxIcon = ctxItem?.GetVisualDescendants()
                .OfType<MenuGlyph>()
                .FirstOrDefault(glyph => glyph.Classes.Contains("menuIcon"));
            Color expectedMenuIcon = ReadThemeColor("MenuIconBrush");
            double ctxThickness = ctxIcon?.StrokeThickness ?? 0;
            bool ctxColorOk = ctxIcon?.Stroke is ISolidColorBrush ctxBrush
                && ctxBrush.Color == expectedMenuIcon;
            Log(dir, $"menu-icon-color: 下拉项描边={ctxThickness:0.##} "
                + $"颜色=MenuIconBrush{ctxColorOk} "
                + $"({(ctxIcon?.Stroke as ISolidColorBrush)?.Color} vs {expectedMenuIcon}) => "
                + ((ctxThickness >= 2.0 && ctxColorOk) ? "PASS" : "FAIL"));
            pointMenu?.Close();
            Dispatcher.UIThread.RunJobs();

            // ── ③ 关于（1.9.1 起是设置里的一页，不再是独立窗口）──
            //
            // 这里只量"数据对不对"（版本、构建信息、更新说明），
            // 界面渲染相关的检查放在设置那一段（那时设置界面才是打开的）。
            MainWindowViewModel shellModel = mainWindow.DataContext as MainWindowViewModel;
            AboutPageViewModel aboutPage = shellModel?.Settings.AboutPage;

            if (aboutPage == null)
            {
                Log(dir, "about-page: 拿不到关于页视图模型 => FAIL");
            }
            else
            {
                bool versionRow = aboutPage.Rows.Any(row =>
                    row.Label == "版本" && row.Value.StartsWith(AppInfo.Version, StringComparison.Ordinal));
                bool hasBuild = aboutPage.Rows.Any(row => row.Label == "构建 ID" && row.Value.Length > 8);
                bool hasOs = aboutPage.Rows.Any(row =>
                    row.Label == "操作系统" && !string.IsNullOrWhiteSpace(row.Value));

                // 更新说明：至少要有当前版本那一条，而且它默认展开
                ReleaseNoteViewModel current = aboutPage.Releases
                    .FirstOrDefault(release => release.IsCurrent);
                bool releaseOk = aboutPage.Releases.Count >= 5
                    && current != null
                    && current.IsExpanded
                    && current.Note.Items.Count > 0
                    && aboutPage.Releases.Any(release => !release.IsCurrent && !release.IsExpanded);

                Log(dir, $"about-page: 版本行={versionRow}({AppInfo.VersionWithArch}) 构建ID={hasBuild} "
                    + $"操作系统={hasOs} 更新说明={aboutPage.Releases.Count}条/当前版展开={releaseOk} => "
                    + ((versionRow && hasBuild && hasOs && releaseOk) ? "PASS" : "FAIL"));
            }

            // 软件名统一：窗口标题、状态栏品牌、关于页产品名都必须是 IEC104-Server
            TextBlock brand = mainWindow.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(text => text.Classes.Contains("statusBrand"));
            bool brandOk = mainWindow.Title == "IEC104-Server"
                && (brand?.Text ?? string.Empty) == "IEC104-Server"
                && (aboutPage?.ProductName ?? string.Empty) == "IEC104-Server";
            Log(dir, $"app-name: 窗口标题={mainWindow.Title} 状态栏={brand?.Text} "
                + $"关于页={aboutPage?.ProductName} => " + (brandOk ? "PASS" : "FAIL"));

            // 主界面不再有"帮助"菜单：它整项搬进了设置（用户要求）
            bool noHelpMenu = !mainWindow.GetVisualDescendants().OfType<MenuItem>()
                .Any(item => (item.Header as string)?.StartsWith("帮助", StringComparison.Ordinal) == true);
            Log(dir, $"menu-no-help: 主菜单里已无「帮助」={noHelpMenu} => "
                + (noHelpMenu ? "PASS" : "FAIL"));

            // ── ④ 标题栏垂直居中 ──
            // 量的是"控件框中心"与"标题栏中线"的差，不是像素墨迹：
            // 图标几何本身可能不占满画布（比如机架图标上下留白），
            // 用墨迹去判断会把"图形留白"误判成"排版没居中"。
            Border titleBar = mainWindow.FindControl<Border>("TitleBar");
            if (titleBar == null)
            {
                Log(dir, "titlebar-center: 找不到标题栏 => FAIL");
                return;
            }

            double barHeight = titleBar.Bounds.Height;
            double barCenter = barHeight / 2;
            var probes = new (string Name, Control Control)[]
            {
                ("logo", mainWindow.FindControl<Control>("TitleLogo")),
                ("菜单", titleMenu),
                ("主题胶囊", mainWindow.FindControl<Control>("ThemeButton")),
                ("GitHub", mainWindow.FindControl<Control>("GitHubButton")),
                ("更新", mainWindow.FindControl<Control>("UpdateButton")),
                ("最大化", mainWindow.FindControl<Control>("MaximizeButton"))
            };

            double worst = 0;
            var detail = new System.Text.StringBuilder();
            foreach ((string name, Control control) in probes)
            {
                if (control == null)
                {
                    continue;
                }

                Point? origin = control.TranslatePoint(new Point(0, 0), titleBar);
                if (origin == null)
                {
                    continue;
                }

                double center = origin.Value.Y + control.Bounds.Height / 2;
                double delta = center - barCenter;
                worst = Math.Max(worst, Math.Abs(delta));
                detail.Append($"{name}={delta:+0.#;-0.#;0} ");
            }

            Log(dir, $"titlebar-center: 栏高={barHeight:0.#} 中线={barCenter:0.#} 偏差 {detail}=> "
                + (worst <= 0.75 ? "PASS" : "FAIL"));
        }

        /// <summary>
        /// 自检（第十二轮）：报文监视的批量刷新链路。
        ///
        /// 为什么专门测它：性能改造把"每条 Insert(0)"换成了"缓冲 → 150 ms 合并 → 一次 Reset"，
        /// 主窗口和监视窗口是靠这个 Reset 联动。Reset 在监视窗口那边必须解释成
        /// "整表重建"，一旦被当成"清空"，窗口会一直空着——而且**截图看不出来**，
        /// 因为截图那一刻可能刚好有一条新报文。这类问题只能靠断言兜。
        /// </summary>
        private static void RunThroughputSelfCheck(string dir, MainWindowViewModel viewModel)
        {
            // ── ① 监视窗口视图：首铺 / 增量追加 / 上限 ──
            RangeObservableCollection<AsduRecord> source = new();

            List<AsduRecord> first = new(100);
            for (int index = 0; index < 100; index++)
            {
                first.Add(MakeRecord(index));
            }
            source.AddRange(first);                       // 一次 Reset（模拟主窗口第一次合并）

            TrafficViewModel view = new(source);
            bool seedOk = view.Records.Count == 100
                && ReferenceEquals(view.Records[0], first[^1]);   // 最新的在最上面

            // 第二次合并：整体替换 + Reset
            List<AsduRecord> second = new(40);
            for (int index = 100; index < 140; index++)
            {
                second.Add(MakeRecord(index));
            }
            source.AddRange(second);

            bool appendOk = view.Records.Count == 140
                && ReferenceEquals(view.Records[0], second[^1]);

            // 单条 Add（非批量路径）也要能进视图，且排在最前
            AsduRecord single = MakeRecord(140);
            source.Add(single);
            bool singleOk = view.Records.Count == 141 && ReferenceEquals(view.Records[0], single);

            // 上限：源集合再多也不能把监视窗口撑爆
            List<AsduRecord> flood = new(4000);
            for (int index = 0; index < 4000; index++)
            {
                flood.Add(MakeRecord(index));
            }
            source.AddRange(flood);
            bool capOk = view.Records.Count == 3000;

            Log(dir, $"traffic-batch: 首铺={seedOk}(100) 批量追加={appendOk}(140) "
                + $"单条追加={singleOk}(141) 上限截断={capOk}({view.Records.Count}) => "
                + ((seedOk && appendOk && singleOk && capOk) ? "PASS" : "FAIL"));

            // ── ③ 选中项要顶得住刷新（用户报的"点了报文，下面还是空的"）──
            AsduRecord picked = view.Records[3];
            view.SelectedRecord = picked;
            string hexBefore = view.SelectedHex;

            // 再推一批（走 Reset）。旧实现在这里会把选中项清成 null，
            // 表现就是点完报文一二百毫秒后下面又变回"（未选中报文）"。
            source.AddRange(new[] { MakeRecord(5000), MakeRecord(5001) });

            bool selectionKept = ReferenceEquals(view.SelectedRecord, picked);
            bool hexKept = view.SelectedHex == hexBefore && hexBefore.Length > 0;
            bool selectionIsBytes = view.SelectedHex.StartsWith("68", StringComparison.Ordinal);

            Log(dir, $"traffic-selection: 选中={picked.ObjectSummary} "
                + $"刷新后仍选中={selectionKept} 字节未变={hexKept} 内容是字节={selectionIsBytes} => "
                + ((selectionKept && hexKept && selectionIsBytes) ? "PASS" : "FAIL"));

            // ── ④ 报文要带值（用户："不要只有类型，那些数据"）──
            ApplicationLayerParameters parameters = ApplicationLayerParameters.CreateForCs104();

            Asdu switchAsdu = new Asdu(parameters)
            {
                TypeId = TypeIdentifier.M_SP_NA_1,
                Cot = CauseOfTransmission.Spontaneous,
                CommonAddress = 1
            };
            switchAsdu.AddInformationObject(new SinglePointInformation(1001, true));
            AsduRecord switchRecord = AsduRecord.FromAsdu(switchAsdu, "发", switchAsdu.Encode());

            Asdu measuredAsdu = new Asdu(parameters)
            {
                TypeId = TypeIdentifier.M_ME_NC_1,
                Cot = CauseOfTransmission.Spontaneous,
                CommonAddress = 1
            };
            measuredAsdu.AddInformationObject(new MeasuredValueShort(4001) { Value = 42.5f });
            AsduRecord measuredRecord = AsduRecord.FromAsdu(measuredAsdu, "发", measuredAsdu.Encode());

            bool switchValueOk = switchRecord.ValueText == "合 (1)";
            bool measuredValueOk = measuredRecord.ValueText == "42.5";
            // 原始字节要真的有：M_ME_NC_1 = 13 = 0x0D，旧实现这里恒为空串
            bool recordHexOk = measuredRecord.Hex.StartsWith("0D", StringComparison.Ordinal);

            Log(dir, $"traffic-value: 遥信值={switchRecord.ValueText} 遥测值={measuredRecord.ValueText} "
                + $"原始字节头={measuredRecord.Hex.Split(' ')[0]} => "
                + ((switchValueOk && measuredValueOk && recordHexOk) ? "PASS" : "FAIL"));

            view.Detach();

            // ── ② 主窗口的缓冲合并：入缓冲不该影响集合，Flush 之后才上屏 ──
            int before = viewModel.Records.Count;
            for (int index = 0; index < 2000; index++)
            {
                viewModel.AppendRecord(MakeRecord(index));
            }

            bool bufferedOk = viewModel.Records.Count == before;   // 还没刷，集合不该动
            viewModel.FlushRecords();
            bool flushedOk = viewModel.Records.Count == Math.Min(3000, before + 2000);
            Log(dir, $"record-buffer: 入缓冲 2,000 条后集合未变={bufferedOk} "
                + $"Flush 后={viewModel.Records.Count}(期望 {Math.Min(3000, before + 2000)}) => "
                + ((bufferedOk && flushedOk) ? "PASS" : "FAIL"));

            // 清干净，别把假报文留在后面的截图和真跑用例里
            viewModel.ClearRecordsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>
        /// 自检（第十二轮补丁）：最大化时标题栏不能被窗口外扩挤出屏幕。
        ///
        /// 这条是给"看着比较靠上"兜底的。原来的 <c>titlebar-center</c> 断言量的是
        /// 控件中心对 **标题栏** 中线的偏差——它一直是全绿的，但用户看到的仍然偏上，
        /// 因为出问题的不是"内容在栏里没居中"，而是"标题栏本身被窗口外扩挤出了屏幕"：
        /// 最大化时窗口比工作区大一圈，多出来的那圈落在顶部，标题栏顶部一截就看不见了，
        /// 内容却还按完整的栏高居中 → 落在可见条的 39% 而不是 50%。
        ///
        /// 教训：**量对了数值，量错了参照系，一样会得出"没问题"的结论。**
        /// 所以这条断言量的是"屏幕坐标"这个真正的参照系：
        /// 标题栏顶边必须正好压在屏幕工作区的顶边上，状态栏底边正好压在工作区底边，
        /// 并且内容在这条真正看得见的标题栏里居中。
        /// </summary>
        private static void RunMaximizedSelfCheck(string dir, MainWindow window)
        {
            if (window == null)
            {
                Log(dir, "titlebar-maximized: 跳过（无主窗口）");
                return;
            }

            WindowState restoreState = window.WindowState;
            Thickness restoreMargin = window.RootLayout.Margin;

            window.WindowState = WindowState.Maximized;
            Dispatcher.UIThread.RunJobs();
            // WindowState 刚变的那一刻 ClientSize / Position 还是旧值，显式再算一次
            window.ApplyMaximizedInset();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            double scale = window.RenderScaling <= 0 ? 1 : window.RenderScaling;
            Avalonia.Platform.Screen screen =
                window.Screens?.ScreenFromWindow(window) ?? window.Screens?.Primary;
            if (screen == null)
            {
                Log(dir, "titlebar-maximized: 跳过（拿不到屏幕信息）");
                window.WindowState = restoreState;
                window.RootLayout.Margin = restoreMargin;
                return;
            }

            PixelRect work = screen.WorkingArea;
            PixelPoint position = window.Position;

            double ScreenY(Control control)
            {
                if (control == null)
                {
                    return double.NaN;
                }

                Point? point = control.TranslatePoint(new Point(0, 0), window);
                return point == null ? double.NaN : position.Y + point.Value.Y * scale;
            }

            double ScreenBottom(Control control)
            {
                if (control == null)
                {
                    return double.NaN;
                }

                Point? point = control.TranslatePoint(new Point(0, 0), window);
                return point == null ? double.NaN : position.Y + (point.Value.Y + control.Bounds.Height) * scale;
            }

            double barTopOnScreen = ScreenY(window.TitleBar);
            double statusBottomOnScreen = ScreenBottom(window.StatusBar);

            // 内容在这条"可见的标题栏"里必须居中
            double barHeight = window.TitleBar?.Bounds.Height ?? 0;
            double worst = 0;
            Control[] probes =
            {
                window.FindControl<Control>("TitleLogo"),
                window.TitleMenu,
                window.ThemeButton,
                window.MaximizeButton
            };
            foreach (Control control in probes)
            {
                if (control == null)
                {
                    continue;
                }

                Point? point = control.TranslatePoint(new Point(0, 0), window.TitleBar);
                if (point == null)
                {
                    continue;
                }

                worst = Math.Max(worst, Math.Abs(point.Value.Y + control.Bounds.Height / 2 - barHeight / 2));
            }

            Thickness margin = window.RootLayout.Margin;
            bool topOk = Math.Abs(barTopOnScreen - work.Y) <= 1.0;
            bool bottomOk = Math.Abs(statusBottomOnScreen - work.Bottom) <= 1.0;
            bool centerOk = worst <= 0.75;

            Log(dir, $"titlebar-maximized: 窗口@({position.X},{position.Y}) 工作区={work.Width}x{work.Height}"
                + $" 边距=({margin.Left:0.#},{margin.Top:0.#},{margin.Right:0.#},{margin.Bottom:0.#})"
                + $" 栏顶屏幕y={barTopOnScreen:0.#}(应 {work.Y})"
                + $" 状态栏底屏幕y={statusBottomOnScreen:0.#}(应 {work.Bottom})"
                + $" 栏高={barHeight:0.#} 内容偏移={worst:0.#} => "
                + ((topOk && bottomOk && centerOk) ? "PASS" : "FAIL"));

            window.WindowState = restoreState;
            window.RootLayout.Margin = restoreMargin;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        /// <summary>造一条用于自检的报文记录</summary>
        /// <summary>
        /// 报文监视截图用的样例数据。
        ///
        /// 为什么不直接拿主窗口的 Records：自检跑到这一步时那个缓冲还是空的，
        /// 截出来是个空窗口——"值"那一列、下面那块原始字节全都看不见，
        /// 而这两样正是 1.10.2 要展示的东西。这里用真 ASDU 编出字节、
        /// 值也走同一套解析，截图和断言说的是同一件事。
        /// </summary>
        private static TrafficViewModel BuildTrafficSampleModel()
        {
            ApplicationLayerParameters parameters = ApplicationLayerParameters.CreateForCs104();

            // 遥信（单点合位）
            Asdu switchAsdu = new(parameters)
            {
                TypeId = TypeIdentifier.M_SP_NA_1,
                Cot = CauseOfTransmission.Spontaneous,
                CommonAddress = 1
            };
            switchAsdu.AddInformationObject(new SinglePointInformation(2003, true));

            // 双点分位（断路器位置）
            Asdu doubleAsdu = new(parameters)
            {
                TypeId = TypeIdentifier.M_DP_NA_1,
                Cot = CauseOfTransmission.Spontaneous,
                CommonAddress = 1
            };
            doubleAsdu.AddInformationObject(new DoublePointInformation(1002, DoublePointValue.Off));

            // 遥测（带 IV 品质，演示品质位是怎么追加到值后面的）
            Asdu measuredAsdu = new(parameters)
            {
                TypeId = TypeIdentifier.M_ME_NC_1,
                Cot = CauseOfTransmission.Spontaneous,
                CommonAddress = 1
            };
            measuredAsdu.AddInformationObject(new MeasuredValueShort(4001)
            {
                Value = 42.5f,
                Quality = new QualityDescriptor { Invalid = true }
            });

            RangeObservableCollection<AsduRecord> source = new();
            source.AddRange(new[]
            {
                AsduRecord.FromAsdu(switchAsdu, "发", switchAsdu.Encode()),
                AsduRecord.FromAsdu(doubleAsdu, "发", doubleAsdu.Encode()),
                AsduRecord.FromAsdu(measuredAsdu, "发", measuredAsdu.Encode()),
                AsduRecord.Note("[自检通道] 链路已建立 192.168.1.10:2404 ← 主站")
            });

            TrafficViewModel model = new(source);

            // 选中一条有字节的：下面的原始字节要能截出来，
            // 否则截图上永远是"（未选中报文）"，等于没验到用户报的那个点。
            if (model.Records.Count > 1)
            {
                model.SelectedRecord = model.Records[1];
            }

            return model;
        }

        private static AsduRecord MakeRecord(int index) => new()        {
            Direction = index % 2 == 0 ? "发" : "收",
            TypeCode = "M_ME_NC_1",
            TypeName = "短浮点遥测",
            CauseText = "响应站召唤(20)",
            CommonAddress = index % 100 + 1,
            ObjectSummary = $"IOA={4001 + index % 1000}",
            Hex = "68 1A 00 00 00 00 0D 01 14 00 01 00 00 00 00 00"
        };

        /// <summary>按令牌名读主题里的颜色（读不到就返回透明，让断言自然失败）。</summary>
        private static Color ReadThemeColor(string key)
        {
            if (Application.Current != null
                && Application.Current.TryFindResource(key, out object resource)
                && resource is ISolidColorBrush brush)
            {
                return brush.Color;
            }

            return Colors.Transparent;
        }

        /// <summary>把窗口视觉树渲染成 PNG（与屏幕是否被遮挡无关）</summary>
        private static void Save(Window window, string path)
        {
            // 让挂起的布局/绑定先跑完，避免截到半成品。
            // 必须按渲染优先级再跑一次：默认优先级跑不到布局/渲染队列，
            // 截图时就会拍到"下拉还是上一档"这种陈旧帧（数据其实早就改对了）。
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            Dispatcher.UIThread.RunJobs();

            double scaling = window.RenderScaling <= 0 ? 1.0 : window.RenderScaling;
            int width = (int)Math.Round(window.ClientSize.Width * scaling);
            int height = (int)Math.Round(window.ClientSize.Height * scaling);
            if (width <= 0 || height <= 0)
            {
                width = (int)Math.Round(window.Width * scaling);
                height = (int)Math.Round(window.Height * scaling);
            }
            if (width <= 0 || height <= 0)
            {
                Log(Path.GetDirectoryName(path), $"skip {Path.GetFileName(path)}: zero size");
                return;
            }

            // dpi 也乘缩放系数，这样逻辑坐标才能正确映射到像素尺寸
            using RenderTargetBitmap bitmap = new RenderTargetBitmap(
                new PixelSize(width, height),
                new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(window);
            bitmap.Save(path);
            Log(Path.GetDirectoryName(path), $"saved {Path.GetFileName(path)} {width}x{height}");
        }

        /// <summary>
        /// 自检（1.11）：本机网卡 / IP 地址 / 账本，以及"启动前确保地址存在"整条链路。
        ///
        /// 【为什么必须换掉执行通道】真加地址要提权，自检是无人值守的——弹一次 UAC
        /// 就永久卡在那。所以先把 <see cref="IpAddressService.Editor"/> 换成假的
        /// （只记录请求、按脚本成功或失败），跑完再换回去。
        /// 这也正是当初把"动系统"抽成 <see cref="IIpAddressEditor"/> 的唯一理由：
        /// 凡是需要人工授权或交互的动作，都要留一个可替换的接缝，否则自检只能覆盖到界面。
        ///
        /// 【账本要备份】添加成功会往 settings.json 里记一条账，那是真实文件。
        /// 自检跑完必须还原，否则用户下次打开"IP 地址管理"会看到一条自检留下的记录，
        /// 而且是删不掉的（那个地址本来就不在本机）。
        /// </summary>
        private static async Task RunIpAddressSelfCheckAsync(string dir, MainWindowViewModel viewModel)
        {
            // 一个几乎不可能出现在本机的地址（TEST-NET-2，专供文档示例）
            const string TestAddress = "198.51.100.77";

            IpAddressService service = IpAddressService.Shared;
            IIpAddressEditor originalEditor = service.Editor;
            FakeIpAddressEditor fake = new();
            service.Editor = fake;

            List<ManagedAddress> ledgerBackup = AppSettingsStore.Current.ManagedAddresses.ToList();
            string nicBackup = AppSettingsStore.Current.DefaultNicId;

            try
            {
                // ① 网卡枚举：本机至少有一块，且回环被排除
                IReadOnlyList<NicInfo> nics = service.ListNics();
                bool loopbackExcluded = nics.All(n =>
                    (n.Description ?? string.Empty).IndexOf("Loopback", StringComparison.OrdinalIgnoreCase) < 0);

                Log(dir, $"ip-list-nics: 网卡 {nics.Count} 块（已连接 {nics.Count(n => n.IsUp)}，"
                    + $"虚拟 {nics.Count(n => n.IsVirtual)}）回环已排除={loopbackExcluded} => "
                    + (nics.Count > 0 && loopbackExcluded ? "PASS" : "FAIL"));

                NicInfo nic = nics.FirstOrDefault(n => n.IsUp && !n.IsVirtual)
                    ?? nics.FirstOrDefault(n => n.IsUp)
                    ?? nics.FirstOrDefault();

                if (nic == null)
                {
                    Log(dir, "ip-*: 跳过（本机没有可用网卡）");
                    return;
                }

                // ② 地址计划文案：三种状态各验一句（全部网卡 / 本机没有 / 启动时添加）
                (string allText, bool allWarn) = IpAddressService.DescribePlan("0.0.0.0", null, false);
                (string missingText, bool missingWarn) = IpAddressService.DescribePlan(TestAddress, nic.Id, false);
                (string willAddText, bool willAddWarn) = IpAddressService.DescribePlan(TestAddress, nic.Id, true);

                bool planOk = allText.Contains("全部网卡") && !allWarn
                    && missingWarn && missingText.Contains("本机没有")
                    && !willAddWarn && willAddText.Contains("启动时添加");

                Log(dir, $"ip-plan-text: 全部网卡=[{allText}] 缺失=[{missingText}] 将添加=[{willAddText}] => "
                    + (planOk ? "PASS" : "FAIL"));

                // ③ 添加：请求真的发出去了、账本真的记上了
                IpChangeResult added = await service
                    .EnsureAsync(TestAddress, nic.Id, IpMask.DefaultMask, "自检");

                string firstAction = fake.Requests.Count > 0 ? fake.Requests[0].Action : "(无)";
                bool ledgerHas = AppSettingsStore.Current.ManagedAddresses
                    .Any(item => item.Address == TestAddress);

                bool addOk = added.Ok && !added.AlreadyExists
                    && firstAction == "add" && ledgerHas;

                Log(dir, $"ip-ensure-add: 结果={added.Ok} 请求={firstAction} 账本已记={ledgerHas} "
                    + $"消息=[{added.Message}] => " + (addOk ? "PASS" : "FAIL"));

                // ④ 同一个地址再加一次：账本不能记出两条（"地址列表里两行一模一样"很难解释）
                int ledgerBefore = AppSettingsStore.Current.ManagedAddresses.Count;
                await service.EnsureAsync(TestAddress, nic.Id, IpMask.DefaultMask, "自检");
                bool dedupe = AppSettingsStore.Current.ManagedAddresses.Count == ledgerBefore;

                Log(dir, $"ip-ledger-dedupe: 账本 {ledgerBefore} → "
                    + $"{AppSettingsStore.Current.ManagedAddresses.Count} => "
                    + (dedupe ? "PASS" : "FAIL"));

                // ⑤ 本机已有就跳过：判据是"任一网卡上已有"，不是只看目标网卡
                string localAddress = nics
                    .SelectMany(item => item.Addresses)
                    .Select(item => item.Address)
                    .FirstOrDefault();

                if (localAddress == null)
                {
                    Log(dir, "ip-ensure-skip: 跳过（本机没有任何 IPv4）");
                }
                else
                {
                    int requestBefore = fake.Requests.Count;
                    IpChangeResult skipped = await service
                        .EnsureAsync(localAddress, nic.Id, IpMask.DefaultMask, "自检");

                    bool skipOk = skipped.Ok && skipped.AlreadyExists
                        && fake.Requests.Count == requestBefore;

                    Log(dir, $"ip-ensure-skip: {localAddress} 已有={skipped.AlreadyExists} "
                        + $"未发请求={fake.Requests.Count == requestBefore} => "
                        + (skipOk ? "PASS" : "FAIL"));
                }

                // ⑥ 清理：只动账本里的，删完账本要空
                IReadOnlyList<IpChangeResult> removed = await service
                    .RemoveManagedAsync(AppSettingsStore.Current.ManagedAddresses.ToList());

                bool deleteSent = fake.Requests.Any(item => item.Action == "delete");
                bool cleanupOk = removed.Count > 0 && removed.All(item => item.Ok)
                    && deleteSent && AppSettingsStore.Current.ManagedAddresses.Count == 0;

                Log(dir, $"ip-cleanup: 删除 {removed.Count} 条 发出delete={deleteSent} "
                    + $"账本剩 {AppSettingsStore.Current.ManagedAddresses.Count} => "
                    + (cleanupOk ? "PASS" : "FAIL"));

                // ⑦ 10049 翻译：本机没有的地址上开监听，必须说清"本机没这个地址"
                //    （原来混成一句"启动连接失败"，用户只能去猜）
                ConnectionViewModel broken = new()
                {
                    Name = "自检-无地址",
                    BindAddress = TestAddress,
                    Port = 24040
                };

                string socketError = null;
                try
                {
                    broken.Open();
                }
                catch (Exception exception)
                {
                    socketError = exception.Message;
                }

                bool socketOk = socketError != null
                    && socketError.Contains("10049")
                    && socketError.Contains("本机没有");

                Log(dir, $"ip-socket-10049: 报错=[{Trim(socketError)}] => "
                    + (socketOk ? "PASS" : "FAIL"));

                // ⑧ 没开"自动添加"时，启动前就该拒绝并说清楚怎么改
                string prepareError = null;
                try
                {
                    await broken.PrepareAddressAsync();
                }
                catch (Exception exception)
                {
                    prepareError = exception.Message;
                }

                bool rejectOk = prepareError != null
                    && prepareError.Contains("本机没有")
                    && prepareError.Contains("自动添加");

                Log(dir, $"ip-start-reject: 报错=[{Trim(prepareError)}] => "
                    + (rejectOk ? "PASS" : "FAIL"));

                // ⑨ 加了自动添加但提权失败 → 回退 0.0.0.0，且**三处都写明**
                //    （通道状态 / 状态栏 / 日志；这里验前两处，日志由 LinkEvent 带出去）
                fake.ForcedFailure = "自检模拟：提权被拒";
                broken.NicId = nic.Id;
                broken.AutoAddAddress = true;

                string reason = await broken.PrepareAddressAsync();
                bool fallbackOk = !string.IsNullOrEmpty(reason)
                    && reason.Contains("已回退")
                    && broken.FellBackToAllNics;

                broken.MarkFallback("已回退 0.0.0.0（原地址未添加成功）");
                bool statusOk = broken.StatusMessage != null
                    && broken.StatusMessage.Contains("已回退");

                Log(dir, $"ip-fallback: 原因=[{Trim(reason)}] 状态=[{Trim(broken.StatusMessage)}] => "
                    + (fallbackOk && statusOk ? "PASS" : "FAIL"));

                fake.ForcedFailure = null;

                // ⑩ 提权 helper 的参数判定：别的参数不能被误判进来
                //    （判错的后果是主程序带错参数跑成了 helper，界面根本不出来）
                bool helperFlag = IpHelperProtocol.IsInvocation(new[] { "--ip-helper", "a.json", "b.json" });
                bool helperNotFlag = !IpHelperProtocol.IsInvocation(new[] { "--capture-ui", "out" });
                bool helperGuard = IpHelperProtocol.Run(Array.Empty<string>()) == 2;

                Log(dir, $"ip-helper-arg: 认得={helperFlag} 不误判={helperNotFlag} 缺参守门={helperGuard} => "
                    + (helperFlag && helperNotFlag && helperGuard ? "PASS" : "FAIL"));

                // ⑪ 窗口：网卡列表、地址表、添加动作三件都要真的连着
                IpManagerWindow window = new() { WindowStartupLocation = WindowStartupLocation.Manual };
                window.Show();
                await Task.Delay(600);

                IpManagerViewModel manager = window.ViewModel;
                bool nicListOk = manager.Nics.Count > 0 && manager.SelectedNic != null;

                manager.SelectedNic = manager.Nics
                    .FirstOrDefault(item => string.Equals(item.Id, nic.Id, StringComparison.OrdinalIgnoreCase))
                    ?? manager.Nics.FirstOrDefault();

                bool gridOk = manager.Addresses.Count == (manager.SelectedNic?.Addresses.Count ?? -1);

                await manager.AddForTestAsync(TestAddress, IpMask.DefaultMask);
                bool addStatusOk = manager.StatusText != null && manager.StatusText.Contains("已添加");

                Save(window, Path.Combine(dir, "46-IP地址管理.png"));
                Log(dir, $"ip-window: 网卡={manager.Nics.Count} 列表={nicListOk} "
                    + $"地址行一致={gridOk} 添加后=[{Trim(manager.StatusText)}] => "
                    + (nicListOk && gridOk && addStatusOk ? "PASS" : "FAIL"));

                window.Close();
                await Task.Delay(200);
            }
            finally
            {
                service.Editor = originalEditor;

                AppSettingsStore.Current.ManagedAddresses.Clear();
                AppSettingsStore.Current.ManagedAddresses.AddRange(ledgerBackup);
                AppSettingsStore.Current.DefaultNicId = nicBackup;
                AppSettingsStore.Save();
            }
        }

        /// <summary>日志里只放前 80 个字：断言的判据在 PASS/FAIL 上，全文会把日志冲得没法看</summary>
        private static string Trim(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "(空)";
            }

            string flat = text.Replace('\r', ' ').Replace('\n', ' ');
            return flat.Length <= 80 ? flat : flat[..80] + "…";
        }

        /// <summary>
        /// 假的执行通道：记录请求，按脚本成功或失败。
        /// 只有自检用，故意不做任何真实的系统操作。
        /// </summary>
        private sealed class FakeIpAddressEditor : IIpAddressEditor
        {
            public List<IpChangeRequest> Requests { get; } = new();

            /// <summary>非空时所有请求都按失败返回（用来验"回退 0.0.0.0"）</summary>
            public string ForcedFailure { get; set; }

            public Task<IReadOnlyList<IpChangeResult>> ApplyAsync(IReadOnlyList<IpChangeRequest> requests)
            {
                foreach (IpChangeRequest request in requests)
                {
                    Requests.Add(request);
                }

                IReadOnlyList<IpChangeResult> results = requests
                    .Select(request => ForcedFailure == null
                        ? new IpChangeResult
                        {
                            Address = request.Address,
                            Ok = true,
                            Message = request.Action == "delete" ? "已删除" : "已添加"
                        }
                        : IpChangeResult.Failed(request.Address, ForcedFailure))
                    .ToList();

                return Task.FromResult(results);
            }
        }
    }
}
