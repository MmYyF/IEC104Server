using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace IEC104.App.Views
{
    /// <summary>
    /// 圆圈问号提示。悬停显示 <see cref="Tip"/>，界面上只占 18×18。
    ///
    /// 做成控件而不是每处手写 Border + ToolTip：手写时总有人忘记挂 ToolTip，
    /// 最后又退化成"一排小字说明"。这里把"必定带说明"固化进类型，XAML 里只有 Tip。
    ///
    /// 外形在 HintIcon.axaml 里画死，不走全局样式 —— 踩过：Button 派生的控件
    /// 匹配不到 `Button.hintIcon` 那组 Setter，改了样式没有任何反应。
    /// </summary>
    public partial class HintIcon : UserControl
    {
        /// <summary>悬停时显示的说明文字。</summary>
        public static readonly StyledProperty<string> TipProperty =
            AvaloniaProperty.Register<HintIcon, string>(nameof(Tip), string.Empty);

        public HintIcon()
        {
            InitializeComponent();

            // 说明是辅助信息，首次悬停就该出来；默认 400ms 让人觉得"没反应"。
            ToolTip.SetShowDelay(this, 200);
            Cursor = new Cursor(StandardCursorType.Help);
        }

        public string Tip
        {
            get => GetValue(TipProperty);
            set => SetValue(TipProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == TipProperty)
            {
                ToolTip.SetTip(this, BuildTip(Tip));
            }
        }

        /// <summary>
        /// 把说明包成一个会换行的 TextBlock 再交给 ToolTip。
        ///
        /// 直接给字符串是不行的：ToolTip 内部会用一个默认 TextBlock 显示，
        /// 而它 **不换行**，长说明就被截成一行、右边直接看不到（要说的正是"别把话堆成一行"，
        /// 结果提示自己先犯了这个毛病）。给控件才谈得上控制换行与宽度。
        /// </summary>
        private static Control BuildTip(string text)
        {
            return new TextBlock
            {
                Text = text ?? string.Empty,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300,
                LineHeight = 19
            };
        }
    }
}
