using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace IEC104.App.Services
{
    /// <summary>
    /// 真正动系统的那一层：逐个执行 netsh，**执行完回读系统状态**判断成败。
    ///
    /// 【为什么不解析 netsh 的输出】
    /// netsh 的错误文本是本地化的：中文系统说"对象已存在"，英文系统说
    /// "The object already exists."，再换个语言又变了。用文本判断成败等于把
    /// 程序绑死在某个语言的某个版本上，而且每次 Windows 更新都可能悄悄改措辞。
    /// 退出码也不可靠：很多"加不上"的情况 netsh 照样返回 0。
    ///
    /// 所以判据只有一条：**动作做完之后去看系统里到底有没有这个地址**。
    /// 加完有 → 成功；删完没有 → 成功；否则拿 netsh 的输出当提示信息，但不参与判定。
    /// </summary>
    internal static class IpCommandRunner
    {
        /// <summary>执行完等系统状态生效的上限（毫秒）</summary>
        private const int VerifyTimeoutMs = 3000;

        private const int VerifyPollMs = 150;

        public static IReadOnlyList<IpChangeResult> Apply(IReadOnlyList<IpChangeRequest> requests)
        {
            List<IpChangeResult> results = new();
            foreach (IpChangeRequest request in requests ?? Array.Empty<IpChangeRequest>())
            {
                results.Add(ApplyOne(request));
            }
            return results;
        }

        private static IpChangeResult ApplyOne(IpChangeRequest request)
        {
            string address = (request?.Address ?? string.Empty).Trim();
            if (request == null || !IPAddress.TryParse(address, out IPAddress parsed)
                || parsed.AddressFamily != AddressFamily.InterNetwork)
            {
                return IpChangeResult.Failed(address, "地址不合法，未执行。");
            }

            bool presentBefore = LocalHasAddress(address);
            bool isAdd = !string.Equals(request.Action, "delete", StringComparison.OrdinalIgnoreCase);

            if (isAdd && presentBefore)
            {
                return IpChangeResult.Skipped(address);
            }

            if (!isAdd && !presentBefore)
            {
                // 要删的本来就不在：算成功（账本自愈），否则用户会一直看到一条删不掉的记录
                return new IpChangeResult
                {
                    Address = address,
                    Ok = true,
                    AlreadyExists = true,
                    Message = "本机已无该地址，无需删除"
                };
            }

            string nicName = request.NicName;
            if (string.IsNullOrWhiteSpace(nicName))
            {
                // 提权进程里现查一次：netsh 只认名字，不认 GUID
                nicName = new IpAddressService().FindNic(request.NicId)?.Name;
            }

            if (string.IsNullOrWhiteSpace(nicName))
            {
                return IpChangeResult.Failed(address, "找不到对应的网卡，未执行。");
            }

            // 网卡名在"父进程决定要加"到"子进程真正执行"之间可能已经变了（拔了、
            // 被禁用了、UAC 窗口停留时用户去改了名）。这时 netsh 会给一句
            // "文件名、目录名或卷标语法不正确"——本地化且完全看不出是网卡没了。
            // 所以在执行前先确认这个名字还在。
            if (!NicExists(nicName))
            {
                return IpChangeResult.Failed(address, $"网卡“{nicName}”现在不在本机（可能被禁用或已拔出）。");
            }

            string mask = string.IsNullOrWhiteSpace(request.Mask) ? IpMask.DefaultMask : request.Mask.Trim();

            string output;
            if (isAdd)
            {
                output = RunNetsh($"interface ipv4 add address name=\"{Clean(nicName)}\" address={address} mask={mask}");
                bool ok = WaitForState(address, expectedPresent: true);
                return ok
                    ? new IpChangeResult { Address = address, Ok = true, Message = "已添加" }
                    : IpChangeResult.Failed(address, Describe(output, "系统没有接受这个地址。"));
            }

            output = RunNetsh($"interface ipv4 delete address name=\"{Clean(nicName)}\" address={address}");
            if (LocalHasAddress(address))
            {
                // 带网关的地址条目要单独指定 gateway=all 才删得掉
                output = RunNetsh($"interface ipv4 delete address name=\"{Clean(nicName)}\" address={address} gateway=all");
            }

            bool removed = WaitForState(address, expectedPresent: false);
            return removed
                ? new IpChangeResult { Address = address, Ok = true, Message = "已删除" }
                : IpChangeResult.Failed(address, Describe(output, "系统没有删掉这个地址。"));
        }

        /// <summary>netsh 的名字参数里不可能有引号，有就说明数据脏了，去掉免得拼坏命令行</summary>
        private static string Clean(string text) => (text ?? string.Empty).Replace("\"", string.Empty);

        /// <summary>系统里还有没有叫这个名字的网卡（netsh 只认名字，所以只能按名字查）</summary>
        private static bool NicExists(string nicName)
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces().Any(nic =>
                    string.Equals(nic.Name, nicName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                // 枚举失败就不拦：真找不到时 netsh 那条路还会失败，只是提示难懂一点
                return true;
            }
        }

        private static string Describe(string output, string fallback)
        {
            string text = (output ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return fallback;
            }

            // netsh 会连着打几行提示，只留前两行，界面上放不下更多
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string joined = string.Join(" ", lines.Take(2)).Trim();
            return joined.Length > 200 ? joined[..200] : joined;
        }

        /// <summary>轮询等系统状态到位。加地址不是瞬时的，服务会有一小段重新配置的时间</summary>
        private static bool WaitForState(string address, bool expectedPresent)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < VerifyTimeoutMs)
            {
                if (LocalHasAddress(address) == expectedPresent)
                {
                    return true;
                }

                System.Threading.Thread.Sleep(VerifyPollMs);
            }

            return LocalHasAddress(address) == expectedPresent;
        }

        internal static bool LocalHasAddress(string address)
        {
            if (!IPAddress.TryParse((address ?? string.Empty).Trim(), out IPAddress parsed))
            {
                return false;
            }

            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    try
                    {
                        foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                        {
                            if (info.Address.Equals(parsed))
                            {
                                return true;
                            }
                        }
                    }
                    catch
                    {
                        // 单块网卡读失败不影响其它网卡
                    }
                }
            }
            catch
            {
                // 枚举失败按"没有"处理，最坏结果是多执行一次 netsh，不会误删
            }

            return false;
        }

        /// <summary>
        /// 调 netsh。不弹窗（CreateNoWindow），但把 stdout/stderr 都收下来，
        /// 失败时当提示信息用。
        /// </summary>
        private static string RunNetsh(string arguments)
        {
            try
            {
                string netsh = Path.Combine(Environment.SystemDirectory ?? @"C:\Windows\System32", "netsh.exe");
                ProcessStartInfo info = new()
                {
                    FileName = File.Exists(netsh) ? netsh : "netsh.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                using Process process = Process.Start(info);
                if (process == null)
                {
                    return string.Empty;
                }

                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(10000);
                return (stdout + " " + stderr).Trim();
            }
            catch (Exception exception)
            {
                return exception.Message;
            }
        }
    }

    /// <summary>
    /// 提权子进程的协议：父进程写请求 JSON → 子进程执行 → 写结果 JSON。
    ///
    /// 【为什么走文件而不是管道/命令行参数】命令行参数会把一长串地址塞进
    /// "进程参数"里，中文网卡名要过一遍编码，一旦碰到引号或空格就散架。
    /// 临时文件是字节流，塞什么都不用转义。两个文件用完即删。
    ///
    /// 【为什么一次 UAC 覆盖一批】批量建从站可能一口气要加十几个地址。
    /// 每个地址弹一次 UAC，用户点到第三次就会去点"否"，然后觉得"这工具坏了"。
    /// </summary>
    internal static class IpHelperProtocol
    {
        public const string Flag = "--ip-helper";

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            // 中文不要被转成 \uXXXX：这两个文件虽然是临时的，但出问题时要靠人打开看
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static bool IsInvocation(string[] args)
        {
            return args != null
                && args.Length > 0
                && string.Equals(args[0], Flag, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 在**子进程**里跑：读请求 → 执行 → 写结果 → 退出。
        /// 返回进程退出码，0 表示"过程本身没出岔子"（每条地址的成败看结果文件）。
        /// </summary>
        public static int Run(string[] args)
        {
            if (args == null || args.Length < 3)
            {
                return 2;
            }

            string requestPath = args[1];
            string resultPath = args[2];

            IReadOnlyList<IpChangeResult> results;
            try
            {
                List<IpChangeRequest> requests = new();
                if (File.Exists(requestPath))
                {
                    string json = File.ReadAllText(requestPath);
                    requests = JsonSerializer.Deserialize<List<IpChangeRequest>>(json, Options) ?? new List<IpChangeRequest>();
                }

                results = IpCommandRunner.Apply(requests);
            }
            catch (Exception exception)
            {
                results = new[]
                {
                    IpChangeResult.Failed(string.Empty, "提权进程出错：" + exception.Message)
                };
            }

            try
            {
                File.WriteAllText(resultPath, JsonSerializer.Serialize(results, Options),
                    new System.Text.UTF8Encoding(false));
            }
            catch
            {
                return 3;
            }

            return 0;
        }

        /// <summary>父进程一侧：拉起提权 helper 并把结果读回来</summary>
        public static async Task<IReadOnlyList<IpChangeResult>> RunElevatedAsync(
            IReadOnlyList<IpChangeRequest> requests)
        {
            if (requests == null || requests.Count == 0)
            {
                return Array.Empty<IpChangeResult>();
            }

            string stamp = DateTime.Now.ToString("HHmmssfff");
            string requestPath = Path.Combine(Path.GetTempPath(), $"iec104-ipreq-{stamp}.json");
            string resultPath = Path.Combine(Path.GetTempPath(), $"iec104-ipres-{stamp}.json");

            try
            {
                File.WriteAllText(requestPath, JsonSerializer.Serialize(requests, Options),
                    new System.Text.UTF8Encoding(false));

                // 单文件发布时 Environment.ProcessPath 才是真 exe；ProcessName 拿不到路径
                string exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    return AllFailed(requests, "拿不到本程序路径，无法提权执行。");
                }

                ProcessStartInfo info = new()
                {
                    FileName = exePath,
                    Arguments = $"{Flag} \"{requestPath}\" \"{resultPath}\"",
                    UseShellExecute = true,      // runas 必须走 ShellExecute
                    Verb = "runas",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                Process process;
                try
                {
                    process = Process.Start(info);
                }
                catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
                {
                    // 1223 = ERROR_CANCELLED，用户在 UAC 上点了"否"
                    return requests
                        .Select(item => IpChangeResult.Denied(item.Address,
                            "需要管理员权限才能修改本机 IP，刚才在授权窗口点了“否”。"))
                        .ToList();
                }
                catch (Exception exception)
                {
                    return AllFailed(requests, "拉起提权进程失败：" + exception.Message);
                }

                if (process == null)
                {
                    return AllFailed(requests, "提权进程没有启动。");
                }

                using (process)
                {
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }

                // 结果文件可能还在落盘，给它一点时间
                for (int attempt = 0; attempt < 20 && !File.Exists(resultPath); attempt++)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }

                if (!File.Exists(resultPath))
                {
                    return AllFailed(requests, "提权进程没有返回结果（可能被安全软件拦下了）。");
                }

                string json = File.ReadAllText(resultPath);
                List<IpChangeResult> parsed = JsonSerializer.Deserialize<List<IpChangeResult>>(json, Options);
                if (parsed == null || parsed.Count == 0)
                {
                    return AllFailed(requests, "提权进程返回了空结果。");
                }

                return parsed;
            }
            catch (Exception exception)
            {
                return AllFailed(requests, "执行提权请求失败：" + exception.Message);
            }
            finally
            {
                TryDelete(requestPath);
                TryDelete(resultPath);
            }
        }

        private static IReadOnlyList<IpChangeResult> AllFailed(
            IReadOnlyList<IpChangeRequest> requests, string message)
        {
            return (requests ?? Array.Empty<IpChangeRequest>())
                .Select(item => IpChangeResult.Failed(item?.Address, message))
                .ToList();
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
            catch
            {
                // 临时文件删不掉不影响功能，%TEMP% 自己会清
            }
        }
    }

    /// <summary>
    /// 默认的地址编辑通道：本来就有管理员权限就直接干，否则拉一个提权子进程。
    ///
    /// 【为什么先判断"已经提权"】工具装在现场的机器上时，运维常常本来就是以管理员
    /// 身份登录的。这种情况下再套一层 runas，等于每次改 IP 都白弹一次 UAC
    /// （或者更糟：在 UAC 关掉的机器上 runas 直接失败）。
    /// </summary>
    public sealed class ElevatedIpAddressEditor : IIpAddressEditor
    {
        public async Task<IReadOnlyList<IpChangeResult>> ApplyAsync(
            IReadOnlyList<IpChangeRequest> requests)
        {
            if (requests == null || requests.Count == 0)
            {
                return Array.Empty<IpChangeResult>();
            }

            if (IpAddressService.IsElevated())
            {
                // netsh 是同步阻塞的，挪到线程池，别卡住界面线程
                return await Task.Run(() => IpCommandRunner.Apply(requests)).ConfigureAwait(true);
            }

            return await IpHelperProtocol.RunElevatedAsync(requests).ConfigureAwait(true);
        }
    }
}
