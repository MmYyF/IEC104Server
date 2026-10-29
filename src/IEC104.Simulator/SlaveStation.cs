using System;
using System.Collections.Generic;
using System.Linq;
using IEC104.Core.CS101;
using IEC104.Core.Time;
using IEC104.Simulator.Handlers;
using IEC104.Simulator.IoModels;

namespace IEC104.Simulator
{
    /// <summary>
    /// 从站（Slave Station）—— 模拟一台现场 RTU/测控装置。
    ///
    /// 复刻说明（对应原实现 IEC104ServerSimulator.Windows.Slave 与 ViewModels 下的从站逻辑）：
    /// 职责是"持有数据点 + 响应主站命令 + 自发上送变化"，
    /// 这是模拟器最核心的业务对象。
    ///
    /// ════════════════════════════════════════════════════════════
    ///  与原实现的最大差异：本复刻版【不含任何授权/激活校验】
    ///  原程序集依赖 Parse.dll 的 ActivateUtil 做在线激活与 RSA 验签，
    ///  且时间基准取自服务端 GetServerTime。
    ///  复刻版完全移除该依赖：不联网、不校验许可、不限制试用期，
    ///  时间一律使用本地时钟。
    /// ════════════════════════════════════════════════════════════
    ///
    /// 其余行为（总召三段式、时钟同步、计数量召唤、自发上送）均按
    /// IEC 60870-5-104 标准实现，与原实现的对外行为一致。
    /// </summary>
    public class SlaveStation
    {
        private readonly Dictionary<int, DataPoint> _points = new Dictionary<int, DataPoint>();

        /// <summary>按点号排序后的缓存（增删点时置空，下次访问重建）</summary>
        private List<DataPoint> _sortedCache;
        private ApplicationLayerParameters _parameters;

        /// <summary>
        /// 应用层参数（传送原因 / 公共地址 / 信息体地址的字节宽度）。
        ///
        /// 可写：从站在被挂到某条通道之前并不知道那条通道用的是哪种宽度，
        /// 所以 <c>ConnectionViewModel.HookSlave</c> 会把通道自己的那份参数实例直接塞进来。
        /// 这里刻意保存"同一个实例"而不是复制值——通道改宽度时从站立刻跟着改，
        /// 不会出现"通道发 2 字节传送原因、从站按 1 字节拼"的错位。
        ///
        /// 注意：每构造一个 ASDU 都会读一次这里，所以赋值必须发生在构造 ASDU 之前。
        /// </summary>
        public ApplicationLayerParameters Parameters
        {
            get => _parameters;
            set => _parameters = value ?? ApplicationLayerParameters.CreateForCs104();
        }

        /// <summary>公共地址（站地址）。主站按此区分不同从站</summary>
        public int CommonAddress { get; set; } = 1;

        /// <summary>站名称，仅用于显示</summary>
        public string Name { get; set; } = "模拟从站";

        /// <summary>总召处理器</summary>
        public InterrogationHandler Interrogation { get; } = new InterrogationHandler();

        /// <summary>时钟同步处理器</summary>
        public ClockSynchronizationHandler ClockSync { get; } = new ClockSynchronizationHandler();

        /// <summary>计数量召唤处理器</summary>
        public CounterInterrogationHandler CounterInterrogation { get; } = new CounterInterrogationHandler();

        /// <summary>需要上送给主站的 ASDU 队列。网络层取出后按 I 帧发出</summary>
        public event Action<Asdu> AsduReadyToSend;

        /// <summary>收到遥控/设定命令时触发，参数为点号与命令值</summary>
        public event Action<int, double> ControlCommandReceived;

        public SlaveStation(ApplicationLayerParameters parameters = null)
        {
            _parameters = parameters ?? ApplicationLayerParameters.CreateForCs104();

            // 数据点变化时自动自发上送（COT=3）
            // 这是 104 里"突发上送"的标准行为：变位即上送，不等总召
            Interrogation.InterrogationRequested += OnInterrogationRequested;
            CounterInterrogation.CounterInterrogationRequested += OnCounterInterrogationRequested;
        }

