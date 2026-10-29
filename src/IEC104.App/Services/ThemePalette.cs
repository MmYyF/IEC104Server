using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace IEC104.App.Services
{
    /// <summary>
    /// ViewModel 层取画刷的统一入口。
    ///
    /// 为什么要有这一层：样式里的颜色可以靠 DynamicResource 自动跟着主题刷新，
    /// 但 ViewModel 里缓存成字段的画刷（品质徽标、状态灯、值着色）不会——
    /// 它们是在第一次访问时算好就存着的。所以这里按令牌名从 Application 资源里取，
    /// 换主题时 ThemeService 会调 Invalidate() 清缓存，下一次取值就是新色。
    ///
    /// 缓存的意义：上千点位、每秒刷新一遍，每次都走 Avalonia 的资源查找太浪费。
    /// </summary>
    public static class ThemePalette
    {
        private static readonly Dictionary<string, IBrush> Cache = new();

        private static readonly IBrush Fallback = new SolidColorBrush(Colors.Gray);

        /// <summary>主题切换后必须调用，否则 ViewModel 里还是旧画刷</summary>
        public static void Invalidate() => Cache.Clear();

        public static IBrush Get(string key)
        {
            if (Cache.TryGetValue(key, out IBrush cached))
            {
                return cached;
            }

            IBrush brush = Fallback;
            if (Application.Current != null
                && Application.Current.TryFindResource(key, out object resource)
                && resource is IBrush found)
            {
                brush = found;
            }

            Cache[key] = brush;
            return brush;
        }

        // 值着色
        public static IBrush ValueOn => Get("ValueOnBrush");

        public static IBrush ValueOff => Get("ValueOffBrush");

        public static IBrush ValueMid => Get("ValueMidBrush");

        public static IBrush ValueBad => Get("ValueBadBrush");

        public static IBrush ValueNeutral => Get("ValueNeutralBrush");

        // 品质徽标
        public static IBrush QualityOkBg => Get("QualityOkBgBrush");

        public static IBrush QualityOkFg => Get("QualityOkFgBrush");

        public static IBrush QualityWarnBg => Get("QualityWarnBgBrush");

        public static IBrush QualityWarnFg => Get("QualityWarnFgBrush");

        public static IBrush QualityBadBg => Get("QualityBadBgBrush");

        public static IBrush QualityBadFg => Get("QualityBadFgBrush");

        // 通道状态灯
        public static IBrush StateConnected => Get("SuccessBrush");

        public static IBrush StateListening => Get("WarningBrush");

        public static IBrush StateStopped => Get("TextDisabledBrush");

        public static IBrush StateConnectedHalo => Get("SuccessSoftBrush");

        public static IBrush StateListeningHalo => Get("WarningSoftBrush");

        public static IBrush StateStoppedHalo => Get("DividerBrush");

        // ── 从站标签（扁平视图）──
        //
        // 状态灯的"实心色"适合 8px 的圆点，铺到一整行标签上就太重了——
        // 一屏十几个从站全是大色块，反而看不出哪条在报警。
        // 所以标签走"浅底 + 同色深字"这一对，跟状态栏健康度胶囊是同一套配法
        // （见 MainWindowViewModel.HealthSoftBrush / HealthForeground），
        // 这两组配对在 6 套主题里都验过对比度，深色主题下也不会出现浅底浅字。

        /// <summary>从站标签的填充色（浅）</summary>
        public static IBrush StateConnectedSoft => Get("SuccessSoftBrush");

        public static IBrush StateListeningSoft => Get("WarningSoftBrush");

        /// <summary>已停止没有"浅色系"可用（灰没有 soft 令牌），用次级表面色，读作中性</summary>
        public static IBrush StateStoppedSoft => Get("SurfaceAltBrush");

        /// <summary>从站标签上文字/描边的颜色（深，与上面三个浅底成对）</summary>
        public static IBrush StateConnectedText => Get("SuccessTextBrush");

        public static IBrush StateListeningText => Get("WarningTextBrush");

        public static IBrush StateStoppedText => Get("TextSecondaryBrush");
    }
}
