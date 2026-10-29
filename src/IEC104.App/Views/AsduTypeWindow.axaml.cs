using Avalonia.Controls;
using Avalonia.Interactivity;
using IEC104.App.ViewModels;

namespace IEC104.App.Views
{
    /// <summary>
    /// 新增 / 编辑自定义 ASDU 类型。
    ///
    /// 只有"确定"时校验，校验通过才置 Confirmed —— 和点表编辑、
    /// 从站编辑几个对话框保持同一套约定，调用方只看 Confirmed 决定要不要落地。
    /// </summary>
    public partial class AsduTypeWindow : DialogWindow
    {
        public AsduTypeWindow()
        {
            InitializeComponent();
        }

        public bool Confirmed { get; private set; }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not AsduTypeEditModel model)
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

            Confirmed = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            Close();
        }
    }
}
