using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Sockets;
using Avalonia.Media;
using Avalonia.Threading;
using IEC104.App.Infrastructure;
using IEC104.App.Services;
using IEC104.Core.CS101;
using IEC104.Core.CS104;

namespace IEC104.App.ViewModels
{
    /// <summary>连接类型</summary>
    public enum ConnectionProtocol
    {
        /// <summary>IEC 60870-5-104（TCP）</summary>
        Iec104,

        /// <summary>IEC 60870-5-101（串口）</summary>
        Iec101
    }

    /// <summary>
    /// 连接（Connection）—— 一条通信通道。
    /// 104 下是一路 TCP 监听（端口），101 下是一个串口。
    /// 一条连接可以挂多个从站，主站按公共地址区分。
    ///
    /// 复刻说明：对应原程序的
    /// ViewModels.ConnectionViewModel / Connection104ViewModel / Connection101ViewModel
    /// 以及 Windows.Connection.CreateConnectionWindow / EditConnectionWindow。
    ///
    /// 本阶段实现：104 完整可用；101 的参数界面与数据结构已就位，串口收发待补齐。
    /// </summary>
    public class ConnectionViewModel : ViewModelBase, IDisposable
    {
        private string _name = "新建通道";
        private ConnectionProtocol _protocol = ConnectionProtocol.Iec104;
        private int _port = 2404;
        private string _bindAddress = "0.0.0.0";
        private string _nicId;
        private bool _autoAddAddress;
        private bool _isStarting;
        private bool _fellBackToAllNics;
        private bool _isOpen;
        private string _statusMessage = "未启动";

        // 链路参数（界面按秒显示，内部按毫秒存储）
        private int _k = 12;
        private int _w = 8;
        private int _t1Seconds = 15;
        private int _t2Seconds = 10;
        private int _t3Seconds = 20;

        // 报文长度（ASDU 里各地址字段的字节宽度）。只在启动时写进 AppParameters，
        // 运行中改了不生效——同一条链路上的宽度不能中途变，否则主站必然解析错位。
        private int _causeOfTransmissionLength = 2;
        private int _commonAddressLength = 2;
        private int _informationObjectAddressLength = 3;

        /// <summary>
        /// 本通道的应用层参数实例。
        ///
        /// 【关键：这是"同一个实例"共享给三方】
        ///   Iec104Server.AppParameters   —— 收报文时按它解析
        ///   Iec104Connection             —— 每个 Iec104Connection 构造时拿它的引用
        ///   每个 SlaveViewModel.Station  —— 发报文时按它编码
        /// 若各自复制一份值，两边一旦不同步，编出来的报文就会错位；
        /// 共用同一实例可以从结构上排除这种错位。
        /// </summary>
        public ApplicationLayerParameters AppParameters { get; } = ApplicationLayerParameters.CreateForCs104();

        /// <summary>已经挂过事件的从站。防止重复挂载导致同一帧被发两遍。</summary>
        private readonly HashSet<SlaveViewModel> _hookedSlaves = new();

        private Iec104Server _server;
        private Iec104Connection _activeConnection;

        /// <summary>当前接入的主站数，驱动状态灯</summary>
        private int _clientCount;

        /// <summary>该连接下的从站</summary>
        public ObservableCollection<SlaveViewModel> Slaves { get; } = new();

        /// <summary>收到/发出 ASDU 时触发，供主窗口写入监视列表</summary>
        public event Action<AsduRecord> AsduRecorded;

        /// <summary>链路事件（启停、连接接入/断开）</summary>
        public event Action<string> LinkEvent;

        public ConnectionViewModel()
        {
        }