        /// <summary>注册一个数据点</summary>
        public void AddPoint(DataPoint point)
        {
            if (point == null)
            {
                throw new ArgumentNullException(nameof(point));
            }

            // 1.9.7：原先这里逐点 point.ValueChanged += OnPointValueChanged，
            // 一百万点就是一百万个委托对象（约 32 MB）。改成回填反向引用，
            // 由 DataPoint.SetValue 直接调回本类，见 DataPoint.Owner 的注释。
            point.Owner = this;
            _points[point.ObjectAddress] = point;
            _sortedCache = null;
        }

        /// <summary>
        /// 预留点位字典容量（批量铺点前调用）。
        ///
        /// 1.9.7：字典按 2 倍扩容，从 0 铺到 100 万点要扩容约 20 次，
        /// 每次都要"新建桶数组 + 重新哈希全部条目"，最后几次扩容要注意
        /// 瞬时同时握着新旧两套桶（几十 MB）。铺点前一次性预留，这步全省。
        /// </summary>
        public void EnsurePointCapacity(int capacity)
        {
            if (capacity > _points.Count)
            {
                _points.EnsureCapacity(capacity);
            }
        }

        /// <summary>按地址取数据点</summary>
        public DataPoint GetPoint(int objectAddress)
        {
            return _points.TryGetValue(objectAddress, out DataPoint point) ? point : null;
        }

        /// <summary>
        /// 移除一个数据点，并断开与从站的关联。
        /// 界面层删除点位时需要它——漏断开会让已删除的点继续触发自发上送。
        /// </summary>
        public bool RemovePoint(int objectAddress)
        {
            if (!_points.TryGetValue(objectAddress, out DataPoint point))
            {
                return false;
            }

            point.Owner = null;
            _sortedCache = null;
            return _points.Remove(objectAddress);
        }

        /// <summary>清空全部数据点</summary>
        public void ClearPoints()
        {
            foreach (DataPoint point in _points.Values)
            {
                point.Owner = null;
            }
            _points.Clear();
            _sortedCache = null;
        }

        /// <summary>
        /// 全部数据点（按地址排序）。
        ///
        /// 性能（第十一轮改）：原来是每次访问都 OrderBy(...).ToList()，
        /// 而总召组帧、分组召唤都会访问它——一台 2000 点的从站每次总召多花一次
        /// 全量排序 + 一次 2000 元素的数组分配。改为缓存，增删点时失效。
        /// 字典本身不保证顺序，所以不能直接返回 Values（总召要求按点号升序上送）。
        /// </summary>
        public IReadOnlyList<DataPoint> Points
        {
            get
            {
                _sortedCache ??= _points.Values.OrderBy(p => p.ObjectAddress).ToList();
                return _sortedCache;
            }
        }

        /// <summary>
        /// 处理主站下发的 ASDU，内部完成命令分派并触发应答。
        /// 这是从站的"收包入口"。
        /// </summary>
        public void HandleReceivedAsdu(Asdu asdu)
        {
            if (asdu == null)
            {
                throw new ArgumentNullException(nameof(asdu));
            }

            switch (asdu.TypeId)
            {
                case TypeIdentifier.C_IC_NA_1:
                    // 总召：先回激活确认，再上送数据，最后回激活终止
                    Interrogation.Handle(asdu);
                    break;

                case TypeIdentifier.C_CI_NA_1:
                    // 计数量召唤：流程与总召同构
                    CounterInterrogation.Handle(asdu);
                    break;

                case TypeIdentifier.C_CS_NA_1:
                    // 时钟同步：解析主站时标并原样回显（处理器内部已完成跳变保护校验）
                    ClockSync.Handle(asdu);
                    Emit(ClockSync.BuildConfirmation(CommonAddress, _parameters));
                    break;

                case TypeIdentifier.C_SC_NA_1:
                case TypeIdentifier.C_DC_NA_1:
                case TypeIdentifier.C_SE_NA_1:
                case TypeIdentifier.C_SE_NC_1:
                    // 单点/双点遥控与设定值命令：落到具体点上并回确认
                    HandleControlCommand(asdu);
                    break;

                case TypeIdentifier.C_TS_NA_1:
                    // 测试命令：原样回送固定测试位组
                    Emit(BuildTestConfirmation(asdu));
                    break;

                case TypeIdentifier.C_RD_NA_1:
                    // 读数据命令：回送被读点的当前值
                    HandleReadCommand(asdu);
                    break;

                case TypeIdentifier.C_RP_NA_1:
                    // 复位进程：回激活确认即可（模拟器不保存事件缓冲区，无需实际复位）
                    Emit(BuildSimpleConfirmation(asdu));
                    break;

                default:
                    // 无法识别的类型：按规约应回"未知类型标识"的否定确认，
                    // 而不是静默丢弃，否则主站会一直等待直到超时
                    Emit(BuildNegativeConfirmation(asdu, CauseOfTransmission.UnknownTypeId));
                    break;
            }
        }

