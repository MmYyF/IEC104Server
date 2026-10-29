using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using IEC104.App.ViewModels;

namespace IEC104.App.Views
{
    /// <summary>
    /// 通信报文监视窗口。
    /// 复刻说明：对应原程序 Windows.Connection.CommunicatingTrafficWindow。
    ///
    /// 本窗口是**非模态**的（由主窗口 Show 打开，不是 ShowDialog）：
    /// 现场要一边盯报文一边在主界面改点，模态会把主窗口锁死。
    /// 标题栏自绘，右侧那颗"钉子"控制置顶，钉住后可以拖到副屏长期挂着。
    /// </summary>
    public partial class TrafficWindow : DialogWindow
    {
        /// <summary>
        /// 置顶状态的跨次记忆。
        /// 用静态字段而不是落到配置文件：这个偏好只对本次运行有意义，
        /// 下次开工具重新决定即可，没必要污染工作区文件。
        /// </summary>
        private static bool s_pinned;

        public TrafficWindow()
        {
            InitializeComponent();

            // 恢复上一次的钉子状态
            SetPinned(s_pinned);
        }

        private TrafficViewModel ViewModel => DataContext as TrafficViewModel;

        /// <summary>当前是否被钉子钉住（自检用）</summary>
        public bool IsPinned => Topmost;

        /// <summary>钉子按钮的视觉状态（自检用）</summary>
        public bool IsPinHighlighted => PinButton.Classes.Contains("pinned");

        /// <summary>
        /// 设置 / 取消置顶。按钮和自检都走这里，避免两套逻辑各自跑偏。
        /// </summary>
        public void SetPinned(bool pinned)
        {
            Topmost = pinned;
            s_pinned = pinned;

            PinButton.Classes.Set("pinned", pinned);
            ToolTip.SetTip(PinButton, pinned ? "已置顶，点击取消" : "置顶窗口");
        }

        private void OnPinClick(object sender, RoutedEventArgs e) => SetPinned(!Topmost);

        // ── 自绘标题栏的三件事：拖动、双击最大化、三个系统按钮 ──

        private void OnTitleBarPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }

            // 点在按钮上时不要当作拖动，否则点钉子窗体会跟着跑
            if ((e.Source as Visual)?.FindAncestorOfType<Button>() != null)
            {
                return;
            }

            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            BeginMoveDrag(e);
        }

        private void OnMinimizeClick(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        private void ToggleMaximize() =>
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        private void OnClearClick(object sender, RoutedEventArgs e) => ViewModel?.Clear();

        protected override void OnClosed(System.EventArgs e)
        {
            // 退订源集合事件，避免窗口关了还被报文事件揪着不放（内存泄漏）
            ViewModel?.Detach();
            base.OnClosed(e);
        }
    }
}
