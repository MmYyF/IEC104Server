using Avalonia.Controls;
using Avalonia.Interactivity;
using IEC104.App.ViewModels;

namespace IEC104.App.Views
{
    /// <summary>
    /// 批量创建从站。每个从站独占一条通道（IP 不同，一条监听只能绑一个端点）。
    /// </summary>
    public partial class BatchCreateSlaveWindow : DialogWindow
    {
        public BatchCreateSlaveWindow()
        {
            InitializeComponent();
        }

        /// <summary>本次实际创建的数量</summary>
        public int CreatedCount { get; private set; }

        private void OnCreateClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not BatchCreateSlaveViewModel model)
            {
                Close();
                return;
            }

            if (model.Preview.Count == 0)
            {
                TextBlock result = this.FindControl<TextBlock>("ResultText");
                if (result != null)
                {
                    result.Text = model.ErrorText ?? "没有可创建的行。";
                }
                return;
            }

            // 创建动作由主窗口 VM 完成（它持有 Connections 集合与 Hook 逻辑），
            // 这里通过委托交出去，避免窗口直接改全局状态
            CreatedCount = model.Create(ViewModelConnections);
            Close();
        }

        /// <summary>由主窗口在打开窗口前注入的连接集合</summary>
        public System.Collections.Generic.IList<ConnectionViewModel> ViewModelConnections { get; set; }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            CreatedCount = 0;
            Close();
        }
    }
}
