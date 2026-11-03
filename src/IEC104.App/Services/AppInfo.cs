using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace IEC104.App.Services
{
    /// <summary>
    /// 软件自身的"身份信息"——关于页、标题栏的仓库按钮、以及"复制信息"共用这一份。
    ///
    /// 【为什么要单独一个类】
    /// 这些值以前散在三处：版本号在 AboutWindow 的 code-behind 里现读程序集，
    /// 仓库地址写死在 MainWindow 的私有常量里，操作系统信息压根没有。
    /// 结果是"关于"窗口和标题栏各自维护一套说法，加一个字段就要改两个地方。
    /// 现在只有这里读程序集 / 环境，界面层只管展示。
    ///
    /// 【构建时间从哪来】
    /// 程序集里没有可靠的编译时间戳（Deterministic 编译会把时间固定成 2000-01-01），
    /// 所以取**可执行文件自身的写入时间**——单文件发布时它就是打出来的时刻，
    /// 也正是用户想知道的"这个 exe 是哪天做的"。
    /// 调试运行时它是 bin 目录里 dll 的时间，语义一样。
    /// </summary>
    public static class AppInfo
    {
        /// <summary>产品名（窗口标题、状态栏、关于页必须一致）</summary>
        public const string ProductName = "IEC104-Server";

        /// <summary>一句话说明：做什么用的</summary>
        public const string Description = "IEC 60870-5-101/104 从站仿真工具";

        /// <summary>
        /// 项目仓库地址，形如 <c>https://github.com/owner/repo</c>。
        ///
        /// **这是全项目唯一一处仓库地址**：关于页的"项目主页"、标题栏的 GitHub 按钮、
        /// 检查更新（GitHub API 与 Release 下载地址都由它推出来）全都读这里。
        /// 换仓库、改组织名只改这一行。
        ///
        /// 留空时这三处都会给出"地址还没确定"的提示，而不是打开 404 或发一个注定失败的请求。
        /// </summary>
        public const string RepositoryUrl = "";

        /// <summary>
        /// 国内镜像的更新清单地址（可选），形如 <c>https://your-bucket.oss-cn-xxx.aliyuncs.com/iec104/update.json</c>。
        ///
        /// 为什么要有：GitHub 的 api / releases 在国内网络下经常连不上或慢到不可用，
        /// 而仿真工具的用户大多在内网现场。留空表示不启用镜像，只用 GitHub。
        /// 镜像只需要托管两个文件（update.json + exe），格式与 CI 生成的一致，
        /// 见 <see cref="UpdateService"/> 里对 update.json 的说明。
        /// </summary>
        public const string MirrorUpdateUrl = "";

        /// <summary>协议与界面框架版本，跟 csproj 里的 PackageReference 对齐</summary>
        private const string FrameworkText = ".NET 8 · Avalonia 11.3.6";

        private static readonly Lazy<string> LazyVersion = new(ReadVersion);
        private static readonly Lazy<DateTime?> LazyBuildTime = new(ReadBuildTime);
        private static readonly Lazy<string> LazyBuildId = new(ReadBuildId);

        private static Assembly Self => typeof(AppInfo).Assembly;

        /// <summary>版本号，如 "1.9.1"（三段）</summary>
        public static string Version => LazyVersion.Value;

        /// <summary>版本 + 架构，如 "1.9.1 · x64"</summary>
        public static string VersionWithArch => $"{Version} · {Architecture}";

        /// <summary>
        /// 是否单文件发布。
        ///
        /// 判据是"程序集有没有磁盘路径"：打进单文件后 <see cref="Assembly.Location"/> 恒为空串
        /// （编译器为这事专门发 IL3000 警告）。崩溃日志里带上这一项很有用——
        /// 单文件版多一层 native 库解压，故障面和框架依赖版不一样。
        /// </summary>
        public static bool IsSingleFile => string.IsNullOrEmpty(Self.Location);

        /// <summary>构建时间（exe 写入时间）；读不到时为 null</summary>
        public static DateTime? BuildTime => LazyBuildTime.Value;

        public static string BuildTimeText => BuildTime?.ToString("yyyy-MM-dd HH:mm") ?? "未知";

        /// <summary>
        /// 构建标识，用于用户报告问题时定位到具体那一次构建。
        /// 格式：20261028T031502Z-1.9.1（时间到秒 + 版本），跟"构建时间"同源，不另起一套。
        /// </summary>
        public static string BuildId => LazyBuildId.Value;

        /// <summary>进程架构（x64 / arm64 / x86），不是操作系统架构</summary>
        public static string Architecture => RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };

        /// <summary>操作系统描述，如 "Microsoft Windows 10.0.22631"</summary>
        public static string OperatingSystem => RuntimeInformation.OSDescription?.Trim() ?? "未知";

        /// <summary>运行时与界面框架</summary>
        public static string Framework => FrameworkText;

        /// <summary>仓库地址是否已配置（界面据此决定按钮是"打开"还是"提示"）</summary>
        public static bool HasRepository => !string.IsNullOrWhiteSpace(RepositoryUrl);

        /// <summary>
        /// 供"复制信息"用的一段纯文本：出问题时让用户把这段发过来，
        /// 比问"你用的哪个版本、什么系统"准得多。
        /// 刻意做成"每行一项"的 key: value，贴到 IM 里也不会错行。
        /// </summary>
        public static string Summary
        {
            get
            {
                StringBuilder builder = new();
                builder.AppendLine($"{ProductName} {VersionWithArch}");
                builder.AppendLine($"构建时间: {BuildTimeText}");
                builder.AppendLine($"构建 ID: {BuildId}");
                builder.AppendLine($"运行时: {Framework}");
                builder.AppendLine($"操作系统: {OperatingSystem}");
                builder.AppendLine($"设置文件: {AppSettingsStore.FilePath}");
                builder.Append($"当前主题: {ThemeService.Current?.Name ?? "默认"}");
                return builder.ToString();
            }
        }

        private static string ReadVersion()
        {
            Version version = Self.GetName().Version;
            // 三段就够（1.9.1），第四段永远是 0，显示出来只会让人以为还有小版本
            return version == null ? "0.0.0" : new Version(
                version.Major, version.Minor, Math.Max(version.Build, 0)).ToString(3);
        }

        private static DateTime? ReadBuildTime()
        {
            try
            {
                // 单文件发布时 Environment.ProcessPath 就是那个 exe；
                // 调试运行时它是 apphost，时间同样等于最近一次构建
                string path = Environment.ProcessPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    path = Self.Location;
                }

                return File.Exists(path) ? File.GetLastWriteTime(path) : null;
            }
            catch (Exception)
            {
                // 读不到不该让关于页崩掉，"未知"是可接受的答案
                return null;
            }
        }

        private static string ReadBuildId()
        {
            DateTime? built = BuildTime;
            return built == null
                ? $"unknown-{Version}"
                : $"{built.Value:yyyyMMdd'T'HHmmss'Z'}-{Version}";
        }
    }
}
