using Avalonia.Controls;
using Avalonia.Interactivity;
using IEC104.App.ViewModels;

namespace IEC104.App.Views
{
    /// <summary>
    /// 从站配置对话框。
    /// 复刻说明：对应原程序 Windows.Slave.CreateSlaveWindow / EditSlaveWindow。
    /// </summary>
    public partial class SlaveWindow : DialogWindow
    {
        public SlaveWindow()
        {
            InitializeComponent();
        }

        public bool Confirmed { get; private set; }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not SlaveEditModel model)
            {
                Close();
                return;
            }

            string error = model.Validate();
            if (error != null)
            {
                TextBlock errorText = this.FindControl<TextBlock>("ErrorText");
                if (errorText != null)
                {
                    errorText.Text = error;
                }
                return;
            }

            model.Apply();
            Confirmed = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            Close();
        }

        /// <summary>
        /// 点"本机地址"候选标签，填进新建通道的监听 IP。
        /// 与连接窗口同样的做法：候选按钮的 DataContext 是字符串本身，
        /// 用代码后置从窗口 DataContext 取模型，避免模板里跨层绑定。
        /// </summary>
        private void OnUseAddressClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: string address }
                && DataContext is SlaveEditModel model)
            {
                model.NewBindAddress = address;
            }
        }

        /// <summary>
        /// 点"本机地址"候选标签，填进**所属通道**的监听 IP（编辑模式那块）。
        /// 不能复用上面那个方法：两块 IP 是两个字段，填错了看着没反应。
        /// </summary>
        private void OnUseOwnerAddressClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: string address }
                && DataContext is SlaveEditModel model)
            {
                model.OwnerBindAddress = address;
            }
        }
    }
}
