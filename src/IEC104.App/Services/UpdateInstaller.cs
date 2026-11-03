using System;
using System.Diagnostics;
using System.IO;

namespace IEC104.App.Services
{
    /// <summary>
    /// 把下载好的新 exe 换上，并在失败时**回滚**。
    ///
    /// 【为什么不能直接覆盖】
    /// 正在运行的 exe 是锁着的，`File.Copy` 覆盖会失败。Windows 允许的是**改名**，
    /// 所以流程是：
    ///   ① 正在运行的 <c>IEC104-Server.exe</c> 改名成 <c>IEC104-Server.exe.old</c>
    ///   ② 新文件放到原名位置
    ///   ③ 用新 exe 启动，然后老进程退出
    ///   ④ 下次启动时把 <c>.old</c> 删掉
    ///
    /// 【跨盘会踩坑】
    /// 下载缓存放在 <c>%TEMP%</c>（通常在 C:），而程序可能被放在 D: 或 U 盘。
    /// `File.Move` 跨卷会抛 IOException——所以"放新文件"这一步用复制+删除，
    /// 只有同目录内的改名（①）才用 Move。
    ///
    /// 【失败必须回滚】
    /// 第 ② 步失败（磁盘满、杀软拦截、权限不足）时，如果不管，用户的程序就只剩
    /// 一个 <c>.old</c> 了——等于更新把软件搞坏。所以必须在 catch 里把 <c>.old</c> 改回去。
    /// </summary>
    public static class UpdateInstaller
    {
        /// <summary>备份后缀。启动时会被清掉，不长期占空间。</summary>
        public const string BackupSuffix = ".old";

        /// <summary>
        /// 用 <paramref name="downloadedExe"/> 替换 <paramref name="targetExe"/>。
        /// 成功返回 true；失败抛异常（消息可以直接显示给用户），且目标文件保持原样。
        /// </summary>
        public static void Apply(string downloadedExe, string targetExe)
        {
            if (!File.Exists(downloadedExe))
            {
                throw new InvalidOperationException("下载的文件不见了，请重试。");
            }

            if (!File.Exists(targetExe))
            {
                throw new InvalidOperationException("找不到当前程序文件：" + targetExe);
            }

            string backup = targetExe + BackupSuffix;

            // 上次更新留下的备份先清掉，否则改名会失败
            TryDelete(backup);

            try
            {
                File.Move(targetExe, backup);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "无法替换程序文件（可能没有写权限）。"
                    + "把整个文件夹放到可写的位置，或到发布页手动下载新版覆盖。", exception);
            }

            try
            {
                // 跨卷必须走复制+删除：File.Move 在 C: → D: 之间会抛 IOException
                CopyThenDelete(downloadedExe, targetExe);
            }
            catch (Exception exception)
            {
                // 回滚：把旧文件改回原名，让用户至少还能用
                TryDelete(targetExe);
                try
                {
                    File.Move(backup, targetExe);
                }
                catch (Exception)
                {
                    // 连回滚都失败就没什么可做的了，把备份路径写进消息，用户还能手工救
                }

                throw new InvalidOperationException(
                    "写入新版本失败，已恢复原来的程序（备份：" + backup + "）。", exception);
            }
        }

        /// <summary>启动新版本并关掉自己。调用方随后要退出进程。</summary>
        public static void Restart(string targetExe)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = targetExe,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(targetExe) ?? string.Empty,
            });
        }

        /// <summary>
        /// 启动时清理上次更新留下的 <c>.old</c>。
        /// 删不掉（比如还开着两个实例）不算错，下次再删。
        /// </summary>
        public static void CleanupLeftover(string targetExe)
        {
            if (string.IsNullOrEmpty(targetExe))
            {
                return;
            }

            TryDelete(targetExe + BackupSuffix);
        }

        private static void CopyThenDelete(string from, string to)
        {
            File.Copy(from, to, true);
            TryDelete(from);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 删不掉就留着，下次启动再删
            }
        }
    }
}
