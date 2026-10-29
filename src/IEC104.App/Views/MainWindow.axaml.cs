using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IEC104.App.Services;
using IEC104.App.ViewModels;

namespace IEC104.App.Views
{
    /// <summary>
    /// 主窗口。
    /// 复刻说明：对应原程序 Windows.MainWindow（1224×701，DockPanel + 顶部工具区 + 内容区）。
    ///
    /// 本文件负责"视图层职责"——弹对话框、选文件、写 CSV。
    /// 业务逻辑全在 MainWindowViewModel 里，二者通过回调委托连接，
    /// 这样 ViewModel 不依赖 Avalonia，可单独测试。
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // 点表的鼠标动作挂在**隧道**阶段，不是 XAML 里的冒泡属性。
            // 原因：DataGridCell 在自己的 PointerPressed 里已经把事件标成 Handled，
            // 冒泡一路上来根本轮不到 DataGrid，双击"看起来没反应"就是这个坑。
            // 隧道是自上而下先经过 DataGrid，抢在单元格之前接一手。
            // 只挂 Tunnel：Tunnel|Bubble 会让同一个按下进来两次，滚轮/品质就会跳两档。
            PointDataGrid.AddHandler(PointerPressedEvent, OnPointGridPointerPressed,
                RoutingStrategies.Tunnel);

            // 滚轮同理（见 OnPointGridWheelChanged 的说明）
            PointDataGrid.AddHandler(PointerWheelChangedEvent, OnPointGridWheelChanged,
                RoutingStrategies.Tunnel);

            // 点表要跟面板的圆角对齐（用户要求"表格左上角/右上角也要圆角"）。
            // Avalonia 的 Border 即使设了 CornerRadius + ClipToBounds，也只按矩形裁子元素，
            // 子元素的方角照样会盖住圆角，所以只能给 DataGrid 自己设一个圆角矩形的 Clip。
            // Clip 的矩形得跟着控件尺寸走，尺寸一变就重算。
            PointDataGrid.SizeChanged += (_, _) => UpdatePointGridClip();

            // 底部监视面板的内容区同理："输出信息框还是直角的，想要一些圆角"。
            // 只圆**下面两角**：上面两角要和页签条贴成一条直线，圆了会露出面板底色的小缺口。
            // 两个页签的内容分别挂在两个控件上，得各挂一次；另一个页签的内容在
            // 没被选中时根本不在可视树上（TabControl 只把选中项的 Content 放出来），
            // 等它第一次被选中时 SizeChanged 会补上。
            RecordGrid.SizeChanged += (_, _) => UpdateMonitorClip();
            LogList.SizeChanged += (_, _) => UpdateMonitorClip();

            // 换主题后，已经实现出来的行容器还挂着旧画刷（隔行底色的画刷对象是加载时取的），
            // 需要主动刷一遍——新行由 LoadingRow 处理，老行只能在这里补。
            ThemeService.Changed += RefreshAllRowBrushes;

            // 最大化时要把内容收进可视区，见 ApplyMaximizedInset。
            // 要接几个信号，因为窗口位置在最大化后会变好几次：
            //   ① WindowState 变了 → 排一串补算（系统随后还会挪窗口）
            //   ② ClientSize 变了 → 立刻算一次
            //   ③ Position 变了 → 立刻算一次（这条最关键：边距是相对窗口位置算的，
            //      位置变了旧边距就错了，表现是顶部多出一条 9px 空白带）
            PropertyChanged += (_, e) =>
            {
                if (e.Property == WindowStateProperty)
                {
                    ScheduleMaximizedInset();
                }
                else if (e.Property == ClientSizeProperty)
                {
                    Dispatcher.UIThread.Post(ApplyMaximizedInset, DispatcherPriority.Loaded);
                }
            };

