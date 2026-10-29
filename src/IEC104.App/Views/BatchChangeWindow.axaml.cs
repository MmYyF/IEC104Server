using Avalonia.Controls;
using Avalonia.Interactivity;
using IEC104.App.ViewModels;

namespace IEC104.App.Views
{
    /// <summary>
    /// 批量变化数据点窗口。
    ///
    /// 用途：按固定节拍从选中的一批点位里随机挑 N 个改值，
    /// 验证主站在"每秒几百个变位"下的接收与处理能力。
    /// 支持随机范围、固定步长两种取值方式，遥测遥信都覆盖。
    /// </summary>
    public partial class BatchChangeWindow : DialogWindow
    {
        public BatchChangeWindow()
        {
            InitializeComponent();
        }

        private BatchChangeViewModel ViewModel => DataContext as BatchChangeViewModel;

        private void OnSelectAllClick(object sender, RoutedEventArgs e) =>
            ViewModel?.SelectAllCommand.Execute(null);

        private void OnClearAllClick(object sender, RoutedEventArgs e) =>
            ViewModel?.ClearAllCommand.Execute(null);

        private void OnInvertClick(object sender, RoutedEventArgs e) =>
            ViewModel?.InvertCommand.Execute(null);

        private void OnSelectMeasuredClick(object sender, RoutedEventArgs e) =>
            ViewModel?.SelectMeasuredCommand.Execute(null);

        private void OnSelectSwitchClick(object sender, RoutedEventArgs e) =>
            ViewModel?.SelectSwitchCommand.Execute(null);

        private void OnItemCheckClick(object sender, RoutedEventArgs e) =>
            ViewModel?.RefreshSelection();

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(System.EventArgs e)
        {
            // 关窗即停：不能让定时器在窗口没了之后还继续往从站写值
            ViewModel?.Dispose();
            base.OnClosed(e);
        }
    }
}
