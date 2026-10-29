using Avalonia.Controls;
using Avalonia.Interactivity;
using IEC104.App.ViewModels;

namespace IEC104.App.Views
{
    /// <summary>
    /// 连接配置对话框。
    /// 复刻说明：对应原程序 Windows.Connection.CreateConnectionWindow / EditConnectionWindow
    /// （原实现新建与编辑分两个窗口，此处合并为一个，字段完全相同）。
    /// </summary>
    public partial class ConnectionWindow : DialogWindow
    {
        public ConnectionWindow()
        {
            InitializeComponent();
        }

        /// <summary>确认时是否保存</summary>
        public bool Confirmed { get; private set; }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ConnectionEditModel model)
            {
                Close();
                return;
            }

            string error = model.Validate();
            if (error != null)
            {
                // 直接在窗口内提示，避免关掉窗口后还要重新填一遍
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
        /// 点"本机地址"候选标签，把该 IP 填进监听 IP 输入框。
        ///
        /// 这里用事件而不是 Command 绑定，是因为候选按钮在 ItemsControl 的
        /// DataTemplate 里，DataContext 是字符串而不是 ViewModel；
        /// 要在模板里跨层拿 ViewModel 的命令，得用带类型转换的绑定，
        /// 而 XamlIl 在模板闭包里解析不到自定义命名空间前缀（实测会抛
        /// "Unable to resolve type vm:ConnectionEditModel"）。
        /// 走代码后置可以直接从窗口的 DataContext 取模型，最稳。
        /// </summary>
        private void OnUseAddressClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: string address }
                && DataContext is ConnectionEditModel model)
            {
                model.BindAddress = address;
            }
        }
    }
}
