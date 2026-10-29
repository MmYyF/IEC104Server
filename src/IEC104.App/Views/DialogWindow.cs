using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace IEC104.App.Views
{
    /// <summary>
    /// 所有弹窗的基类：打开时把窗口尺寸夹进当前屏幕的工作区。
    ///
    /// 为什么需要它 —— 小屏笔记本（1366×768，或 1080p 开了 150% 缩放后逻辑高度只剩 720）
    /// 上，一个写死 <c>Height="900"</c> 的配置窗口底边会落到屏幕外，
    /// 而"确定"按钮正好在底边，用户既点不到也没法滚。这不是内容多不多的问题，
    /// 是窗口根本比屏幕高 —— 所以必须在窗口层解决，光加 ScrollViewer 没用。
    ///
    /// 两条约束一起成立才管用：
    ///   ① 窗口不超工作区（本类负责）；
    ///   ② 内容区可滚动且按钮在固定行（各弹窗的 Grid RowDefinitions 负责）。
    ///
    /// 只在 Opened 里夹一次，不改写各窗口自己声明的 Width/Height ——
    /// 屏幕够大时它们仍按设计尺寸显示，尺寸偏好不被这个兜底逻辑吞掉。
    /// </summary>
    public class DialogWindow : Window
    {
        /// <summary>窗口最多占工作区宽度的比例。留出一点边，免得贴着屏幕边缘。</summary>
        private const double MaxWidthRatio = 0.96;

        /// <summary>窗口最多占工作区高度的比例。留出标题栏与任务栏的余量。</summary>
        private const double MaxHeightRatio = 0.92;

        public DialogWindow()
        {
            Opened += OnDialogOpened;
        }

        private void OnDialogOpened(object sender, EventArgs e)
        {
            FitToWorkArea();

            // 尺寸定下来之后位置还要再夹一次：WindowStartupLocation="CenterOwner"
            // 是按母窗口中心算的，母窗口靠上时子窗口会被算到负坐标，
            // 结果标题栏跑到屏幕外（用户看到的就是"窗口顶部缺一截"）。
            // 放到 Loaded 优先级，等布局把最终尺寸算完再动位置。
            Dispatcher.UIThread.Post(ClampToWorkArea, DispatcherPriority.Loaded);
        }

        /// <summary>
        /// 把窗口整体移进工作区（只在超出时才动，正常情况下不改位置）。
        /// Position 是物理像素、ClientSize 是 DIP，必须换算后再比。
        /// </summary>
        public void ClampToWorkArea()
        {
            Screen screen = Screens?.ScreenFromWindow(this) ?? Screens?.Primary;
            if (screen == null)
            {
                return;
            }

            double scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            PixelRect work = screen.WorkingArea;

            int pixelWidth = (int)Math.Round(ClientSize.Width * scaling);
            int pixelHeight = (int)Math.Round(ClientSize.Height * scaling);
            int x = Position.X;
            int y = Position.Y;

            if (x + pixelWidth > work.Right)
            {
                x = work.Right - pixelWidth;
            }

            if (y + pixelHeight > work.Bottom)
            {
                y = work.Bottom - pixelHeight;
            }

            // 先夹右下再夹左上：窗口比工作区还大时，左上优先（至少标题栏和顶部内容可见）。
            if (x < work.X)
            {
                x = work.X;
            }

            if (y < work.Y)
            {
                y = work.Y;
            }

            if (x != Position.X || y != Position.Y)
            {
                Position = new PixelPoint(x, y);
            }
        }

        /// <summary>
        /// 按工作区夹紧尺寸。幂等，可以重复调用（布局改完后再夹一次也无害）。
        /// </summary>
        public void FitToWorkArea()
        {
            Screen screen = Screens?.ScreenFromWindow(this) ?? Screens?.Primary;
            if (screen == null)
            {
                return;
            }

            double scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            PixelRect work = screen.WorkingArea;

            // WorkingArea 是物理像素，窗口的 Width/Height 是 DIP，必须换算，
            // 否则 150% 缩放下算出来的上限会大出一半，等于没夹。
            double workWidth = work.Width / scaling;
            double workHeight = work.Height / scaling;

            double maxWidth = Math.Max(320, workWidth * MaxWidthRatio);
            double maxHeight = Math.Max(240, workHeight * MaxHeightRatio);

            // 先让 Max 生效，再压 Min：Min 大于 Max 时 Avalonia 会按 Min 撑开，
            // 那就又超屏了。所以 Min 必须跟着 Max 往下走。
            MaxWidth = maxWidth;
            MaxHeight = maxHeight;

            if (MinWidth > maxWidth)
            {
                MinWidth = maxWidth;
            }

            if (MinHeight > maxHeight)
            {
                MinHeight = maxHeight;
            }

            // 固定尺寸的窗口要主动缩：MaxWidth/MaxHeight 对显式 Width/Height 的截断
            // 在部分平台上不可靠（会先按 Width 布局再被裁），直接改值最稳。
            if (!double.IsNaN(Width) && Width > maxWidth)
            {
                Width = maxWidth;
            }

            if (!double.IsNaN(Height) && Height > maxHeight)
            {
                Height = maxHeight;
            }
        }
    }
}
