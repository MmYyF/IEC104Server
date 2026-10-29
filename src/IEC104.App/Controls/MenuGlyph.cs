using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace IEC104.App.Controls
{
    /// <summary>
    /// 菜单/工具条用的线条图标控件：把 24×24 坐标里的描边几何，
    /// 按**固定画布**缩放到控件尺寸（而不是按几何自身的墨迹包围盒缩放）。
    ///
    /// 为什么不用 Path + Stretch=Uniform：
    ///   Path 的 Uniform 是"把墨迹包围盒撑满控件框"。几何宽高比不一样时，
    ///   同一批图标的缩放比就各不相同 —— 10×10 的方块被放大 1.6 倍、
    ///   19×12 的眼睛只放大 0.84 倍，结果就是"描边粗细不一、看着也不齐"。
    ///   这正是用户说的"这些线条图标看着比较糊，而且没有对齐"里"没对齐"的那一半；
    ///   "糊"的一半是缩放后线宽只有 1 个物理像素左右（见 AppTheme 里的描边注释）。
    ///
    /// 做法：统一按 20 个坐标单位（画布 24 居中取 20，见 CanvasWindow）等比缩放。
    /// 所有图标因此共用同一个比例尺：
    ///   · 描边粗细天然一致（同一 StrokeThickness 出来就是同一个线宽）；
    ///   · 圆心/重心都在 24 的中心，缩放到控件中心 → 垂直水平都对齐；
    ///   · 宽扁的图标（眼睛、波形）保持它本来的宽扁比例，不会被强行撑成方块。
    /// </summary>
    public class MenuGlyph : Control
    {
        /// <summary>几何的坐标系边长（所有图标的 Data 都按 24×24 画）。</summary>
        private const double DesignUnits = 24.0;

        /// <summary>
        /// 实际参与缩放的窗口边长。
        /// 24 的框里图标墨迹一般在 3~21（18 个单位），取 20 做窗口，
        /// 四周各留 1 个单位的呼吸位：既不会贴边，又不会缩得太小。
        /// </summary>
        private const double CanvasWindow = 20.0;

        public static readonly StyledProperty<Geometry> DataProperty =
            AvaloniaProperty.Register<MenuGlyph, Geometry>(nameof(Data));

        public static readonly StyledProperty<IBrush> StrokeProperty =
            AvaloniaProperty.Register<MenuGlyph, IBrush>(nameof(Stroke));

        public static readonly StyledProperty<double> StrokeThicknessProperty =
            AvaloniaProperty.Register<MenuGlyph, double>(nameof(StrokeThickness), 2.0);

        public static readonly StyledProperty<IBrush> FillProperty =
            AvaloniaProperty.Register<MenuGlyph, IBrush>(nameof(Fill));

        static MenuGlyph()
        {
            AffectsRender<MenuGlyph>(DataProperty, StrokeProperty, StrokeThicknessProperty, FillProperty);
            AffectsMeasure<MenuGlyph>(WidthProperty, HeightProperty);
        }

        /// <summary>图标几何（24×24 坐标系）。</summary>
        public Geometry Data
        {
            get => GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }

        /// <summary>描边画刷。</summary>
        public IBrush Stroke
        {
            get => GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        /// <summary>描边宽度，单位是 24×24 坐标系里的单位（不是显示像素）。</summary>
        public double StrokeThickness
        {
            get => GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        /// <summary>填充画刷（只有实心图标才用，比如闪电、播放三角）。</summary>
        public IBrush Fill
        {
            get => GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        /// <summary>没有显式尺寸时给一个菜单常用的默认值，避免量成 0。</summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            double width = double.IsNaN(Width) ? 16 : Width;
            double height = double.IsNaN(Height) ? 16 : Height;
            return new Size(width, height);
        }

        public override void Render(DrawingContext context)
        {
            Geometry data = Data;
            if (data == null)
            {
                return;
            }

            double width = Bounds.Width;
            double height = Bounds.Height;
            double side = Math.Min(width, height);
            if (side <= 0)
            {
                return;
            }

            double scale = side / CanvasWindow;
            // 缩放后再把"窗口"的左下角从 (2,2) 挪到控件原点；
            // 控件不是正方形时，多出的那一维居中。
            double offset = (DesignUnits - CanvasWindow) / 2.0;
            double dx = (width - side) / 2.0 - offset * scale;
            double dy = (height - side) / 2.0 - offset * scale;

            Pen pen = Stroke != null && StrokeThickness > 0
                ? new Pen(Stroke, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)
                : null;

            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
            {
                context.DrawGeometry(Fill, pen, data);
            }
        }
    }
}
