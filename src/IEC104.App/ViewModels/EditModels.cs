using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using IEC104.App.Infrastructure;
using IEC104.App.Services;
using IEC104.Core.CS101;

namespace IEC104.App.ViewModels
{
    /// <summary>
    /// 连接编辑模型 —— 对话框用的"草稿副本"。
    ///
    /// 为什么要草稿副本：用户点"取消"时不能已经改掉了真实对象。
    /// 这是对话框编辑的标准做法，原程序的 CreateConnectionWindowViewModel /
    /// EditConnectionWindow 也是同样的思路（新建时构造新对象，编辑时复制字段）。
    /// </summary>
    public class ConnectionEditModel : ViewModelBase
    {
        private string _name;
        private int _protocolIndex;
        private int _port;
        private string _serialPortName;
        private int _baudRate;
        private string _bindAddress;

        private readonly ConnectionViewModel _target;

        public ConnectionEditModel(ConnectionViewModel target)
        {
            _target = target;
            _name = target.Name;
            _protocolIndex = target.Protocol == ConnectionProtocol.Iec104 ? 0 : 1;
            _port = target.Port;
            _serialPortName = target.SerialPortName;
            _baudRate = target.BaudRate;
            _bindAddress = target.BindAddress;

            Link = new LinkParametersModel(target);
        }

        /// <summary>
        /// 链路参数 + 报文长度的草稿。
        /// 下面那几个 k / w / t1..t3 / 三个长度属性都是它的转发，
        /// 保留它们只是为了不改调用方（自检和旧代码都按这些名字取值）。
        /// </summary>
        public LinkParametersModel Link { get; }

        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        public int ProtocolIndex
        {
            get => _protocolIndex;
            set
            {
                if (SetField(ref _protocolIndex, value))
                {
                    // 协议切换会决定显示 TCP 参数还是串口参数
                    OnPropertyChanged(nameof(IsTcp));
                    OnPropertyChanged(nameof(IsSerialPort));
                }
            }
        }

        /// <summary>是否 TCP（IEC 104）</summary>
        public bool IsTcp => _protocolIndex == 0;

        /// <summary>是否串口（IEC 101）</summary>
        public bool IsSerialPort => _protocolIndex == 1;

        /// <summary>串口名，如 COM3</summary>
        public string SerialPortName
        {
            get => _serialPortName;
            set => SetField(ref _serialPortName, value);
        }

        /// <summary>
        /// 监听 IP。空/"0.0.0.0" 表示全部网卡。
        /// 下拉里给本机网卡 IP，也允许直接输入——现场改网段时不用等程序更新。
        /// </summary>
        public string BindAddress
        {
            get => _bindAddress;
            set => SetField(ref _bindAddress, value);
        }

        /// <summary>本机可用 IPv4 地址列表；点选后由 ConnectionWindow 代码后置填进输入框</summary>
        public IReadOnlyList<string> AvailableAddresses { get; } = LoadAddresses();

