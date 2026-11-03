using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using IEC104.App.ViewModels;

namespace IEC104.App.Views
{
    /// <summary>
    /// IP 地址管理窗口（工具 → IP 地址管理）。
    ///
    /// 代码后置只做三件事：建 ViewModel、弹批量添加、关窗。所有动作都在
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

        private async void OnBatchAddClick(object sender, RoutedEventArgs e)
        {
            IpManagerViewModel manager = ViewModel;
            if (manager?.SelectedNic == null)
            {
                return;
            }

            IpBatchAddWindow window = new()
            {
                DataContext = new IpBatchAddViewModel(manager.SelectedNic)
            };

            await window.ShowDialog(this);

            // 加完回来必须重读一遍网卡：界面要立刻显示刚加进去的那些地址
            if (window.AddedCount > 0)
            {
                manager.ReportBatchResult($"已添加 {window.AddedCount} 个地址。");
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    }
}
