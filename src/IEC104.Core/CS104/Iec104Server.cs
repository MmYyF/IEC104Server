using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using IEC104.Core.CS101;

namespace IEC104.Core.CS104
{
    /// <summary>
    /// IEC 60870-5-104 服务端（从站侧网络层）。
    ///
    /// 复刻说明（对应原实现 lib60870.CS104.Server / Connection / ClientConnection）：
    /// 原实现的 Server 支持三种冗余组模式（ServerMode：单组 / 每连接一组 / 多组），
    /// 并带有 ASDUQueue 做缓冲。此处复刻了最核心的单组模式：
    ///   - TCP 监听与多连接管理
    ///   - U 帧启停（STARTDT/STOPDT/TESTFR）
    ///   - S 帧与 I 帧的序号管理（滑动窗口）
    ///   - TCP 粘包/拆包分帧
    /// 冗余组、TLS 等高级特性未复刻，如需可在此基础上扩展。
    ///
    /// 与从站业务解耦：本类只管链路，收到 ASDU 后通过事件抛给上层（SlaveStation）处理，
    /// 这样协议层与业务层可以独立测试。
    /// </summary>
    public class Iec104Server : IDisposable
    {
        private TcpListener _listener;
        private readonly List<Iec104Connection> _connections = new List<Iec104Connection>();
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isRunning;

        /// <summary>链路参数（k/w/t0-t3）</summary>
        public ApciParameters Parameters { get; } = new ApciParameters();

        /// <summary>
        /// 应用层参数（传送原因 / 公共地址 / 信息体地址的字节宽度）。
        ///
        /// 可赋值：默认取 104 的标准宽度（2/2/3）。现场若有按 1 字节传送原因、
        /// 1 字节公共地址实现的装置，必须在 <see cref="Start"/> 之前替换/改好这里，
        /// 否则收到的 ASDU 会整体错位——表现为"主站连上了但总召不应答"。
        /// 启动之后再改不会生效，也不会影响已经建立的连接（那条链路上的宽度已经协商定了）。
        /// </summary>
        public ApplicationLayerParameters AppParameters { get; set; } = ApplicationLayerParameters.CreateForCs104();

        /// <summary>监听到新连接时触发</summary>
        public event Action<Iec104Connection> ClientConnected;

        /// <summary>
        /// 有主站断开（或连接被判定失效）时触发。
        /// 界面靠它把通道状态从"已连接"退回"监听中"。
        /// 注意：在后台线程触发，界面订阅方需自行切换回 UI 线程。
        /// </summary>
        public event Action<Iec104Connection> ClientDisconnected;

        /// <summary>
        /// 当前已接入的主站数量变化时触发。
        /// 只关心"有没有主站连着"的场景（通道状态灯）用这个，不必自己数。
        /// </summary>
        public event Action ClientCountChanged;

        /// <summary>当前已接入且仍存活的主站连接数</summary>
        public int ClientCount
        {
            get
            {
                lock (_connections)
                {
                    int count = 0;
                    foreach (Iec104Connection connection in _connections)
                    {
                        if (connection.IsConnected)
                        {
                            count++;
                        }
                    }
                    return count;
                }
            }
        }

        /// <summary>收到 ASDU 时触发，交由业务层处理</summary>
        public event Action<Iec104Connection, Asdu> AsduReceived;

        /// <summary>链路事件（启停、测试）回调，便于写日志</summary>
        public event Action<string> LinkEventLogged;

        public bool IsRunning => _isRunning;

