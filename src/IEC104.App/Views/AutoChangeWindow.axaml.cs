using Avalonia.Controls;
using Avalonia.Interactivity;

namespace IEC104.App.Views
{
    /// <summary>
    /// 缓变配置窗口。
    /// 复刻说明：对应原程序 Windows.EaseFunctionConfigWindow。
    /// </summary>
    public partial class AutoChangeWindow : DialogWindow
    {
        public AutoChangeWindow()
        {
            InitializeComponent();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    }
}
