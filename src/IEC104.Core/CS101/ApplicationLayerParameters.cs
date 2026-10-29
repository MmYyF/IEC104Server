namespace IEC104.Core.CS101
{
    /// <summary>
    /// 应用层参数 —— 决定 ASDU 中各地址与原因字段的字节宽度。
    ///
    /// 复刻说明（源程序集：lib60870.dll 中 lib60870.CS101.ApplicationLayerParameters）：
    /// 101 与 104 的字段宽度不同，且 101 本身还允许主站/从站协商，
    /// 所以编解码必须依赖这套参数，写死宽度是常见的兼容性翻车点。
    ///
    /// 典型取值：
    ///   104：传送原因 2 字节（含源发地址）、公共地址 2 字节、信息对象地址 3 字节
    ///   101：传送原因 1 字节、公共地址 1 字节、信息对象地址 2 字节（可按工程调整）
    /// </summary>
    public class ApplicationLayerParameters
    {
        /// <summary>传送原因长度的合法下限</summary>
        public const int MinSizeOfCauseOfTransmission = 1;

        /// <summary>传送原因长度的合法上限</summary>
        public const int MaxSizeOfCauseOfTransmission = 2;

        /// <summary>公共地址长度的合法下限</summary>
        public const int MinSizeOfCommonAddress = 1;

        /// <summary>公共地址长度的合法上限</summary>
        public const int MaxSizeOfCommonAddress = 2;

        /// <summary>信息对象地址长度的合法下限</summary>
        public const int MinSizeOfInformationObjectAddress = 1;

        /// <summary>信息对象地址长度的合法上限</summary>
        public const int MaxSizeOfInformationObjectAddress = 3;

        /// <summary>传送原因字段字节数，1 或 2。为 2 时其后紧跟 1 字节源发地址</summary>
        public int SizeOfCauseOfTransmission { get; set; }

        /// <summary>公共地址字节数，1 或 2</summary>
        public int SizeOfCommonAddress { get; set; }

        /// <summary>信息对象地址字节数，1..3</summary>
        public int SizeOfInformationObjectAddress { get; set; }

        /// <summary>
        /// 当前公共地址宽度能表示的最大值。
        ///
        /// 【为什么必须有这个属性】
        /// 1 字节的公共地址只能装 0..255。以前宽度写死 2 字节时，公共地址的取值范围
        /// 和字段宽度天然匹配；一旦允许把宽度改成 1 字节，"公共地址填了 1000 的从站"
        /// 就会被编码成 0xE8 发出去，主站按 0xE8 找不到站 —— 表现为"连上了但收不到数据"，
        /// 现场极难定位。所以编码前必须拿这个上限做一次拦截。
        /// </summary>
        public int MaxCommonAddress => SizeOfCommonAddress >= 2 ? 0xFFFF : 0xFF;

        /// <summary>当前信息对象地址宽度能表示的最大值（1 字节 255 / 2 字节 65535 / 3 字节 16777215）</summary>
        public int MaxInformationObjectAddress => SizeOfInformationObjectAddress switch
        {
            1 => 0xFF,
            2 => 0xFFFF,
            _ => 0xFFFFFF
        };

        /// <summary>三处长度是否都在合法范围内，非法时返回可直接展示给用户的说明</summary>
        public string Validate()
        {
            if (SizeOfCauseOfTransmission < MinSizeOfCauseOfTransmission
                || SizeOfCauseOfTransmission > MaxSizeOfCauseOfTransmission)
            {
                return $"传送原因长度需在 {MinSizeOfCauseOfTransmission}..{MaxSizeOfCauseOfTransmission} 字节之间"
                    + $"（当前 {SizeOfCauseOfTransmission}）。";
            }
            if (SizeOfCommonAddress < MinSizeOfCommonAddress
                || SizeOfCommonAddress > MaxSizeOfCommonAddress)
            {
                return $"公共地址长度需在 {MinSizeOfCommonAddress}..{MaxSizeOfCommonAddress} 字节之间"
                    + $"（当前 {SizeOfCommonAddress}）。";
            }
            if (SizeOfInformationObjectAddress < MinSizeOfInformationObjectAddress
                || SizeOfInformationObjectAddress > MaxSizeOfInformationObjectAddress)
            {
                return $"信息体地址长度需在 {MinSizeOfInformationObjectAddress}.."
                    + $"{MaxSizeOfInformationObjectAddress} 字节之间（当前 {SizeOfInformationObjectAddress}）。";
            }
            return null;
        }

        /// <summary>
        /// 把另一份参数的三个宽度抄过来（原地修改）。
        ///
        /// 【为什么要"原地修改"而不是换对象】
        /// 从站的 SlaveStation 和每一条连接的 Iec104Connection 都持有同一个实例的引用，
        /// 换对象会出现"通道用新宽度、从站还用旧宽度"的错位——编出来的报文主站解析直接错位。
        /// 原地改，所有持有者一起生效。
        /// </summary>
        public void CopyFrom(ApplicationLayerParameters other)
        {
            if (other == null || ReferenceEquals(other, this))
            {
                return;
            }
            SizeOfCauseOfTransmission = other.SizeOfCauseOfTransmission;
            SizeOfCommonAddress = other.SizeOfCommonAddress;
            SizeOfInformationObjectAddress = other.SizeOfInformationObjectAddress;
        }

        /// <summary>复制一份（用于临时校验，不影响在用的实例）</summary>
        public ApplicationLayerParameters Clone()
        {
            return new ApplicationLayerParameters
            {
                SizeOfCauseOfTransmission = SizeOfCauseOfTransmission,
                SizeOfCommonAddress = SizeOfCommonAddress,
                SizeOfInformationObjectAddress = SizeOfInformationObjectAddress
            };
        }

        /// <summary>三个宽度是否完全一致</summary>
        public bool HasSameWidths(ApplicationLayerParameters other)
        {
            return other != null
                && other.SizeOfCauseOfTransmission == SizeOfCauseOfTransmission
                && other.SizeOfCommonAddress == SizeOfCommonAddress
                && other.SizeOfInformationObjectAddress == SizeOfInformationObjectAddress;
        }

        /// <summary>IEC 60870-5-104 的默认参数（TCP/IP 网络场景）</summary>
        public static ApplicationLayerParameters CreateForCs104()
        {
            return new ApplicationLayerParameters
            {
                SizeOfCauseOfTransmission = 2,
                SizeOfCommonAddress = 2,
                SizeOfInformationObjectAddress = 3
            };
        }

        /// <summary>IEC 60870-5-101 的默认参数（串行链路场景）</summary>
        public static ApplicationLayerParameters CreateForCs101()
        {
            return new ApplicationLayerParameters
            {
                SizeOfCauseOfTransmission = 1,
                SizeOfCommonAddress = 1,
                SizeOfInformationObjectAddress = 2
            };
        }

        public override string ToString()
        {
            return $"COT={SizeOfCauseOfTransmission}B CA={SizeOfCommonAddress}B " +
                   $"IOA={SizeOfInformationObjectAddress}B";
        }
    }
}
