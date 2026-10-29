using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using IEC104.App.Services;
using IEC104.App.ViewModels;
using IEC104.Core.CS101;

namespace IEC104.App.Views
{
    /// <summary>
    /// 规模压测（开发/验收用，正式交互不启用）。
    ///
    /// 用法： IEC104-Server.exe --bench-scale 100,2000[,输出目录]
    ///        参数 1 = 从站台数，参数 2 = 每台点位数（一半遥信一半遥测）
    ///
    /// 为什么要单独做这个模式：
    ///   现场用法是"100 台装置、10 万遥测 + 10 万遥信"，这种量级下
    ///   靠人点界面是测不出来的——点一次要等十几秒才知道卡不卡，
    ///   而且每次都要重新铺一遍点表。这个模式把"建站 → 铺点 → 切标签 →
    ///   总召组帧 → 自动变化 → 批量改值 → 报文灌入"这条链路一次性跑完，
    ///   每段都计时，输出到 _bench.log，优化前后可以用同一把尺子对比。
    ///
    /// 计时口径（很重要）：
    ///   全部在 UI 线程上同步执行，因为界面卡顿就是 UI 线程被占住的时间。
    ///   不要把这些操作丢到后台线程去"测得更快"，那测的就不是用户感受了。
    /// </summary>
    internal static class ScaleBench
    {
        public static async Task RunAsync(Window mainWindow, MainWindowViewModel viewModel, int slaveCount, int pointsPerSlave, string dir)
        {
            Directory.CreateDirectory(dir);
            string logPath = Path.Combine(dir, "_bench.log");

            void Log(string message)
            {
                string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
                File.AppendAllText(logPath, line + Environment.NewLine);
                Console.WriteLine(line);
            }

            // 等主窗口完成首帧布局：窗口没铺开时 DataGrid 还没生成行容器，
            // 批量灌点时省掉的正是最贵的那部分，数字会假性变好
            await Task.Delay(900);

            // ── 0. 基线 ──
            using Process process = Process.GetCurrentProcess();
            long memBefore = GC.GetTotalMemory(forceFullCollection: true);
            Log($"[bench] 参数：从站 {slaveCount} 台 × 每台 {pointsPerSlave} 点，合计 {(long)slaveCount * pointsPerSlave:N0} 点");

            int teleSignal = pointsPerSlave / 2;              // 遥信 M_SP_NA_1
            int teleMeter = pointsPerSlave - teleSignal;       // 遥测 M_ME_NC_1（短浮点）

            // ── 1. 建站 ──
            Stopwatch sw = Stopwatch.StartNew();
            ConnectionViewModel connection = new()
            {
                Name = "压测通道",
                Protocol = ConnectionProtocol.Iec104,
                Port = 2499        // 不启动通道，只占个号，避免和真实端口冲突
            };

            List<SlaveViewModel> slaves = new(slaveCount);
            for (int index = 0; index < slaveCount; index++)
            {
                SlaveViewModel slave = new()
                {
                    Name = $"压测从站{index + 1:000}",
                    CommonAddress = index + 1
                };
                connection.Slaves.Add(slave);
                connection.HookSlave(slave);
                slaves.Add(slave);
            }

            viewModel.Connections.Add(connection);
            Log($"[bench] 1.建站 {slaveCount} 台用时 {sw.ElapsedMilliseconds} ms");

            // ── 2. 铺点：走"初始化数据"窗口的真实入口，跟用户点按钮完全同一条路径 ──
            sw.Restart();
            long worstSlaveMs = 0;
            int worstSlaveIndex = -1;
            for (int index = 0; index < slaves.Count; index++)
            {
                SlaveViewModel slave = slaves[index];
                Stopwatch one = Stopwatch.StartNew();

                // 用户实际操作就是先选中从站、再打开"初始化数据"
                viewModel.SelectedSlave = slave;

                InitDataViewModel dialog = new(slave);
                InitDataRow signalRow = dialog.Rows.First(row => row.Type == TypeIdentifier.M_SP_NA_1);
                signalRow.Enabled = true;
                signalRow.StartAddress = 1;
                signalRow.Count = teleSignal;
                signalRow.NamePrefix = "遥信";

                InitDataRow meterRow = dialog.Rows.First(row => row.Type == TypeIdentifier.M_ME_NC_1);
                meterRow.Enabled = true;
                meterRow.StartAddress = 4001;
                meterRow.Count = teleMeter;
                meterRow.NamePrefix = "遥测";
                meterRow.StartValue = 0;
                meterRow.Step = 0.5;

                dialog.Generate();

                one.Stop();
                if (one.ElapsedMilliseconds > worstSlaveMs)
                {
                    worstSlaveMs = one.ElapsedMilliseconds;
                    worstSlaveIndex = index;
                }

                // 每台之间让出一次消息循环：否则整段压测期间窗口完全无响应，
                // 连"进度条不动"都看不出来是卡死还是在跑
                await Task.Yield();
            }

            long buildMs = sw.ElapsedMilliseconds;
            int totalPoints = slaves.Sum(slave => slave.Points.Count);
            Log($"[bench] 2.铺点 {totalPoints:N0} 点用时 {buildMs} ms"
                + $"（平均 {(double)buildMs / Math.Max(1, slaves.Count):0} ms/台，"
                + $"最慢 {worstSlaveMs} ms/台 @第{worstSlaveIndex + 1}台）");

            // ── 3. 切从站（触发标签重建 + 表格换源）──
            sw.Restart();
            viewModel.SelectedSlave = slaves[0];
            Dispatcher.UIThread.RunJobs();
            viewModel.SelectedSlave = slaves[^1];
            Dispatcher.UIThread.RunJobs();
            Log($"[bench] 3.切换从站 2 次（含标签重建+表格换源）用时 {sw.ElapsedMilliseconds} ms");

            // ── 4. 按点号取点（遥控/读命令都走它）──
            SlaveViewModel bigSlave = slaves[^1];
            sw.Restart();
            int hit = 0;
            for (int round = 0; round < 1000; round++)
            {
                int address = 4001 + (round % Math.Max(1, teleMeter));
                if (bigSlave.FindPoint(address) != null)
                {
                    hit++;
                }
            }

            Log($"[bench] 4.FindPoint ×1000 用时 {sw.ElapsedMilliseconds} ms（命中 {hit}）");

            // ── 5. 总召组帧（一台 2000 点）──
            sw.Restart();
            List<IEC104.Core.CS101.Asdu> frames = bigSlave.Station.BuildInterrogationData(0);
            Log($"[bench] 5.总召组帧 用时 {sw.ElapsedMilliseconds} ms，生成 {frames.Count} 帧");

            // ── 6. 自动变化：给 10% 的点开变化，跑 5 个节拍 ──
            int activeCount = 0;
            foreach (SlaveViewModel slave in slaves)
            {
                for (int index = 0; index < slave.Points.Count; index++)
                {
                    if (index % 10 != 0)
                    {
                        continue;
                    }
                    PointViewModel point = slave.Points[index];
                    point.ChangePattern = point.Type?.Type == TypeIdentifier.M_ME_NC_1
                        ? ChangePattern.RandomWalk
                        : ChangePattern.Toggle;
                    activeCount++;
                }
            }

            viewModel.AutoChange.SetInterval(1000);
            sw.Restart();
            for (int tick = 0; tick < 5; tick++)
            {
                viewModel.AutoChange.TickNow();
            }

            long tickTotal = sw.ElapsedMilliseconds;
            Log($"[bench] 6.自动变化 {activeCount:N0} 个活跃点 ×5 拍 用时 {tickTotal} ms"
                + $"（{tickTotal / 5.0:0.0} ms/拍）");

            // 关掉变化，避免后续测量被定时器干扰
            foreach (SlaveViewModel slave in slaves)
            {
                foreach (PointViewModel point in slave.Points)
                {
                    point.ChangePattern = ChangePattern.None;
                }
            }

            // ── 7. 批量改值 2 万点（"批量变化"的最坏情况：一次点全量变化）──
            int bulkCount = Math.Min(20000, totalPoints);
            List<PointViewModel> bulk = new(bulkCount);
            foreach (SlaveViewModel slave in slaves)
            {
                foreach (PointViewModel point in slave.Points)
                {
                    if (bulk.Count >= bulkCount)
                    {
                        break;
                    }
                    bulk.Add(point);
                }
                if (bulk.Count >= bulkCount)
                {
                    break;
                }
            }

            sw.Restart();
            for (int index = 0; index < bulk.Count; index++)
            {
                bulk[index].Value = bulk[index].Value + 1;
            }

            Log($"[bench] 7.批量改值 {bulk.Count:N0} 点用时 {sw.ElapsedMilliseconds} ms");

            // ── 8. 报文监视灌入 1 万条（一次 100 台总召的规模）──
            // 拆成两段计时，因为这两件事的代价差好几个数量级：
            //   8a 入缓冲区：只是 List.Add，跟界面无关，正常情况下几十毫秒内
            //   8b 合并刷新：建新集合 + 单次 Reset + 表格重建可见行，这才是真正会卡界面的部分
            // 只报 8a 是没有意义的（旧版就只报了 8a，4 ms 看着很漂亮，其实一条都没上屏）
            sw.Restart();
            for (int index = 0; index < 10000; index++)
            {
                viewModel.AppendRecord(new AsduRecord
                {
                    Direction = "发",
                    TypeCode = "M_ME_NC_1",
                    TypeName = "短浮点遥测",
                    CauseText = "响应站召唤(20)",
                    CommonAddress = index % 100 + 1,
                    ObjectSummary = $"IOA={4001 + index % 1000}",
                    Hex = "68 1A 00 00 00 00 0D 01 14 00 01 00 00 00 00 00"
                });
            }

            long appendMs = sw.ElapsedMilliseconds;

            sw.Restart();
            viewModel.FlushRecords();          // 强制把缓冲区合并上屏，量真实开销
            Dispatcher.UIThread.RunJobs();     // 让表格完成一次布局，把行容器重建也算进去
            long flushMs = sw.ElapsedMilliseconds;

            Log($"[bench] 8.报文灌入 10,000 条：入缓冲 {appendMs} ms + 合并刷新 {flushMs} ms"
                + $"（上屏 {viewModel.Records.Count:N0} 条）");

            // ── 9. 主题切换：会给每个点位刷新 3 个依赖颜色的属性 ──
            sw.Restart();
            ThemeService.Apply(ThemeService.Options[^1].Id);
            Dispatcher.UIThread.RunJobs();
            Log($"[bench] 9.切换主题（刷新 {totalPoints:N0} 个点位的配色）用时 {sw.ElapsedMilliseconds} ms");
            ThemeService.Apply(ThemeService.Options[0].Id);
            Dispatcher.UIThread.RunJobs();

            // ── 10. 内存（20 万点常驻的真实占用）──
            // 必须在工作区存取之前量：载入会把整棵对象树换掉，
            // 而本方法里的 slaves 列表还攥着旧的那一份，量出来是双份，不是运行占用。
            long memAfter = GC.GetTotalMemory(forceFullCollection: true);
            process.Refresh();
            Log($"[bench] 10.内存：托管 {(memAfter - memBefore) / 1024.0 / 1024.0:0.0} MB"
                + $"，进程私有 {process.PrivateMemorySize64 / 1024.0 / 1024.0:0.0} MB");

            // ── 11. 工程存取：现场最容易被忽略的一次性"卡死" ──
            // 20 万点序列化成 JSON 是几十 MB，保存和打开都在 UI 线程上跑，
            // 这俩操作如果没优化，用户点一次"保存"就是十几秒无响应。
            string workspacePath = Path.Combine(dir, "bench-project" + ProjectStore.Extension);

            sw.Restart();
            viewModel.SaveProjectTo(workspacePath, "压测工程");
            long saveMs = sw.ElapsedMilliseconds;
            long fileBytes = File.Exists(workspacePath) ? new FileInfo(workspacePath).Length : 0;

            sw.Restart();
            int loaded = viewModel.OpenProject(workspacePath);
            Dispatcher.UIThread.RunJobs();
            long loadMs = sw.ElapsedMilliseconds;

            Log($"[bench] 11.工程存取 {totalPoints:N0} 点：保存 {saveMs} ms"
                + $"（{fileBytes / 1024.0 / 1024.0:0.0} MB）+ 载入 {loadMs} ms（回读 {loaded:N0} 点）");

            Log("[bench] done");
        }
    }
}
