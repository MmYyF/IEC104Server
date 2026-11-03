using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using IEC104.App.Infrastructure;
using IEC104.App.Services;

namespace IEC104.App.ViewModels
{
    /// <summary>批量建从站的一行预览</summary>
    public class BatchSlavePreviewItem
    {
        public int Index { get; init; }
        public string ChannelName { get; init; }
        public string Ip { get; init; }
        public int Port { get; init; }
        public int CommonAddress { get; init; }
        public string SlaveName { get; init; }

        public string EndPoint => $"{Ip}:{Port}";

        /// <summary>这个地址在本机的状态（已有 / 启动时添加 / 本机没有）——一行看完整批</summary>
        public string AddressStatus { get; init; }

        /// <summary>冲突/越界提示，为空表示这一行可以建</summary>
        public string Warning { get; init; }

        public string StatusText => string.IsNullOrEmpty(Warning) ? "待创建" : Warning;
    }

    /// <summary>
    /// 批量创建从站。
    ///
    /// 现场场景（用户原话）："如果我的每一个从站都是不一样的 IP，
    /// 那我还需要先建立多个通道，然后再去每个通道下建立不同的从站"。
    /// 手工一条条点几十次不现实，所以这里按"IP 递增 / 按步长"一次生成一批。
    ///
    /// 生成规则：每个从站独占一个通道（因为 IP 不同，一条 TCP 监听只能绑一个端点），
    /// 通道的 IP 从起始值按步长递增，端口可以固定也可以跟着递增。
    /// </summary>
    public class BatchCreateSlaveViewModel : ViewModelBase
    {
        /// <summary>一次最多建多少个，防止手滑填 100000 把界面撑死</summary>
        public const int MaxCount = 500;

        private string _startIp = "192.168.1.10";
        private int _ipStep = 1;
        private int _count = 4;
        private int _startPort = 2404;
        private int _portStep;
        private int _startCommonAddress = 1;
        private int _addressStep = 1;
        private string _channelNamePattern = "通道{n}";
        private string _slaveNamePattern = "从站{n}";
        private string _errorText;
        private bool _skipConflicts = true;

        private readonly IList<ConnectionViewModel> _connections;

        public BatchCreateSlaveViewModel(IList<ConnectionViewModel> connections)
        {
            _connections = connections;
            GenerateCommand = new RelayCommand(RegeneratePreview);

            // 链路参数/报文长度变了要重算预览：三个长度会改变"公共地址装不装得下"，
            // 预览里的告警必须跟着变，否则用户改窄了宽度却看到一片"待创建"
            Link.PropertyChanged += (_, _) => RegeneratePreview();

            // 换网卡或开关自动添加，都要重算"本机地址"那一列
            AddressPlan.Changed += () => RegeneratePreview();

            RegeneratePreview();
        }

        /// <summary>
        /// 这一批通道共用的链路参数与报文长度。
        ///
        /// 批量建出来的每条通道都套同一份——这正是批量的意义。
        /// 要单独调某一台，建完后在从站上双击改（预览里那句提示也是这么说的）。
        /// </summary>
        public LinkParametersModel Link { get; } = new();

        /// <summary>
        /// 这一批共用的通信网卡 + "启动时自动添加"（1.11）。
        ///
        /// 按用户的说法："批量创建时所有的默认一个网卡，但是可以自行去某一个通道下面修改通信网卡。"
        /// 所以这里只给一个默认值，落到每条通道上是各自可改的（建完后双击从站标签就能改）。
        ///
        /// ShowPlanText 关掉：这里一整批有几十个地址，说一句"本机没有 192.168.1.10"
        /// 会让人以为只有一个地址。逐行状态交给预览表格的「本机地址」列。
        /// </summary>
        public NicPickerModel AddressPlan { get; } = new() { ShowPlanText = false };

        // ── 参数 ────────────────────────────────────────────────

        /// <summary>起始 IP（末段递增，超过 255 自动进位到前一段）</summary>
        public string StartIp
        {
            get => _startIp;
            set { if (SetField(ref _startIp, value)) { RegeneratePreview(); } }
        }

        /// <summary>IP 递增步长。填 1 就是连续地址，填 2/10 就是跳着分配</summary>
        public int IpStep
        {
            get => _ipStep;
            set { if (SetField(ref _ipStep, Math.Clamp(value, 1, 65535))) { RegeneratePreview(); } }
        }

        public int Count
        {
            get => _count;
            set { if (SetField(ref _count, Math.Clamp(value, 1, MaxCount))) { RegeneratePreview(); } }
        }

        public int StartPort
        {
            get => _startPort;
            set { if (SetField(ref _startPort, Math.Clamp(value, 1, 65535))) { RegeneratePreview(); } }
        }

        /// <summary>端口递增步长；0 表示所有通道用同一个端口（IP 不同，不冲突）</summary>
        public int PortStep
        {
            get => _portStep;
            set { if (SetField(ref _portStep, Math.Clamp(value, 0, 65535))) { RegeneratePreview(); } }
        }

