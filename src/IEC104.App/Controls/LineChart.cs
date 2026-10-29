using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace IEC104.App.Controls
{
    /// <summary>一条曲线的数据与样式</summary>
    public sealed class ChartSeries
    {
        public ChartSeries(string name, Color color, IReadOnlyList<double> values, string unit = "")
        {
            Name = name;
            Color = color;
            Values = values;
            Unit = unit;
        }

        public string Name { get; }
        public Color Color { get; }
        public IReadOnlyList<double> Values { get; }
        public string Unit { get; }
    }

    /// <summary>
    /// 轻量实时折线图。
    ///
    /// 复刻说明：原程序实时曲线用 ScottPlot 5.0.56（含 ScottPlot.Avalonia）。
    /// 这里没有引第三方绘图库，直接重写 Render 用 DrawingContext 画——
    /// 需求只有"看最近若干采样点的走势"，引入一整套绘图库反而增加体积和版本风险。
    ///
    /// 画法：先算所有曲线的全局 Y 范围（留 10% 余量），再按样本序号横排布点。
    /// </summary>
    public class LineChart : Control
    {
        // 必须是 AvaloniaProperty 才能被 XAML 的 Binding 赋值；
        // 普通 CLR 属性在 XamlIl 编译期会被判成“找不到合适的 setter”。
        public static readonly StyledProperty<IReadOnlyList<ChartSeries>> SeriesProperty =
            AvaloniaProperty.Register<LineChart, IReadOnlyList<ChartSeries>>(nameof(Series));

        public static readonly StyledProperty<int> MaxPointsProperty =
            AvaloniaProperty.Register<LineChart, int>(nameof(MaxPoints), 120);

        private IReadOnlyList<ChartSeries> _series = Array.Empty<ChartSeries>();
        private int _maxPoints = 120;

        /// <summary>曲线数据；赋值后自动重绘</summary>
        public IReadOnlyList<ChartSeries> Series
        {
            get => GetValue(SeriesProperty) ?? Array.Empty<ChartSeries>();
            set => SetValue(SeriesProperty, value);
        }

        /// <summary>X 轴最大采样点（不足时按实际数量铺满）</summary>
        public int MaxPoints
        {
            get => GetValue(MaxPointsProperty);
            set => SetValue(MaxPointsProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == SeriesProperty || change.Property == MaxPointsProperty)
            {
                _series = Series;
                _maxPoints = Math.Max(2, MaxPoints);
                InvalidateVisual();
            }
        }

        private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#EDEFF2"));
        private static readonly IBrush AxisBrush = new SolidColorBrush(Color.Parse("#C9CDD4"));
        private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#6B7280"));
        private static readonly IBrush BackBrush = Brushes.White;

        public override void Render(DrawingContext context)
        {
            Rect bounds = new(Bounds.Size);
            context.FillRectangle(BackBrush, bounds);

            // 绘图区（左侧留给 Y 轴刻度，底部留给时间轴）
            // padTop 留 34：图例画在绘图区上方的独立带上，避免压在曲线尖峰上
            const double padLeft = 56;
            const double padRight = 12;
            const double padTop = 34;
            const double padBottom = 26;

            double plotWidth = bounds.Width - padLeft - padRight;
            double plotHeight = bounds.Height - padTop - padBottom;
            if (plotWidth <= 10 || plotHeight <= 10)
            {
                return;
            }

            Rect plot = new(padLeft, padTop, plotWidth, plotHeight);

            // ── 计算 Y 范围 ──
            double min = double.MaxValue;
            double max = double.MinValue;
            int sampleCount = 0;

            foreach (ChartSeries series in _series)
            {
                sampleCount = Math.Max(sampleCount, series.Values.Count);
                foreach (double value in series.Values)
                {
                    if (double.IsNaN(value) || double.IsInfinity(value))
                    {
                        continue;
                    }
                    min = Math.Min(min, value);
                    max = Math.Max(max, value);
                }
            }

            if (min == double.MaxValue)
            {
                min = 0;
                max = 1;
            }
            if (Math.Abs(max - min) < 1e-9)
            {
                // 常量序列：给一个人为的上下沿，否则线会贴在边框上
                max += 1;
                min -= 1;
            }
            else
            {
                double margin = (max - min) * 0.1;
                max += margin;
                min -= margin;
            }

            // ── 网格与 Y 轴刻度 ──
            const int gridLines = 4;
            for (int i = 0; i <= gridLines; i++)
            {
                double ratio = i / (double)gridLines;
                double y = plot.Bottom - plot.Height * ratio;
                context.DrawLine(new Pen(GridBrush, 1), new Point(plot.Left, y), new Point(plot.Right, y));

                double value = min + (max - min) * ratio;
                DrawText(context, FormatValue(value), new Point(6, y - 8), 11, TextBrush);
            }

            // 边框
            context.DrawRectangle(null, new Pen(AxisBrush, 1), plot);

            // ── 曲线 ──
            int capacity = Math.Max(_maxPoints, sampleCount);
            foreach (ChartSeries series in _series)
            {
                if (series.Values.Count < 2)
                {
                    continue;
                }

                StreamGeometry geometry = new();
                using (StreamGeometryContext ctx = geometry.Open())
                {
                    bool started = false;
                    // 右对齐：最新的点永远在最右边，数据不足时左侧留白
                    int offset = capacity - series.Values.Count;
                    for (int i = 0; i < series.Values.Count; i++)
                    {
                        double value = series.Values[i];
                        if (double.IsNaN(value) || double.IsInfinity(value))
                        {
                            continue;
                        }

                        double x = plot.Left + plot.Width * ((offset + i) / (double)Math.Max(1, capacity - 1));
                        double y = plot.Bottom - plot.Height * ((value - min) / (max - min));
                        Point point = new(x, y);

                        if (!started)
                        {
                            ctx.BeginFigure(point, false);
                            started = true;
                        }
                        else
                        {
                            ctx.LineTo(point);
                        }
                    }

                    if (started)
                    {
                        ctx.EndFigure(false);
                    }
                }

                context.DrawGeometry(null, new Pen(new SolidColorBrush(series.Color), 1.6), geometry);
            }

            // ── 图例（画在绘图区上方的独立带里，不遮挡曲线）──
            double legendX = plot.Left + 2;
            double legendY = 10;
            foreach (ChartSeries series in _series)
            {
                context.FillRectangle(new SolidColorBrush(series.Color), new Rect(legendX, legendY + 5, 12, 3));
                DrawText(context, series.Name, new Point(legendX + 17, legendY - 1), 11, TextBrush);
                legendX += 17 + MeasureText(series.Name, 11) + 16;
            }

            // 空态提示
            if (sampleCount == 0)
            {
                DrawText(context, "点击“开始采样”后，这里显示选中点位的实时曲线",
                    new Point(plot.Left + 12, plot.Top + plot.Height / 2), 12, TextBrush);
            }
        }

        private static string FormatValue(double value)
        {
            double abs = Math.Abs(value);
            if (abs >= 100000)
            {
                return value.ToString("0.0E+0", CultureInfo.InvariantCulture);
            }
            if (abs >= 100)
            {
                return value.ToString("0", CultureInfo.InvariantCulture);
            }
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static void DrawText(DrawingContext context, string text, Point origin,
            double fontSize, IBrush brush)
        {
            FormattedText formatted = new(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI, Segoe UI"),
                fontSize,
                brush);
            context.DrawText(formatted, origin);
        }

        private static double MeasureText(string text, double fontSize)
        {
            FormattedText formatted = new(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI, Segoe UI"),
                fontSize,
                TextBrush);
            return formatted.Width;
        }
    }
}
