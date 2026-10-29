using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using IEC104.App.Services;
using IEC104.App.ViewModels;
using IEC104.App.Views;

namespace IEC104.App
{
    /// <summary>
    /// 应用入口。
    ///
    /// 与原实现的差异：原程序在这里会先走 Parse.dll 的激活流程
    /// （ActivateUtil.Activate / FreeTrial / GetServerTime），
    /// 未激活则弹 LicenseManagementWindow 并阻断启动。
    /// 复刻版【没有任何激活相关代码】，直接创建主窗口。
    /// </summary>
    public partial class App : Application
    {
        public override void Initialize()
        {
            Program.StartupStage = "xaml-load";
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // 整段包一层：主窗口出现之前没有任何界面可以说话，出错只能落日志。
                // 打点分成四段（load-settings / build-viewmodel / build-window / window-ready），
                // 这样 crash.log 里的"阶段="能直接指出是设置、视图模型还是窗口构建出的问题，
                // 不用再靠"我这边复现不出来"。
                try
                {
                    // 设置先加载：1.8.2 起主题选择也存在 settings.json 里，
                    // 反过来的话 ThemeService 读到的是默认对象（Theme=null），
                    // 每次启动都会回落到第一套主题。两件事都必须在窗口创建之前完成——
                    // 窗口的样式在构建时就会求值 DynamicResource，晚一步会出现"先默认色再闪一下"；
                    // 自定义 ASDU 类型也要赶在窗口构建前进目录，否则第一批点位选不到用户自己加的类型。
                    Program.StartupStage = "load-settings";
                    AppSettingsStore.Initialize();
                    ThemeService.Initialize();

                    Program.StartupStage = "build-viewmodel";
                    MainWindowViewModel viewModel = new MainWindowViewModel();

                    // 三个命令行模式（压测 / 界面自检 / 内存探针）要的是构造函数里那份
                    // **演示点表**，不能被"恢复上次工程"清掉——它们拿它当断言素材。
                    // 所以参数先读出来，再决定要不要走启动恢复。
                    string benchArgs = ReadArgument("--bench-scale");
                    string captureDir = ReadArgument("--capture-ui");
                    string memProbePath = ReadArgument("--mem-probe");

                    // 启动恢复：读上次的工程 → 打开；没有就退回欢迎页。
                    // 放在窗口构建之前：标题栏、工具条、状态栏第一次渲染就要拿到
                    // 工程名和"有没有保存必要"，事后再补会闪一下默认态。
                    //
                    // 逃生开关：万一上次那个工程文件坏了/所在盘掉线，导致一启动就打不开，
                    // 现场需要"先把工具打开"的路。设 IEC104_NO_RESTORE=1 跳过恢复直接进欢迎页。
                    bool skipRestore = !string.IsNullOrWhiteSpace(
                        Environment.GetEnvironmentVariable("IEC104_NO_RESTORE"));

                    Program.StartupStage = "startup-restore";
                    if (benchArgs == null && captureDir == null && memProbePath == null && !skipRestore)
                    {
                        viewModel.InitializeStartup();
                    }

                    Program.Trace("build-window");
                    Program.StartupStage = "build-window";
                    MainWindow mainWindow = new MainWindow { DataContext = viewModel };
                    desktop.MainWindow = mainWindow;

                    // ShutdownMode 定义在 Avalonia.Controls 命名空间下
                    desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;

                    // 规模压测：--bench-scale <从站数> <每台点数> [输出目录]
                    // 用来回答"100 台装置 / 20 万点会不会卡"这类问题（见 Views/ScaleBench.cs）
                    if (benchArgs != null)
                    {
                        string[] parts = benchArgs.Split(',', ';', ' ');
                        int slaveCount = parts.Length > 0 && int.TryParse(parts[0], out int parsedSlaves) ? parsedSlaves : 100;
                        int pointsPerSlave = parts.Length > 1 && int.TryParse(parts[1], out int parsedPoints) ? parsedPoints : 2000;
                        string benchDir = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2])
                            ? parts[2]
                            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "iec104-bench");

                        Dispatcher.UIThread.Post(async () =>
                        {
                            try
                            {
                                await ScaleBench.RunAsync(mainWindow, viewModel, slaveCount, pointsPerSlave, benchDir);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("[bench] FAILED: " + ex);
                            }
                            finally
                            {
                                mainWindow.Close();
                            }
                        });
                    }

                    // 自检模式：--capture-ui <目录>，把每个窗口渲染成 PNG 后退出
                    if (captureDir != null)
                    {
                        UiCapture.Directory = captureDir;
                        // 先落地一个目录和标记文件：这样即使后面卡住/被杀，
                        // 也能一眼看出“参数解析到了、只是流程没跑完”
                        System.IO.Directory.CreateDirectory(captureDir);
                        System.IO.File.AppendAllText(
                            System.IO.Path.Combine(captureDir, "_capture.log"),
                            $"{DateTime.Now:HH:mm:ss.fff} app-init{Environment.NewLine}");
                        // Post 到消息循环启动之后，此时主窗口已完成第一次布局。
                        // 注意：这里必须自己吞掉异常 —— Post 接收的是 Action，
                        // async lambda 会变成 async void，异常一旦冒出就会带崩进程。
                        Dispatcher.UIThread.Post(async () =>
                        {
                            try
                            {
                                await UiCapture.RunAsync(mainWindow, viewModel);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("[ui-capture] FAILED: " + ex);
                                mainWindow.Close();
                            }
                        });
                    }

                    // 内存探针：--mem-probe=<日志路径>，量完空载基线就退出。
                    // 用来回答"还没建站点就占 200MB，这钱花在哪了"（见 Views/MemoryProbe.cs）
                    if (memProbePath != null)
                    {
                        Dispatcher.UIThread.Post(async () =>
                        {
                            try
                            {
                                await MemoryProbe.RunAsync(mainWindow, memProbePath);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("[mem-probe] FAILED: " + ex);
                            }
                            finally
                            {
                                mainWindow.Close();
                            }
                        });
                    }

                    Program.StartupStage = "window-ready";
                    Program.Trace("window-ready");
                }
                catch (Exception ex)
                {
                    Program.WriteLog("Startup", ex);
                    throw;
                }
            }

            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>取形如 "--name value" 或 "--name=value" 的命令行参数值</summary>
        private static string ReadArgument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return i + 1 < args.Length ? args[i + 1] : null;
                }
                if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                {
                    return args[i].Substring(name.Length + 1);
                }
            }
            return null;
        }
    }
}
