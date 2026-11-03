using Avalonia.Controls;
using Avalonia.Interactivity;
using IEC104.App.ViewModels;

namespace IEC104.App.Views
{
    /// <summary>
    /// IP 地址管理窗口（工具 → IP 地址管理）。
    ///
    /// 代码后置只做两件事：建 ViewModel、关窗。所有动作都在
    /// <see cref="IpManagerViewModel"/> 里，这样自检可以不点按钮直接调。
    /// </summary>
    public partial class IpManagerWindow : DialogWindow
    {
        public IpManagerWindow()
        {
            InitializeComponent();
            DataContext = new IpManagerViewModel();
        }

        public IpManagerViewModel ViewModel => DataContext as IpManagerViewModel;

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    }
}
