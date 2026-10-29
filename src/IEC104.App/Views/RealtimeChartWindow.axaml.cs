using Avalonia.Controls;
using Avalonia.Interactivity;
using IEC104.App.ViewModels;

namespace IEC104.App.Views
{
    /// <summary>
    /// 实时曲线窗口。
    /// 复刻说明：对应原程序 Windows.RealtimeChartingWindow。
    /// </summary>
    public partial class RealtimeChartWindow : DialogWindow
    {
        public RealtimeChartWindow()
        {
            InitializeComponent();
        }

        private RealtimeChartViewModel ViewModel => DataContext as RealtimeChartViewModel;

        private void OnToggleClick(object sender, RoutedEventArgs e)
        {
            ViewModel?.Toggle();
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            ViewModel?.ClearHistory();
        }

        /// <summary>
        /// 勾选后立刻刷新一次曲线，
        /// 否则用户点了勾还要等一个采样周期才看到反应，像是没生效。
        /// </summary>
        private void OnItemCheckClick(object sender, RoutedEventArgs e)
        {
            ViewModel?.RefreshSeries();
        }

        protected override void OnClosed(System.EventArgs e)
        {
            // 窗口关掉必须停表，不然定时器会一直往已关闭的窗口推数据
            ViewModel?.Stop();
            base.OnClosed(e);
        }
    }
}
