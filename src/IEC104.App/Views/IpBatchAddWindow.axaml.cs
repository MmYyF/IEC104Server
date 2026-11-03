using Avalonia.Controls;
using Avalonia.Interactivity;
using IEC104.App.ViewModels;

namespace IEC104.App.Views
{
    /// <summary>
    /// 批量添加 IP 地址（从 IP 地址管理进来）。
    ///
    /// 代码后置只做两件事：建 ViewModel、关窗。动作全在
    /// <see cref="IpBatchAddViewModel"/> 里，自检可以不点按钮直接调。
    /// </summary>
    public partial class IpBatchAddWindow : DialogWindow
    {
        public IpBatchAddWindow()
        {
            InitializeComponent();
        }

        public IpBatchAddViewModel ViewModel => DataContext as IpBatchAddViewModel;

        /// <summary>真的加进去了几个，调用方据此决定要不要刷新</summary>
        public int AddedCount => ViewModel?.AddedCount ?? 0;

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    }
}
