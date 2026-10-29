using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;

namespace IEC104.App
{
    internal static class Program
    {
        /// <summary>
        /// 启动进行到哪一步了。
        ///
        /// 为什么需要这个：1.9.1 发布后有一次双击没出窗口、只留下一个 0 字节的 crash.log
        /// （真正原因是写日志的 StreamWriter 没 flush，已修）。但那次即便写进去了，
        /// 光有一句 "NullReferenceException" 也判断不出是加载设置、建视图模型还是建窗口时崩的。
        /// 主窗口出现之前的异常都没有界面可以说话，只能靠日志——那就把阶段一起写进去。
        /// 只在崩溃时读，正常路径不产生任何输出。
        /// </summary>
        internal static volatile string StartupStage = "main";

        [STAThread]
        public static void Main(string[] args)
        {
            InstallCrashLogging();
            Trace("main-enter args=" + string.Join(' ', args));

            // 启动链路自己再兜一层。AppDomain.UnhandledException 收得到，但它拿到的
            // 异常未必带着这句"崩在哪个阶段"的上下文；而且 certaines 平台上
            // 启动期异常会先被 Avalonia 的 platform 探测吞一轮，这里包一层最稳。
            try
            {
                StartupStage = "avalonia-start";
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                WriteLog("Main", ex);
                throw;
            }
        }

        /// <summary>
        /// 崩溃日志（只落盘，不干预运行时）。
        ///
        /// 踩过的坑：最初这里还注册了
        ///   Dispatcher.UIThread.UnhandledException += (_, e) => e.Handled = true;
        /// 结果整个窗口能画出来却完全不响应 —— Avalonia 启动时会用一次异常做
        /// 平台能力探测，把它吞掉之后调度器状态不一致，主循环里抛
        /// PlatformNotSupportedException，界面卡死但不退出。
        /// 所以这里只记录：UI 线程异常仍交给 Avalonia 默认处理，
        /// 进程该崩就崩，至少日志和堆栈是完整的。
        /// </summary>
        private static void InstallCrashLogging()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                WriteLog("AppDomain", e.ExceptionObject as Exception);

            // 观察掉未处理的 Task 异常，避免 socket 收发偶发异常直接把进程带走。
            // SetObserved 只影响本 Task 的异常状态，不改变 Avalonia 调度器行为。
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                WriteLog("Task", e.Exception);
                e.SetObserved();
            };
        }

        /// <summary>
        /// 启动/退出轨迹：只在设了环境变量 IEC104_STARTUP_LOG 时写文件。
        ///
        /// 为什么需要它：WinExe 没有控制台，"双击后窗口一闪就没了"这种事只能靠
        /// 文件说话——crash.log 只记异常，而"正常退出"是不留痕迹的。
        /// 这里记的是阶段名与窗口的打开/关闭，用来回答"是没起来，还是起来后被关掉了"。
        /// </summary>
        internal static void Trace(string message)
        {
            string path = Environment.GetEnvironmentVariable("IEC104_STARTUP_LOG");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
                // 轨迹写不进去不能影响启动
            }
        }

        internal static void WriteLog(string source, Exception ex)
        {
            if (ex == null) return;

            // 与设置同目录（默认 exe 旁边）——现场把整个文件夹打包发回来时，
            // 配置和崩溃现场就在一起。该目录只读时自动退回 LocalAppData，见 PortableFolder。
            //
            // 头部这几项是"用户描述不清时最省事的部分"：哪一版、是不是单文件版
            // （单文件会多一层 native 库解压，故障面不同）、装在哪个目录、崩在哪个阶段。
            string header = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] "
                + $"阶段={StartupStage} 版本={Services.AppInfo.Version} "
                + $"单文件={(Services.AppInfo.IsSingleFile ? "是" : "否")} "
                + $"目录={AppContext.BaseDirectory}{Environment.NewLine}";

            Services.PortableFolder.TryAppendLog("crash.log",
                header + ex + Environment.NewLine + Environment.NewLine);
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();
        }
    }
}
