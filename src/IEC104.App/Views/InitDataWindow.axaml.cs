using Avalonia.Controls;
using Avalonia.Interactivity;
using IEC104.App.ViewModels;

namespace IEC104.App.Views
{
    /// <summary>
    /// 初始化数据窗口。
    /// 复刻说明：对应原程序 Windows.InitDataWindow。
    /// </summary>
    public partial class InitDataWindow : DialogWindow
    {
        public InitDataWindow()
        {
            InitializeComponent();
        }

        private void OnGenerateClick(object sender, RoutedEventArgs e)
        {
            (DataContext as InitDataViewModel)?.Generate();
        }

        private void OnContinueClick(object sender, RoutedEventArgs e)
        {
            (DataContext as InitDataViewModel)?.ContinueFromMaxAddress();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    }
}