        /// <summary>
        /// 处理读数据命令（C_RD_NA_1 / TypeID 102）。
        /// 从站需把被请求点的当前值作为"响应请求"（COT=5）回送，
        /// 而不是回"激活确认"——这是读命令与遥控命令应答语义的关键区别。
        /// </summary>
        private void HandleReadCommand(Asdu asdu)
        {
            foreach (InformationObject commandObject in asdu.InformationObjects)
            {
                DataPoint point = GetPoint(commandObject.ObjectAddress);
                if (point == null)
                {
                    // 点号不存在：回"未知信息对象地址"的否定确认，而不是静默不答
                    Emit(BuildNegativeConfirmation(asdu, CauseOfTransmission.UnknownInformationObjectAddress));
                    continue;
                }

                Asdu response = new Asdu(_parameters)
                {
                    TypeId = point.Type,
                    Cot = CauseOfTransmission.Request,
                    CommonAddress = CommonAddress
                };
                response.AddInformationObject(BuildInformationObject(point, point.Type));
                Emit(response);
            }
        }

        /// <summary>构造一个与原命令同类型、COT=7 的确认帧（用于无需改状态的命令）</summary>
        private Asdu BuildSimpleConfirmation(Asdu received)
        {
            Asdu confirmation = new Asdu(_parameters)
            {
                TypeId = received.TypeId,
                Cot = CauseOfTransmission.ActivationCon,
                CommonAddress = CommonAddress
            };
            foreach (InformationObject commandObject in received.InformationObjects)
            {
                confirmation.AddInformationObject(commandObject);
            }
            return confirmation;
        }

        /// <summary>
        /// 处理遥控/设点命令（C_SC / C_DC / C_SE_NA / C_SE_NC）。
        ///
        /// 规约要点：遥控分"选择—执行"两步。
        ///   选择阶段（S/E=0）：从站只做合法性校验并回确认，不出口
        ///   执行阶段（S/E=1）：从站才真正改变数据点状态
        /// 复刻版在此模拟出口行为——把命令值写进数据点，进而触发自发上送。
        /// 真实装置在此处应驱动继电器/开出板，并做返校（双点位置回读）。
        /// </summary>
        private void HandleControlCommand(Asdu asdu)
        {
            Asdu confirmation = new Asdu(_parameters)
            {
                TypeId = asdu.TypeId,
                Cot = CauseOfTransmission.ActivationCon,
                CommonAddress = CommonAddress
            };

            foreach (InformationObject commandObject in asdu.InformationObjects)
            {
                bool isSelectStage;
                double commandValue;

                switch (commandObject)
                {
                    case SingleCommand singleCommand:
                        isSelectStage = singleCommand.IsSelect;
                        commandValue = singleCommand.Value ? 1 : 0;
                        break;

                    case DoubleCommand doubleCommand:
                        isSelectStage = doubleCommand.IsSelect;
                        // 双点的"不确定/不允许"不应当作有效出口值，此处按 0 处理
                        commandValue = doubleCommand.Value == DoubleCommandValue.On ? 1 : 0;
                        break;

                    case SetpointCommandNormalized normalizedSetpoint:
                        isSelectStage = normalizedSetpoint.Qualifier.IsSelect;
                        commandValue = normalizedSetpoint.RawValue / 32768.0;
                        break;

                    case SetpointCommandShort shortSetpoint:
                        isSelectStage = shortSetpoint.Qualifier.IsSelect;
                        commandValue = shortSetpoint.Value;
                        break;

                    default:
                        // 类型与 TypeID 不匹配说明对端组帧有误，回否定确认让主站知道失败
                        Emit(BuildNegativeConfirmation(asdu, CauseOfTransmission.UnknownInformationObjectAddress));
                        return;
                }

                // 选择阶段不下发到数据点，仅回确认
                if (!isSelectStage)
                {
                    DataPoint point = GetPoint(commandObject.ObjectAddress);
                    if (point != null)
                    {
                        point.SetValue(commandValue);
                    }
                }

                ControlCommandReceived?.Invoke(commandObject.ObjectAddress, commandValue);

                // 应答帧中原样回显命令对象（含 S/E 位），主站据此确认是哪一步被确认了
                confirmation.AddInformationObject(commandObject);
            }

            Emit(confirmation);
        }

