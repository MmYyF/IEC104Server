using Avalonia.Controls;

namespace IEC104.App.Views
{
    /// <summary>
    /// 链路参数 + 报文长度面板。
    ///
    /// 只负责显示与绑定，值都在 <see cref="ViewModels.LinkParametersModel"/> 里，
    /// 校验也在那边——面板本身不做任何判断，这样四个调用点的行为必然一致。
    /// </summary>
    public partial class LinkParametersPanel : UserControl
    {
        public LinkParametersPanel()
        {
            InitializeComponent();
        }

        // 自检要按名字拿到这三个下拉（它们在这个用户控件自己的命名作用域里，
        // 外层窗口的 FindControl 找不着），所以在这里开三个只读入口。
        internal ComboBox CauseOfTransmissionBox => this.FindControl<ComboBox>("CotLengthBox");

        internal ComboBox CommonAddressBox => this.FindControl<ComboBox>("CaLengthBox");

        internal ComboBox InformationObjectAddressBox => this.FindControl<ComboBox>("IoaLengthBox");
    }
}