        /// <summary>枚举本机可用 IPv4（从站窗口的新建通道也要用，所以提到 internal 静态）</summary>
        internal static IReadOnlyList<string> LoadAddresses()
        {
            List<string> list = new() { "0.0.0.0" };
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface nic
                         in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    {
                        continue;
                    }

                    foreach (System.Net.NetworkInformation.UnicastIPAddressInformation info
                             in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (info.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                            && !System.Net.IPAddress.IsLoopback(info.Address))
                        {
                            list.Add(info.Address.ToString());
                        }
                    }
                }
            }
            catch
            {
                // 取网卡信息失败不影响手填 IP，忽略即可
            }
            return list;
        }

        /// <summary>波特率</summary>
        public int BaudRate
        {
            get => _baudRate;
            set => SetField(ref _baudRate, value);
        }

        /// <summary>常见的串口名与波特率候选（原版由 AddSerialPortNameWindow 手工维护，这里直接给全）</summary>
        public IReadOnlyList<string> AvailablePorts { get; } = SerialPortNames;

        public IReadOnlyList<int> AvailableBaudRates { get; } = StandardBaudRates;

        /// <summary>串口名候选。从站窗口的"新建通道"也要用，所以做成静态的共享一份</summary>
        internal static readonly IReadOnlyList<string> SerialPortNames =
            Enumerable.Range(1, 32).Select(i => "COM" + i).ToList();

        /// <summary>常用波特率</summary>
        internal static readonly IReadOnlyList<int> StandardBaudRates = new[]
        {
            1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200
        };

        public int Port
        {
            get => _port;
            set => SetField(ref _port, (int)value);
        }

        // ── 链路参数 + 报文长度：全部转发给 Link，避免和别的窗口各存一份 ──

        public int K
        {
            get => Link.K;
            set => Link.K = value;
        }

        public int W
        {
            get => Link.W;
            set => Link.W = value;
        }

        public int T1Seconds
        {
            get => Link.T1Seconds;
            set => Link.T1Seconds = value;
        }

        public int T2Seconds
        {
            get => Link.T2Seconds;
            set => Link.T2Seconds = value;
        }

        public int T3Seconds
        {
            get => Link.T3Seconds;
            set => Link.T3Seconds = value;
        }

        /// <summary>传送原因长度（字节）</summary>
        public int CauseOfTransmissionLength
        {
            get => Link.CauseOfTransmissionLength;
            set => Link.CauseOfTransmissionLength = value;
        }

        /// <summary>传送原因长度的下拉索引（0 → 1 字节，1 → 2 字节）</summary>
        public int CauseOfTransmissionLengthIndex
        {
            get => Link.CauseOfTransmissionLengthIndex;
            set => Link.CauseOfTransmissionLengthIndex = value;
        }

        /// <summary>公共地址长度（字节）</summary>
        public int CommonAddressLength
        {
            get => Link.CommonAddressLength;
            set => Link.CommonAddressLength = value;
        }

        /// <summary>公共地址长度的下拉索引（0 → 1 字节，1 → 2 字节）</summary>
        public int CommonAddressLengthIndex
        {
            get => Link.CommonAddressLengthIndex;
            set => Link.CommonAddressLengthIndex = value;
        }

        /// <summary>信息体地址长度（字节）</summary>
        public int InformationObjectAddressLength
        {
            get => Link.InformationObjectAddressLength;
            set => Link.InformationObjectAddressLength = value;
        }

        /// <summary>信息体地址长度的下拉索引（0 → 1 字节，1 → 2 字节，2 → 3 字节）</summary>
        public int InformationObjectAddressLengthIndex
        {
            get => Link.InformationObjectAddressLengthIndex;
            set => Link.InformationObjectAddressLengthIndex = value;
        }

        /// <summary>校验，返回错误说明或 null</summary>
        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(_name))
            {
                return "名称不能为空。";
            }
            if (_protocolIndex == 0 && (_port < 1 || _port > 65535))
            {
                return "端口需在 1..65535 之间。";
            }
            if (_protocolIndex == 1 && string.IsNullOrWhiteSpace(_serialPortName))
            {
                return "串口名不能为空（例如 COM3）。";
            }

            // k/w/t 约束与三个报文长度的校验都在 Link 里（含"装不装得下"的核对）：
            // 宽度改小以后，公共地址 1000 的从站会被编码成低字节 0xE8 发出去——
            // 报文本身格式完全合法，主站却按错的站地址去查，永远查不到。
            return Link.Validate(_target);
        }

        /// <summary>把草稿写回真实对象</summary>
        public void Apply()
        {
            _target.Name = _name;
            _target.Protocol = _protocolIndex == 0 ? ConnectionProtocol.Iec104 : ConnectionProtocol.Iec101;
            _target.Port = _port;
            _target.BindAddress = _bindAddress;
            _target.SerialPortName = _serialPortName;
            _target.BaudRate = _baudRate;
            Link.ApplyTo(_target);
        }
    }

    /// <summary>
    /// 链路参数 + 报文长度（草稿副本）。
    ///
    /// 【为什么要单独抽一个模型】
    /// 这两组参数现在要在四个地方出现：通道配置、联合建从站（新建通道）、
    /// 双击从站标签（改所属通道）、批量建从站。四处各写一份字段和校验，
    /// 迟早会出现"这里能填 0、那里不能"的漂移——而这几项恰好是**错了就连不上主站**的参数。
    /// 所以字段、挡位、校验、落盘全部收口到这里，界面只是一个可复用的面板。
    ///
    /// 【为什么是草稿】
    /// 和别的编辑模型一样：用户点"取消"时不能已经改掉了真实对象。
    /// 只有 <see cref="ApplyTo"/> 才会写回通道。
    /// </summary>
    public class LinkParametersModel : ViewModelBase
    {
        private int _k = 12;
        private int _w = 8;
        private int _t1Seconds = 15;
        private int _t2Seconds = 10;
        private int _t3Seconds = 20;

        private int _causeOfTransmissionLength = 2;
        private int _commonAddressLength = 2;
        private int _informationObjectAddressLength = 3;

        public LinkParametersModel()
        {
        }

        /// <summary>从一条已有通道读初值</summary>
        public LinkParametersModel(ConnectionViewModel source)
        {
            LoadFrom(source);
        }

        /// <summary>把某条通道的当前值读进草稿</summary>
        public void LoadFrom(ConnectionViewModel source)
        {
            if (source == null)
            {
                return;
            }

            K = source.K;
            W = source.W;
            T1Seconds = source.T1Seconds;
            T2Seconds = source.T2Seconds;
            T3Seconds = source.T3Seconds;
            CauseOfTransmissionLength = source.CauseOfTransmissionLength;
            CommonAddressLength = source.CommonAddressLength;
            InformationObjectAddressLength = source.InformationObjectAddressLength;
        }

        /// <summary>把草稿写回通道（只在"确定"里调用）</summary>
        public void ApplyTo(ConnectionViewModel target)
        {
            if (target == null)
            {
                return;
            }

            target.K = _k;
            target.W = _w;
            target.T1Seconds = _t1Seconds;
            target.T2Seconds = _t2Seconds;
            target.T3Seconds = _t3Seconds;
            target.CauseOfTransmissionLength = _causeOfTransmissionLength;
            target.CommonAddressLength = _commonAddressLength;
            target.InformationObjectAddressLength = _informationObjectAddressLength;
        }

        // ── 链路参数 ────────────────────────────────────────────

        public int K
        {
            get => _k;
            set => SetField(ref _k, value);
        }

        public int W
        {
            get => _w;
            set => SetField(ref _w, value);
        }

        public int T1Seconds
        {
            get => _t1Seconds;
            set => SetField(ref _t1Seconds, value);
        }

        public int T2Seconds
        {
            get => _t2Seconds;
            set => SetField(ref _t2Seconds, value);
        }

        public int T3Seconds
        {
            get => _t3Seconds;
            set => SetField(ref _t3Seconds, value);
        }

        // ── 报文长度 ────────────────────────────────────────────
        //
        // 界面用 ComboBox 的 SelectedIndex 绑：索引 = 字节数 - 1。
        // 值的映射在这里收口，XAML 里就不用写转换器。

        /// <summary>传送原因长度（字节）：1 或 2</summary>
        public int CauseOfTransmissionLength
        {
            get => _causeOfTransmissionLength;
            set
            {
                if (SetField(ref _causeOfTransmissionLength, value))
                {
                    // 下拉绑的是 *Index，改长度必须连带通知索引，否则界面不会跟着动
                    OnPropertyChanged(nameof(CauseOfTransmissionLengthIndex));
                    OnPropertyChanged(nameof(ApplicationWidthsText));
                }
            }
        }

        public int CauseOfTransmissionLengthIndex
        {
            get => _causeOfTransmissionLength - 1;
            set
            {
                if (SetField(ref _causeOfTransmissionLength, value + 1, nameof(CauseOfTransmissionLength)))
                {
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ApplicationWidthsText));
                }
            }
        }

        /// <summary>公共地址长度（字节）：1 或 2</summary>
        public int CommonAddressLength
        {
            get => _commonAddressLength;
            set
            {
                if (SetField(ref _commonAddressLength, value))
                {
                    OnPropertyChanged(nameof(CommonAddressLengthIndex));
                    OnPropertyChanged(nameof(ApplicationWidthsText));
                }
            }
        }

        public int CommonAddressLengthIndex
        {
            get => _commonAddressLength - 1;
            set
            {
                if (SetField(ref _commonAddressLength, value + 1, nameof(CommonAddressLength)))
                {
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ApplicationWidthsText));
                }
            }
        }

        /// <summary>信息体地址长度（字节）：1..3</summary>
        public int InformationObjectAddressLength
        {
            get => _informationObjectAddressLength;
            set
            {
                if (SetField(ref _informationObjectAddressLength, value))
                {
                    OnPropertyChanged(nameof(InformationObjectAddressLengthIndex));
                    OnPropertyChanged(nameof(ApplicationWidthsText));
                }
            }
        }

        public int InformationObjectAddressLengthIndex
        {
            get => _informationObjectAddressLength - 1;
            set
            {
                if (SetField(ref _informationObjectAddressLength, value + 1, nameof(InformationObjectAddressLength)))
                {
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ApplicationWidthsText));
                }
            }
        }

        /// <summary>一句话摘要，界面上用来一眼确认按什么宽度收发</summary>
        public string ApplicationWidthsText =>
            $"COT {_causeOfTransmissionLength}B · CA {_commonAddressLength}B · IOA {_informationObjectAddressLength}B";

        /// <summary>按当前挡位拼一份应用层参数（新实例）</summary>
        public ApplicationLayerParameters BuildParameters()
        {
            return new ApplicationLayerParameters
            {
                SizeOfCauseOfTransmission = _causeOfTransmissionLength,
                SizeOfCommonAddress = _commonAddressLength,
                SizeOfInformationObjectAddress = _informationObjectAddressLength
            };
        }

        /// <summary>
        /// 校验。返回可直接展示给用户的错误说明，null 表示没问题。
        ///
        /// <paramref name="owner"/> 传通道时额外做两件事：
        ///   ① 通道正在跑就不许改宽度（同一链路中途变宽窄，主站必然解析错位）；
        ///   ② 核对这条通道下现有从站的公共地址与点号装不装得进新宽度。
        /// 传 null 表示"这两个值还没有归属通道"（比如批量创建时），
        /// 那种场景由调用方按自己的规则核对。
        /// </summary>
        public string Validate(ConnectionViewModel owner = null)
        {
            if (_w >= _k)
            {
                return $"w({_w}) 必须小于 k({_k})。";
            }
            if (_t2Seconds >= _t1Seconds)
            {
                return $"t2({_t2Seconds}s) 必须小于 t1({_t1Seconds}s)。";
            }

            ApplicationLayerParameters widths = BuildParameters();
            string error = widths.Validate();
            if (error != null)
            {
                return error;
            }

            if (owner == null)
            {
                return null;
            }

            if (owner.IsOpen && !widths.HasSameWidths(owner.AppParameters))
            {
                return "报文长度（传送原因 / 公共地址 / 信息体地址）不能在线修改，"
                    + "请先停止这条通道，改完再启动。";
            }

            foreach (SlaveViewModel slave in owner.Slaves)
            {
                if (slave.CommonAddress < 1 || slave.CommonAddress > widths.MaxCommonAddress)
                {
                    return $"从站“{slave.Name}”的公共地址 {slave.CommonAddress} 超过公共地址长度 "
                        + $"{widths.SizeOfCommonAddress} 字节的上限 {widths.MaxCommonAddress}，"
                        + "改小长度后该站将无法被主站寻址。";
                }

                foreach (PointViewModel point in slave.Points)
                {
                    if (point.ObjectAddress > widths.MaxInformationObjectAddress)
                    {
                        return $"从站“{slave.Name}”的点号 {point.ObjectAddress} 超过信息体地址长度 "
                            + $"{widths.SizeOfInformationObjectAddress} 字节的上限 "
                            + $"{widths.MaxInformationObjectAddress}。";
                    }
                }
            }

            return null;
        }
    }

    /// <summary>
    /// 从站编辑模型（草稿副本）。
    ///
    /// 【两种模式】
    ///   编辑模式：改名称、公共地址；<b>扁平视图下还能改监听 IP / 端口</b>。
    ///   新建模式：多出一块"归属通道"选择——可以挂到已有通道，也可以直接新建一个通道。
    ///
    /// 【为什么要有新建模式这块】
    /// 原版（StationViewModel(ConnectionViewModel) 构造函数强制依赖连接）和本复刻的
    /// 早期版本都要求"先建通道，再在通道下建从站"。当现场每台装置都是不同 IP 时，
    /// 这个两步流程要重复 N 遍，纯属折腾。所以这里把"建通道"并进了建从站：
    /// 填个 IP 就完事，通道按需自动创建。
    ///
    /// 【为什么编辑模式也允许改 IP / 端口】
    /// 在扁平视图里，"从站"和"那台装置的 IP"在用户心里是同一件事——
    /// 双击标签想改的就是 IP，让他先去另一棵树里找到通道、右键"编辑通道参数"、
    /// 再回来，等于逼着他记住一次协议分层的实现细节。
    /// 树形视图里不显示这块（那儿通道是可见的对象，各改各的，职责清楚）。
    /// </summary>
    public class SlaveEditModel : ViewModelBase
    {
        private string _name;
        private int _commonAddress;

        private readonly SlaveViewModel _target;
        private readonly Func<int, bool> _isAddressTaken;
        private readonly IList<ConnectionViewModel> _connections;

        // ── 新建模式下的"归属通道" ──
        private ConnectionViewModel _selectedConnection;
        private bool _createNewChannel;
        private string _newChannelName;
        private string _newBindAddress = "0.0.0.0";
        private int _newPort = 2404;

        // 新建通道时可选规约：0 = IEC 104（TCP），1 = IEC 101（串口）
        private int _newChannelProtocolIndex;
        private string _newSerialPortName = "COM1";
        private int _newBaudRate = 9600;

        // ── 编辑模式下可改的"所属通道监听信息" ──
        private string _ownerBindAddress;
        private int _ownerPort;

        /// <summary>编辑模式</summary>
        public SlaveEditModel(SlaveViewModel target, Func<int, bool> isAddressTaken = null)
        {
            _target = target;
            _isAddressTaken = isAddressTaken;
            _name = target.Name;
            _commonAddress = target.CommonAddress;
        }

        /// <summary>
        /// 编辑模式 + 允许改所属通道的监听 IP / 端口（扁平视图双击从站标签走这条）。
        /// <paramref name="owner"/> 为该从站所在的通道；传 null 就退化成老行为。
        /// <paramref name="connections"/> 用来查端点冲突，传 null 就跳过查重。
        /// </summary>
        public SlaveEditModel(SlaveViewModel target, ConnectionViewModel owner,
            IList<ConnectionViewModel> connections, Func<int, bool> isAddressTaken = null)
            : this(target, isAddressTaken)
        {
            OwnerChannel = owner;
            _connections = connections;

            if (owner != null)
            {
                _ownerBindAddress = string.IsNullOrWhiteSpace(owner.BindAddress) ? "0.0.0.0" : owner.BindAddress;
                _ownerPort = owner.Port;

                // 双击标签就能顺手改这条通道的链路参数与报文长度——
                // 用户心里的"这台装置"本来就包含它挂在哪个口、用哪套宽度，
                // 让他先去树里找到通道、右键编辑、再回来，等于逼他记住一次协议分层。
                OwnerChannelLink = new LinkParametersModel(owner);
            }
        }

        /// <summary>新建模式：带上可选通道列表（可写，用于落地新建的通道）</summary>
        public SlaveEditModel(SlaveViewModel target, IList<ConnectionViewModel> connections,
            ConnectionViewModel current, bool preferNewChannel = false)
            : this(target, null)
        {
            IsCreateMode = true;
            _connections = connections;
            _selectedConnection = current
                ?? (connections != null && connections.Count > 0 ? connections[0] : null);
            _newChannelName = $"104通道{(connections?.Count ?? 0) + 1}";

            // 默认端口别跟已有通道撞：从 2404 起找第一个没人用的
            _newPort = FindFreePort(connections);

            // 一个通道都没有时只能新建；否则沿用上一次的选择——
            // 连续给"每台装置一个 IP"建从站时，不用每次都重新点"新建通道"。
            // preferNewChannel（扁平视图）直接当"新建通道"用：那儿根本没有"通道"这个概念，
            // 用户点"新建从站"就是要再加一台装置
            _createNewChannel = preferNewChannel || _selectedConnection == null || s_lastUsedNewChannel;
        }

        /// <summary>
        /// 新建通道时的链路参数 + 报文长度（草稿）。
        /// 与"单独建通道"是同一套东西、同一份校验——用户明确要求两边都能改，
        /// 那就不能出现"这里少一个 k、那里少一个 t3"的差异。
        /// </summary>
        public LinkParametersModel NewChannelLink { get; } = new();

        /// <summary>编辑模式下所属通道的链路参数 + 报文长度（草稿）。只有 CanEditChannel 时才非 null</summary>
        public LinkParametersModel OwnerChannelLink { get; }

        /// <summary>上一次是否用了"新建通道"，用来让连续新建从站少点一次</summary>
        private static bool s_lastUsedNewChannel;

        /// <summary>从 2404 开始找一个没被 0.0.0.0 占用的端口</summary>
        internal static int FindFreePort(IList<ConnectionViewModel> connections)
        {
            const int StandardPort = 2404;
            if (connections == null || connections.Count == 0)
            {
                return StandardPort;
            }

            for (int port = StandardPort; port < StandardPort + 200; port++)
            {
                bool taken = connections.Any(c =>
                    c.Protocol == ConnectionProtocol.Iec104
                    && c.Port == port
                    && (string.IsNullOrWhiteSpace(c.BindAddress)
                        || c.BindAddress.Trim() == "0.0.0.0"));

                if (!taken)
                {
                    return port;
                }
            }
            return StandardPort;
        }

        /// <summary>true = 新建从站（显示通道选择）</summary>
        public bool IsCreateMode { get; }

        /// <summary>草稿对应的从站对象，视图层用来判断"这次弹窗是新建还是编辑"</summary>
        public SlaveViewModel Target => _target;

        /// <summary>
        /// 解析完成后的归属通道。
        /// 由 <see cref="Apply"/> 写入；新建通道模式下这里就是新建出来的那个通道。
        /// </summary>
        public ConnectionViewModel ResolvedChannel { get; private set; }

        /// <summary>
        /// 编辑模式下这个从站所在的通道。非 null = 本次弹窗里可以改 IP / 端口。
        /// 由扁平视图双击时注入；树形视图传 null（那儿通道是自己可见的一行）。
        /// </summary>
        public ConnectionViewModel OwnerChannel { get; }

        /// <summary>true = 显示"监听 IP / 端口"那一块</summary>
        public bool CanEditChannel => !IsCreateMode && OwnerChannel != null;

        /// <summary>所属通道的名称，显示在分组标题旁边让用户确认改的是哪条</summary>
        public string OwnerChannelName => OwnerChannel?.Name ?? string.Empty;

        /// <summary>所属通道当前是否在跑（改 IP/端口要先停，界面给一句提示）</summary>
        public bool OwnerChannelIsOpen => OwnerChannel?.IsOpen == true;

        /// <summary>所属通道的监听 IP（"0.0.0.0" = 全部网卡）</summary>
        public string OwnerBindAddress
        {
            get => _ownerBindAddress;
            set => SetField(ref _ownerBindAddress, value);
        }

        /// <summary>所属通道的监听端口</summary>
        public int OwnerPort
        {
            get => _ownerPort;
            set => SetField(ref _ownerPort, value);
        }

        /// <summary>本机可用 IPv4，编辑通道时也能一键填</summary>
        public IReadOnlyList<string> OwnerAddresses { get; } = ConnectionEditModel.LoadAddresses();

        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        /// <summary>公共地址（站地址），1..65535</summary>
        public int CommonAddress
        {
            get => _commonAddress;
            set => SetField(ref _commonAddress, (int)value);
        }

        // ── 通道选择 ────────────────────────────────────────────

        public IReadOnlyList<ConnectionViewModel> Connections =>
            _connections as IReadOnlyList<ConnectionViewModel>
            ?? _connections?.ToList()
            ?? (IReadOnlyList<ConnectionViewModel>)Array.Empty<ConnectionViewModel>();

        public ConnectionViewModel SelectedConnection
        {
            get => _selectedConnection;
            set => SetField(ref _selectedConnection, value);
        }

        /// <summary>true = 新建一个通道来放这个从站</summary>
        public bool CreateNewChannel
        {
            get => _createNewChannel;
            set
            {
                if (SetField(ref _createNewChannel, value))
                {
                    OnPropertyChanged(nameof(IsExistingChannelMode));
                    OnPropertyChanged(nameof(IsNewChannelMode));
                }
            }
        }

        /// <summary>单选按钮用（与 IsNewChannelMode 互斥）</summary>
        public bool IsExistingChannelMode
        {
            get => !_createNewChannel;
            set
            {
                if (value)
                {
                    CreateNewChannel = false;
                }
            }
        }

        /// <summary>单选按钮用</summary>
        public bool IsNewChannelMode
        {
            get => _createNewChannel;
            set
            {
                if (value)
                {
                    CreateNewChannel = true;
                }
            }
        }

        public string NewChannelName
        {
            get => _newChannelName;
            set => SetField(ref _newChannelName, value);
        }

        /// <summary>新通道的监听 IP</summary>
        public string NewBindAddress
        {
            get => _newBindAddress;
            set => SetField(ref _newBindAddress, value);
        }

        /// <summary>新通道的监听端口</summary>
        public int NewPort
        {
            get => _newPort;
            set => SetField(ref _newPort, value);
        }

        /// <summary>本机可用 IPv4，供"新建通道"里一键填入</summary>
        public IReadOnlyList<string> AvailableAddresses { get; } = ConnectionEditModel.LoadAddresses();

        // ── 新建通道的规约选择 ──────────────────────────────────
        //
        // 为什么这块要和"单独建通道"分开看：
        // 单独建通道时用户是"在配一条链路"，k / w / t1..t3、报文长度这些都要摆出来；
        // 而这里用户心里想的是"再加一台装置"，顺手填个 IP 就完事。
        // 所以这里只给"走 TCP 还是走串口"这个必须先定的选择，以及由此决定的端点参数，
        // 其余链路细节留给通道自己去配——否则这个窗口会长到没人愿意填。

        /// <summary>新通道的规约：0 = IEC 104（TCP），1 = IEC 101（串口）</summary>
        public int NewChannelProtocolIndex
        {
            get => _newChannelProtocolIndex;
            set
            {
                if (SetField(ref _newChannelProtocolIndex, value))
                {
                    OnPropertyChanged(nameof(NewChannelIsTcp));
                    OnPropertyChanged(nameof(NewChannelIsSerial));
                    OnPropertyChanged(nameof(ShowNewChannelTcp));
                    SyncAutoChannelName(value);
                }
            }
        }

        /// <summary>
        /// 默认通道名跟着规约走：选 101 就别再叫"104通道3"。
        /// 只在名字还是自动生成的那一个时才替换，用户手工填过就不动。
        /// </summary>
        private void SyncAutoChannelName(int protocolIndex)
        {
            int index = (_connections?.Count ?? 0) + 1;
            string tcpName = $"104通道{index}";
            string serialName = $"101通道{index}";

            if (string.IsNullOrWhiteSpace(_newChannelName)
                || _newChannelName == tcpName
                || _newChannelName == serialName)
            {
                NewChannelName = protocolIndex == 0 ? tcpName : serialName;
            }
        }

        /// <summary>新通道走 TCP（104）</summary>
        public bool NewChannelIsTcp => _newChannelProtocolIndex == 0;

        /// <summary>新通道走串口（101）</summary>
        public bool NewChannelIsSerial => _newChannelProtocolIndex == 1;

        /// <summary>"新建通道 + 104"时才显示本机地址快捷填入</summary>
        public bool ShowNewChannelTcp => IsCreateMode && NewChannelIsTcp;

        /// <summary>新通道的串口名（101 时用）</summary>
        public string NewSerialPortName
        {
            get => _newSerialPortName;
            set => SetField(ref _newSerialPortName, value);
        }

        /// <summary>新通道的波特率（101 时用）</summary>
        public int NewBaudRate
        {
            get => _newBaudRate;
            set => SetField(ref _newBaudRate, value);
        }

        /// <summary>串口名候选</summary>
        public IReadOnlyList<string> AvailableSerialPorts => ConnectionEditModel.SerialPortNames;

        /// <summary>波特率候选</summary>
        public IReadOnlyList<int> AvailableBaudRates => ConnectionEditModel.StandardBaudRates;

        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(_name))
            {
                return "名称不能为空。";
            }
            if (_commonAddress < 1 || _commonAddress > 65535)
            {
                return "公共地址需在 1..65535 之间。";
            }

            // 新建模式：先校验"放哪儿"
            if (IsCreateMode)
            {
                if (_createNewChannel)
                {
                    string error = ValidateNewChannel();
                    if (error != null)
                    {
                        return error;
                    }

                    // 新通道还没有从站/点表，"装不装得下"这一层交给用户建完点位后再校验；
                    // 这里只挡挡位本身（k/w/t 约束、三个长度的取值范围）
                    return NewChannelLink.Validate();
                }
                else
                {
                    if (_selectedConnection == null)
                    {
                        return "请选择一个已有通道，或改选\"新建通道\"。";
                    }
                    if (_selectedConnection.Slaves.Any(s => s != _target && s.CommonAddress == _commonAddress))
                    {
                        return $"通道\"{_selectedConnection.Name}\"下公共地址 {_commonAddress} 已被占用。";
                    }
                }
                return null;
            }

            if (_commonAddress != _target.CommonAddress
                && _isAddressTaken != null && _isAddressTaken(_commonAddress))
            {
                return $"公共地址 {_commonAddress} 已被同连接下的其它从站占用。";
            }

            if (CanEditChannel)
            {
                string error = ValidateOwnerChannel();
                if (error != null)
                {
                    return error;
                }

                // 双击标签也能改链路参数与报文长度：这里要连带核"这条通道下现有从站装不装得下"
                return OwnerChannelLink?.Validate(OwnerChannel);
            }

            return null;
        }

        /// <summary>
        /// 校验"改通道端点"这块。
        /// 和自己比不算冲突（端口没改就照旧），这一点跟新建通道的校验不同——
        /// 那边是"新占一个端点"，这边是"把已经占着的端点改成另一个"。
        /// </summary>
        private string ValidateOwnerChannel()
        {
            string address = (_ownerBindAddress ?? string.Empty).Trim();
            bool anyAddress = address.Length == 0 || address == "0.0.0.0" || address == "*";
            if (!anyAddress)
            {
                if (!System.Net.IPAddress.TryParse(address, out System.Net.IPAddress parsed)
                    || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    return $"监听 IP \"{address}\" 不是合法的 IPv4 地址。";
                }
            }

            if (_ownerPort < 1 || _ownerPort > 65535)
            {
                return "端口需在 1..65535 之间。";
            }

            if (_connections == null)
            {
                return null;
            }

            // 端点没变就不用再查重（改的只是从站名/公共地址这类情况）
            string normalized = anyAddress ? "0.0.0.0" : address;
            string currentAddress = string.IsNullOrWhiteSpace(OwnerChannel.BindAddress) ? "0.0.0.0" : OwnerChannel.BindAddress.Trim();
            if (_ownerPort == OwnerChannel.Port
                && string.Equals(normalized, currentAddress, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            ConnectionViewModel clash = _connections.FirstOrDefault(c =>
                !ReferenceEquals(c, OwnerChannel)
                && c.Protocol == ConnectionProtocol.Iec104
                && c.Port == _ownerPort
                && string.Equals(string.IsNullOrWhiteSpace(c.BindAddress) ? "0.0.0.0" : c.BindAddress.Trim(),
                    normalized, StringComparison.OrdinalIgnoreCase));

            return clash == null
                ? null
                : $"端口 {_ownerPort} 在 {normalized} 上已被通道\"{clash.Name}\"占用，请换端口。";
        }

        /// <summary>新建通道的参数校验</summary>
        private string ValidateNewChannel()
        {
            // 101 走串口：没有 IP / 端口的概念，校验项完全不同
            if (NewChannelIsSerial)
            {
                if (string.IsNullOrWhiteSpace(_newSerialPortName))
                {
                    return "串口名不能为空（例如 COM3）。";
                }
                if (_newBaudRate <= 0)
                {
                    return "波特率必须大于 0。";
                }

                ConnectionViewModel serialClash = _connections?.FirstOrDefault(c =>
                    c.Protocol == ConnectionProtocol.Iec101
                    && string.Equals(c.SerialPortName, _newSerialPortName.Trim(),
                        StringComparison.OrdinalIgnoreCase));

                return serialClash == null
                    ? null
                    : $"串口 {_newSerialPortName} 已被通道“{serialClash.Name}”占用，请换一个串口。";
            }

            string address = (_newBindAddress ?? string.Empty).Trim();
            bool anyAddress = address.Length == 0 || address == "0.0.0.0" || address == "*";
            if (!anyAddress)
            {
                if (!System.Net.IPAddress.TryParse(address, out System.Net.IPAddress parsed)
                    || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    return $"监听 IP \"{address}\" 不是合法的 IPv4 地址。";
                }
            }
            if (_newPort < 1 || _newPort > 65535)
            {
                return "端口需在 1..65535 之间。";
            }

            // 同一 IP:端口 不能开两个通道——两个 TcpListener 绑同一端点必有一个失败
            string normalized = anyAddress ? "0.0.0.0" : address;
            ConnectionViewModel clash = _connections?.FirstOrDefault(c =>
                c.Protocol == ConnectionProtocol.Iec104
                && c.Port == _newPort
                && string.Equals(string.IsNullOrWhiteSpace(c.BindAddress) ? "0.0.0.0" : c.BindAddress.Trim(),
                    normalized, StringComparison.OrdinalIgnoreCase));

            if (clash != null)
            {
                return $"端口 {_newPort} 在 {normalized} 上已被通道\"{clash.Name}\"占用，请换端口或直接复用它。";
            }
            return null;
        }

        public void Apply()
        {
            _target.Name = _name;
            _target.CommonAddress = _commonAddress;

            // 顺手把所属通道的端点写回去（扁平视图里"从站 = 那台装置的 IP"）
            if (CanEditChannel)
            {
                OwnerChannel.BindAddress = string.IsNullOrWhiteSpace(_ownerBindAddress)
                    ? "0.0.0.0"
                    : _ownerBindAddress.Trim();
                OwnerChannel.Port = _ownerPort;
                OwnerChannelLink?.ApplyTo(OwnerChannel);
            }

            if (!IsCreateMode)
            {
                return;
            }

            // 记住这次的选择，下一次新建从站默认沿用
            s_lastUsedNewChannel = _createNewChannel;

            if (!_createNewChannel)
            {
                ResolvedChannel = _selectedConnection;
                return;
            }

            // 落地一个新通道：只建对象、加入列表，真正监听要等用户点"启动"
            bool useSerial = NewChannelIsSerial;
            string fallbackName = useSerial
                ? $"101通道{(_connections?.Count ?? 0) + 1}"
                : $"104通道{(_connections?.Count ?? 0) + 1}";

            ConnectionViewModel channel = new()
            {
                Name = string.IsNullOrWhiteSpace(_newChannelName) ? fallbackName : _newChannelName.Trim(),
                Protocol = useSerial ? ConnectionProtocol.Iec101 : ConnectionProtocol.Iec104,
                Port = _newPort,
                BindAddress = string.IsNullOrWhiteSpace(_newBindAddress) ? "0.0.0.0" : _newBindAddress.Trim(),
                SerialPortName = string.IsNullOrWhiteSpace(_newSerialPortName) ? "COM1" : _newSerialPortName.Trim(),
                BaudRate = _newBaudRate
            };

            NewChannelLink.ApplyTo(channel);

            _connections?.Add(channel);
            ResolvedChannel = channel;
        }
    }

    /// <summary>信息对象（数据点）编辑模型（草稿副本）</summary>
    public class PointEditModel : ViewModelBase
    {
        private int _objectAddress;
        private string _name;
        private TypeDescriptor _type;
        private double _value;
        private bool _isInvalid;
        private bool _isBlocked;
        private bool _isSubstituted;

        private readonly PointViewModel _target;
        private readonly SlaveViewModel _slave;

        public PointEditModel(PointViewModel target, SlaveViewModel slave)
        {
            _target = target;
            _slave = slave;
            _objectAddress = target.ObjectAddress;
            _name = target.Name;
            _type = target.Type;
            _value = target.Value;
            _isInvalid = target.IsInvalid;
            _isBlocked = target.IsBlocked;
            _isSubstituted = target.IsSubstituted;
        }

        /// <summary>可选的类型列表（监视方向）</summary>
        public IReadOnlyList<TypeDescriptor> AvailableTypes => TypeIdCatalog.MonitorTypes;

        public int ObjectAddress
        {
            get => _objectAddress;
            set => SetField(ref _objectAddress, (int)value);
        }

        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        public TypeDescriptor Type
        {
            get => _type;
            set => SetField(ref _type, value);
        }

        public double Value
        {
            get => _value;
            set => SetField(ref _value, value);
        }

        public bool IsInvalid
        {
            get => _isInvalid;
            set => SetField(ref _isInvalid, value);
        }

        public bool IsBlocked
        {
            get => _isBlocked;
            set => SetField(ref _isBlocked, value);
        }

        public bool IsSubstituted
        {
            get => _isSubstituted;
            set => SetField(ref _isSubstituted, value);
        }

        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(_name))
            {
                return "名称不能为空。";
            }
            if (_objectAddress < 1 || _objectAddress > 0xFFFFFF)
            {
                return "信息对象地址需在 1..16777215 之间。";
            }
            if (_type == null)
            {
                return "请选择 ASDU 类型。";
            }
            if (_objectAddress != _target.ObjectAddress
                && _slave.FindPoint(_objectAddress) != null)
            {
                return $"点号 {_objectAddress} 已被占用。";
            }
            return null;
        }

        public void Apply()
        {
            _target.Name = _name;
            _target.Type = _type;
            _target.Value = _value;
            _target.IsInvalid = _isInvalid;
            _target.IsBlocked = _isBlocked;
            _target.IsSubstituted = _isSubstituted;

            // 点号变更必须走"先删后加"重建协议层索引，否则会出现同一点被上送两次
            if (_objectAddress != _target.ObjectAddress)
            {
                _slave.ChangePointAddress(_target, _objectAddress);
            }
        }
    }
}
