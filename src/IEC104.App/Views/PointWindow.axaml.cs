using Avalonia.Controls;
using Avalonia.Interactivity;
using IEC104.App.ViewModels;

namespace IEC104.App.Views
{
    /// <summary>
    /// 信息对象（数据点）配置对话框。
    /// 复刻说明：对应原程序里每种 ASDU 类型各自的编辑界面
    /// （SinglePointInformationViewModel 等 60 余个），此处收敛为统一的类型下拉 + 值 + 品质。
    /// </summary>
    public partial class PointWindow : DialogWindow
    {
        public PointWindow()
        {
            InitializeComponent();
        }

        public bool Confirmed { get; private set; }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not PointEditModel model)
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
    }
}