        private void OnInterrogationRequested(int qualifier)
        {
            // 【易错点·已修复】QOI 是"召唤限定词"，不是组号。
            //   20      = 站总召（全数据）—— 主站最常用的值
            //   21..36  = 第 1..16 组
            // 早先这里把 QOI 直接当组号往下传，于是标准值 20 去筛"点号千位=20"的点，
            // 一个都筛不到：主站发站总召只收到"激活确认 + 激活终止"，中间一条数据都没有。
            // 症状是"链路上明明有报文，点表却全是空的"，而且从报文上完全看不出错。
            int group = qualifier switch
            {
                0 => 0,                              // 兼容旧约定：QOI=0 也当全数据
                20 => 0,                             // 站总召：全数据（标准值）
                > 20 and <= 36 => qualifier - 20,    // 第 1..16 组
                _ => qualifier                       // 其余按旧的"点号千位=组号"约定
            };

            // 三段式第 1 步：激活确认
            Emit(Interrogation.BuildActivationConfirmation(CommonAddress, _parameters));

            // 三段式第 2 步：上送全部数据（按组分批，避免超出 APDU 255 字节）
            foreach (Asdu dataAsdu in BuildInterrogationData(group))
            {
                Emit(dataAsdu);
            }

            // 三段式第 3 步：激活终止。缺了这步主站会一直等
            Emit(Interrogation.BuildActivationTermination(CommonAddress, _parameters));
        }

        private void OnCounterInterrogationRequested()
        {
            Emit(CounterInterrogation.BuildActivationConfirmation(CommonAddress, _parameters));

            foreach (Asdu dataAsdu in BuildCounterData())
            {
                Emit(dataAsdu);
            }

            Emit(CounterInterrogation.BuildActivationTermination(CommonAddress, _parameters));
        }

        /// <summary>单帧 ASDU 允许承载的最大信息对象数</summary>
        public int MaxObjectsPerAsdu { get; set; } = 20;

