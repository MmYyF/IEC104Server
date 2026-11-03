using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace IEC104.App.Services
{
    /// <summary>一块网卡（读出来的快照，不持有系统句柄）</summary>
    public sealed class NicInfo
    {
        /// <summary>网卡 GUID。**配置里只存这个**：名字可以改、中英文系统名字不同，GUID 不会变</summary>
        public string Id { get; init; }

        /// <summary>网卡名称（"以太网" / "Ethernet"，用户可改）</summary>
        public string Name { get; init; }

        /// <summary>驱动描述（"Intel(R) I219-V" 这类），用来在名字相同时区分</summary>
        public string Description { get; init; }

        public bool IsUp { get; init; }

        /// <summary>虚拟网卡（VMware / Hyper-V / VPN / TAP）：能选，但要标出来，防止选错</summary>
        public bool IsVirtual { get; init; }

        public long SpeedMbps { get; init; }

        public string MacAddress { get; init; }

        public IReadOnlyList<IpAddressEntry> Addresses { get; init; } = Array.Empty<IpAddressEntry>();

        public string StatusText => IsUp ? "已连接" : "未连接";

        public string DisplayText => $"{Name} · {Description}";

        /// <summary>列表里第一行显示的内容："以太网  192.168.1.5/24"</summary>
        public string SummaryText
        {
            get
            {
                IpAddressEntry first = Addresses.FirstOrDefault();
                string address = first == null ? "无 IPv4" : $"{first.Address}/{first.PrefixLength}";
                string more = Addresses.Count > 1 ? $" 等 {Addresses.Count} 个" : string.Empty;
                return $"{address}{more}";
            }
        }
    }

    /// <summary>网卡上的一个 IPv4 地址</summary>
    public sealed class IpAddressEntry
    {
        public string Address { get; init; }

        public int PrefixLength { get; init; }

        public bool IsDhcp { get; init; }

        /// <summary>本工具添加的（账本里有）→ 允许删除</summary>
        public bool IsManaged { get; set; }

        public string MaskText => IpMask.PrefixToMask(PrefixLength);

        public string SourceText => IsManaged ? "本工具" : IsDhcp ? "DHCP" : "系统";

        public string DisplayText => $"{Address}/{PrefixLength}";
    }

    /// <summary>子网掩码与前缀长度的互转</summary>
    public static class IpMask
    {
        public const string DefaultMask = "255.255.255.0";

        /// <summary>掩码 → 前缀长度；不是连续掩码返回 -1</summary>
        public static int MaskToPrefix(string mask)
        {
            if (!IPAddress.TryParse(mask ?? string.Empty, out IPAddress parsed)
                || parsed.AddressFamily != AddressFamily.InterNetwork)
            {
                return -1;
            }

            byte[] bytes = parsed.GetAddressBytes();
            int prefix = 0;
            bool sawZero = false;
            foreach (byte value in bytes)
            {
                for (int bit = 7; bit >= 0; bit--)
                {
                    bool one = (value & (1 << bit)) != 0;
                    if (one && sawZero)
                    {
                        return -1;          // 中间夹了 0，不是连续掩码
                    }
                    if (one)
                    {
                        prefix++;
                    }
                    else
                    {
                        sawZero = true;
                    }
                }
            }
            return prefix;
        }

        /// <summary>前缀长度 → 掩码</summary>
        public static string PrefixToMask(int prefixLength)
        {
            if (prefixLength <= 0 || prefixLength > 32)
            {
                return DefaultMask;
            }

            uint value = prefixLength == 32 ? uint.MaxValue : ~(uint.MaxValue >> prefixLength);
            byte[] bytes =
            {
                (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
            };
            return new IPAddress(bytes).ToString();
        }
    }

    /// <summary>一次地址变更请求（提权进程按这个执行）</summary>
    public sealed class IpChangeRequest
    {
        /// <summary>"add" 或 "delete"</summary>
        public string Action { get; set; }

        public string NicId { get; set; }

        /// <summary>网卡名称（提权进程里现查一次填进来，netsh 只认名字不认 GUID）</summary>
        public string NicName { get; set; }

        public string Address { get; set; }

        public string Mask { get; set; } = IpMask.DefaultMask;

        public static IpChangeRequest Add(string nicId, string nicName, string address, string mask) =>
            new() { Action = "add", NicId = nicId, NicName = nicName, Address = address, Mask = mask };

        public static IpChangeRequest Delete(string nicId, string nicName, string address, string mask) =>
            new() { Action = "delete", NicId = nicId, NicName = nicName, Address = address, Mask = mask };
    }

    /// <summary>一条变更的执行结果</summary>
    public sealed class IpChangeResult
    {
        public string Address { get; set; }

        public bool Ok { get; set; }

        /// <summary>本来就有，跳过（算成功）</summary>
        public bool AlreadyExists { get; set; }

        /// <summary>需要管理员权限（UAC 被拒 / 没提权）</summary>
        public bool NeedElevation { get; set; }

        public string Message { get; set; }

        public static IpChangeResult Skipped(string address) =>
            new() { Address = address, Ok = true, AlreadyExists = true, Message = "本机已有该地址，跳过" };

        public static IpChangeResult Denied(string address, string message) =>
            new() { Address = address, Ok = false, NeedElevation = true, Message = message };

        public static IpChangeResult Failed(string address, string message) =>
            new() { Address = address, Ok = false, Message = message };
    }

    /// <summary>
    /// 真正执行地址变更的通道。真实实现是"提权子进程"，自检里换成假的。
    ///
    /// 【为什么抽成接口】自检是无人值守的，不能真的弹 UAC。
    /// 这和 1.10.2 把"另存为"抽成 SaveAsFallback 是同一个思路：
    /// 凡是需要人工交互或系统授权的动作，都要留一个可替换的接缝。
    /// </summary>
    public interface IIpAddressEditor
    {
        System.Threading.Tasks.Task<IReadOnlyList<IpChangeResult>> ApplyAsync(
            IReadOnlyList<IpChangeRequest> requests);
    }

    /// <summary>
    /// 本机网卡与 IPv4 地址的读写服务。
    ///
    /// 分工：
    ///   · **读**（ListNics / IsLocalAddress）不需要管理员权限，直接走 .NET 的 NetworkInterface；
    ///   · **写**（加 / 删）必须管理员，交给 <see cref="IIpAddressEditor"/>（默认是提权子进程）；
    ///   · **账本**记在本机 settings.json 里，只记本工具加过的地址 —— 删的时候只认账本。
    /// </summary>
    public sealed class IpAddressService
    {
        /// <summary>
        /// 全应用共用的一个实例。
        ///
        /// 【为什么要共用】<see cref="Editor"/> 是可替换的接缝：自检把它换成假实现，
        /// 如果每个 ViewModel 各 new 一个服务，替换到的那份和真正被执行到的不是同一个对象，
        /// 自检就会变成"断言绿了但动作没发生"。共用一份才不会踩这种假绿。
        /// </summary>
        public static IpAddressService Shared { get; } = new();

        /// <summary>可替换的执行通道（自检注入替身）</summary>
        public IIpAddressEditor Editor { get; set; } = new ElevatedIpAddressEditor();

        /// <summary>
        /// 当前进程是不是已经具备管理员权限。
        ///
        /// 界面上要用到它：非管理员时"添加地址"旁边标一句"会弹一次授权"，
        /// 用户就不会在 UAC 弹出来的时候以为是病毒。提权通道也用它决定要不要拉子进程。
        /// </summary>
        public static bool IsElevated()
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                using System.Security.Principal.WindowsIdentity identity =
                    System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal principal = new(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                // 受限环境：按"没提权"处理，走提权子进程那条路
                return false;
            }
        }

        /// <summary>枚举本机全部网卡（含未连接；排除回环）</summary>
        public IReadOnlyList<NicInfo> ListNics()
        {
            List<NicInfo> list = new();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    IPInterfaceProperties properties;
                    try
                    {
                        properties = nic.GetIPProperties();
                    }
                    catch
                    {
                        // 某些虚拟网卡取属性会抛，跳过而不是让整个列表空掉
                        continue;
                    }

                    // DhcpServerAddresses 在非 Windows 平台没有实现（本工具只发 Windows 版），
                    // 这里单点压掉平台分析告警，而不是把整个类标成 Windows-only ——
                    // 标了以后所有调用方都要跟着加守卫，代价远大于收益。
#pragma warning disable CA1416
                    bool dhcp = properties.DhcpServerAddresses.Count > 0;
#pragma warning restore CA1416
                    List<IpAddressEntry> addresses = new();
                    foreach (UnicastIPAddressInformation info in properties.UnicastAddresses)
                    {
                        if (info.Address.AddressFamily != AddressFamily.InterNetwork)
                        {
                            continue;
                        }

                        addresses.Add(new IpAddressEntry
                        {
                            Address = info.Address.ToString(),
                            PrefixLength = info.PrefixLength,
                            IsDhcp = dhcp
                        });
                    }

                    list.Add(new NicInfo
                    {
                        Id = nic.Id,
                        Name = nic.Name,
                        Description = nic.Description,
                        IsUp = nic.OperationalStatus == OperationalStatus.Up,
                        IsVirtual = LooksVirtual(nic),
                        SpeedMbps = nic.Speed > 0 ? nic.Speed / 1_000_000 : 0,
                        MacAddress = FormatMac(nic.GetPhysicalAddress()),
                        Addresses = addresses
                    });
                }
            }
            catch
            {
                // 枚举失败就返回已拿到的部分，界面至少不是空的
            }

            // 已连接的排前面：用户十有八九要往"正在用的那块"上加地址
            return list
                .OrderByDescending(nic => nic.IsUp)
                .ThenBy(nic => nic.IsVirtual)
                .ThenBy(nic => nic.Name, StringComparer.CurrentCulture)
                .ToList();
        }

        /// <summary>这个地址是不是本机拥有（任意网卡、任意状态）</summary>
        public bool IsLocalAddress(string address)
        {
            if (!IPAddress.TryParse((address ?? string.Empty).Trim(), out IPAddress parsed))
            {
                return false;
            }

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
                    // 单块网卡读失败不影响判断其它网卡
                }
            }

            return false;
        }

        /// <summary>这个地址目前挂在哪块网卡上（没有则 null），用于给用户一句"本机已有此地址（以太网）"</summary>
        public NicInfo FindOwner(string address)
        {
            if (!IPAddress.TryParse((address ?? string.Empty).Trim(), out IPAddress parsed))
            {
                return null;
            }

            return ListNics().FirstOrDefault(nic =>
                nic.Addresses.Any(entry => IPAddress.TryParse(entry.Address, out IPAddress existing)
                    && existing.Equals(parsed)));
        }

        public NicInfo FindNic(string nicId) =>
            string.IsNullOrWhiteSpace(nicId)
                ? null
                : ListNics().FirstOrDefault(nic => string.Equals(nic.Id, nicId, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// 把"这个监听地址在本机是什么状态"说成一句话。
        ///
        /// **只有这一份定义**：通道属性、新建从站对话框、批量建站预览三个地方
        /// 说的都是同一句话，否则同一件事在三处措辞不同，用户会以为是三件事。
        /// 第二个返回值表示"需要用户注意"（本机没有又没开自动添加 / 地址不合法）。
        /// </summary>
        public static (string Text, bool Warning) DescribePlan(string address, string nicId, bool autoAdd)
        {
            string ip = (address ?? string.Empty).Trim();
            if (ip.Length == 0 || ip == "0.0.0.0" || ip == "*")
            {
                return ("监听全部网卡，不需要添加地址", false);
            }

            if (!IPAddress.TryParse(ip, out _))
            {
                return ($"“{ip}”不是合法的 IPv4 地址", true);
            }

            NicInfo owner = Shared.FindOwner(ip);
            if (owner != null)
            {
                return ($"{ip} 已在本机（{owner.Name}）", false);
            }

            if (!autoAdd)
            {
                return ($"本机没有 {ip}。勾上“启动时自动添加”并选好网卡，启动时才补得上。", true);
            }

            string nicName = string.IsNullOrWhiteSpace(nicId)
                ? "未选网卡"
                : Shared.FindNic(nicId)?.Name ?? "网卡已不存在";
            return ($"本机没有 {ip}，启动时添加到 {nicName}。", false);
        }

        /// <summary>
        /// 同一件事的短版本，给表格单元格用（批量建站的预览列）。
        /// 长句子塞进 40px 宽的列只会被截成"本机没有 192.1…"，反而看不出结论。
        /// </summary>
        public static string ShortStatus(string address, string nicId, bool autoAdd)
        {
            string ip = (address ?? string.Empty).Trim();
            if (ip.Length == 0 || ip == "0.0.0.0" || ip == "*")
            {
                return "全部网卡";
            }

            if (!IPAddress.TryParse(ip, out _))
            {
                return "地址不合法";
            }

            NicInfo owner = Shared.FindOwner(ip);
            if (owner != null)
            {
                return "已有 · " + owner.Name;
            }

            if (!autoAdd)
            {
                return "本机没有";
            }

            string nicName = string.IsNullOrWhiteSpace(nicId)
                ? "未选网卡"
                : Shared.FindNic(nicId)?.Name ?? "网卡已不存在";
            return "启动时添加 · " + nicName;
        }

        /// <summary>
        /// IP 字符串 → 32 位整数，方便按步长相加（末段超 255 会自动进位）。
        ///
        /// **只有这一份实现**：批量建站和批量加地址都要按步长算一串 IP，
        /// 各写一份迟早出现"一个会进位、一个会算错"。
        /// </summary>
        public static bool TryParseIpToUInt32(string text, out uint value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string[] parts = text.Trim().Split('.');
            if (parts.Length != 4)
            {
                return false;
            }

            uint result = 0;
            foreach (string part in parts)
            {
                if (!byte.TryParse(part, out byte octet))
                {
                    return false;
                }
                result = (result << 8) | octet;
            }

            value = result;
            return true;
        }

        /// <summary>32 位整数 → IP 字符串</summary>
        public static string UInt32ToIp(uint value) =>
            $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";

        /// <summary>账本（本工具加过的地址）</summary>
        public IReadOnlyList<ManagedAddress> Managed => AppSettingsStore.Current.ManagedAddresses;

        /// <summary>
        /// 本机全部 IPv4 的快照。
        ///
        /// 【为什么要这个】`IsLocalAddress` 每调一次就把所有网卡重新枚举一遍。
        /// 批量加 200 个地址时逐个判断 = 200 次枚举，界面上会明显卡一下。
        /// 批处理的场合先取一次快照，之后只用集合查。
        /// </summary>
        public HashSet<string> SnapshotLocalAddresses()
        {
            HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
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
                            if (info.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                set.Add(info.Address.ToString());
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
                // 枚举失败就当"都没有"：最坏是多执行一次 netsh，不会误删
            }

            return set;
        }

        /// <summary>
        /// 确保某个地址在本机存在：已在 → 跳过；不在 → 交给提权通道添加并记账。
        /// 单个与批量走的是同一条路径（<see cref="EnsureManyAsync"/>），
        /// 免得"手工加一个"和"批量加十个"出现两套判定。
        /// </summary>
        public async System.Threading.Tasks.Task<IpChangeResult> EnsureAsync(
            string address, string nicId, string mask, string source)
        {
            IReadOnlyList<IpChangeResult> results = await EnsureManyAsync(
                new[] { address }, nicId, mask, source).ConfigureAwait(true);

            string ip = (address ?? string.Empty).Trim();
            return results?.FirstOrDefault() ?? IpChangeResult.Failed(ip, "提权进程没有返回结果。");
        }

        /// <summary>
        /// 批量确保地址存在。**一次提权覆盖整批** —— 每个地址弹一次 UAC，
        /// 用户点到第三次就会去点"否"，然后觉得"这工具坏了"。
        ///
        /// 返回的顺序与入参一一对应（跳过的和失败的也在里面），调用方可以按下标回填。
        /// </summary>
        public async System.Threading.Tasks.Task<IReadOnlyList<IpChangeResult>> EnsureManyAsync(
            IReadOnlyList<string> addresses, string nicId, string mask, string source)
        {
            List<string> inputs = (addresses ?? Array.Empty<string>())
                .Select(item => (item ?? string.Empty).Trim())
                .ToList();

            IpChangeResult[] results = new IpChangeResult[inputs.Count];

            string target = string.IsNullOrWhiteSpace(nicId) ? AppSettingsStore.Current.DefaultNicId : nicId;
            NicInfo nic = FindNic(target);

            List<string> pending = new();
            List<int> pendingIndex = new();
            HashSet<string> snapshot = SnapshotLocalAddresses();

            for (int index = 0; index < inputs.Count; index++)
            {
                string ip = inputs[index];

                if (!IPAddress.TryParse(ip, out _))
                {
                    results[index] = IpChangeResult.Failed(ip, $"“{ip}”不是合法的 IPv4 地址。");
                    continue;
                }

                // 关键：判据是"本机任一网卡上已有"，不是只看目标网卡。
                // 否则同一个地址会在两块网卡上各挂一份，主站从哪块来都说不清。
                if (snapshot.Contains(ip))
                {
                    results[index] = IpChangeResult.Skipped(ip);
                    continue;
                }

                if (nic == null)
                {
                    results[index] = IpChangeResult.Failed(ip,
                        string.IsNullOrWhiteSpace(target)
                            ? "没有选择网卡。"
                            : "指定的网卡在本机找不到（换机器或被禁用），请重新选择网卡。");
                    continue;
                }

                if (!nic.IsUp)
                {
                    results[index] = IpChangeResult.Failed(ip,
                        $"网卡“{nic.Name}”未连接，地址加上去也不会生效。");
                    continue;
                }

                pending.Add(ip);
                pendingIndex.Add(index);
            }

            if (pending.Count == 0)
            {
                return results;
            }

            string effectiveMask = string.IsNullOrWhiteSpace(mask) ? IpMask.DefaultMask : mask.Trim();

            List<IpChangeRequest> requests = pending
                .Select(ip => IpChangeRequest.Add(nic.Id, nic.Name, ip, effectiveMask))
                .ToList();

            IReadOnlyList<IpChangeResult> applied = await Editor.ApplyAsync(requests).ConfigureAwait(true);

            for (int slot = 0; slot < pending.Count; slot++)
            {
                string ip = pending[slot];
                IpChangeResult one = applied != null && slot < applied.Count
                    ? applied[slot]
                    : IpChangeResult.Failed(ip, "提权进程没有返回结果。");

                results[pendingIndex[slot]] = one;

                if (one.Ok)
                {
                    Remember(ip, nic, effectiveMask, source);
                }
            }

            return results.Select(item => item ?? IpChangeResult.Failed(string.Empty, "未执行。")).ToList();
        }

        /// <summary>删掉账本里的地址（只认账本，删不到别的东西）</summary>
        public async System.Threading.Tasks.Task<IReadOnlyList<IpChangeResult>> RemoveManagedAsync(
            IReadOnlyList<ManagedAddress> targets)
        {
            List<ManagedAddress> real = (targets ?? Array.Empty<ManagedAddress>())
                .Where(item => item != null)
                .ToList();
            if (real.Count == 0)
            {
                return Array.Empty<IpChangeResult>();
            }

            List<IpChangeRequest> requests = new();
            foreach (ManagedAddress item in real)
            {
                NicInfo nic = FindNic(item.NicId);
                if (nic == null)
                {
                    // 网卡没了：账本里这条已经无法操作，直接清掉，否则永远删不掉
                    Forget(item);
                    continue;
                }
                requests.Add(IpChangeRequest.Delete(nic.Id, nic.Name, item.Address, item.Mask));
            }

            if (requests.Count == 0)
            {
                return Array.Empty<IpChangeResult>();
            }

            IReadOnlyList<IpChangeResult> results = await Editor.ApplyAsync(requests).ConfigureAwait(true);
            foreach (IpChangeResult result in results ?? Array.Empty<IpChangeResult>())
            {
                if (result.Ok)
                {
                    ManagedAddress match = real.FirstOrDefault(item =>
                        string.Equals(item.Address, result.Address, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        Forget(match);
                    }
                }
            }
            return results ?? Array.Empty<IpChangeResult>();
        }

        /// <summary>把"本工具加过"记进账本（同一地址不重复记）</summary>
        public void Remember(string address, NicInfo nic, string mask, string source)
        {
            if (Managed.Any(item => string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            AppSettingsStore.Current.ManagedAddresses.Add(new ManagedAddress
            {
                NicId = nic?.Id,
                NicName = nic?.Name,
                Address = address,
                Mask = string.IsNullOrWhiteSpace(mask) ? IpMask.DefaultMask : mask,
                AddedAt = DateTime.Now,
                Source = source
            });
            AppSettingsStore.Save();
        }

        /// <summary>从账本里去掉一条</summary>
        public void Forget(ManagedAddress item)
        {
            if (item == null)
            {
                return;
            }

            AppSettingsStore.Current.ManagedAddresses.Remove(item);
            AppSettingsStore.Save();
        }

        /// <summary>把账本状态刷到地址行上（哪几条是本工具加的）</summary>
        public void MarkManaged(IReadOnlyList<NicInfo> nics)
        {
            if (nics == null)
            {
                return;
            }

            foreach (NicInfo nic in nics)
            {
                foreach (IpAddressEntry entry in nic.Addresses)
                {
                    entry.IsManaged = Managed.Any(item =>
                        string.Equals(item.Address, entry.Address, StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        /// <summary>
        /// 账本自愈：地址已经不在系统里（重启后被人手工删了 / 网卡换了），
        /// 就从账本里去掉，免得"清理"按钮对着空气点。
        /// </summary>
        public int PruneLedger()
        {
            List<ManagedAddress> list = AppSettingsStore.Current.ManagedAddresses;
            int removed = 0;
            for (int index = list.Count - 1; index >= 0; index--)
            {
                if (!IsLocalAddress(list[index].Address))
                {
                    list.RemoveAt(index);
                    removed++;
                }
            }

            if (removed > 0)
            {
                AppSettingsStore.Save();
            }
            return removed;
        }

        private static bool LooksVirtual(NetworkInterface nic)
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel
                or NetworkInterfaceType.Ppp)
            {
                return true;
            }

            string text = (nic.Description ?? string.Empty) + " " + (nic.Name ?? string.Empty);
            string[] hints =
            {
                "VMware", "VirtualBox", "Hyper-V", "Virtual", "TAP-", "TUN", "WAN Miniport",
                "Loopback", "Npcap", "VPN", "ZeroTier", "Tailscale"
            };
            return hints.Any(hint => text.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string FormatMac(PhysicalAddress address)
        {
            byte[] bytes = address?.GetAddressBytes();
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }
            return string.Join("-", bytes.Select(value => value.ToString("X2")));
        }
    }
}
