using System;
using System.Collections.Generic;

namespace IEC104.Core.CS101
{
    /// <summary>
    /// 应用层协议数据单元（ASDU）—— 104 报文中承载真正业务数据的部分。
    ///
    /// ┌──────────────────────────────────────────────────────────┐
    /// │  完整 104 帧 = APCI(6 字节) + ASDU                        │
    /// │  ASDU = TypeID + VSQ + COT(+OA) + CA + 信息对象序列        │
    /// └──────────────────────────────────────────────────────────┘
    ///
    /// 复刻说明（源程序集：lib60870.dll 中 lib60870.CS101.ASDU，反编译约 1000 行）：
    /// 原实现的字段构成为 typeId / vsq / cot / oa / isTest / isNegative / ca /
    /// payload / informationObjects，此处保留同样的字段语义，但重写为
    /// 显式编解码 + 不可变核心字段的形式，并补齐每个字段的规约依据注释。
    ///
    /// 各字段的位布局（这是最容易写错的地方，逐条注明）：
    ///   VSQ  bit7   = SQ，0=信息对象地址不连续（每个对象都带地址），1=连续（仅首个带地址）
    ///        bit0-6 = 信息对象数目 0..127
    ///   COT  bit7   = T，  1=测试帧（不计入正常业务）
    ///        bit6   = PN，1=否定确认（从站拒绝/无法执行）
    ///        bit0-5 = 传送原因值 0..63
    ///        注：当参数配置"传送原因含源发地址"时，COT 占 2 字节，第 2 字节为源发地址 OA
    ///
    /// 【易错点·已核对】传送原因是 6 位而非 5 位。
    /// 若误按 5 位（&amp; 0x1F）取值，则总召响应(20..36)、计数量响应(37..41)、
    /// 否定确认(44..47) 全部会被截断成错误值（例如 20 变成 20&amp;0x1F=20 尚可，
    /// 但 37 变成 5、44 变成 12），主站侧表现为"原因莫名不对、数据被丢弃"。
    /// 此处严格按 lib60870 原实现的 (b &amp; 0x3F) / T=0x80 / PN=0x40 实现。
    /// </summary>
    public class Asdu
    {
        private readonly ApplicationLayerParameters _parameters;
        private byte _vsq;
        private readonly List<InformationObject> _informationObjects = new List<InformationObject>();

        /// <summary>类型标识，决定信息对象的编解码方式</summary>
        public TypeIdentifier TypeId { get; set; }

        /// <summary>传送原因</summary>
        public CauseOfTransmission Cot { get; set; }

        /// <summary>源发地址（Originator Address）。仅在参数启用时有效，否则恒为 0</summary>
        public byte OriginatorAddress { get; set; }

        /// <summary>公共地址（Common Address，即站地址/RTU 地址）</summary>
        public int CommonAddress { get; set; }

        /// <summary>测试标志（COT 的 T 位）。测试帧不应对现场设备产生实际作用</summary>
        public bool IsTest { get; set; }

        /// <summary>否定确认标志（COT 的 PN 位）</summary>
        public bool IsNegative { get; set; }

        /// <summary>
        /// SQ 位：true 表示信息对象地址连续，报文中只写第一个对象的地址，
        /// 其余对象地址由首个地址依次 +1 推导。总召上送连续遥测时用它可显著省字节。
        /// </summary>
        public bool IsSequence
        {
            get => (_vsq & 0x80) != 0;
            set => _vsq = value ? (byte)(_vsq | 0x80) : (byte)(_vsq & 0x7F);
        }

