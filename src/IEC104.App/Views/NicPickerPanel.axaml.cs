using Avalonia.Controls;

namespace IEC104.App.Views
{
    /// <summary>
    /// 「监听地址 / 通信网卡 / 启动时自动添加」面板。
    ///
    /// 只负责显示与绑定，判断全在 <see cref="ViewModels.NicPickerModel"/> 里——
    /// 面板不做任何决策，三个调用点的行为就必然一致。
    /// </summary>
    public partial class NicPickerPanel : UserControl
    {
        public NicPickerPanel()
        {
            InitializeComponent();
        }

        // 自检要按名字拿到这个下拉。控件在本用户控件自己的命名作用域里，
        // 外层窗口的 FindControl 找不着，所以在这里开一个只读入口。
        // （名字不能叫 NicBox —— Avalonia 的 NameGenerator 会为 x:Name 生成同名字段，
        //   重名直接编译失败。）
        internal ComboBox NicComboBox => this.FindControl<ComboBox>("NicBox");
    }
}
