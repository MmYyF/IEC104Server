using System;
using System.IO;
using System.Text;

namespace IEC104.App.Services
{
    /// <summary>
    /// 程序的"用户数据目录"——默认就是 exe 所在目录（便携式：整个文件夹拷走，设置跟着走）。
    ///
    /// 【为什么统一在这里决定，而不是各处自己拼路径】
    /// 设置（settings.json）与崩溃现场（crash.log）是同一份"这台机器上这个人的偏好"。
    /// 两处各写一遍 %APPDATA% 拼接，就会出现"设置搬了、日志没搬"这种半截状态——
    /// 现场收日志时找不到文件，而配置明明带过去了。
    /// （主题选择 1.8.2 起并进了 settings.json，不再单独占一个文件。）
    ///
    /// 【为什么不硬编码成 exe 目录就完事】
    /// exe 可能落在只读目录（Program Files）、共享盘、或者被安全软件锁住的位置。
    /// 那种情况下"配置写不进去"是现场最难解释的一类故障：改完设置重启全丢，界面上又没报错。
    /// 所以这里做三件事：
    ///   ① 启动时探一次可写性（写一个临时探针文件再删掉）；
    ///   ② 不可写就回退到 %APPDATA%\IEC104Simulator，并把原因挂在 <see cref="Note"/> 上，
    ///      由设置页原样显示给用户看——宁可多一句解释，也不要静默降级；
    ///   ③ 老版本（一直存在 %APPDATA%）的文件，首次运行到新位置时**复制**过来
    ///      （保留原件当备份，不删：用户可能还需要回退到旧版本用）。
    /// </summary>
    public static class PortableFolder
    {
        private const string LegacyFolderName = "IEC104Simulator";

        /// <summary>
        /// 需要跟着搬的文件（同一目录下的用户数据）。
        ///
        /// 只列 settings.json：1.8.2 起主题选择并进了设置文件，theme.txt 成了历史文件，
        /// 它里面的值由 <see cref="ThemeService"/> 在启动时读一次并写进设置（然后删掉旧文件）。
        /// 把 theme.txt 也列进来会让"搬过去的旧 txt"和"新写入的 json 里的主题"打架。
        /// </summary>
        private static readonly string[] MigratedFiles = { "settings.json" };

        private static readonly string ProbeName = ".write-probe";

        static PortableFolder()
        {
            string exeFolder = Normalize(AppContext.BaseDirectory);

            if (CanWrite(exeFolder))
            {
                Path = exeFolder;
                Migrated = Migrate(exeFolder);
                Note = Migrated
                    ? $"已把旧位置（{LegacyPath}）里的设置迁移到这里；旧文件保留，没有删除。"
                    : string.Empty;
                return;
            }

            Path = LegacyPath;
            Note = $"exe 所在目录不可写（只读目录或权限不足），设置改存到 {LegacyPath}。";
            try
            {
                Directory.CreateDirectory(LegacyPath);
            }
            catch
            {
                // 连回退目录都建不出来：保持路径不变，让 Save/Load 各自去报错
            }
        }

        /// <summary>设置类数据实际存放的目录（exe 目录，或只读时的 %APPDATA% 回退目录）</summary>
        public static string Path { get; }

        /// <summary>旧版本的位置：%APPDATA%\IEC104Simulator</summary>
        public static string LegacyPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyFolderName);

        /// <summary>是否真的落在 exe 目录（便携）；false 表示已回退</summary>
        public static bool IsPortable => string.Equals(Path, Normalize(AppContext.BaseDirectory),
            StringComparison.OrdinalIgnoreCase);

        /// <summary>本次是否发生过旧数据迁移</summary>
        public static bool Migrated { get; }

        /// <summary>给界面看的一句话；正常（就在 exe 目录、也没迁移）时是空串</summary>
        public static string Note { get; }

        /// <summary>拼一个本目录下的文件路径</summary>
        public static string Combine(string fileName) => System.IO.Path.Combine(Path, fileName);

        /// <summary>
        /// 写日志这类"尽力而为"的写入：本目录写不进去时退回 LocalAppData，再不行才放弃。
        ///
        /// 为什么日志要单独一条路：设置目录只读时设置确实存不下（必须让用户知道），
        /// 但崩溃日志是排查用的副产品，丢在这里等于把唯一的现场证据丢了。
        /// </summary>
        public static bool TryAppendLog(string fileName, string content)
        {
            foreach (string folder in new[]
                     {
                         Path,
                         System.IO.Path.Combine(
                             Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             LegacyFolderName)
                     })
            {
                try
                {
                    Directory.CreateDirectory(folder);

                    // 必须用 AppendAllText，不能写成 File.AppendText(...).Write(...)。
                    // 后者拿到的 StreamWriter 没有释放，内容只停在它自己的缓冲区里；
                    // 而调用这里的时候进程马上就要死了，缓冲区根本没有机会 Flush ——
                    // 结果是"crash.log 建出来了，但是 0 字节"，比没有日志更误导人。
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(folder, fileName), content);
                    return true;
                }
                catch
                {
                    // 换下一个位置
                }
            }

            return false;
        }

        /// <summary>探测目录可写：建目录 + 写一个探针文件再删掉</summary>
        private static bool CanWrite(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                string probe = System.IO.Path.Combine(folder, ProbeName);
                System.IO.File.WriteAllText(probe, "probe", new UTF8Encoding(false));
                System.IO.File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把旧位置的文件复制到新位置（只复制"新的还没有"的那些）</summary>
        private static bool Migrate(string targetFolder)
        {
            string legacyFolder = LegacyPath;
            if (string.Equals(Normalize(targetFolder), Normalize(legacyFolder),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bool migrated = false;
            foreach (string name in MigratedFiles)
            {
                try
                {
                    string source = System.IO.Path.Combine(legacyFolder, name);
                    string destination = System.IO.Path.Combine(targetFolder, name);
                    if (System.IO.File.Exists(source) && !System.IO.File.Exists(destination))
                    {
                        System.IO.File.Copy(source, destination);
                        migrated = true;
                    }
                }
                catch
                {
                    // 单个文件搬不过来不影响启动：设置回到默认值，用户重设一次即可
                }
            }

            return migrated;
        }

        private static string Normalize(string folder)
            => string.IsNullOrEmpty(folder)
                ? folder
                : folder.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }
}