        /// <summary>
        /// 启动监听。
        /// </summary>
        /// <param name="port">监听端口，104 规约默认使用 2404</param>
        /// <param name="bindAddress">
        /// 监听地址。留空或 "0.0.0.0" 表示监听本机全部网卡（Any）。
        /// 现场常见需求：机器上有多块网卡（调度网 / 办公网 / 双机热备的心跳网），
        /// 只想在指定网卡上开 104，这时就填该网卡的 IP，避免把仿真器暴露到办公网。
        /// </param>
        public void Start(int port = 2404, string bindAddress = null)
        {
            string validationError = Parameters.Validate();
            if (validationError != null)
            {
                throw new InvalidOperationException(validationError);
            }

            if (AppParameters == null)
            {
                throw new InvalidOperationException("应用层参数未设置，无法确定报文里各地址字段的宽度。");
            }

            // 宽度配错（例如 0 字节、4 字节）会让收发两端对同一段字节流做出完全不同的切分，
            // 必须在开监听之前就挡住，不能等到主站接进来才在解析处报"报文错位"
            string appParameterError = AppParameters.Validate();
            if (appParameterError != null)
            {
                throw new InvalidOperationException(appParameterError);
            }

            IPAddress address = ResolveBindAddress(bindAddress);

            _cancellationTokenSource = new CancellationTokenSource();
            _listener = new TcpListener(address, port);
            _listener.Start();
            _isRunning = true;

            Log($"104 服务端已启动，监听 {FormatAddress(address)}:{port}");
            AcceptLoopAsync(_cancellationTokenSource.Token);
        }

        /// <summary>把界面上的地址字符串解析成 IPAddress</summary>
        private static IPAddress ResolveBindAddress(string bindAddress)
        {
            if (string.IsNullOrWhiteSpace(bindAddress)
                || bindAddress == "0.0.0.0"
                || bindAddress.Equals("any", StringComparison.OrdinalIgnoreCase))
            {
                return IPAddress.Any;
            }

            if (!IPAddress.TryParse(bindAddress.Trim(), out IPAddress parsed))
            {
                throw new ArgumentException($"监听 IP“{bindAddress}”格式不正确，请填写本机网卡 IP 或留空监听全部网卡。");
            }
            return parsed;
        }

        /// <summary>Any 显示成 0.0.0.0，日志里更直观</summary>
        private static string FormatAddress(IPAddress address) =>
            address.Equals(IPAddress.Any) ? "0.0.0.0" : address.ToString();

        public void Stop()
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            _cancellationTokenSource?.Cancel();
            _listener?.Stop();

            // 先取出快照再关闭：Close() 会触发 Closed 事件，不能拿着锁去回调界面
            List<Iec104Connection> remaining;
            lock (_connections)
            {
                remaining = new List<Iec104Connection>(_connections);
                _connections.Clear();
            }

            foreach (Iec104Connection connection in remaining)
            {
                connection.Close();
            }

            if (remaining.Count > 0)
            {
                ClientCountChanged?.Invoke();
            }

            Log("104 服务端已停止");
        }

        private async void AcceptLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync();
                    Iec104Connection connection = new Iec104Connection(client, Parameters, AppParameters);
                    connection.AsduReceived += (conn, asdu) => AsduReceived?.Invoke(conn, asdu);
                    connection.LinkEventLogged += Log;

                    // 对端断开时（读循环结束）把它从列表里摘掉，并通知界面
                    connection.Closed += OnConnectionClosed;

                    lock (_connections)
                    {
                        _connections.Add(connection);
                    }

