using System;
using IEC104.Core.CS101;
using IEC104.Core.Time;

namespace IEC104.Simulator.Handlers
{
    /// <summary>
    /// 从站命令处理器集合。
    ///
    /// 复刻说明（对应原实现 IEC104ServerSimulator 中 Handler / ViewModel 目录下的同名处理器）：
    /// 原实现把每个命令的处理逻辑放在对应 ViewModel 里，与界面代码耦合较紧；
    /// 此处剥离界面依赖，做成可独立单测的处理器类，职责更清晰。
    ///
    /// 三个处理器共同遵循的模式：
    ///   Handle(asdu) 只负责"解析命令 + 抛出事件"，不负责组帧；
    ///   BuildXxx(...) 只负责"组装应答帧"，不改状态。
    /// 解析与组帧分离，是为了让"命令解析"和"应答格式"能分别被测试与复用。
    /// </summary>

    /// <summary>
    /// 总召唤（C_IC_NA_1 / TypeID 100）处理器。
    ///
    /// 总召是 104 最核心的交互流程，规约要求的三段式响应必须严格照做，
    /// 少发任何一段都会导致主站一直等待、判定总召超时：
    ///
    ///   1) 主站下发 C_IC_NA_1，COT=6（激活），信息元素为 QOI
    ///   2) 从站回 C_IC_NA_1，COT=7（激活确认）—— 先确认收到
    ///   3) 从站按 COT=20（响应站召唤）逐组上送全部数据
    ///   4) 从站回 C_IC_NA_1，COT=10（激活终止）—— 告知数据已送完
    ///
    /// 第 4 步的 COT=10 是主站判定总召完成的唯一依据，绝不能省。
    /// 现场最常见的"总召超时"故障，就是从站漏发了激活终止帧。
    /// </summary>
    public class InterrogationHandler
    {
        /// <summary>总召被激活时触发，参数为召唤组号（0=站总召，1..16=分组）</summary>
        public event Action<int> InterrogationRequested;

        /// <summary>最近一次总召的召唤限定词，用于组装应答帧时原样回显</summary>
        private byte _lastQualifier;

        /// <summary>
        /// 处理主站下发的总召命令。
        /// </summary>
        /// <param name="asdu">收到的总召 ASDU</param>
        /// <returns>召唤组号；0 表示站总召</returns>
        public int Handle(Asdu asdu)
        {
            if (asdu == null)
            {
                throw new ArgumentNullException(nameof(asdu));
            }
            if (asdu.TypeId != TypeIdentifier.C_IC_NA_1)
            {
                throw new ArgumentException(
                    $"InterrogationHandler 只处理 C_IC_NA_1，收到的是 {asdu.TypeId}。");
            }

            // QOI 是信息元素而非信息对象地址。
            // 部分调试工具误把 QOI 填在 IOA 位置，此处做了兼容：
            // 若解析出的信息对象是 InterrogationCommand，直接取其 QOI 字段；
            // 否则退回用 IOA 兜底，避免现场工具差异导致总召完全不可用。
            byte qualifier = 0;
            if (asdu.InformationObjects.Count > 0)
            {
                qualifier = asdu.InformationObjects[0] is InterrogationCommand interrogation
                    ? interrogation.Qoi
                    : (byte)asdu.InformationObjects[0].ObjectAddress;
            }
            _lastQualifier = qualifier;

            InterrogationRequested?.Invoke(qualifier);
            return qualifier;
        }

        /// <summary>构造总召激活确认帧（COT=7），回显 QOI</summary>
        public Asdu BuildActivationConfirmation(int commonAddress, ApplicationLayerParameters parameters)
        {
            Asdu confirmation = new Asdu(parameters)
            {
                TypeId = TypeIdentifier.C_IC_NA_1,
                Cot = CauseOfTransmission.ActivationCon,
                CommonAddress = commonAddress
            };
            confirmation.AddInformationObject(new InterrogationCommand(0, _lastQualifier));
            return confirmation;
        }

        /// <summary>构造总召终止帧（COT=10），回显 QOI</summary>
        public Asdu BuildActivationTermination(int commonAddress, ApplicationLayerParameters parameters)
        {
            Asdu termination = new Asdu(parameters)
            {
                TypeId = TypeIdentifier.C_IC_NA_1,
                Cot = CauseOfTransmission.ActivationTermination,
                CommonAddress = commonAddress
            };
            termination.AddInformationObject(new InterrogationCommand(0, _lastQualifier));
            return termination;
        }
    }

    /// <summary>
    /// 时钟同步（C_CS_NA_1 / TypeID 103）处理器。
    ///
    /// 复刻说明（对应原实现的 ClockSynchronizationHandler）：
    /// 主站下发一个 CP56Time2a 时标，从站应当据此校准本地时钟，
    /// 并在"激活确认"帧中【原样回送】同一个时标。
    ///
    /// 工程要点：
    ///   - 从站回送的确认帧必须携带主站发来的时标（原样回显），主站据此计算通道延时
    ///   - 时钟同步直接影响 SOE 事件的准确性，变电站对时精度通常要求 ≤ 1ms（依赖 IRIG-B/1588）
    ///   - 安全风险：盲目信任主站下发的时钟会被用来扰乱事件顺序，
    ///     因此这里内置了"时钟跳变幅度限制"，超过阈值直接拒绝
    ///
    /// 与原实现的一处差异值得说明：
    /// 原实现的从站时间基准取自授权服务器的 GetServerTime（联网获取），
    /// 本复刻版移除授权模块后一律使用本地时钟 <see cref="DateTime.Now"/>。
    /// </summary>
    public class ClockSynchronizationHandler
    {
        /// <summary>接收到主站时钟时触发，参数为解析出的时间（无效时标时为 null）</summary>
        public event Action<DateTime?> ClockReceived;