        public int StartCommonAddress
        {
            get => _startCommonAddress;
            set { if (SetField(ref _startCommonAddress, Math.Clamp(value, 1, 65535))) { RegeneratePreview(); } }
        }

        /// <summary>公共地址递增步长</summary>
        public int AddressStep
        {
            get => _addressStep;
            set { if (SetField(ref _addressStep, Math.Clamp(value, 0, 65535))) { RegeneratePreview(); } }
        }

        /// <summary>通道名模板，可用 {n} 序号、{ip}、{port}</summary>
        public string ChannelNamePattern
        {
            get => _channelNamePattern;
            set { if (SetField(ref _channelNamePattern, value)) { RegeneratePreview(); } }
        }

        /// <summary>从站名模板，可用 {n} 序号、{ip}、{port}、{ca} 公共地址</summary>
        public string SlaveNamePattern
        {
            get => _slaveNamePattern;
            set { if (SetField(ref _slaveNamePattern, value)) { RegeneratePreview(); } }
        }

        /// <summary>遇到已存在的 IP:端口 时跳过（而不是整批失败）</summary>
        public bool SkipConflicts
        {
            get => _skipConflicts;
            set { if (SetField(ref _skipConflicts, value)) { RegeneratePreview(); } }
        }

        public string ErrorText
        {
            get => _errorText;
            private set => SetField(ref _errorText, value);
        }

        // ── 预览 ────────────────────────────────────────────────

        public ObservableCollection<BatchSlavePreviewItem> Preview { get; } = new();

        public string SummaryText
        {
            get
            {
                int ready = Preview.Count(p => string.IsNullOrEmpty(p.Warning));
                int blocked = Preview.Count - ready;
                return blocked == 0
                    ? $"共 {Preview.Count} 条，可全部创建"
                    : $"共 {Preview.Count} 条，可创建 {ready} 条，{blocked} 条有问题";
            }
        }

        public ICommand GenerateCommand { get; }

        /// <summary>把参数换算成预览行；任何非法输入都会在 ErrorText 里说清楚</summary>
        public void RegeneratePreview()
        {
            Preview.Clear();
            ErrorText = null;

            // 链路参数/报文长度先过一遍：k/w/t 的约束、三个长度的取值挡位
            string linkError = Link.Validate();
            if (linkError != null)
            {
                ErrorText = linkError;
                OnPropertyChanged(nameof(SummaryText));
                return;
            }

            // 公共地址能表示到多大，取决于这一批统一用的公共地址长度
            int maxCommonAddress = Link.BuildParameters().MaxCommonAddress;

            if (!TryParseIp(_startIp, out uint baseIp))
            {
                ErrorText = $"起始 IP \"{_startIp}\" 不是合法的 IPv4 地址。";
                OnPropertyChanged(nameof(SummaryText));
                return;
            }

            // 勾了"启动时自动添加"却没选网卡：这一批每一条都会在启动时失败，
            // 与其建完再逐条踩，不如现在就挡住
            if (AddressPlan.AutoAddAddress && string.IsNullOrWhiteSpace(AddressPlan.NicId))
            {
                ErrorText = "勾选了“启动时自动添加地址”，但没有选择通信网卡。";
                OnPropertyChanged(nameof(SummaryText));
                return;
            }

            // 让网卡那一段的"是否需要自动添加"判断按真实地址来（起始 IP 一定是具体的）
            AddressPlan.BindAddress = IpToString(baseIp);

            long portSpan = (long)_portStep * (_count - 1);
            if (_startPort + portSpan > 65535)
            {
                ErrorText = $"端口从 {_startPort} 起按步长 {_portStep} 递增 {_count} 个会超出 65535。";
                OnPropertyChanged(nameof(SummaryText));
                return;
            }

            if ((long)_startCommonAddress + (long)_addressStep * (_count - 1) > 65535)
            {
                ErrorText = $"公共地址从 {_startCommonAddress} 起按步长 {_addressStep} 递增 {_count} 个会超出 65535。";
                OnPropertyChanged(nameof(SummaryText));
                return;
            }

            for (int i = 0; i < _count; i++)
            {
                uint ipValue = baseIp + (uint)((long)_ipStep * i);
                if (ipValue > 0xFFFFFFFF)
                {
                    ErrorText = $"第 {i + 1} 个地址已超出 IPv4 范围（{_startIp} + 步长 {_ipStep} × {i}）。";
                    break;
                }

                string ip = IpToString(ipValue);
                int port = _startPort + _portStep * i;
                int ca = _startCommonAddress + _addressStep * i;
                int index = i + 1;

                string warning = null;

                // 同一个 IP:端口 不能开两个通道
                ConnectionViewModel clash = _connections?.FirstOrDefault(c =>
                    c.Protocol == ConnectionProtocol.Iec104
                    && c.Port == port
                    && string.Equals(
                        string.IsNullOrWhiteSpace(c.BindAddress) ? "0.0.0.0" : c.BindAddress.Trim(),
                        ip, StringComparison.OrdinalIgnoreCase));

                if (clash != null)
                {
                    warning = _skipConflicts ? $"端口已被\"{clash.Name}\"占用，将跳过" : $"端口已被\"{clash.Name}\"占用";
                }

                if (ca > maxCommonAddress)
                {
                    // 这一段单独说清楚"是宽度不够"而不是含糊的"越界"：
                    // 公共地址 1 字节只能表示 0..255，填到 300 会被编码成低字节发出去，
                    // 报文格式合法、主站却按错的站地址查找 —— 建出来也连不通。
                    warning = $"公共地址 {ca} 超出 {Link.CommonAddressLength} 字节上限 {maxCommonAddress}";
                }

                Preview.Add(new BatchSlavePreviewItem
                {
                    Index = index,
                    Ip = ip,
                    Port = port,
                    CommonAddress = ca,
                    ChannelName = Format(_channelNamePattern, index, ip, port, ca, "通道"),
                    SlaveName = Format(_slaveNamePattern, index, ip, port, ca, "从站"),
                    AddressStatus = IpAddressService.ShortStatus(ip, AddressPlan.NicId, AddressPlan.AutoAddAddress),
                    Warning = warning
                });
            }

            OnPropertyChanged(nameof(SummaryText));
        }