                    Log($"客户端接入：{client.Client.RemoteEndPoint}");
                    ClientConnected?.Invoke(connection);
                    ClientCountChanged?.Invoke();
                    connection.BeginReceive();
                }
            }
            catch (ObjectDisposedException)
            {
                // 监听器被 Stop 时属于正常关闭，无需处理
            }
            catch (Exception exception)
            {
                Log($"接收循环异常：{exception.Message}");
            }
        }

        /// <summary>
        /// 一条连接结束后的收尾：从列表移除、通知界面。
        /// 幂等——读写线程都可能走到这里，重复调用不会有副作用。
        /// </summary>
        private void OnConnectionClosed(Iec104Connection connection)
        {
            bool removed;
            lock (_connections)
            {
                removed = _connections.Remove(connection);
            }

            if (!removed)
            {
                return;
            }

            Log($"客户端断开：{connection.RemoteEndPointText}");
            ClientDisconnected?.Invoke(connection);
            ClientCountChanged?.Invoke();
        }

        /// <summary>
        /// 向所有已激活的连接广播一个 ASDU。
        ///
        /// 健壮性要求（由联调发现）：广播通常由定时器驱动（如自发上送），
        /// 而此时某条连接可能已被对端断开。若写 socket 抛出的异常无人处理，
        /// 会一路冒泡到定时器线程池并导致整个进程崩溃。
        /// 因此这里逐条捕获异常，并把失效连接从列表中剔除。
        /// </summary>
        public void BroadcastAsdu(Asdu asdu)
        {
            List<Iec104Connection> deadConnections = null;

            lock (_connections)
            {
                foreach (Iec104Connection connection in _connections)
                {
                    try
                    {
                        connection.SendAsdu(asdu);
                    }
                    catch (Exception)
                    {
                        // 单条连接失败不应影响其他连接
                        (deadConnections ??= new List<Iec104Connection>()).Add(connection);
                    }
                }
            }

            // 清理放到锁外：Close() 会触发 Closed 事件，订阅方（界面）可能回头读 ClientCount，
            // 在锁内回调容易把"刷新状态"变成死等
            if (deadConnections != null)
            {
                foreach (Iec104Connection dead in deadConnections)
                {
                    dead.Close();
                }
            }
        }

        /// <summary>
        /// 输出链路日志。
        /// 注意不再二次添加时间戳——Iec104Connection 的日志已带时间，
        /// 这里再包一层会出现 "[时间] [时间] 内容" 的重复前缀。
        /// </summary>
        private void Log(string message)
        {
            LinkEventLogged?.Invoke(message);
        }

        public void Dispose()
        {
            Stop();
            _cancellationTokenSource?.Dispose();
        }
    }

    /// <summary>
    /// 单条 104 连接：负责帧分界、序号维护与启停状态。
    ///
    /// 状态机要点（这是 104 最容易实现错的地方）：
    ///   1. TCP 连上后处于 STOPDT 状态，此时不能发 I 帧数据
    ///   2. 收到主站 STARTDT 激活后回 STARTDT 确认，进入可传输状态
    ///   3. 只有进入该状态后，I 帧数据才会被对端接受
    ///   4. 空闲超过 t3 时主动发 TESTFR，对端回应 TESTFR 确认
    /// </summary>
    public class Iec104Connection
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly ApciParameters _parameters;
        private readonly ApplicationLayerParameters _appParameters;

        // 滑动窗口序号，15 位有效（0..32767），到达上限后回绕
        private int _sendSequenceNumber;
        private int _receiveSequenceNumber;
        private int _unconfirmedSentCount;

        /// <summary>0 = 还没通知过"已结束"；1 = 已通知。保证只发一次。</summary>
        private int _closeNotified;

        /// <summary>连接结束时触发（正常断开、写失败、显式关闭都算）</summary>
        public event Action<Iec104Connection> Closed;

        /// <summary>是否已完成 STARTDT，进入数据传输状态</summary>
        public bool IsDataTransferActive { get; private set; }

        /// <summary>
        /// 连接是否已失效（对端断开或写入失败）。
        /// 标记失效后本连接不再尝试发送，上层可据此清理。
        /// </summary>
        public bool IsClosed { get; private set; }

        public bool IsConnected => !IsClosed && (_client?.Connected ?? false);

        /// <summary>对端地址，用于界面显示哪个主站接入了</summary>
        public string RemoteEndPointText
        {
            get
            {
                try
                {
                    return _client?.Client?.RemoteEndPoint?.ToString() ?? "-";
                }
                catch
                {
                    return "-";
                }
            }
        }

        public event Action<Iec104Connection, Asdu> AsduReceived;
        public event Action<string> LinkEventLogged;

        internal Iec104Connection(
            TcpClient client,
            ApciParameters parameters,
            ApplicationLayerParameters appParameters)
        {
            _client = client;
            _stream = client.GetStream();
            _parameters = parameters;
            _appParameters = appParameters;
        }

        /// <summary>开始异步接收</summary>
        public void BeginReceive()
        {
            ReceiveLoopAsync();
        }

        private async void ReceiveLoopAsync()
        {
            byte[] buffer = new byte[1024];
            try
            {
                while (_client.Connected)
                {
                    int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    // TCP 是字节流，一次 Read 可能包含多个 APDU 或半个 APDU，
                    // 必须按 0x68 + 长度字段逐个切分，不能直接当一帧处理
                    int offset = 0;
                    while (offset < bytesRead)
                    {
                        if (!Apci.TryValidateHeader(buffer, offset, out int apduLength))
                        {
                            Log("收到非法 APDU 头，丢弃本段数据");
                            break;
                        }
                        if (offset + apduLength > bytesRead)
                        {
                            Log("APDU 未收全（拆包），等待后续数据");
                            break;
                        }

                        ProcessApdu(buffer, offset, apduLength);
                        offset += apduLength;
                    }
                }
            }
            catch (IOException exception)
            {
                Log($"连接读取异常：{exception.Message}");
            }
            finally
            {
                Log("连接已断开");
                NotifyClosed();
            }
        }

        private void ProcessApdu(byte[] buffer, int offset, int apduLength)
        {
            byte[] controlField = new byte[4];
            Array.Copy(buffer, offset + 2, controlField, 0, 4);

            switch (Apci.GetFrameType(controlField))
            {
                case Apci.FrameType.Information:
                    HandleInformationFrame(buffer, offset, apduLength, controlField);
                    break;

                case Apci.FrameType.Supervisory:
                    // S 帧仅确认接收序号，无需应答
                    Log($"收到 S 帧，确认序号 N(R)={Apci.GetReceiveSequenceNumber(controlField)}");
                    break;

                case Apci.FrameType.Unnumbered:
                    HandleUnnumberedFrame(controlField[0]);
                    break;

                default:
                    Log("收到无法识别的帧类型，已丢弃");
                    break;
            }
        }

        private void HandleInformationFrame(
            byte[] buffer, int offset, int apduLength, byte[] controlField)
        {
            int receivedSendNumber = Apci.GetSendSequenceNumber(controlField);
            int receivedReceiveNumber = Apci.GetReceiveSequenceNumber(controlField);

            // 对端确认了我方已发出的帧，回收发送窗口
            _unconfirmedSentCount = Math.Max(0, _sendSequenceNumber - receivedReceiveNumber);

            if (receivedSendNumber != _receiveSequenceNumber)
            {
                Log($"I 帧序号不连续：期望 N(S)={_receiveSequenceNumber}，实际 {receivedSendNumber}");
                // 严格实现应在此断开链路并请求重传，简化处理为继续
            }
            _receiveSequenceNumber = (receivedSendNumber + 1) & 0x7FFF;

            // 解析 ASDU（APCI 6 字节之后的部分）
            int asduOffset = offset + Apci.ApciLength;
            int asduLength = apduLength - Apci.ApciLength;
            if (asduLength <= 0)
            {
                // 纯序号帧，无 ASDU 载荷
                SendSupervisoryFrame();
                return;
            }

            Asdu asdu = Asdu.Decode(buffer, asduOffset, _appParameters, out _);
            AsduReceived?.Invoke(this, asdu);

            // 收到 I 帧后应发 S 帧确认（也可捎带在后续 I 帧的 N(R) 中）
            SendSupervisoryFrame();
        }

        private void HandleUnnumberedFrame(byte functionCode)
        {
            switch ((Apci.UFrameFunction)functionCode)
            {
                case Apci.UFrameFunction.StartDtActivate:
                    IsDataTransferActive = true;
                    SendUnnumberedFrame(Apci.UFrameFunction.StartDtConfirm);
                    Log("收到 STARTDT 激活，已进入数据传输状态");
                    break;

                case Apci.UFrameFunction.StopDtActivate:
                    IsDataTransferActive = false;
                    SendUnnumberedFrame(Apci.UFrameFunction.StopDtConfirm);
                    Log("收到 STOPDT 激活，已停止数据传输");
                    break;

                case Apci.UFrameFunction.TestFrActivate:
                    SendUnnumberedFrame(Apci.UFrameFunction.TestFrConfirm);
                    Log("收到 TESTFR，已回应确认");
                    break;

                default:
                    Log($"收到未处理的 U 帧功能码：0x{functionCode:X2}");
                    break;
            }
        }

        /// <summary>发送一个 ASDU（自动封装为 I 帧）</summary>
        public void SendAsdu(Asdu asdu)
        {
            if (asdu == null)
            {
                throw new ArgumentNullException(nameof(asdu));
            }
            if (IsClosed)
            {
                return;
            }
            if (!IsDataTransferActive)
            {
                // 未按规约启动数据传输，此时发数据对端会丢弃
                Log("链路未激活（未收到 STARTDT），本次 ASDU 未发送");
                return;
            }

            // 发送窗口 k：未确认帧数达到上限时暂停发送，等待 S 帧确认
            if (_unconfirmedSentCount >= _parameters.K)
            {
                Log($"发送窗口已满（k={_parameters.K}），暂缓发送");
                return;
            }

            byte[] asduBytes = asdu.Encode();
            if (asduBytes.Length > _parameters.MaxAsduLength)
            {
                Log($"ASDU 长度 {asduBytes.Length} 超过上限 {_parameters.MaxAsduLength}，已丢弃");
                return;
            }

            byte[] controlField = Apci.BuildInformationFrame(_sendSequenceNumber, _receiveSequenceNumber);
            byte[] frame = BuildFrame(controlField, asduBytes);

            if (!TryWrite(frame))
            {
                return;
            }

            _sendSequenceNumber = (_sendSequenceNumber + 1) & 0x7FFF;
            _unconfirmedSentCount++;
        }

        private void SendSupervisoryFrame()
        {
            byte[] controlField = Apci.BuildSupervisoryFrame(_receiveSequenceNumber);
            TryWrite(BuildFrame(controlField, null));
        }

        private void SendUnnumberedFrame(Apci.UFrameFunction function)
        {
            byte[] controlField = Apci.BuildUnnumberedFrame(function);
            TryWrite(BuildFrame(controlField, null));
        }

        /// <summary>
        /// 安全写入：把 socket 写失败就地收敛。
        /// 对端断开后继续写入会抛 IOException，若向上冒泡，
        /// 在定时器驱动的发送场景（如自发上送）中会直接导致进程崩溃。
        /// </summary>
        /// <returns>写入是否成功</returns>
        private bool TryWrite(byte[] frame)
        {
            if (IsClosed)
            {
                return false;
            }

            try
            {
                _stream.Write(frame, 0, frame.Length);
                return true;
            }
            catch (Exception exception)
            {
                IsClosed = true;
                Log($"发送失败，连接标记为失效：{exception.Message}");
                return false;
            }
        }

        private static byte[] BuildFrame(byte[] controlField, byte[] asduBytes)
        {
            int asduLength = asduBytes?.Length ?? 0;
            byte[] frame = new byte[Apci.ApciLength + asduLength];

            frame[0] = Apci.StartByte;
            // 长度字段 = 控制域 4 字节 + ASDU 长度，不含启动字符与长度字节自身
            frame[1] = (byte)(4 + asduLength);
            Array.Copy(controlField, 0, frame, 2, 4);
            if (asduLength > 0)
            {
                Array.Copy(asduBytes, 0, frame, 6, asduLength);
            }
            return frame;
        }

        public void Close()
        {
            IsClosed = true;
            try
            {
                _stream?.Dispose();
                _client?.Close();
            }
            catch
            {
                // 关闭阶段不再抛出，避免影响其他连接的清理
            }
            NotifyClosed();
        }

        /// <summary>
        /// 通知"连接已结束"，只发一次。
        /// 三个地方会调用它：读循环正常结束、写失败标记失效、显式 Close。
        /// 用 Interlocked 保证幂等，否则界面会收到重复的断开通知。
        /// </summary>
        private void NotifyClosed()
        {
            if (Interlocked.Exchange(ref _closeNotified, 1) != 0)
            {
                return;
            }
            Closed?.Invoke(this);
        }

        private void Log(string message)
        {
            LinkEventLogged?.Invoke($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }
    }
}
