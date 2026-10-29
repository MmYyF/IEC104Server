using System;
using System.Globalization;
using Avalonia.Data.Converters;
using IEC104.App.Services;

namespace IEC104.App.Infrastructure
{
    /// <summary>
    /// 把 <see cref="ChangePattern"/> 枚举转成中文显示名。
    ///
    /// 为什么需要：DataGrid 里的 ComboBox 直接绑枚举会显示 None/Sine/RandomWalk，
    /// 现场看不懂；加一层转换器就能让下拉框显示"不变/正弦波动/随机游走"。
    /// </summary>
    public sealed class ChangePatternNameConverter : IValueConverter
    {
        public static readonly ChangePatternNameConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is ChangePattern pattern ? pattern.DisplayName() : string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException("该转换器只用于显示，反向转换请直接用 SelectedItem 绑定。");
        }
    }
}