        /// <summary>
        /// 落地：逐行建通道 + 从站。
        /// 返回实际创建的数量。
        ///
        /// 两个关键点：
        /// 1) 先 <see cref="RegeneratePreview"/> 重算一遍——参数或已有通道可能在上次预览之后
        ///    又变了，拿过期预览去建会重复创建（自检里就踩到了）。
        /// 2) 冲突一律不建。同一 IP:端口 开两个通道必然有一个绑不上，
        ///    与其建一半失败，不如直接跳过并如实返回数量。
        /// </summary>
        public int Create(IList<ConnectionViewModel> connections)
        {
            if (connections == null)
            {
                return 0;
            }

            RegeneratePreview();

            // 参数不合法就一条都不建：批量操作最忌讳"建了一半才发现参数错"，
            // 那时用户已经要一条条删回去了
            if (!string.IsNullOrEmpty(ErrorText))
            {
                return 0;
            }

            List<BatchSlavePreviewItem> rows = Preview
                .Where(p => string.IsNullOrEmpty(p.Warning))
                .ToList();

            // 没勾"跳过"且存在冲突行：整批不动，由调用方提示用户改参数
            if (!_skipConflicts && rows.Count != Preview.Count)
            {
                return 0;
            }

            int created = 0;
            foreach (BatchSlavePreviewItem item in rows)
            {
                // 实时复查：上面 RegeneratePreview 之后到这一步之间，集合理论上不会变，
                // 但这一层兜底能保证绝不产生重复端点
                bool clash = connections.Any(c =>
                    c.Protocol == ConnectionProtocol.Iec104
                    && c.Port == item.Port
                    && string.Equals(
                        string.IsNullOrWhiteSpace(c.BindAddress) ? "0.0.0.0" : c.BindAddress.Trim(),
                        item.Ip, StringComparison.OrdinalIgnoreCase));

                if (clash)
                {
                    continue;
                }

                ConnectionViewModel channel = new()
                {
                    Name = item.ChannelName,
                    Protocol = ConnectionProtocol.Iec104,
                    BindAddress = item.Ip,
                    Port = item.Port,
                    // 这一批统一用界面上选的那块网卡；要单独调某一台，建完后双击从站标签改
                    NicId = AddressPlan.NicId,
                    AutoAddAddress = AddressPlan.AutoAddAddress
                };

                // 这一批统一套用界面上那套链路参数与报文长度
                Link.ApplyTo(channel);

                connections.Add(channel);

                SlaveViewModel slave = new()
                {
                    Name = item.SlaveName,
                    CommonAddress = item.CommonAddress
                };

                channel.Slaves.Add(slave);
                channel.HookSlave(slave);
                created++;
            }

            return created;
        }

        // ── 工具 ────────────────────────────────────────────────

        private static string Format(string pattern, int index, string ip, int port, int ca, string fallback)
        {
            string text = string.IsNullOrWhiteSpace(pattern) ? fallback + "{n}" : pattern;
            return text
                .Replace("{n}", index.ToString())
                .Replace("{ip}", ip)
                .Replace("{port}", port.ToString())
                .Replace("{ca}", ca.ToString());
        }

        /// <summary>IP 字符串 → 32 位整数，方便按步长相加（末段超 255 会自动进位）</summary>
        internal static bool TryParseIp(string text, out uint value)
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

        internal static string IpToString(uint value) =>
            $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";
    }
}