        /// <summary>信息对象个数（VSQ 低 7 位，0..127）</summary>
        public int NumberOfObjects
        {
            get => _vsq & 0x7F;
            set
            {
                if (value < 0 || value > 127)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "信息对象个数需在 0..127 之间。");
                }
                _vsq = (byte)((_vsq & 0x80) | (value & 0x7F));
            }
        }

        /// <summary>本 ASDU 携带的信息对象集合</summary>
        public IReadOnlyList<InformationObject> InformationObjects => _informationObjects;

        public Asdu(ApplicationLayerParameters parameters)
        {
            _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        }

        /// <summary>
        /// 添加一个信息对象，并同步维护 VSQ 的数量位与 SQ 位。
        /// 把数量维护收敛到这一个入口，避免调用方手改 VSQ 导致数量与实际对象数不一致
        /// （不一致时主站解析会直接错位，是联调期很常见的隐蔽故障）。
        /// </summary>
        public void AddInformationObject(InformationObject informationObject)
        {
            if (informationObject == null)
            {
                throw new ArgumentNullException(nameof(informationObject));
            }

            // 连续地址模式要求地址严格递增 1，否则自动退回不连续模式
            if (_informationObjects.Count > 0 && IsSequence)
            {
                int previousAddress = _informationObjects[_informationObjects.Count - 1].ObjectAddress;
                if (informationObject.ObjectAddress != previousAddress + 1)
                {
                    IsSequence = false;
                }
            }

            _informationObjects.Add(informationObject);
            NumberOfObjects = _informationObjects.Count;
        }

        /// <summary>
        /// 从报文字节流解析 ASDU。
        /// </summary>
        /// <param name="buffer">完整报文缓冲区</param>
        /// <param name="startIndex">ASDU 起始位置（即 APCI 之后的第一个字节）</param>
        /// <param name="parameters">应用层参数（决定地址/原因字段宽度）</param>
        /// <param name="bytesConsumed">输出：本 ASDU 实际占用的字节数</param>
        public static Asdu Decode(
            byte[] buffer,
            int startIndex,
            ApplicationLayerParameters parameters,
            out int bytesConsumed)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            int cursor = startIndex;
            Asdu asdu = new Asdu(parameters)
            {
                TypeId = (TypeIdentifier)buffer[cursor++],
                // VSQ 直接读取，序列位与数量位由属性解析
                _vsq = buffer[cursor++]
            };

            // ── 传送原因 ────────────────────────────────────────────
            byte cotLow = buffer[cursor++];
            asdu.IsTest = (cotLow & 0x80) != 0;
            asdu.IsNegative = (cotLow & 0x40) != 0;

            // 低 6 位为传送原因（0..63），与 lib60870 原实现一致
            int cotValue = cotLow & 0x3F;
            if (parameters.SizeOfCauseOfTransmission == 2)
            {
                // 第二字节的低 6 位是源发地址
                asdu.OriginatorAddress = (byte)(buffer[cursor++] & 0x3F);
            }
            asdu.Cot = (CauseOfTransmission)cotValue;

            // ── 公共地址 ────────────────────────────────────────────
            asdu.CommonAddress = parameters.SizeOfCommonAddress == 2
                ? buffer[cursor] | (buffer[cursor + 1] << 8)
                : buffer[cursor];
            cursor += parameters.SizeOfCommonAddress;

            // ── 信息对象序列 ────────────────────────────────────────
            int objectCount = asdu.NumberOfObjects;
            bool isSequence = asdu.IsSequence;
            int firstAddress = 0;

            for (int objectIndex = 0; objectIndex < objectCount; objectIndex++)
            {
                if (objectIndex == 0 || !isSequence)
                {
                    // 不连续模式：每个对象都带自己的地址；连续模式：仅首个对象带地址
                    firstAddress = ReadInformationObjectAddress(buffer, cursor, parameters);
                    cursor += parameters.SizeOfInformationObjectAddress;
                }
                else
                {
                    firstAddress++;
                }

                InformationObject informationObject = InformationObjectFactory.Create(
                    asdu.TypeId, firstAddress, buffer, cursor, parameters, out int elementLength);

                cursor += elementLength;
                asdu._informationObjects.Add(informationObject);
            }

            bytesConsumed = cursor - startIndex;
            return asdu;
        }

        /// <summary>编码为字节序列（不含 APCI，APCI 由 <see cref="CS104.Apci"/> 负责）</summary>
        public byte[] Encode()
        {
            // 先做宽度检查：宁可在这里抛出一条读得懂的错，也不要把超范围的地址
            // 截断成低字节发出去。截断之后的报文"格式完全合法"，主站只会
            // 按错误的站地址/点号去查，查不到就静默丢弃 —— 现场表现为
            // "链路正常、总召有确认、就是没有数据"，几乎无法从报文上看出问题。
            EnsureAddressesFitWidths();

            List<byte> output = new List<byte>();

            output.Add((byte)TypeId);
            output.Add(_vsq);

            // COT：低 6 位为原因值，高位拼装 T 位与 PN 位
            byte cotByte = (byte)((byte)Cot & 0x3F);
            if (IsTest)
            {
                cotByte |= 0x80;
            }
            if (IsNegative)
            {
                cotByte |= 0x40;
            }
            output.Add(cotByte);

            if (_parameters.SizeOfCauseOfTransmission == 2)
            {
                output.Add((byte)(OriginatorAddress & 0x3F));
            }

            // 公共地址，低字节在前
            output.Add((byte)(CommonAddress & 0xFF));
            if (_parameters.SizeOfCommonAddress == 2)
            {
                output.Add((byte)((CommonAddress >> 8) & 0xFF));
            }

            // 信息对象
            for (int objectIndex = 0; objectIndex < _informationObjects.Count; objectIndex++)
            {
                InformationObject informationObject = _informationObjects[objectIndex];

                // 序列模式下只有首个对象需要写地址，其余由对端按顺序推导
                bool writeAddress = objectIndex == 0 || !IsSequence;
                if (writeAddress)
                {
                    WriteInformationObjectAddress(output, informationObject.ObjectAddress);
                }
                informationObject.Encode(output, _parameters);
            }

            return output.ToArray();
        }

        /// <summary>
        /// 发送前预检：地址能否装进配置的字段宽度。
        ///
        /// 编码时也会查一遍，那为什么还要单独暴露出来？
        /// 因为广播路径上"编码抛异常"会被当成"这条连接坏了"而直接关掉 socket ——
        /// 一个填错的点号会连带把好好的链路踢掉。调用方先用这个方法探一下，
        /// 就能把这种配置错误收敛成一条日志，而不是一次断链。
        /// </summary>
        public void ValidateAddresses() => EnsureAddressesFitWidths();

        /// <summary>
        /// 校验本 ASDU 里的公共地址、信息对象地址能否装进配置的字段宽度。
        ///
        /// 传送原因一侧也要看：宽度为 1 字节时，源发地址（OA）在报文里根本没有位置，
        /// 若调用方真的设了一个非 0 的源发地址，只能说明配置和意图不符，直接报错更安全。
        /// </summary>
        private void EnsureAddressesFitWidths()
        {
            int maxCommonAddress = _parameters.MaxCommonAddress;
            if (CommonAddress < 0 || CommonAddress > maxCommonAddress)
            {
                throw new InvalidOperationException(
                    $"公共地址 {CommonAddress} 超出公共地址长度 {_parameters.SizeOfCommonAddress} 字节的可表示范围"
                    + $" 0..{maxCommonAddress}。请把从站公共地址改小，或把通道的公共地址长度调回 2 字节。");
            }

            if (_parameters.SizeOfCauseOfTransmission == 1 && OriginatorAddress != 0)
            {
                throw new InvalidOperationException(
                    $"传送原因长度为 1 字节时没有源发地址字段，但本帧的源发地址为 {OriginatorAddress}。"
                    + "请把源发地址清零，或把通道的传送原因长度改为 2 字节。");
            }

            int maxObjectAddress = _parameters.MaxInformationObjectAddress;
            for (int index = 0; index < _informationObjects.Count; index++)
            {
                int address = _informationObjects[index].ObjectAddress;
                if (address < 0 || address > maxObjectAddress)
                {
                    throw new InvalidOperationException(
                        $"点号 {address} 超出信息体地址长度 {_parameters.SizeOfInformationObjectAddress} 字节的"
                        + $"可表示范围 0..{maxObjectAddress}。请改小该点号，或把通道的信息体地址长度调回 3 字节。");
                }
            }
        }

        private static int ReadInformationObjectAddress(
            byte[] buffer, int offset, ApplicationLayerParameters parameters)
        {
            int size = parameters.SizeOfInformationObjectAddress;
            int address = buffer[offset];
            if (size >= 2)
            {
                address |= buffer[offset + 1] << 8;
            }
            if (size == 3)
            {
                address |= buffer[offset + 2] << 16;
            }
            return address;
        }

        private void WriteInformationObjectAddress(List<byte> output, int address)
        {
            int size = _parameters.SizeOfInformationObjectAddress;
            output.Add((byte)(address & 0xFF));
            if (size >= 2)
            {
                output.Add((byte)((address >> 8) & 0xFF));
            }
            if (size == 3)
            {
                output.Add((byte)((address >> 16) & 0xFF));
            }
        }

        public override string ToString()
        {
            return $"ASDU TypeId={TypeId} COT={Cot} CA={CommonAddress} " +
                   $"对象数={NumberOfObjects} 序列={IsSequence} 测试={IsTest} 否定={IsNegative}";
        }
    }
}
