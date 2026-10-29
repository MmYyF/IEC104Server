using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace IEC104.App.Services
{
    /// <summary>一套可选主题的元信息（真正的颜色在 Styles/Themes/*.axaml 里）</summary>
    public sealed class AppThemeOption
    {
        public string Id { get; init; }

        public string Name { get; init; }

        /// <summary>Styles/Themes 下的文件名（不含扩展名）</summary>
        public string FileName { get; init; }

        public bool IsDark { get; init; }

        public string Hint { get; init; }

        /// <summary>
        /// 主题选择弹窗里那颗圆点的颜色。
        /// 不是 PrimaryBrush 的直接拷贝：三套浅色主题的主色都是蓝，
        /// 摆在一起六个点全是蓝的分不出来。这里取各主题的"代表性色相"，
        /// 只用于色块预览，界面实际用色仍以主题字典里的令牌为准。
        /// </summary>
        public string Swatch { get; init; }
    }

    /// <summary>
    /// 运行时主题切换。
    ///
    /// 做法：把每套主题各写成一个 ResourceDictionary（键完全相同），
    /// 切换时只换 Application.Resources 里的那一份，同时把
    /// RequestedThemeVariant 跟着 Light/Dark 切——这一步很关键，
    /// 否则 SimpleTheme 自带的滚动条、下拉、菜单、MessageBox 还是浅色的，
    /// 在深色主题里就是一片刺眼的白块。
    ///
    /// 为什么样式里必须用 DynamicResource：
    /// StaticResource 在加载时就把画刷实例解析掉了，换字典不会重新求值，
    /// 表现就是"切了主题只有一半区域变色"。这点在 AppTheme.axaml 里逐个改过。
    /// </summary>
    public static class ThemeService
    {
        /// <summary>1.8.2 之前的主题文件（只在迁移时读一次，之后删掉）</summary>
        private const string LegacyStorageFile = "theme.txt";

        private static ResourceDictionary _currentDictionary;

        public static IReadOnlyList<AppThemeOption> Options { get; } = new List<AppThemeOption>
        {
            new() { Id = "mint-blue", Name = "Mint Blue", FileName = "MintBlue", IsDark = false, Hint = "明亮 · 薄荷水蓝", Swatch = "#3FB6A8" },
            new() { Id = "industrial-blue", Name = "Industrial Blue", FileName = "IndustrialBlue", IsDark = false, Hint = "明亮 · 工业蓝", Swatch = "#3A5FD0" },
            new() { Id = "warm-white", Name = "Warm White", FileName = "WarmWhite", IsDark = false, Hint = "明亮 · 暖白", Swatch = "#D9A441" },
            // 主题 F（用户提供的 主题F.md）：纯白清新绿。色点用规格里的品牌绿 #239653，
            // 令牌里的主色比它深一档（见 gen_themes.py 的注释：白字对比度不够）。
            new() { Id = "fresh-green", Name = "Fresh Green", FileName = "FreshGreen", IsDark = false, Hint = "明亮 · 纯白清新绿", Swatch = "#239653" },
            new() { Id = "graphite-dark", Name = "Graphite Dark", FileName = "GraphiteDark", IsDark = true, Hint = "暗色 · 石墨", Swatch = "#4A5568" },
            new() { Id = "deep-sea", Name = "Deep Sea", FileName = "DeepSea", IsDark = true, Hint = "暗色 · 深海", Swatch = "#1E88C7" },
        };

        public static AppThemeOption Current { get; private set; } = Options[0];

        /// <summary>主题变化后触发；用来让 ViewModel 里缓存了画刷的地方重新取色</summary>
        public static event Action Changed;

        /// <summary>
        /// 启动时调用：读取上次选择（读不到就用第一套）。
        ///
        /// 必须在 <see cref="AppSettingsStore.Initialize"/> **之后**调用——
        /// 1.8.2 起主题 Id 存在 settings.json 里，设置没加载完就来这里，
        /// 读到的是默认对象（Theme=null），于是每次启动都回落到第一套主题，
        /// 现象是"我明明选过，重启就变回去了"。
        /// </summary>
        public static void Initialize()
        {
            Apply(ReadSavedId() ?? Options[0].Id, persist: false);
        }

        public static void Apply(string id, bool persist = true)
        {
            AppThemeOption theme = Options.FirstOrDefault(item => item.Id == id) ?? Options[0];
            Application app = Application.Current;
            if (app == null)
            {
                return;
            }

            try
            {
                ResourceDictionary dictionary = (ResourceDictionary)AvaloniaXamlLoader.Load(
                    new Uri($"avares://IEC104Simulator/Styles/Themes/{theme.FileName}.axaml"));

                // 只替换主题那一份，别把其它资源字典（图标、ControlTheme）冲掉
                if (_currentDictionary != null)
                {
                    app.Resources.MergedDictionaries.Remove(_currentDictionary);
                }

                app.Resources.MergedDictionaries.Insert(0, dictionary);
                _currentDictionary = dictionary;

                // 让内置控件（滚动条、菜单、对话框）跟着变明暗
                app.RequestedThemeVariant = theme.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;

                Current = theme;

                if (persist)
                {
                    Save(theme.Id);
                }

                // ViewModel 里缓存的画刷要先作废，再通知各处刷新
                ThemePalette.Invalidate();
                Changed?.Invoke();
            }
            catch (Exception exception)
            {
                // 切主题失败不该影响主流程，记录一条就够了
                System.Diagnostics.Debug.WriteLine($"[theme] apply {id} failed: {exception.Message}");
            }
        }

        /// <summary>
        /// 读上次选的主题 Id：**先读 settings.json，再兜旧版的 theme.txt**。
        ///
        /// 旧文件只是一次性的迁移来源：读到就把它写进 settings.json 并把那个 txt 删掉，
        /// 免得用户程序目录里永远躺着一个"不知道干嘛的"文件——这一轮要解决的就是它。
        /// 两处都找不到（全新机器）时返回 null，由调用方回落到第一套主题。
        /// </summary>
        private static string ReadSavedId()
        {
            string saved = AppSettingsStore.Current?.Theme;
            if (!string.IsNullOrWhiteSpace(saved))
            {
                return saved.Trim();
            }

            string legacyId = ReadLegacyFile();
            if (string.IsNullOrWhiteSpace(legacyId))
            {
                return null;
            }

            // 迁移：写进 settings.json 再删旧文件。写失败就不删——宁可多留一个文件，
            // 也不能出现"旧文件删了、新文件没写进去"导致主题选择凭空丢失。
            AppSettingsStore.Current.Theme = legacyId;
            if (AppSettingsStore.Save())
            {
                DeleteLegacyFiles();
            }

            return legacyId;
        }

        /// <summary>找旧版 theme.txt：先看程序目录，再看 %APPDATA%（1.8.1 之前的位置）</summary>
        private static string ReadLegacyFile()
        {
            foreach (string folder in new[] { PortableFolder.Path, PortableFolder.LegacyPath })
            {
                try
                {
                    string path = Path.Combine(folder ?? string.Empty, LegacyStorageFile);
                    if (!string.IsNullOrEmpty(folder) && File.Exists(path))
                    {
                        string id = File.ReadAllText(path).Trim();
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            return id;
                        }
                    }
                }
                catch
                {
                    // 读不到就试下一个位置
                }
            }

            return null;
        }

        /// <summary>删掉旧版 theme.txt（两个可能的位置都清一遍；删不掉不算错）</summary>
        private static void DeleteLegacyFiles()
        {
            foreach (string folder in new[] { PortableFolder.Path, PortableFolder.LegacyPath })
            {
                try
                {
                    string path = Path.Combine(folder ?? string.Empty, LegacyStorageFile);
                    if (!string.IsNullOrEmpty(folder) && File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // 删不掉就留着：它已经不影响任何行为了
                }
            }
        }

        /// <summary>
        /// 保存主题选择 —— 直接写进 settings.json，不再单独落 theme.txt。
        ///
        /// 先改内存再落盘：Save 会把整个设置对象序列化出去，
        /// 所以必须先把 Theme 赋进 Current，否则写回去的还是旧值（这个坑很容易踩）。
        /// </summary>
        private static void Save(string id)
        {
            AppSettingsStore.Current.Theme = id;
            AppSettingsStore.Save();
        }
    }
}