            PositionChanged += (_, _) =>
            {
                if (WindowState == WindowState.Maximized)
                {
                    Dispatcher.UIThread.Post(ApplyMaximizedInset, DispatcherPriority.Loaded);
                }
            };
        }

        /// <summary>最大化补算用的短定时器（只为吃掉"窗口位置还没稳定"这段时间）</summary>
        private DispatcherTimer _insetTimer;
        private int _insetTicks;

        /// <summary>
        /// 最大化之后，系统会把窗口位置摆两次：先按"工作区 − 不可见边框"摆一次，
        /// 随后 Avalonia/系统再修正一次。我们算边距要读窗口位置，所以不能只算一次，
        /// 得在随后约 0.4 秒里补算几次，等位置稳定。
        /// 只算一次的话会拿旧位置算出的边距一直贴着不放——
        /// 实测表现就是"顶部多出一条 9px 的空白带、内容整体偏下 9px"。
        /// </summary>
        private void ScheduleMaximizedInset()
        {
            ApplyMaximizedInset();

            if (_insetTimer == null)
            {
                _insetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                _insetTimer.Tick += (_, _) =>
                {
                    ApplyMaximizedInset();
                    if (++_insetTicks >= 7)
                    {
                        _insetTimer.Stop();
                    }
                };
            }

            _insetTicks = 0;
            _insetTimer.Start();
        }

        /// <summary>
        /// 最大化时给根布局加的外边距（逻辑像素），正常状态为 0。
        /// 自检里要读它，所以暴露出去。
        /// </summary>
        internal double MaximizedInset => _maximizedInset;

        private double _maximizedInset;

        /// <summary>
        /// 最大化时把整个界面收进屏幕工作区。
        ///
        /// 问题（第十二轮用户反馈"放大后图标和按钮都比较靠上"）：
        /// 本程序是 ExtendClientArea + NoChrome 自绘标题栏。最大化时窗口会比工作区
        /// 大一圈（125% 缩放下客户区 2578×1398，工作区只有 2560×1380），
        /// 多出来的那一圈如果落在顶部，标题栏最上面 9 物理像素就被屏幕顶边切掉，
        /// 状态栏最下面一截则落到任务栏后面。
        ///
        /// 后果很具体：40 高的标题栏只剩 31 高可见，但内容仍按 40 居中，
        /// 墨迹中心落在可见条的 39% 处而不是 50%——看着就是"贴着顶、偏上"。
        /// （用户那张截图正好 41 像素高 = 50 物理 − 9 裁剪，墨迹中心也正好量到
        /// 16/41 ≈ 39%，和这个推断完全吻合。）
        ///
        /// 为什么**按边**算而不是"溢出量 ÷ 2"：
        /// 多出来的那一圈落在哪一边，取决于系统把窗口摆在哪，实测两种都出现过——
        /// 窗口在 (-9,-9) 时上下左右各溢出 9；窗口在 (0,0) 时顶部不溢出、
        /// 右侧和底部各溢出 18。用对称公式会在第二种情况下凭空给顶部加 9px 留白。
        /// 所以这里改成用 窗口位置 vs 工作区，一条边一条边算，
        /// 保证"标题栏顶边正好压在屏幕顶边、状态栏底边正好压在工作区底边"。
        ///
        /// 没有被裁的那几条边不会加边距；被裁的几条边上露出的是窗口底色，
        /// 而它们都在屏幕外或任务栏后面——用户看不到。
        /// </summary>
        internal void ApplyMaximizedInset()
        {
            Thickness inset = default;
            Screens screens = Screens;
            double scale = RenderScaling;

            if (WindowState == WindowState.Maximized && screens != null && scale > 0)
            {
                Screen screen = screens.ScreenFromWindow(this) ?? screens.Primary;
                if (screen != null)
                {
                    PixelRect work = screen.WorkingArea;
                    PixelPoint position = Position;
                    double width = ClientSize.Width * scale;
                    double height = ClientSize.Height * scale;

                    double left = work.X - position.X;
                    double top = work.Y - position.Y;
                    double right = position.X + width - work.Right;
                    double bottom = position.Y + height - work.Bottom;

                    if (left > 0.5 || top > 0.5 || right > 0.5 || bottom > 0.5)
                    {
                        inset = new Thickness(
                            RoundInset(left, scale),
                            RoundInset(top, scale),
                            RoundInset(right, scale),
                            RoundInset(bottom, scale));
                    }
                }
            }

            _maximizedInset = inset.Top;

            if (RootLayout.Margin != inset)
            {
                RootLayout.Margin = inset;
            }
        }

        /// <summary>把"被裁掉的物理像素"换算成逻辑边距；没被裁（负数）就当 0</summary>
        private static double RoundInset(double physical, double scale)
        {
            return physical <= 0.5 ? 0 : Math.Round(physical / scale, 1);
        }

        /// <summary>
        /// 给点表套一个圆角矩形裁剪。
        /// 半径取 7 而面板是 8：面板还有 1px 描边，7 正好贴在描边内侧，
        /// 不会露出白边，也不会把描边啃掉一块。
        /// </summary>
        private void UpdatePointGridClip()
        {
            double width = PointDataGrid.Bounds.Width;
            double height = PointDataGrid.Bounds.Height;
            if (width <= 2 || height <= 2)
            {
                return;
            }

            PointDataGrid.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 7, 7);
        }

        /// <summary>
        /// 底部监视面板内容区的圆角裁剪（报文表格 + 链路日志列表）。
        /// 半径 7 而面板是 8：面板还有 1px 描边，7 正好贴在描边内侧。
        /// </summary>
        private void UpdateMonitorClip()
        {
            ApplyBottomRoundedClip(RecordGrid, 7);
            ApplyBottomRoundedClip(LogList, 7);
        }

        /// <summary>
        /// 只把**下面两角**裁成圆角的矩形。
        /// 为什么不能用 RectangleGeometry：它四个角一起圆，上面两角一圆，
        /// 内容区顶边和页签条之间就会各露出一小牙面板底色，接缝立刻看得见。
        /// 所以这里自己描一条路径：上边一条直线，下边两角落圆弧。
        /// 圆角用二次贝塞尔而不是 ArcTo —— 控制点就取直角顶点，方向不可能写反，
        /// 半径 7 的尺度下和真圆弧肉眼无差。
        /// </summary>
        private static void ApplyBottomRoundedClip(Control control, double radius)
        {
            if (control == null)
            {
                return;
            }

            double width = control.Bounds.Width;
            double height = control.Bounds.Height;
            if (width <= 2 || height <= 2)
            {
                return;
            }

            StreamGeometry geometry = new();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(new Point(0, 0), isFilled: true);
                context.LineTo(new Point(width, 0));
                context.LineTo(new Point(width, height - radius));
                context.QuadraticBezierTo(new Point(width, height), new Point(width - radius, height));
                context.LineTo(new Point(radius, height));
                context.QuadraticBezierTo(new Point(0, height), new Point(0, height - radius));
                context.EndFigure(isClosed: true);
            }

            control.Clip = geometry;
        }

        private MainWindowViewModel ViewModel => DataContext as MainWindowViewModel;

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            MainWindowViewModel viewModel = ViewModel;
            if (viewModel == null)
            {
                return;
            }

            // 把"需要视图参与"的操作注入给 ViewModel
            viewModel.EditConnectionRequested = async (connection, done) =>
                done(await ShowConnectionDialogAsync(connection));

            viewModel.EditSlaveRequested = async (slave, done) =>
                done(await ShowSlaveDialogAsync(slave));

            viewModel.EditPointRequested = async (point, slave, done) =>
                done(await ShowPointDialogAsync(point, slave));

            viewModel.ShowFrameParserDialog = protocol =>
            {
                // 带上当前通道的报文长度：现场抓的报文若是按非默认宽度组装的，
                // 解析器还按 2/2/3 去切，每一层都会错位，看起来像"报文全乱了"
                FrameParserWindow window = new()
                {
                    InitialProtocol = protocol,
                    InitialParameters = viewModel.SelectedConnection?.BuildConfiguredParameters()
                };
                _ = window.ShowDialog(this);
            };

            viewModel.ShowRedundancyCheckDialog = () => _ = new RedundancyCheckWindow().ShowDialog(this);

            viewModel.ShowStringCodecDialog = () => _ = new StringCodecWindow().ShowDialog(this);

            viewModel.ShowMessage = (title, content) => _ = ShowMessageAsync(title, content);

            viewModel.ShowAutoChangeDialog = ShowAutoChangeDialog;

            viewModel.ShowBatchCreateSlaveDialog = async () =>
            {
                BatchCreateSlaveViewModel model = new(viewModel.Connections);
                BatchCreateSlaveWindow window = new()
                {
                    DataContext = model,
                    ViewModelConnections = viewModel.Connections
                };
                await window.ShowDialog(this);
                viewModel.NotifyBatchCreated(window.CreatedCount);
            };

            viewModel.ExportRequested = slave => _ = ExportPointsAsync(slave);

            // ── 设置界面需要的视图能力 ──
            // 设置里的"新增/编辑自定义类型"要弹对话框、"删除/恢复默认"要二次确认、
            // "打开所在文件夹"要调系统资源管理器——这三件事都只有视图层能做，
            // 所以照样用委托注入，ViewModel 不引用 Window / Process。
            viewModel.Settings.EditAsduTypeDialog = async (draft, done) =>
            {
                AsduTypeWindow window = new() { DataContext = draft };
                await window.ShowDialog(this);
                done(window.Confirmed);
            };

            viewModel.Settings.ConfirmDialog = async (title, message, done) =>
                done(await ShowConfirmAsync(title, message));

            viewModel.Settings.StoragePage.OpenFolderRequested = folder =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = folder,
                        UseShellExecute = true
                    });
                }
                catch (Exception exception)
                {
                    _ = ShowMessageAsync("打开文件夹失败", exception.Message);
                }
            };

            // "关于"页：复制信息要剪贴板、项目主页要开浏览器——
            // 这两件事同样只能由视图层做，照样走委托。
            viewModel.Settings.ShowMessage = (title, content) => _ = ShowMessageAsync(title, content);

            viewModel.Settings.OpenUrl = url => OpenExternal(url);

            viewModel.Settings.AboutPage.CopyRequested = text =>
            {
                // Avalonia 11 的剪贴板是异步的，这里不 await：
                // 复制是个"发起即完成"的动作，为它把整个调用链改成 async 不值得。
                // 拿不到剪贴板（平台不支持）时给一句人话，而不是静默失败。
                try
                {
                    if (Clipboard != null)
                    {
                        _ = Clipboard.SetTextAsync(text ?? string.Empty);
                    }
                }
                catch (Exception exception)
                {
                    _ = ShowMessageAsync("复制失败", exception.Message);
                }
            };

            // 底部监视面板的收起/展开：状态存在 ViewModel 里（按钮、视图菜单、自检
            // 三个入口都要写同一个值），但"高度"是纯视图的事，所以由这里跟状态走。
            // 订阅而不是让每个入口各改一次：以后再加第四个入口也不会漏。
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ApplyMonitorExpanded();
        }

        /// <summary>收起前的面板高度。展开时原样还回去，用户拖过多高就还是多高。</summary>
        private GridLength _monitorHeightBeforeCollapse = new(240);

        /// <summary>分隔条所在的行的索引（见 MonitorHostGrid 的行定义）</summary>
        private const int MonitorSplitterRowIndex = 1;

        /// <summary>监视面板所在行的索引</summary>
        private const int MonitorRowIndex = 2;

        private void OnViewModelPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsMonitorExpanded))
            {
                ApplyMonitorExpanded();
            }
        }

        /// <summary>
        /// 把"展开与否"落到实际布局上。
        ///
        /// 收起 = 行高压到 36（页签条 33 + 上下各 1.5 的余量）+ 分隔条行归零并隐藏；
        /// 展开 = 还原收起前记录的高度。分隔条那 4px 必须一起收掉——
        /// 只把面板压扁、留一条能拖的分隔条，看起来就像"面板还在，只是里面空了"。
        /// </summary>
        private void ApplyMonitorExpanded()
        {
            bool expanded = ViewModel?.IsMonitorExpanded ?? true;
            RowDefinition panelRow = MonitorHostGrid.RowDefinitions[MonitorRowIndex];
            RowDefinition splitterRow = MonitorHostGrid.RowDefinitions[MonitorSplitterRowIndex];

            if (expanded)
            {
                panelRow.Height = _monitorHeightBeforeCollapse;
                splitterRow.Height = new GridLength(4);
                MonitorSplitter.IsVisible = true;
            }
            else
            {
                if (panelRow.Height.IsAbsolute && panelRow.Height.Value > 60)
                {
                    _monitorHeightBeforeCollapse = panelRow.Height;
                }
                panelRow.Height = new GridLength(36);
                splitterRow.Height = new GridLength(0);
                MonitorSplitter.IsVisible = false;
            }

            // 图标方向就是动作本身：展开时指向下（点了往下收），收起时指向上
            MonitorToggleIcon.Data = Geometry.Parse(expanded ? "M5 9l7 7 7-7" : "M5 15l7-7 7 7");

            // 内容区尺寸变了，圆角裁剪要重算；展开时表格是刚被塞回来的，
            // Bounds 还要等一次布局才有值，所以排到 Loaded 优先级再补一次。
            Dispatcher.UIThread.Post(UpdateMonitorClip, DispatcherPriority.Loaded);
        }

        /// <summary>面板右上角的收起 / 展开按钮</summary>
        private void OnToggleMonitorClick(object sender, RoutedEventArgs e)
        {
            MainWindowViewModel viewModel = ViewModel;
            if (viewModel != null)
            {
                viewModel.IsMonitorExpanded = !viewModel.IsMonitorExpanded;
            }
        }

        /// <summary>
        /// 面板右上角的"清空"按钮。
        ///
        /// 走 Click 而不是在 XAML 里直接绑 ClearRecordsCommand：两者对用户是一回事，
        /// 但自检要验证"这颗按钮真的连着清空逻辑"，而 Avalonia 的 Button 只有在
        /// OnClick 里才执行 Command——测试里 RaiseEvent(ClickEvent) 只会触发 Click 处理器，
        /// 绑 Command 的按钮点了毫无反应，自检就会一直是"绿"的假象。
        /// 这里显式转发一次，按钮的接线就是可被点出来的。
        /// </summary>
        private void OnClearRecordsClick(object sender, RoutedEventArgs e)
        {
            ICommand command = ViewModel?.ClearRecordsCommand;
            if (command != null && command.CanExecute(null))
            {
                command.Execute(null);
            }
        }

        /// <summary>
        /// 标题栏那颗钉子：一级动作，点一下就在"固定在最上层"和"不固定"之间切。
        /// 只改 ViewModel 的属性——窗口的 Topmost 在 XAML 里绑着它（见 MainWindow 的
        /// Topmost="{Binding AlwaysOnTop}"），视图菜单里的勾选项也绑着同一个属性，
        /// 所以三处状态天然一致，不需要各自同步。
        /// </summary>
        private void OnToggleAlwaysOnTopClick(object sender, RoutedEventArgs e)
        {
            MainWindowViewModel viewModel = ViewModel;
            if (viewModel != null)
            {
                viewModel.AlwaysOnTop = !viewModel.AlwaysOnTop;
            }
        }

        /// <summary>用系统默认浏览器打开一个链接（URL 由 AppInfo 统一维护）</summary>
        private void OpenExternal(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                _ = ShowMessageAsync("打开失败", $"没能调用系统浏览器：{exception.Message}\n\n地址：{url}");
            }
        }

        // ── 对话框 ──────────────────────────────────────────────

        private async Task<bool> ShowConnectionDialogAsync(ConnectionViewModel connection)
        {
            ConnectionEditModel model = new ConnectionEditModel(connection);
            ConnectionWindow window = new ConnectionWindow { DataContext = model };
            await window.ShowDialog(this);
            return window.Confirmed;
        }

        private async Task<bool> ShowSlaveDialogAsync(SlaveViewModel slave)
        {
            // 新建从站时，ViewModel 已经把带"通道选择"的草稿模型准备好了；
            // 编辑已有从站时没有草稿，就地构造一个只改名称/公共地址的模型。
            SlaveEditModel model = ViewModel?.PendingSlaveEdit;
            if (model == null || !ReferenceEquals(model.Target, slave))
            {
                ConnectionViewModel owner = ViewModel?.SelectedConnection;
                bool addressTaken(int address) =>
                    owner != null && owner.Slaves.Any(s => s != slave && s.CommonAddress == address);

                // 扁平视图里通道不是用户能看见的对象，双击标签想改的就是 IP / 端口，
                // 所以把这块并进同一个对话框；树形视图里通道自己有一行，各改各的。
                model = ViewModel is { IsFlatView: true } flat
                    ? new SlaveEditModel(slave, flat.SelectedConnection, flat.Connections, addressTaken)
                    : new SlaveEditModel(slave, addressTaken);
            }

            SlaveWindow window = new SlaveWindow { DataContext = model };
            await window.ShowDialog(this);
            return window.Confirmed;
        }

        private async Task<bool> ShowPointDialogAsync(PointViewModel point, SlaveViewModel slave)
        {
            PointEditModel model = new PointEditModel(point, slave);
            PointWindow window = new PointWindow { DataContext = model };
            await window.ShowDialog(this);
            return window.Confirmed;
        }

        /// <summary>
        /// 轻量消息框。
        /// 复刻说明：原程序有独立的 MessageBox / MessageBoxViewModel 与 PopupWindow，
        /// 此处用代码构建一个最小模态窗，避免为提示再增加一套样式文件。
        /// </summary>
        private async Task ShowMessageAsync(string title, string content)
        {
            Window dialog = new Window
            {
                Title = title,
                // 代码建的窗口没有 axaml，不显式给图标就会用 Windows 默认空白图标
                Icon = AppIcons.Main,
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                // 底色跟着主题走：写死白色的话，深色主题下会弹出刺眼的白框，
                // 而里面的文字已经变成浅色，直接看不见
                Background = ThemePalette.Get("SurfaceBrush")
            };

            Button okButton = new Button
            {
                Content = "确定",
                Width = 88,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Avalonia.Thickness(0, 12, 0, 0)
            };
            okButton.Classes.Add("primary");
            okButton.Click += (_, _) => dialog.Close();

            dialog.Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 13 },
                    new TextBlock { Text = content, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    okButton
                }
            };

            await dialog.ShowDialog(this);
        }

        /// <summary>
        /// 二次确认框，返回 true = 用户点了"确定"。
        /// 和 ShowMessageAsync 同一套代码构造，只是多一个"取消"按钮——
        /// 删除自定义类型、恢复默认设置这类不可逆操作必须先问一句。
        /// </summary>
        private async Task<bool> ShowConfirmAsync(string title, string content)
        {
            bool confirmed = false;

            Window dialog = new Window
            {
                Title = title,
                Icon = AppIcons.Main,
                Width = 480,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Background = ThemePalette.Get("SurfaceBrush")
            };

            Button cancelButton = new Button
            {
                Content = "取消",
                Width = 88,
                Margin = new Avalonia.Thickness(0, 12, 8, 0)
            };
            cancelButton.Click += (_, _) => dialog.Close();

            Button okButton = new Button
            {
                Content = "确定",
                Width = 88,
                Margin = new Avalonia.Thickness(0, 12, 0, 0)
            };
            okButton.Classes.Add("primary");
            okButton.Click += (_, _) =>
            {
                confirmed = true;
                dialog.Close();
            };

            dialog.Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 13 },
                    new TextBlock { Text = content, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { cancelButton, okButton }
                    }
                }
            };

            await dialog.ShowDialog(this);
            return confirmed;
        }

        // ── 点表导出 ────────────────────────────────────────────

        /// <summary>
        /// 导出当前从站点表为 CSV。
        /// 复刻说明：原程序用 CsvHelper 实现 ExportInformationObjectsCommand，
        /// 此处手写输出（字段一致：点号,名称,类型,值），不引入额外依赖。
        /// 用 UTF-8 BOM 是为了让 Excel 直接双击打开不乱码——这是国内现场最常踩的坑。
        /// </summary>
        private async Task ExportPointsAsync(SlaveViewModel slave)
        {
            IStorageFile file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出点表",
                SuggestedFileName = $"{slave.Name}_点表.csv",
                DefaultExtension = "csv",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("CSV 文件") { Patterns = new[] { "*.csv" } }
                }
            });

            if (file == null)
            {
                return;
            }

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("点号,名称,ASDU类型,类型名称,当前值,品质,变化时间");

            foreach (PointViewModel point in slave.Points)
            {
                builder.AppendLine(string.Join(",",
                    point.ObjectAddress,
                    Escape(point.Name),
                    point.TypeCode,
                    Escape(point.Type?.DisplayName ?? string.Empty),
                    Escape(point.ValueText),
                    Escape(point.QualityText),
                    point.LastChangedAtText));
            }

            await using System.IO.Stream stream = await file.OpenWriteAsync();
            await using System.IO.StreamWriter writer = new System.IO.StreamWriter(
                stream, new UTF8Encoding(true));
            await writer.WriteAsync(builder.ToString());

            ViewModel?.AppendLog($"已导出点表：{file.Name}（{slave.Points.Count} 条）");
        }

        /// <summary>CSV 字段转义：含逗号/引号/换行时用双引号包裹</summary>
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
        }

        // ── 实时曲线 / 通信报文监视 ────────────────────────────

        /// <summary>打开实时曲线窗口（对应原版 RealtimeChartingWindow）</summary>
        private async void OnRealtimeChartClick(object sender, RoutedEventArgs e)
        {
            MainWindowViewModel viewModel = ViewModel;
            SlaveViewModel slave = viewModel?.SelectedSlave;
            if (slave == null || slave.Points.Count == 0)
            {
                await ShowMessageAsync("没有可绘制的点位",
                    "请先选中一个从站，并为它添加信息对象。");
                return;
            }

            RealtimeChartViewModel dialogModel = new(slave.Points);
            RealtimeChartWindow window = new() { DataContext = dialogModel };
            _ = window.ShowDialog(this);
        }

        /// <summary>打开独立的通信报文监视窗口（对应原版 CommunicatingTrafficWindow）</summary>
        /// <summary>
        /// 打开通信报文监视。
        ///
        /// 用非模态 Show 而不是 ShowDialog：现场经常把报文窗口拖到另一块屏上
        /// 一直盯着，主界面同时还要改点位、点启动——模态对话框会把主界面锁死，
        /// 这就完全没法用了。
        /// 加 Topmost 开关，是为了在主界面最大化覆盖时也能看到报文。
        /// </summary>
        private void OnTrafficWindowClick(object sender, RoutedEventArgs e) => OpenTrafficWindow();

        /// <summary>
        /// 打开（或激活已有的）通信报文监视窗口。
        ///
        /// 用 Show 而不是 ShowDialog：这是个"陪着看"的窗口，用户要一边盯报文
        /// 一边在主界面上点来点去，模态会把主窗口锁死，正好违背需求。
        /// 配合窗口里的"窗口置顶"勾选，可以把它拖到副屏长期挂着。
        /// </summary>
        public void OpenTrafficWindow()
        {
            MainWindowViewModel viewModel = ViewModel;
            if (viewModel == null)
            {
                return;
            }

            // 已经开着就直接激活，避免重复开出一堆窗口
            if (_trafficWindow != null && _trafficWindow.IsVisible)
            {
                _trafficWindow.Activate();
                return;
            }

            TrafficViewModel model = new(viewModel.Records);
            _trafficWindow = new TrafficWindow { DataContext = model };
            _trafficWindow.Closed += (_, _) => _trafficWindow = null;
            _trafficWindow.Show(this);
        }

        /// <summary>报文监视窗口是否开着（自检用）</summary>
        public bool IsTrafficWindowOpen => _trafficWindow != null && _trafficWindow.IsVisible;

        /// <summary>关掉报文监视窗口（自检用）</summary>
        public void CloseTrafficWindow()
        {
            _trafficWindow?.Close();
            _trafficWindow = null;
        }

        private TrafficWindow _trafficWindow;

        // ── 右键菜单 ────────────────────────────────────────────

        /// <summary>
        /// 右键点击列表项时先把它选中。
        /// Avalonia 的 ListBox 默认只在左键时改选中项，不处理的话右键菜单
        /// 操作的会是"上一次选中的那一项"，很容易误删/误改。
        /// </summary>
        private void OnListPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                return;
            }

            if (sender is ListBox list
                && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>() is { } item)
            {
                list.SelectedItem = item.DataContext;
            }
        }

        /// <summary>
        /// 点表：右键选中落点行；左键双击进入单元格就地编辑。
        ///
        /// 为什么双击不用 DoubleTapped 事件：那个事件只在命中某个可交互控件时
        /// 才会被 Gestures 合成，用户双击在单元格空白处（文本右侧一截）就什么都不会发生，
        /// 现场用起来会觉得"有时候能改有时候不能改"。改用 PointerPressed 的
        /// ClickCount==2 + 沿可视树向上找带 Tag 的单元格，命中范围就是整格。
        ///
        /// 为什么挂在隧道阶段：见构造函数里的说明（DataGridCell 会先把事件吃掉）。
        /// </summary>
        private void OnPointGridPointerPressed(object sender, PointerPressedEventArgs e)
        {
            PointerPoint currentPoint = e.GetCurrentPoint(this);
            bool isRight = currentPoint.Properties.IsRightButtonPressed;
            bool isLeft = currentPoint.Properties.IsLeftButtonPressed;

            Probe($"grid-press: click={e.ClickCount} right={isRight} left={isLeft} src={e.Source?.GetType().Name}");

            if (isRight)
            {
                if (sender is DataGrid grid
                    && (e.Source as Visual)?.FindAncestorOfType<DataGridRow>() is { } row)
                {
                    grid.SelectedItem = row.DataContext;
                }
                return;
            }

            if (!isLeft || e.ClickCount != 2)
            {
                return;
            }

            // 双击既进编辑也顺带选中落点行，和用户对表格的直觉一致
            if (sender is DataGrid owner
                && (e.Source as Visual)?.FindAncestorOfType<DataGridRow>() is { } hitRow)
            {
                owner.SelectedItem = hitRow.DataContext;
            }

            Control cell = FindTaggedCell(e.Source as Visual);
            Probe($"grid-press-dbl: tag={(cell?.Tag as string) ?? "<null>"}");

            if (cell != null)
            {
                BeginCellEdit(cell);
                // 吃掉这次按下，避免单元格后续处理把焦点从刚弹出的输入框抢回去
                e.Handled = true;
            }
        }

        /// <summary>
        /// 现场探针日志：只在设置了环境变量 IEC104_PROBE_LOG 时写文件。
        /// 真实鼠标的问题（双击、滚轮）在自检里 RaiseEvent 是测不出来的，
        /// 只能让程序自己把"收到了什么"记下来。
        /// </summary>
        private static void Probe(string message)
        {
            string path = Environment.GetEnvironmentVariable("IEC104_PROBE_LOG");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                System.IO.File.AppendAllText(path,
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
                // 探针失败不能影响主流程
            }
        }

        /// <summary>从命中的元素往上找最近一个带 Tag 的单元格控件（Tag 就是字段名）</summary>
        private static Control FindTaggedCell(Visual source)
        {
            Visual current = source;
            while (current != null)
            {
                if (current is Control control && control.Tag is string)
                {
                    return control;
                }

                // 落在单元格自身的边框上（文字右侧那一截空白）时往上找不到 Tag。
                // 早期做法是给 CellTemplate 的 Grid 铺 Transparent 背景让整格可命中，
                // 但那会把单元格的悬停高亮也一起盖掉；这里改成按"列"去内容里取带 Tag 的元素，
                // 效果等价于整格都能双击，且不影响主题样式。
                if (current is DataGridCell cell)
                {
                    return cell.GetVisualDescendants()
                        .OfType<Control>()
                        .FirstOrDefault(item => item.Tag is string);
                }

                if (current is DataGrid)
                {
                    // 已经爬到表格本身了，说明点在行外/表头上
                    return null;
                }
                current = current.GetVisualParent();
            }
            return null;
        }

        /// <summary>按 Tag 决定编辑哪一列，并把焦点交给输入框</summary>
        private void BeginCellEdit(Control cell)
        {
            if (cell.DataContext is not PointViewModel point || cell.Tag is not string tag)
            {
                return;
            }

            // 品质不是文本，双击的语义按"换下一档"处理，和滚轮保持一致
            if (tag == "quality")
            {
                point.NudgeQuality(1);
                return;
            }

            PointField field = tag switch
            {
                "address" => PointField.Address,
                "name" => PointField.Name,
                "value" => PointField.Value,
                _ => PointField.None
            };
            if (field == PointField.None)
            {
                return;
            }

            point.BeginEdit(field);

            // 输入框和文本块在同一个 Grid 里，取出来给焦点
            if (cell.Parent is Panel panel
                && panel.Children.OfType<TextBox>().FirstOrDefault() is { } box)
            {
                // 等这一轮布局把输入框变成可见之后再聚焦，否则 Focus 会被忽略
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        box.Focus();
                        box.SelectAll();
                    },
                    DispatcherPriority.Input);
            }
        }

        private void OnConnMenuToggleClick(object sender, RoutedEventArgs e) =>
            ViewModel?.OpenConnectionCommand?.Execute(null);

        private void OnConnMenuOpenAllClick(object sender, RoutedEventArgs e) =>
            ViewModel?.OpenAllCommand?.Execute(null);

        private void OnConnMenuCloseAllClick(object sender, RoutedEventArgs e) =>
            ViewModel?.CloseAllCommand?.Execute(null);

        private void OnConnMenuEditClick(object sender, RoutedEventArgs e) =>
            ViewModel?.EditConnectionCommand?.Execute(null);

        private void OnConnMenuAddSlaveClick(object sender, RoutedEventArgs e) =>
            ViewModel?.CreateSlaveCommand?.Execute(null);

        // ── 侧边栏与分组折叠 ────────────────────────────────────
        //
        // 为什么用代码后置而不是绑定：折叠只是"这个控件显示不显示"，
        // 没有业务含义，放 ViewModel 里反而污染状态（还要考虑工作区是否持久化）。
        // 另外整栏折叠要改 ColumnDefinition.Width，那是纯视图层的东西。

        private const string ArrowCollapse = "M9 6l-4 6 4 6 M15 6l-4 6 4 6";   // «  可收起
        private const string ArrowExpand = "M9 6l4 6-4 6 M15 6l4 6-4 6";        // »  可展开

        /// <summary>
        /// 整栏收起 / 展开。
        ///
        /// 第八轮改法：不再往主界面里塞一条"展开按钮车道"。
        /// 之前收起后左侧会留出 24px 的竖条放按钮，虽然保证了不遮挡数据，
        /// 但 24px 在 1224 宽的窗口里看着很扎眼，用户明确说"太大、占用空间"。
        /// 现在开关只有一处——状态栏最左边、紧挨"就绪"前面那颗小箭头，
        /// 收起和展开都点它，主界面一像素都不让出来。
        /// </summary>
        private void OnToggleSidebarClick(object sender, RoutedEventArgs e) =>
            SetSidebarCollapsed(!IsSidebarCollapsed);

        /// <summary>
        /// 侧栏折叠的唯一实现。
        ///
        /// 抽成公开方法是为了让 UI 自检走**真实路径**：以前自检直接改列宽，
        /// 结果漏掉了"浮出展开按钮"这一步，截图上压根没有那个按钮——
        /// 断言过得去、人眼一看就穿帮。现在按钮和自检共用这一处。
        /// </summary>
        public void SetSidebarCollapsed(bool collapsed)
        {
            SidebarGrid.ColumnDefinitions[0].Width = new GridLength(collapsed ? 0 : 280);
            SidebarGrid.ColumnDefinitions[1].Width = new GridLength(collapsed ? 0 : 4);
            SidebarPanel.IsVisible = !collapsed;

            // 状态栏那顆开关同时承担"收起"和"展开"两个方向的职责，
            // 所以图标和提示语都要跟着状态翻转，否则收起后还写着"收起侧栏"就说不通了。
            SidebarToggleIcon.Data = Geometry.Parse(collapsed ? ArrowExpand : ArrowCollapse);
            ToolTip.SetTip(SidebarToggleButton, collapsed ? "展开侧栏" : "收起侧栏");
            // Tag 记一个可读的状态，供 UI 自检判断"图标到底翻没翻"。
            // 不能拿 Geometry.ToString() 比：StreamGeometry 没重写 ToString，
            // 两个不同的路径打出来是同一个类型名，永远判断成"没变"。
            SidebarToggleButton.Tag = collapsed ? "expand" : "collapse";
        }

        /// <summary>当前侧栏是否处于收起状态（自检用）</summary>
        public bool IsSidebarCollapsed => SidebarGrid.ColumnDefinitions[0].Width.Value <= 0;

        /// <summary>通道分组收起 / 展开</summary>
        private void OnToggleChannelGroupClick(object sender, RoutedEventArgs e)
        {
            ConnectionList.IsVisible = !ConnectionList.IsVisible;
            ChannelChevron.Text = ConnectionList.IsVisible ? "▼" : "▶";
        }

        /// <summary>从站分组收起 / 展开</summary>
        private void OnToggleSlaveGroupClick(object sender, RoutedEventArgs e)
        {
            SlaveList.IsVisible = !SlaveList.IsVisible;
            SlaveChevron.Text = SlaveList.IsVisible ? "▼" : "▶";
        }

        private void OnConnMenuDeleteClick(object sender, RoutedEventArgs e) =>
            ViewModel?.DeleteConnectionCommand?.Execute(null);

        private void OnSlaveMenuEditClick(object sender, RoutedEventArgs e) =>
            ViewModel?.EditSlaveCommand?.Execute(null);

        private void OnSlaveMenuCopyClick(object sender, RoutedEventArgs e) =>
            ViewModel?.CopySlaveCommand?.Execute(null);

        private void OnSlaveMenuDeleteClick(object sender, RoutedEventArgs e) =>
            ViewModel?.DeleteSlaveCommand?.Execute(null);

        private void OnPointMenuAddClick(object sender, RoutedEventArgs e) =>
            ViewModel?.AddPointCommand?.Execute(null);

        private void OnPointMenuEditClick(object sender, RoutedEventArgs e) =>
            ViewModel?.EditPointCommand?.Execute(null);

        private void OnPointMenuDeleteClick(object sender, RoutedEventArgs e) =>
            ViewModel?.DeletePointCommand?.Execute(null);

        private void OnPointMenuExportClick(object sender, RoutedEventArgs e) =>
            ViewModel?.ExportPointsCommand?.Execute(null);

        // ── 批量变化 ────────────────────────────────────────────

        /// <summary>
        /// 打开批量变化窗口。
        /// 作用域取当前从站的全部点位，窗口里再按类型勾选。
        /// </summary>
        private async void OnBatchChangeClick(object sender, RoutedEventArgs e)
        {
            MainWindowViewModel viewModel = ViewModel;
            SlaveViewModel slave = viewModel?.SelectedSlave;
            if (slave == null || slave.Points.Count == 0)
            {
                await ShowMessageAsync("没有可变化的点位",
                    "请先选中一个从站，并为它添加信息对象。");
                return;
            }

            BatchChangeViewModel dialogModel = new(slave.Points);
            BatchChangeWindow window = new() { DataContext = dialogModel };
            _ = window.ShowDialog(this);
        }

        // ── 初始化数据 ──────────────────────────────────────────

        /// <summary>
        /// 打开“初始化数据”窗口，对当前选中从站批量生成点表。
        /// 复刻说明：对应原程序 InitDataWindow。
        /// </summary>
        private async void OnInitDataClick(object sender, RoutedEventArgs e)
        {
            MainWindowViewModel viewModel = ViewModel;
            if (viewModel?.SelectedSlave == null)
            {
                await ShowMessageAsync("没有可用的从站",
                    "请先在左侧选中（或新建）一个从站，再用初始化数据批量生成点表。");
                return;
            }

            InitDataViewModel dialogModel = new(viewModel.SelectedSlave);
            InitDataWindow window = new() { DataContext = dialogModel };
            await window.ShowDialog(this);

            // 生成后主界面的点表与计数需要刷新
            viewModel.NotifyPointsChanged();
        }

        // ── 缓变配置 ────────────────────────────────────────────

        /// <summary>
        /// 打开缓变配置窗口。
        /// 按当前选中从站的点表打开——现场习惯是“先把一台装置的点配完再配下一台”。
        /// </summary>
        private void ShowAutoChangeDialog()
        {
            MainWindowViewModel viewModel = ViewModel;
            if (viewModel == null)
            {
                return;
            }

            SlaveViewModel slave = viewModel.SelectedSlave;
            if (slave == null || slave.Points.Count == 0)
            {
                _ = ShowMessageAsync("没有可配置的点位",
                    "请先在左侧选中一个从站，并为它添加信息对象，然后再配置缓变。");
                return;
            }

            AutoChangeViewModel dialogModel = new(viewModel.AutoChange, slave.Points);
            AutoChangeWindow window = new() { DataContext = dialogModel };
            _ = window.ShowDialog(this);
        }

        // ── 工作区 ──────────────────────────────────────────────

        /// <summary>
        /// 打开工作区文件（.iec104ws）。
        /// 复刻说明：对应原程序 OpenWorkspaceCommand；
        /// 扩展名用自有的 .iec104ws，同时允许选 .json，方便现场手工编辑后沿用。
        /// </summary>
        private async void OnOpenWorkspaceClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null)
            {
                return;
            }

            IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "打开工作区",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("IEC104 工作区") { Patterns = new[] { "*.iec104ws", "*.json" } },
                        new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
                    }
                });

            if (files.Count == 0)
            {
                return;
            }

            string path = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                await ShowMessageAsync("无法打开", "该文件不在本地磁盘上，暂不支持。");
                return;
            }

            try
            {
                int count = ViewModel.LoadWorkspace(path);
                ViewModel.AppendLog($"已打开工作区 {path}（{count} 个信息对象）");
            }
            catch (Exception exception)
            {
                await ShowMessageAsync("打开工作区失败", exception.Message);
            }
        }

        /// <summary>保存工作区（含连接参数、从站、完整点表与品质位）</summary>
        private async void OnSaveWorkspaceClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null)
            {
                return;
            }

            IStorageFile file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存工作区",
                SuggestedFileName = "工作区.iec104ws",
                DefaultExtension = "iec104ws",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("IEC104 工作区") { Patterns = new[] { "*.iec104ws" } },
                    new FilePickerFileType("JSON 文件") { Patterns = new[] { "*.json" } }
                }
            });

            if (file == null)
            {
                return;
            }

            string path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                await ShowMessageAsync("无法保存", "请选择本地磁盘路径。");
                return;
            }

            try
            {
                ViewModel.SaveWorkspace(path);
                ViewModel.AppendLog($"工作区已保存到 {path}");
            }
            catch (Exception exception)
            {
                await ShowMessageAsync("保存工作区失败", exception.Message);
            }
        }

        // ── 自绘标题栏 ──────────────────────────────────────────
        //
        // 为什么自绘：主题切换按钮要摆在系统标题栏的位置（用户指定截图 3 那个位置），
        // 而系统标题栏里塞不了自定义控件。只能 ExtendClientAreaToDecorationsHint
        // + NoChrome 把标题栏让出来自己画，代价是窗口拖动/最大化/最小化/关闭
        // 这一整套系统行为都得自己实现。

        /// <summary>
        /// 标题栏拖动与双击最大化。
        /// 双击要自己判断（e.ClickCount == 2），因为 NoChrome 下没有系统标题栏去接这个手势。
        /// </summary>
        private void OnTitleBarPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }

            // 标题栏这一行现在也放着菜单和主题按钮。
            // 按在可交互控件上时不能启动窗口拖动，否则点"文件"的同时窗口跟着鼠标跑。
            // Button 会在自己的 PointerPressed 里标 Handled（默认不回调这里），
            // 但 Menu / MenuItem 不标，所以这里显式挡一层。
            if (IsInteractive(e.Source as Visual))
            {
                return;
            }

            if (e.ClickCount == 2)
            {
                if (CanResize)
                {
                    ToggleMaximize();
                }
                e.Handled = true;
                return;
            }

            // 最大化状态下不让拖：Windows 上拖最大化窗口的语义是"还原并跟随鼠标"，
            // 要做出那个效果还得算鼠标相对位置，收益不大，直接按不响应处理。
            if (WindowState == WindowState.Normal)
            {
                BeginMoveDrag(e);
            }
        }

        /// <summary>命中元素是不是"菜单 / 按钮"这类可交互控件（要排除它们再判断拖动）</summary>
        private static bool IsInteractive(Visual source)
        {
            Visual current = source;
            while (current != null && current is not Window)
            {
                if (current is Menu or MenuItem or Button or TextBox)
                {
                    return true;
                }
                current = current.GetVisualParent();
            }
            return false;
        }

        private void OnMinimizeClick(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            UpdateMaximizeIcon();
        }

        /// <summary>
        /// 最大化按钮的图标：还原态画"两个叠着的方框"，最大化态画"一个方框"。
        /// 和系统标题栏一致，用户不用重新学。
        /// </summary>
        private void UpdateMaximizeIcon()
        {
            MaximizeIcon.Data = Geometry.Parse(WindowState == WindowState.Maximized
                ? "M3 6h7v7H3z M6 3h7v7"
                : "M3 3h10v10H3z");
            ToolTip.SetTip(MaximizeButton, WindowState == WindowState.Maximized ? "向下还原" : "最大化");
        }

        // ── 主题切换 ────────────────────────────────────────────

        /// <summary>
        /// 弹出主题选择卡片。
        ///
        /// 之前是 ContextMenu：方角、灰底、前面一个黑圆点，五套主题长得一样，
        /// 得读完名字才知道哪个是哪个。现在换成自绘 Popup：
        /// 每项一个该主题的代表色圆点 + 名称 + 明暗提示，当前项浅底高亮并带勾。
        ///
        /// 条目仍然从 ThemeService.Options 生成，不写进 XAML：
        /// 以后加主题只改一处，不会出现"字典里有六套、菜单里只有五套"。
        /// </summary>
        private void OnThemeMenuClick(object sender, RoutedEventArgs e)
        {
            BuildThemeMenu();
            ThemePopup.IsOpen = !ThemePopup.IsOpen;
            Probe($"theme-menu: 打开={ThemePopup.IsOpen} 项数={ThemeList.Children.Count}");
        }

        /// <summary>最近一次构建出来的主题列表（自检用）</summary>
        internal StackPanel ThemeMenuItems => ThemeList;

        internal Avalonia.Controls.Primitives.Popup ThemeMenuPopup => ThemePopup;

        /// <summary>弹窗里那块圆角卡片（自检要量它的圆角与内边距）</summary>
        internal Border ThemeMenuCard => ThemeCard;

        /// <summary>按 ThemeService.Options 重建列表，顺带把"当前生效"标出来</summary>
        internal void BuildThemeMenu()
        {
            ThemeList.Children.Clear();

            foreach (AppThemeOption option in ThemeService.Options)
            {
                bool active = string.Equals(option.Id, ThemeService.Current?.Id,
                    StringComparison.OrdinalIgnoreCase);
                ThemeList.Children.Add(CreateThemeItem(option, active));
            }
        }

        private Button CreateThemeItem(AppThemeOption option, bool active)
        {
            Button button = new() { Tag = option.Id, Content = BuildThemeItemContent(option, active) };
            button.Classes.Add("themeItem");
            if (active)
            {
                button.Classes.Add("active");
            }
            button.Click += OnThemeItemClick;
            return button;
        }

        /// <summary>一行 = 色点 + 名字 + 明暗提示 + 勾</summary>
        private static Control BuildThemeItemContent(AppThemeOption option, bool active)
        {
            Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

            Ellipse swatch = new()
            {
                Fill = new SolidColorBrush(Color.Parse(option.Swatch)),
                Margin = new Thickness(0, 0, 10, 0)
            };
            swatch.Classes.Add("themeSwatch");
            grid.Children.Add(swatch);

            StackPanel labels = new()
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };

            TextBlock name = new() { Text = option.Name };
            name.Classes.Add("themeName");
            labels.Children.Add(name);

            TextBlock hint = new() { Text = option.Hint };
            hint.Classes.Add("themeHint");
            labels.Children.Add(hint);

            Grid.SetColumn(labels, 1);
            grid.Children.Add(labels);

            // 勾只给当前生效的那一项；不生效时整块不占位（IsVisible=false 不参与布局）
            Avalonia.Controls.Shapes.Path check = new()
            {
                Data = Geometry.Parse("M5 12.5l4.5 4.5L19 7.5"),
                IsVisible = active,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            check.Classes.Add("themeCheck");
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);

            return grid;
        }

        private void OnThemeItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string themeId })
            {
                ViewModel?.ApplyTheme(themeId);
                ThemePopup.IsOpen = false;
            }
        }

        // ── 标题栏：仓库与更新 ──────────────────────────────────

        /// <summary>
        /// 打开项目仓库。
        ///
        /// 地址已经收到 <see cref="AppInfo.RepositoryUrl"/> 一处（关于页的"项目主页"用的是同一个），
        /// 这里不再各存一份——以前两边各写一处，发布时最容易漏改一边。
        /// </summary>
        private void OnGitHubClick(object sender, RoutedEventArgs e)
        {
            if (!AppInfo.HasRepository)
            {
                _ = ShowMessageAsync("项目仓库",
                    "仓库地址还没确定，等发布到 GitHub 后这里就能一键打开。\n\n"
                    + "（地址写在 Services/AppInfo.cs 的 RepositoryUrl 常量里）");
                return;
            }

            OpenExternal(AppInfo.RepositoryUrl);
        }

        /// <summary>
        /// 检查更新。本工具是离线单文件发布，没有更新服务端，
        /// 所以这里只报当前版本，并说明更新方式（换掉 exe 即可）。
        /// 真接了发布源以后，把这里换成取版本号比较就行。
        /// </summary>
        private void OnCheckUpdateClick(object sender, RoutedEventArgs e)
        {
            System.Version version = typeof(MainWindow).Assembly.GetName().Version;
            _ = ShowMessageAsync("检查更新",
                $"当前版本：{version?.ToString(3) ?? "1.5.0"}\n\n"
                + "本工具为离线单文件发布，未配置更新源。\n"
                + "需要升级时，直接用新版 IEC104Simulator.exe 覆盖旧文件即可，"
                + "主题选择与工作区配置都保存在用户目录，不会丢。");
        }

        // ── 事件处理 ────────────────────────────────────────────

        private void OnExitClick(object sender, RoutedEventArgs e) => Close();

        // 原来这里还有 OnAboutClick（菜单栏「帮助 → 关于」）。
        // 1.9.1 起"关于"是设置里的一页，主界面不再有这个入口。

        private void OnFrame104Click(object sender, RoutedEventArgs e) =>
            ViewModel?.ShowFrameParserDialog?.Invoke("104");

        private void OnFrame101Click(object sender, RoutedEventArgs e) =>
            ViewModel?.ShowFrameParserDialog?.Invoke("101");

        private void OnRedundancyClick(object sender, RoutedEventArgs e) =>
            ViewModel?.ShowRedundancyCheckDialog?.Invoke();

        private void OnStringCodecClick(object sender, RoutedEventArgs e) =>
            ViewModel?.ShowStringCodecDialog?.Invoke();

        private void OnAutoChangeClick(object sender, RoutedEventArgs e) =>
            ViewModel?.ShowAutoChangeCommand?.Execute(null);

        // 启停按钮已合并成"启停合一"，直接绑 ToggleSelectedConnectionCommand，
        // 不再需要单独的启动/停止点击处理方法。

        // ── 表格隔行底色 ────────────────────────────────────────
        //
        // 纯白长表格里，一行 30px、十几列数据，视线横着走过去很容易串行。
        // 隔行换个极浅的灰是目前最省事也最有效的办法。
        //
        // 为什么不用样式：Avalonia 的 DataGrid 没有 WPF 的 AlternatingRowBackground
        // （写了 AVLN2000），而 nth-child 选择器打到 /template/ Rectangle#BackgroundRectangle
        // 在虚拟化行容器上不生效（实测每行都是同一个底色）。
        // LoadingRow 是行容器"准备好"时触发，按行序号（Index）奇偶赋底色，
        // 行被回收复用时也会重新触发，所以滚动多远都不会错位。
        //
        // 底色必须从主题取（SurfaceBrush / GridRowAltBrush），不能写死白和浅灰：
        // 深色主题下写死的白色行会白得刺眼，而且行文字已经跟着主题变浅，
        // 浅底浅字直接看不见。
        private void OnDataGridLoadingRow(object sender, DataGridRowEventArgs e)
        {
            e.Row.Background = RowBrushFor(e.Row.Index);
        }

        private static IBrush RowBrushFor(int index) =>
            index % 2 == 0
                ? ThemePalette.Get("SurfaceBrush")
                : ThemePalette.Get("GridRowAltBrush");

        /// <summary>
        /// 换主题后把已实现的行容器底色重新刷一遍。
        /// LoadingRow 只对"新建出来的行"生效，屏幕上已经存在的那些行不会重新触发，
        /// 所以出现过"切了主题，可见的十几行还是旧底色，滚动一下才变"的现象。
        /// </summary>
        private void RefreshAllRowBrushes()
        {
            foreach (DataGridRow row in this.GetVisualDescendants().OfType<DataGridRow>())
            {
                row.Background = RowBrushFor(row.Index);
            }
        }

        // ── 侧栏列表：双击就地打开编辑 ──────────────────────────
        //
        // 双击列表项是这类工具的通例（原版也是双击进编辑），
        // 但要注意"双击空白区"不该弹窗，所以先确认落点确实在某个 ListBoxItem 上。

        private void OnConnectionListDoubleTapped(object sender, TappedEventArgs e)
        {
            if (sender is ListBox list
                && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>() is { } item)
            {
                list.SelectedItem = item.DataContext;
                ViewModel?.EditConnectionCommand?.Execute(null);
                e.Handled = true;
            }
        }

        private void OnSlaveListDoubleTapped(object sender, TappedEventArgs e)
        {
            if (sender is ListBox list
                && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>() is { } item)
            {
                list.SelectedItem = item.DataContext;
                ViewModel?.EditSlaveCommand?.Execute(null);
                e.Handled = true;
            }
        }

        /// <summary>
        /// 扁平从站列表的双击。
        ///
        /// 用户原话："在从站的标签上双击可以去编辑对应的 IP、端口信息"——
        /// 在这儿"从站"和"那台装置的 IP"在用户心里是同一件事，
        /// 所以双击打开的还是同一个"装置配置"对话框，只是多给一块监听 IP/端口。
        /// 具体给不给那块由 ViewModel 决定（IsFlatView），视图层不判断。
        /// </summary>
        private void OnSlaveRowListDoubleTapped(object sender, TappedEventArgs e)
        {
            if (sender is ListBox list
                && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>() is { } item)
            {
                list.SelectedItem = item.DataContext;
                ViewModel?.EditSlaveCommand?.Execute(null);
                e.Handled = true;
            }
        }

        // ── 点表：就地编辑 ──────────────────────────────────────
        //
        // 双击单元格进入编辑态（点号 / 名称 / 当前值），回车提交、Esc 取消、失焦提交。
        // 为什么不用 DataGrid 自带的编辑模式：本表显示的是规约化文本
        // （"合 (1)""档位 3""0x00000001"），交给 DataGridTextColumn 进编辑
        // 会把裸数字塞进框里，回填时语义又丢一次。
        // 这里用"文本块 + 输入框"叠放、按 IsXxxEditing 切可见性，两边各司其职。

        private void OnCellEditKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box || box.DataContext is not PointViewModel point)
            {
                return;
            }

            if (e.Key == Key.Enter)
            {
                ApplyCellEdit(point);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                point.CancelEdit();
                PointDataGrid?.Focus();
                e.Handled = true;
            }
        }

        private void OnCellEditLostFocus(object sender, RoutedEventArgs e)
        {
            // 回车提交后 EditingField 已是 None，这里的 CommitEdit 直接返回，天然幂等
            if (sender is TextBox box && box.DataContext is PointViewModel point)
            {
                ApplyCellEdit(point);
            }
        }

        private void ApplyCellEdit(PointViewModel point)
        {
            if (point.EditingField == PointField.None)
            {
                return;
            }

            if (!point.CommitEdit())
            {
                ViewModel?.ReportEditRejected();
            }

            PointDataGrid?.Focus();
        }

        /// <summary>
        /// 滚轮调值 / 调品质。
        ///
        /// 只有"当前值"和"品质"两列吃滚轮，其它列照常让列表滚动——
        /// 否则一张点表里到处滚一下就改数据，很容易误操作。
        ///
        /// 同样挂在表格的隧道阶段：单元格内容区（Grid）没有背景不参与命中测试，
        /// 滚轮落在文字右侧的空白处时事件源是单元格边框，挂在 TextBlock 上的处理器收不到。
        /// </summary>
        private void OnPointGridWheelChanged(object sender, PointerWheelEventArgs e)
        {
            int direction = Math.Sign(e.Delta.Y);
            if (direction == 0)
            {
                return;
            }

            Control cell = FindTaggedCell(e.Source as Visual);
            if (cell?.DataContext is not PointViewModel point || cell.Tag is not string tag)
            {
                return;
            }

            if (tag == "quality")
            {
                point.NudgeQuality(direction);
            }
            else if (tag == "value")
            {
                point.NudgeValue(direction, (e.KeyModifiers & KeyModifiers.Shift) != 0);
            }
            else
            {
                // 点号/名称这些列不吃滚轮，交回给表格正常纵向滚动
                return;
            }

            e.Handled = true;
        }
    }
}