        /// <summary>
        /// 生成总召数据帧。
        ///
        /// 两个硬约束决定了这里的分批策略：
        ///   1) 一个 APDU 最多 255 字节，扣掉 APCI 6 字节后 ASDU 只剩 249
        ///   2) 一帧 ASDU 只能承载【一种】TypeID（ASDU 结构里 TypeID 只有一个）
        ///
        /// 【联调发现的缺陷·已修复】
        /// 早期版本按固定步长切批、再取"批次内首个点的类型"作为整帧类型，
        /// 结果是混合类型的批次会被截断——只有与首个点同类型的点被上送，
        /// 其余点位在总召中静默丢失（表现为"主站点表不全、部分遥测一直没值"）。
        /// 正确做法是【先按类型分组，再在组内分批】。
        /// </summary>
        /// <param name="group">召唤组号；0 为站总召</param>
        public List<Asdu> BuildInterrogationData(int group)
        {
            List<Asdu> result = new List<Asdu>();

            // 分组召唤的划分规则：点号千位与组号相同的点属于该组
            // （这是本模拟器约定的演示规则，实际工程应按点表配置来划分）
            IEnumerable<DataPoint> targets = group == 0
                ? Points
                : Points.Where(p => p.ObjectAddress / 1000 == group);

            foreach (IGrouping<TypeIdentifier, DataPoint> sameType in targets.GroupBy(p => p.Type))
            {
                List<DataPoint> batch = new List<DataPoint>();
                foreach (DataPoint point in sameType)
                {
                    batch.Add(point);
                    if (batch.Count >= MaxObjectsPerAsdu)
                    {
                        result.Add(BuildSingleTypeAsdu(
                            batch, sameType.Key, CauseOfTransmission.InterrogatedByStation));
                        batch = new List<DataPoint>();
                    }
                }
                if (batch.Count > 0)
                {
                    result.Add(BuildSingleTypeAsdu(
                        batch, sameType.Key, CauseOfTransmission.InterrogatedByStation));
                }
            }

            return result;
        }

        /// <summary>生成计数量（电度）上送帧，COT 必须用 37 而不是 20</summary>
        public List<Asdu> BuildCounterData()
        {
            List<DataPoint> counters = Points
                .Where(p => p.Type == TypeIdentifier.M_IT_NA_1)
                .ToList();

            List<Asdu> result = new List<Asdu>();
            if (counters.Count == 0)
            {
                return result;
            }

            // 与总召同样受 VSQ 数量位（0..127）与 APDU 249 字节的双重限制，需分批
            for (int start = 0; start < counters.Count; start += MaxObjectsPerAsdu)
            {
                List<DataPoint> batch = counters.Skip(start).Take(MaxObjectsPerAsdu).ToList();

                Asdu asdu = new Asdu(_parameters)
                {
                    TypeId = TypeIdentifier.M_IT_NA_1,
                    Cot = CauseOfTransmission.RequestedByGeneralCounter,
                    CommonAddress = CommonAddress
                };

                foreach (DataPoint counter in batch)
                {
                    asdu.AddInformationObject(new IntegratedTotals(counter.ObjectAddress)
                    {
                        CounterReading = (int)counter.Value,
                        SequenceNotation = 0
                    });
                }

                result.Add(asdu);
            }

            return result;
        }

        /// <summary>
        /// 把一批【同类型】的数据点组装成单个 ASDU。
        /// 调用方必须保证 points 内类型一致，否则编码出的报文主站无法解析。
        /// </summary>
        private Asdu BuildSingleTypeAsdu(
            List<DataPoint> points, TypeIdentifier frameType, CauseOfTransmission cot)
        {
            Asdu asdu = new Asdu(_parameters)
            {
                TypeId = frameType,
                Cot = cot,
                CommonAddress = CommonAddress
            };

            foreach (DataPoint point in points)
            {
                asdu.AddInformationObject(BuildInformationObject(point, frameType));
            }

            return asdu;
        }