        /// <summary>允许的最大时钟跳变（秒）。超过则拒绝同步，防止主站误发或恶意篡改</summary>
        public int MaxClockJumpSeconds { get; set; } = 3600;

        /// <summary>最近一次收到的主站时标，用于组装确认帧时原样回显</summary>
        private Cp56Time2a _lastReceivedTime;

        /// <summary>处理时钟同步命令，返回主站下发的时间</summary>
        public DateTime? Handle(Asdu asdu)
        {
            if (asdu == null)
            {
                throw new ArgumentNullException(nameof(asdu));
            }
            if (asdu.TypeId != TypeIdentifier.C_CS_NA_1)
            {
                throw new ArgumentException(
                    $"ClockSynchronizationHandler 只处理 C_CS_NA_1，收到的是 {asdu.TypeId}。");
            }
            if (asdu.InformationObjects.Count == 0)
            {
                throw new ArgumentException("时钟同步命令缺少信息对象，无法提取时标。");
            }

            // 时标承载在 ClockSynchronizationCommand 信息元素中（CP56Time2a 7 字节）
            Cp56Time2a received = asdu.InformationObjects[0] is ClockSynchronizationCommand command
                ? command.NewTime
                : default(Cp56Time2a);
            _lastReceivedTime = received;

            DateTime? targetTime = received.ToDateTime();

            if (targetTime.HasValue)
            {
                // 时钟跳变保护：与本地时间相差过大时拒绝，避免事件顺序被打乱
                double jumpSeconds = Math.Abs((targetTime.Value - DateTime.Now).TotalSeconds);
                if (jumpSeconds > MaxClockJumpSeconds)
                {
                    throw new InvalidOperationException(
                        $"拒绝时钟同步：时间跳变 {jumpSeconds:F0} 秒，超过阈值 {MaxClockJumpSeconds} 秒。");
                }
            }

            ClockReceived?.Invoke(targetTime);
            return targetTime;
        }

        /// <summary>构造时钟同步确认帧，原样回显主站时标</summary>
        public Asdu BuildConfirmation(int commonAddress, ApplicationLayerParameters parameters)
        {
            Asdu confirmation = new Asdu(parameters)
            {
                TypeId = TypeIdentifier.C_CS_NA_1,
                Cot = CauseOfTransmission.ActivationCon,
                CommonAddress = commonAddress
            };
            confirmation.AddInformationObject(
                new ClockSynchronizationCommand(0, _lastReceivedTime));
            return confirmation;
        }
    }

    /// <summary>
    /// 计数量召唤（C_CI_NA_1 / TypeID 101）处理器，即电度召唤。
    ///
    /// 复刻说明（对应原实现的 CounterInterrogationHandler）：
    /// 流程与总召同构（激活 6 → 确认 7 → 数据 → 终止 10），
    /// 但数据上送时 COT 必须用 37..41（计数量召唤响应），不能用 20..36。
    /// 这是最容易写错的地方：用错 COT 主站会丢弃全部电度数据。
    ///
    /// 另外电度数据通常用 TypeID 15（M_IT_NA_1 累计量）上送，
    /// 且要处理计数器溢出位（CY），主站需据此做跨窗口累加修正。
    /// </summary>
    public class CounterInterrogationHandler
    {
        /// <summary>计数量召唤被激活时触发</summary>
        public event Action CounterInterrogationRequested;

        /// <summary>最近一次的计数量召唤限定词，用于应答帧回显</summary>
        private byte _lastQualifier;

        public void Handle(Asdu asdu)
        {
            if (asdu == null)
            {
                throw new ArgumentNullException(nameof(asdu));
            }
            if (asdu.TypeId != TypeIdentifier.C_CI_NA_1)
            {
                throw new ArgumentException(
                    $"CounterInterrogationHandler 只处理 C_CI_NA_1，收到的是 {asdu.TypeId}。");
            }

            byte qualifier = 0;
            if (asdu.InformationObjects.Count > 0)
            {
                qualifier = asdu.InformationObjects[0] is CounterInterrogationCommand command
                    ? command.Qualifier
                    : (byte)asdu.InformationObjects[0].ObjectAddress;
            }
            _lastQualifier = qualifier;

            CounterInterrogationRequested?.Invoke();
        }

        /// <summary>构造计数量召唤激活确认（COT=7）</summary>
        public Asdu BuildActivationConfirmation(int commonAddress, ApplicationLayerParameters parameters)
        {
            Asdu confirmation = new Asdu(parameters)
            {
                TypeId = TypeIdentifier.C_CI_NA_1,
                Cot = CauseOfTransmission.ActivationCon,
                CommonAddress = commonAddress
            };
            confirmation.AddInformationObject(new CounterInterrogationCommand(0, _lastQualifier));
            return confirmation;
        }

        /// <summary>构造计数量召唤终止（COT=10）</summary>
        public Asdu BuildActivationTermination(int commonAddress, ApplicationLayerParameters parameters)
        {
            Asdu termination = new Asdu(parameters)
            {
                TypeId = TypeIdentifier.C_CI_NA_1,
                Cot = CauseOfTransmission.ActivationTermination,
                CommonAddress = commonAddress
            };
            termination.AddInformationObject(new CounterInterrogationCommand(0, _lastQualifier));
            return termination;
        }
    }
}