        public string Name
        {
            get => _name;
            set
            {
                if (SetField(ref _name, value))
                {
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public ConnectionProtocol Protocol
        {
            get => _protocol;
            set
            {
                if (SetField(ref _protocol, value))
                {
                    OnPropertyChanged(nameof(ProtocolText));
                    OnPropertyChanged(nameof(IsTcp));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public string ProtocolText => _protocol == ConnectionProtocol.Iec104 ? "IEC 104" : "IEC 101";

        /// <summary>是否为 TCP 连接（决定界面显示端口还是串口参数）</summary>
        public bool IsTcp => _protocol == ConnectionProtocol.Iec104;

        /// <summary>104 监听端口</summary>
        public int Port
        {
            get => _port;
            set
            {
                if (SetField(ref _port, value))
                {
                    OnPropertyChanged(nameof(DisplayText));
                    OnPropertyChanged(nameof(EndPointText));
                }
            }
        }

        /// <summary>
        /// 104 监听 IP。
        /// 空 或 "0.0.0.0" = 监听本机全部网卡（默认，主站从任意网段都能连）。
        /// 填具体网卡 IP = 只在该网卡上监听，适合多网卡机器做隔离。
        /// </summary>
        public string BindAddress
        {
            get => _bindAddress;
            set
            {
                if (SetField(ref _bindAddress, string.IsNullOrWhiteSpace(value) ? "0.0.0.0" : value.Trim()))
                {
                    OnPropertyChanged(nameof(DisplayText));
                    OnPropertyChanged(nameof(EndPointText));
                    OnPropertyChanged(nameof(AddressPlanText));
                    OnPropertyChanged(nameof(AddressPlanNeedsAttention));
                    AddressPlanChanged?.Invoke();
                }
            }
        }

        /// <summary>监听端点的显示文本，如 "0.0.0.0:2404"</summary>
        public string EndPointText => $"{(string.IsNullOrWhiteSpace(_bindAddress) ? "0.0.0.0" : _bindAddress)}:{_port}";

        // ── 网卡与自动添加地址（1.11）────────────────────────────
        //
        // 【为什么挂在通道上而不是全局】绑定地址本来就是通道的属性（BindAddress）。
        // 一个主站可能接三个网段，每个网段一条通道、一块网卡；做成全局开关就变成
        // "所有通道都用同一块网卡"，多网卡隔离的场景立刻失效。
        // 批量建站时仍然默认统一一块（见 BatchCreateSlaveViewModel），但落到每条通道上
        // 是可以单独改的 —— 默认值省事，个体差异留口子。

        /// <summary>通信网卡 GUID。空 = 没选过（此时启动不做自动添加）</summary>
        public string NicId
        {
            get => _nicId;
            set
            {
                if (SetField(ref _nicId, string.IsNullOrWhiteSpace(value) ? null : value.Trim()))
                {
                    OnPropertyChanged(nameof(NicDisplayText));
                    OnPropertyChanged(nameof(AddressPlanText));
                    OnPropertyChanged(nameof(AddressPlanNeedsAttention));
                    AddressPlanChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// 启动这条通道时，若本机没有 <see cref="BindAddress"/>，就先把它加到 <see cref="NicId"/> 那块网卡上。
        ///
        /// 默认**关**：这是个会改动操作系统的动作，不该在用户没要求的时候发生。
        /// </summary>
        public bool AutoAddAddress
        {
            get => _autoAddAddress;
            set
            {
                if (SetField(ref _autoAddAddress, value))
                {
                    OnPropertyChanged(nameof(AddressPlanText));
                    OnPropertyChanged(nameof(AddressPlanNeedsAttention));
                    AddressPlanChanged?.Invoke();
                }
            }
        }

        /// <summary>网卡名（给界面看）。GUID 失效时说清楚，不要显示一个 GUID</summary>
        public string NicDisplayText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_nicId))
                {
                    return "未选择网卡";
                }

                NicInfo nic = IpAddressService.Shared.FindNic(_nicId);
                return nic == null ? "网卡已不存在" : nic.Name;
            }
        }

        /// <summary>
        /// 这条通道的地址计划，一行给用户看。
        /// 措辞统一由 <see cref="IpAddressService.DescribePlan"/> 提供——三个地方说同一句话。
        /// </summary>
        public string AddressPlanText =>
            IpAddressService.DescribePlan(_bindAddress, _nicId, _autoAddAddress).Text;

        /// <summary>地址计划是否需要用户注意（本机没有又没开自动添加这类）</summary>
        public bool AddressPlanNeedsAttention =>
            IpAddressService.DescribePlan(_bindAddress, _nicId, _autoAddAddress).Warning;

        /// <summary>地址相关设置变了（界面上的提示要跟着刷新）</summary>
        public event Action AddressPlanChanged;

        /// <summary>
        /// 启动前确保地址存在。
        ///
        /// 返回 null = 可以按原地址启动；返回非空 = **已回退到 0.0.0.0**，字符串是原因。
        /// 回退绝不静默：调用方要把这句话同时写进通道状态、状态栏和日志。
        /// </summary>
        public async System.Threading.Tasks.Task<string> PrepareAddressAsync()
        {
            string address = _bindAddress;
            if (string.IsNullOrWhiteSpace(address) || address == "0.0.0.0")
            {
                return null;
            }

            if (IpAddressService.Shared.IsLocalAddress(address))
            {
                return null;   // 本机已有，直接起
            }

            if (!_autoAddAddress)
            {
                // 没开自动添加就照旧失败，但把话说清楚 —— 原来的
                // "启动连接失败：..." 完全看不出是"本机没这个地址"
                throw new InvalidOperationException(
                    $"本机没有地址 {address}（这条通道指定的绑定地址）。"
                    + "可以在通道设置里开启“启动时自动添加地址”，或改用 0.0.0.0 监听全部网卡。");
            }

            IpChangeResult result = await IpAddressService.Shared
                .EnsureAsync(address, _nicId, IpMask.DefaultMask, $"启动从站 · {_name}")
                .ConfigureAwait(true);

            if (result.Ok)
            {
                if (!result.AlreadyExists)
                {
                    // 用一个现查的名字：这条通道可能没单独选网卡，用的是设置里的默认网卡
                    NicInfo used = IpAddressService.Shared.FindNic(_nicId)
                        ?? IpAddressService.Shared.FindNic(AppSettingsStore.Current.DefaultNicId);
                    LinkEvent?.Invoke($"[{_name}] 已把地址 {address} 添加到网卡 {used?.Name ?? "?"}");
                }
                return null;
            }

            // 加不上：按用户的选择回退到 0.0.0.0 先起来，把原因交回去让三处都写明
            string reason = $"{address} 未能添加到本机（{result.Message}），已回退为监听 0.0.0.0。";
            _fellBackToAllNics = true;
            LinkEvent?.Invoke($"[{_name}] {reason}");
            return reason;
        }

        /// <summary>上一次启动是不是回退到了 0.0.0.0（状态栏据此显示警告色）</summary>
        public bool FellBackToAllNics => _fellBackToAllNics;

        /// <summary>
        /// 启动成功后把"已回退"写进通道自己的状态文字。
        ///
        /// 必须在 Open() 之后调：Open() 会把 StatusMessage 覆盖成"监听 x:y"，
        /// 而回退恰恰是"显示的监听地址和配置里的地址不一样"，这条信息不能被覆盖掉。
        /// </summary>
        public void MarkFallback(string shortText)
        {
            _fellBackToAllNics = true;
            StatusMessage = shortText;
            OnPropertyChanged(nameof(FellBackToAllNics));
        }

        /// <summary>启动进行中（防止地址还没加完用户又点一次，起两条服务）</summary>
        public bool IsStarting => _isStarting;

        /// <summary>启动前的一小段异步准备（加地址）；界面据此禁用按钮</summary>
        public bool BeginStart()
        {
            if (_isStarting)
            {
                return false;
            }

            _isStarting = true;
            OnPropertyChanged(nameof(IsStarting));
            return true;
        }

        /// <summary>准备结束，恢复按钮</summary>
        public void EndStart()
        {
            _isStarting = false;
            OnPropertyChanged(nameof(IsStarting));
        }

        /// <summary>101 串口名（如 COM3）</summary>
        public string SerialPortName { get; set; } = "COM1";

        /// <summary>101 波特率</summary>
        public int BaudRate { get; set; } = 9600;

        public string DisplayText => _protocol == ConnectionProtocol.Iec104
            ? $"{_name} · TCP:{_port}"
            : $"{_name} · {SerialPortName}";

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

        // ── 报文长度（地址字段宽度）─────────────────────────────

        /// <summary>传送原因长度（字节）：1 或 2。为 2 时其后紧跟 1 字节源发地址</summary>
        public int CauseOfTransmissionLength
        {
            get => _causeOfTransmissionLength;
            set
            {
                if (SetField(ref _causeOfTransmissionLength, value))
                {
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
                    OnPropertyChanged(nameof(ApplicationWidthsText));
                }
            }
        }

        /// <summary>三个长度的一句话摘要，界面上用来一眼确认这条通道按什么宽度收发</summary>
        public string ApplicationWidthsText =>
            $"COT {_causeOfTransmissionLength}B · CA {_commonAddressLength}B · IOA {_informationObjectAddressLength}B";

        /// <summary>
        /// 按界面上配置的三个长度拼一份参数（新实例）。
        /// 启动时用它写进共享的 <see cref="AppParameters"/>；
        /// 报文解析器也用它——解析器看的是"你配的宽度"，而不是"当前正在跑的宽度"，
        /// 通道没启动时后者还是默认值，拿它去解非默认宽度的报文会整段错位。
        /// </summary>
        public ApplicationLayerParameters BuildConfiguredParameters()
        {
            return new ApplicationLayerParameters
            {
                SizeOfCauseOfTransmission = _causeOfTransmissionLength,
                SizeOfCommonAddress = _commonAddressLength,
                SizeOfInformationObjectAddress = _informationObjectAddressLength
            };
        }

        /// <summary>是否已启动</summary>
        public bool IsOpen
        {
            get => _isOpen;
            private set
            {
                if (SetField(ref _isOpen, value))
                {
                    OnPropertyChanged(nameof(StateText));
                    OnPropertyChanged(nameof(CanOpen));
                    OnPropertyChanged(nameof(CanClose));
                    RaiseStateVisuals();
                }
            }
        }

        public bool CanOpen => !IsOpen;
        public bool CanClose => IsOpen;

        /// <summary>
        /// 通道状态灯的三态。
        /// 为什么要区分"监听中"和"已连接"：现场排查时"端口开了但主站没连上"
        /// 和"主站已经连上但不发数据"是两类完全不同的问题，状态灯得能一眼分开。
        /// </summary>
        public enum ChannelState
        {
            Stopped = 0,
            Listening = 1,
            Connected = 2
        }

        /// <summary>当前已接入的主站数量</summary>
        public int ClientCount
        {
            get => _clientCount;
            private set
            {
                if (SetField(ref _clientCount, value))
                {
                    OnPropertyChanged(nameof(StateText));
                    RaiseStateVisuals();
                }
            }
        }

        public ChannelState State =>
            !IsOpen ? ChannelState.Stopped
            : ClientCount > 0 ? ChannelState.Connected
            : ChannelState.Listening;

        /// <summary>状态灯颜色：灰=停止，橙=监听中，绿=主站已接入（跟主题令牌走）</summary>
        public IBrush StateBrush => State switch
        {
            ChannelState.Connected => ThemePalette.StateConnected,
            ChannelState.Listening => ThemePalette.StateListening,
            _ => ThemePalette.StateStopped
        };

        /// <summary>状态灯外圈（同色系淡一点，做出"亮起"的观感）</summary>
        public IBrush StateHaloBrush => State switch
        {
            ChannelState.Connected => ThemePalette.StateConnectedHalo,
            ChannelState.Listening => ThemePalette.StateListeningHalo,
            _ => ThemePalette.StateStoppedHalo
        };

        public string StateText => State switch
        {
            ChannelState.Connected => ClientCount > 1 ? $"已连接 {ClientCount} 个主站" : "主站已接入",
            ChannelState.Listening => "监听中（等待主站）",
            _ => "已停止"
        };

        private void RaiseStateVisuals()
        {
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(StateBrush));
            OnPropertyChanged(nameof(StateHaloBrush));
            OnPropertyChanged(nameof(StateText));
        }

        /// <summary>主题切换后重新取状态灯颜色</summary>
        public void RefreshThemeVisuals() => RaiseStateVisuals();

        /// <summary>主站连接数变化时刷新状态灯（可能在后台线程触发）</summary>
        private void OnClientCountChanged()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(OnClientCountChanged);
                return;
            }

            ClientCount = _server?.ClientCount ?? 0;
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetField(ref _statusMessage, value);
        }

        /// <summary>
        /// 启动连接。
        /// 104：创建 Iec104Server 并开始监听；失败（端口占用等）以异常返回给上层提示。
        /// </summary>
        public void Open()
        {
            if (IsOpen)
            {
                return;
            }

            _fellBackToAllNics = false;

            if (_protocol == ConnectionProtocol.Iec101)
            {
                // 101 串口收发尚未复刻，这里明确告知而不是假装成功
                throw new NotSupportedException(
                    "IEC 101 串口收发尚未实现。104（TCP）模式已可完整运行。");
            }

            ApciParameters parameters = new ApciParameters
            {
                K = _k,
                W = _w,
                T1Milliseconds = _t1Seconds * 1000,
                T2Milliseconds = _t2Seconds * 1000,
                T3Milliseconds = _t3Seconds * 1000
            };

            string error = parameters.Validate();
            if (error != null)
            {
                throw new InvalidOperationException(error);
            }

            // ── 三个长度参数：启动时统一写进共享参数实例，之后收发两端都按它走 ──
            ApplicationLayerParameters appParameters = BuildConfiguredParameters();

            string appError = appParameters.Validate();
            if (appError != null)
            {
                throw new InvalidOperationException(appError);
            }

            // 宽度合法 ≠ 现有地址装得下。1 字节公共地址只能放 0..255，
            // 若这台通道下挂着 CA=1000 的从站，编码时会被截断成 0xE8 —— 报文格式合法、
            // 主站却找不到站。这种"格式没错但内容错了"的故障最难查，必须提前拦。
            string fitError = ValidateAddressWidths(appParameters);
            if (fitError != null)
            {
                throw new InvalidOperationException(fitError);
            }

            AppParameters.CopyFrom(appParameters);

            _server = new Iec104Server();
            _server.AppParameters = AppParameters;
            _server.Parameters.K = parameters.K;
            _server.Parameters.W = parameters.W;
            _server.Parameters.T1Milliseconds = parameters.T1Milliseconds;
            _server.Parameters.T2Milliseconds = parameters.T2Milliseconds;
            _server.Parameters.T3Milliseconds = parameters.T3Milliseconds;

            _server.LinkEventLogged += message => LinkEvent?.Invoke($"[{_name}] {message}");
            _server.AsduReceived += OnAsduReceived;
            _server.ClientConnected += connection =>
            {
                // 新连接接入时把已有点位的变化也定向到这条连接
                _activeConnection = connection;
                LinkEvent?.Invoke($"[{_name}] 主站接入，来源 {connection.RemoteEndPointText}");
            };

            // 主站接入/断开都刷新状态灯：灰(停止) → 橙(监听中) → 绿(主站已接入)
            _server.ClientCountChanged += OnClientCountChanged;

            // 先把从站的事件接好，避免启动后收到的变化无人接收
            foreach (SlaveViewModel slave in Slaves)
            {
                HookSlave(slave);
            }

            try
            {
                _server.Start(_port, _bindAddress);
            }
            catch (SocketException exception)
            {
                // 起不来就把半成品收干净：_server 已经挂过事件了，留着等于一个
                // "看起来存在但没在监听"的对象，后面的 Close() 还会以为要停它。
                // 从站也要解钩，否则它们继续往这条死通道发 ASDU。
                _server.Dispose();
                _server = null;
                foreach (SlaveViewModel slave in Slaves)
                {
                    UnhookSlave(slave);
                }
                throw new InvalidOperationException(DescribeSocketError(exception), exception);
            }

            IsOpen = true;
            StatusMessage = $"监听 {EndPointText}";
            LinkEvent?.Invoke($"[{_name}] 已启动，监听 {EndPointText}");
        }

        /// <summary>把套接字错误翻译成用户能动手解决的一句话</summary>
        private string DescribeSocketError(SocketException exception)
        {
            switch (exception.SocketErrorCode)
            {
                case SocketError.AddressNotAvailable:
                    // 10049：本机没有这个地址。原来这里只会显示"启动连接失败"，用户看不出原因
                    return $"本机没有地址 {_bindAddress}，所以无法在它上面监听（系统错误 10049）。"
                        + "可以：① 通道设置里勾选“启动时自动添加地址”并选一块网卡；"
                        + "② 用「工具 → IP 地址管理」手工把它加进去；"
                        + "③ 改成 0.0.0.0 监听全部网卡。";
                case SocketError.AddressAlreadyInUse:
                    return $"端口 {_port} 已被占用（系统错误 10048）。"
                        + "可能是本程序已经起过一个实例，或者别的软件在监听这个端口，换一个端口再试。";
                case SocketError.AccessDenied:
                    return $"系统拒绝绑定端口 {_port}（1024 以下的端口通常需要管理员权限）。";
                default:
                    return $"监听 {EndPointText} 失败：{exception.Message}"
                        + $"（系统错误 {(int)exception.SocketErrorCode}）";
            }
        }

        /// <summary>停止连接并释放资源</summary>
        public void Close()
        {
            if (!IsOpen)
            {
                return;
            }

            foreach (SlaveViewModel slave in Slaves)
            {
                UnhookSlave(slave);
            }

            if (_server != null)
            {
                _server.ClientCountChanged -= OnClientCountChanged;
            }

            _server?.Dispose();
            _server = null;
            _activeConnection = null;
            ClientCount = 0;
            IsOpen = false;
            StatusMessage = "已停止";
            LinkEvent?.Invoke($"[{_name}] 已停止");
        }

        /// <summary>
        /// 把从站接到本连接：
        /// 1) 让从站按本通道的报文长度编码（共用同一个参数实例）
        /// 2) 订阅其"待发送 ASDU"事件，把应答定向回发起请求的那条 TCP 连接
        /// 3) 订阅其遥控回调，刷新界面点位
        ///
        /// 幂等：重复调用只做参数同步，不会重复订阅事件。
        /// （启动时会对所有从站再调一次，重复订阅会让同一帧被发两遍。）
        /// </summary>
        public void HookSlave(SlaveViewModel slave)
        {
            if (slave == null)
            {
                return;
            }

            slave.Station.Parameters = AppParameters;

            if (!_hookedSlaves.Add(slave))
            {
                return;
            }

            slave.Station.AsduReadyToSend += asdu => SendAsdu(asdu, slave);
            slave.Station.ControlCommandReceived += (ioa, value) => slave.RefreshPoint(ioa);
        }

        public void UnhookSlave(SlaveViewModel slave)
        {
            // 事件是 Action<Asdu> 委托，这里只能整体清空该从站的订阅
            // （复刻版每个从站只被一条连接持有，故无副作用）
            if (slave != null)
            {
                _hookedSlaves.Remove(slave);
                slave.Station.ClearSendSubscribers();
            }
        }

        /// <summary>
        /// 检查现有从站的公共地址、点号能否装进配置的地址宽度。
        /// 返回 null 表示都装得下。
        /// </summary>
        private string ValidateAddressWidths(ApplicationLayerParameters appParameters)
        {
            foreach (SlaveViewModel slave in Slaves)
            {
                if (slave.CommonAddress < 1 || slave.CommonAddress > appParameters.MaxCommonAddress)
                {
                    return $"从站“{slave.Name}”的公共地址 {slave.CommonAddress} 超过公共地址长度 "
                        + $"{appParameters.SizeOfCommonAddress} 字节的上限 {appParameters.MaxCommonAddress}。"
                        + "请把公共地址改小，或把该长度改回 2 字节。";
                }

                foreach (PointViewModel point in slave.Points)
                {
                    if (point.ObjectAddress > appParameters.MaxInformationObjectAddress)
                    {
                        return $"从站“{slave.Name}”的点号 {point.ObjectAddress} 超过信息体地址长度 "
                            + $"{appParameters.SizeOfInformationObjectAddress} 字节的上限 "
                            + $"{appParameters.MaxInformationObjectAddress}。请把该点号改小，或把该长度改回 3 字节。";
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 给报文监视编码一份 ASDU 原始字节（不含 APCI，APCI 是逐帧的序号/长度，不归 ASDU 管）。
        ///
        /// 监视是旁路：编码失败（地址超宽之类）绝不能把收发带下去，
        /// 拿不到就记空——面板里会明确写"这条记录没有原始字节"，而不是留一片白。
        /// </summary>
        private static byte[] EncodeForMonitor(Asdu asdu)
        {
            try
            {
                return asdu?.Encode();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>发送一个 ASDU：优先定向回当前交互的连接，否则广播</summary>
        private void SendAsdu(Asdu asdu, SlaveViewModel slave)        {
            if (_server == null)
            {
                return;
            }

            Iec104Connection target = _activeConnection;
            try
            {
                // 先做地址宽度预检：填错的点号不该把整条连接带下去
                asdu.ValidateAddresses();

                if (target != null && target.IsConnected)
                {
                    target.SendAsdu(asdu);
                }
                else
                {
                    _server.BroadcastAsdu(asdu);
                }
            }
            catch (Exception exception)
            {
                LinkEvent?.Invoke($"[{_name}] 发送失败：{exception.Message}");
                return;
            }

            // 原始字节要真的带上：1.10.1 之前这里恒传 null，
            // 于是报文监视下面那块"原始字节"永远是一片空白——
            // 面板上没有任何东西，用户根本分不清是"没选中"还是"取不到"。
            OnAsduRecorded(AsduRecord.FromAsdu(asdu, "发", EncodeForMonitor(asdu)), slave);
        }

        /// <summary>
        /// 收到主站 ASDU：按公共地址路由到对应从站。
        /// 路由失败时不能静默丢弃，否则表现为"主站发了但没反应"，极难排查。
        /// </summary>
        private void OnAsduReceived(Iec104Connection connection, Asdu asdu)
        {
            _activeConnection = connection;

            SlaveViewModel target = Slaves.FirstOrDefault(s => s.CommonAddress == asdu.CommonAddress);
            if (target == null)
            {
                LinkEvent?.Invoke(
                    $"[{_name}] 收到未知公共地址 {asdu.CommonAddress} 的报文，已忽略");
                return;
            }

            OnAsduRecorded(AsduRecord.FromAsdu(asdu, "收", EncodeForMonitor(asdu)), target);

            try
            {
                target.Station.HandleReceivedAsdu(asdu);
            }
            catch (Exception exception)
            {
                LinkEvent?.Invoke($"[{_name}] 处理 ASDU 失败：{exception.Message}");
            }
        }

        private void OnAsduRecorded(AsduRecord record, SlaveViewModel slave)
        {
            AsduRecorded?.Invoke(record);
        }

        public void Dispose()
        {
            Close();
        }
    }
}
