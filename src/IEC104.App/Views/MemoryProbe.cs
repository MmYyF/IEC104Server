using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace IEC104.App.Views
{
    /// <summary>
    /// 内存探针：把"进程工作集"拆成能看懂的四块，用来回答
    /// "为什么还没建站点就占 200MB"。
    ///
    /// 光看任务管理器那一列没有意义 —— 那是工作集，里面混着 .NET 运行时、
    /// 渲染器的 native 映射、GC 保留段和托管对象。分开量，才知道该动哪一块。
    ///
    /// 连续采三次（2s / 5s / 10s）是为了区分"启动期一次性分配"和"还在涨"：
    /// 后者才是泄漏。
    ///
    /// 用法：<c>IEC104-Server.exe --mem-probe=D:\temp\mem.log</c>
    /// </summary>
    internal static class MemoryProbe
    {
        public static async Task RunAsync(Window window, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(Path.GetTempPath(), "iec104-mem.log");
            }

            StringBuilder report = new StringBuilder();
            report.AppendLine("== 内存探针（空载）==");
            report.AppendLine($"时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}  版本 {Services.AppInfo.Version} "
                + $"单文件 {(Services.AppInfo.IsSingleFile ? "是" : "否")}");

            await Task.Delay(2000);
            Snapshot(report, "启动后 2 秒", window);

            await Task.Delay(3000);
            Snapshot(report, "启动后 5 秒", window);

            await Task.Delay(5000);
            Snapshot(report, "启动后 10 秒", window);

            report.AppendLine();
            report.AppendLine("== 结束 ==");

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(path, report.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 写不进就算了，控制台那份还在
            }

            Console.WriteLine("[mem-probe] 已写入 " + path);
        }

        private static void Snapshot(StringBuilder report, string label, Window window)
        {
            // 强制一次完整回收再量，避免把尚未回收的垃圾算成"占用"
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            GCMemoryInfo info = GC.GetGCMemoryInfo();
            Process process = Process.GetCurrentProcess();
            process.Refresh();

            const double MB = 1024.0 * 1024.0;

            report.AppendLine();
            report.AppendLine($"── {label} ──");
            report.AppendLine($"  工作集   {process.WorkingSet64 / MB:0.0} MB   （任务管理器看到的就是它）");
            report.AppendLine($"  私有     {process.PrivateMemorySize64 / MB:0.0} MB   虚拟 {process.VirtualMemorySize64 / MB:0.0} MB");
            report.AppendLine($"  托管堆   {info.HeapSizeBytes / MB:0.0} MB   已提交 {info.TotalCommittedBytes / MB:0.0} MB   碎片 {info.FragmentedBytes / MB:0.0} MB");

            string[] generationNames = { "Gen0", "Gen1", "Gen2", "LOH", "POH" };
            List<string> generations = new List<string>();
            for (int i = 0; i < info.GenerationInfo.Length && i < generationNames.Length; i++)
            {
                generations.Add($"{generationNames[i]} {info.GenerationInfo[i].SizeAfterBytes / MB:0.0}");
            }

            report.AppendLine($"  分代     {string.Join(" / ", generations)} MB");
            report.AppendLine($"  程序集   {AppDomain.CurrentDomain.GetAssemblies().Length} 个   "
                + $"线程 {process.Threads.Count}   句柄 {process.HandleCount}");

            if (window != null)
            {
                report.AppendLine($"  可视节点 {window.GetVisualDescendants().Count():N0} 个");
            }
        }
    }
}