        /// <summary>按类型把数据点编码为对应的信息对象</summary>
        private InformationObject BuildInformationObject(DataPoint point, TypeIdentifier type)
        {
            switch (type)
            {
                case TypeIdentifier.M_SP_NA_1:
                    return new SinglePointInformation(point.ObjectAddress, point.Value != 0);

                case TypeIdentifier.M_SP_TB_1:
                    return new SinglePointWithCp56Time2a(point.ObjectAddress)
                    {
                        Quality = point.BuildSinglePointQuality(),
                        Timestamp = Cp56Time2a.FromDateTime(point.LastChangedAt)
                    };

                case TypeIdentifier.M_DP_NA_1:
                    return new DoublePointInformation(
                        point.ObjectAddress,
                        point.Value != 0 ? DoublePointValue.On : DoublePointValue.Off);

                case TypeIdentifier.M_ME_NA_1:
                    return new MeasuredValueNormalized(point.ObjectAddress)
                    {
                        RawValue = (short)(point.Value * 32768),
                        Quality = point.BuildQuality()
                    };

                case TypeIdentifier.M_ME_NC_1:
                    return new MeasuredValueShort(point.ObjectAddress)
                    {
                        Value = (float)point.Value,
                        Quality = point.BuildQuality()
                    };

                case TypeIdentifier.M_IT_NA_1:
                    return new IntegratedTotals(point.ObjectAddress)
                    {
                        CounterReading = (int)point.Value
                    };

                case TypeIdentifier.M_ST_NA_1:
                    return new StepPositionInformation(point.ObjectAddress)
                    {
                        Value = (sbyte)point.Value,
                        Quality = point.BuildQuality()
                    };

                default:
                    // 强类型实现里没有的类型：查运行期注册表（内置补录 + 用户自定义）。
                    // 这条分支以前是"直接按单点发"——TypeID 写的是 M_ME_NB_1，
                    // 载荷却是 1 字节 SIQ，主站按 3 字节解析必然错位。
                    // 现在按登记的元素格式编码，帧长和类型对得上了。
                    if (InformationObjectRegistry.TryGet(type, out ElementFormat format, out TimeTagKind timeTag))
                    {
                        return new GenericInformationObject(point.ObjectAddress, format, timeTag)
                        {
                            Value = point.Value,
                            Timestamp = point.LastChangedAt,
                            Quality = point.BuildQuality()
                        };
                    }

                    // 表里也没有：退回单点，保证单个点配置错误不会让整个从站崩掉
                    return new SinglePointInformation(point.ObjectAddress, point.Value != 0);
            }
        }

        /// <summary>
        /// 点位值变化回调（原先是 DataPoint.ValueChanged 的订阅目标，1.9.7 起由
        /// <see cref="DataPoint.SetValue"/> 直接调用）。必须保持 internal 以上可见性，
        /// 因为它在 Simulator 程序集内部被跨类调用。
        /// </summary>
        internal void OnPointValueChanged(DataPoint point, double newValue)
        {
            // 自发上送：变位即发，COT=3
            Asdu asdu = new Asdu(_parameters)
            {
                TypeId = point.Type,
                Cot = CauseOfTransmission.Spontaneous,
                CommonAddress = CommonAddress
            };
            asdu.AddInformationObject(BuildInformationObject(point, point.Type));
            Emit(asdu);
        }

        /// <summary>
        /// 构造测试命令应答。
        /// 规约要求回送的固定测试位组必须与主站下发的一致（标准为 0x55AA），
        /// 主站比对位组来判断链路是否存在数据错乱。
        /// </summary>
        private Asdu BuildTestConfirmation(Asdu received)
        {
            ushort pattern = TestCommand.ExpectedBitPattern;
            if (received.InformationObjects.Count > 0
                && received.InformationObjects[0] is TestCommand testCommand)
            {
                pattern = testCommand.FixedBitPattern;
            }

            Asdu confirmation = new Asdu(_parameters)
            {
                TypeId = TypeIdentifier.C_TS_NA_1,
                Cot = CauseOfTransmission.ActivationCon,
                CommonAddress = CommonAddress
            };
            confirmation.AddInformationObject(new TestCommand(0, pattern));
            return confirmation;
        }

        private Asdu BuildNegativeConfirmation(Asdu original, CauseOfTransmission reason)
        {
            Asdu negative = new Asdu(_parameters)
            {
                TypeId = original.TypeId,
                Cot = reason,
                CommonAddress = CommonAddress,
                IsNegative = true
            };
            return negative;
        }

        private void Emit(Asdu asdu)
        {
            AsduReadyToSend?.Invoke(asdu);
        }

        /// <summary>
        /// 解除全部"待发送"订阅。
        /// 界面层在关闭连接/删除从站时调用，避免已关闭的连接继续收到事件。
        /// （事件只能在其声明类内部赋值清空，故以方法形式暴露）
        /// </summary>
        public void ClearSendSubscribers()
        {
            AsduReadyToSend = null;
        }
    }
}
