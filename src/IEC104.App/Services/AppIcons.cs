using System;
using Avalonia.Controls;
using Avalonia.Platform;

namespace IEC104.App.Services
{
    /// <summary>
    /// 窗口图标的统一入口。
    ///
    /// 为什么要单独抽出来：
    ///   1. .axaml 里写 Icon="/Assets/app.ico" 只能覆盖"有 axaml 的窗口"。
    ///      代码里 new 出来的提示框（MainWindow.ShowMessageAsync）没有 axaml，
    ///      不显式给 Icon，任务栏/标题栏上就是 Windows 默认的空白程序图标——
    ///      用户反馈"很多弹窗里都没有图标，还是 Windows 原始那样"，就是这一类。
    ///   2. 图标文件只解析一次，别每次弹窗都读一遍资源。
    ///
    /// 路径里的程序集名是 IEC104Simulator（csproj 的 AssemblyName），
    /// 不是命名空间 IEC104.App——avares 按程序集名解析，写错会抛"资源找不到"。
    /// </summary>
    public static class AppIcons
    {
        private static WindowIcon _main;

        /// <summary>主图标（多尺寸 ico：16/24/32/48/64/128/256）。</summary>
        public static WindowIcon Main => _main ??= Load("avares://IEC104Simulator/Assets/app.ico");

        private static WindowIcon Load(string uri)
        {
            try
            {
                return new WindowIcon(AssetLoader.Open(new Uri(uri)));
            }
            catch (Exception)
            {
                // 图标加载失败不该让窗口打不开：返回 null 就是系统默认图标
                return null;
            }
        }
    }
}
