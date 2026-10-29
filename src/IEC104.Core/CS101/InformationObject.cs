using System;
using System.Collections.Generic;
using IEC104.Core.Time;

namespace IEC104.Core.CS101
{
    /// <summary>
    /// 信息对象（Information Object）抽象基类。
    ///
    /// 一个信息对象 = 信息对象地址（IOA）+ 一组信息元素 + 可选的时标。
    /// 原实现（lib60870.CS101.InformationObject）用统一的 Encode/Decode 接口，
    /// 所有具体类型（单点、双点、测量值、累计量…）都由此派生——
    /// 这个"一个 TypeID 对应一个具体类"的设计被完整保留下来，
    /// 因为它让新增 ASDU 类型变成"加一个类 + 工厂里注册一行"，改动是收敛的。
    /// </summary>
    public abstract class InformationObject
    {
        /// <summary>信息对象地址（IOA），即通常说的点号</summary>
        public int ObjectAddress { get; protected set; }

        protected InformationObject(int objectAddress)
        {
            if (objectAddress < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(objectAddress), "信息对象地址不能为负。");
            }
            ObjectAddress = objectAddress;
        }

        /// <summary>把信息元素与时标写入字节流（不含 IOA，IOA 由 ASDU 统一写入）</summary>
        public abstract void Encode(List<byte> output, ApplicationLayerParameters parameters);

        /// <summary>读取信息元素与时标；返回本对象占用的字节数</summary>
        public abstract int Decode(byte[] buffer, int startIndex, ApplicationLayerParameters parameters);

        public override string ToString()
        {
            return $"{GetType().Name}(IOA={ObjectAddress})";
        }
    }

    /// <summary>
    /// 信息对象工厂 —— 按 TypeID 分派到具体类型。
    ///
    /// 复刻说明：原实现把这个逻辑放在 ASDU 内部的 switch 里（约 60 个分支）。
    /// 此处抽成独立工厂，好处是新增类型不必改动 ASDU 主体；
    /// 同时集中处理"不支持的 TypeID"，避免在各处重复抛异常。
    /// </summary>
    public static class InformationObjectFactory
    {
        /// <summary>
        /// 依据类型标识创建并解析具体信息对象。
        /// </summary>
        /// <param name="typeId">ASDU 类型标识</param>
        /// <param name="objectAddress">信息对象地址</param>
        /// <param name="buffer">报文缓冲区</param>
        /// <param name="startIndex">信息元素起始下标</param>
        /// <param name="parameters">应用层参数</param>
        /// <param name="bytesConsumed">输出：信息元素占用的字节数</param>
        public static InformationObject Create(
            TypeIdentifier typeId,
            int objectAddress,
            byte[] buffer,
            int startIndex,
            ApplicationLayerParameters parameters,
            out int bytesConsumed)
        {
            InformationObject informationObject;

            switch (typeId)
            {
                case TypeIdentifier.M_SP_NA_1:
                    informationObject = new SinglePointInformation(objectAddress);
                    break;

                case TypeIdentifier.M_SP_TA_1:
                    informationObject = new SinglePointWithCp24Time2a(objectAddress);
                    break;

                case TypeIdentifier.M_SP_TB_1:
                    informationObject = new SinglePointWithCp56Time2a(objectAddress);
                    break;

                case TypeIdentifier.M_DP_NA_1:
                    informationObject = new DoublePointInformation(objectAddress);
                    break;

                case TypeIdentifier.M_DP_TA_1:
                    informationObject = new DoublePointWithCp24Time2a(objectAddress);
                    break;

                case TypeIdentifier.M_DP_TB_1:
                    informationObject = new DoublePointWithCp56Time2a(objectAddress);
                    break;

                case TypeIdentifier.M_ME_NA_1:
                    informationObject = new MeasuredValueNormalized(objectAddress);
                    break;

                case TypeIdentifier.M_ME_NB_1:
                    informationObject = new MeasuredValueScaled(objectAddress);
                    break;

                case TypeIdentifier.M_ME_NC_1:
                    informationObject = new MeasuredValueShort(objectAddress);
                    break;

                case TypeIdentifier.M_ME_ND_1:
                    informationObject = new MeasuredValueNormalizedWithoutQuality(objectAddress);
                    break;

                case TypeIdentifier.M_IT_NA_1:
                    informationObject = new IntegratedTotals(objectAddress);
                    break;

                case TypeIdentifier.M_ST_NA_1:
                    informationObject = new StepPositionInformation(objectAddress);
                    break;

                case TypeIdentifier.M_BO_NA_1:
                    informationObject = new Bitstring32(objectAddress);
                    break;

                case TypeIdentifier.M_EI_NA_1:
                    informationObject = new EndOfInitialization(objectAddress);
                    break;

                // ── 以下为命令方向（主站 → 从站）类型 ──────────────────
                // 从站必须能解析这些，否则收到命令后无从生成应答

                case TypeIdentifier.C_IC_NA_1:
                    informationObject = new InterrogationCommand(objectAddress);
                    break;

                case TypeIdentifier.C_CI_NA_1:
                    informationObject = new CounterInterrogationCommand(objectAddress);
                    break;

                case TypeIdentifier.C_CS_NA_1:
                    informationObject = new ClockSynchronizationCommand(objectAddress, default(Cp56Time2a));
                    break;

                case TypeIdentifier.C_SC_NA_1:
                    informationObject = new SingleCommand(objectAddress);
                    break;

                case TypeIdentifier.C_DC_NA_1:
                    informationObject = new DoubleCommand(objectAddress);
                    break;

                case TypeIdentifier.C_SE_NA_1:
                    informationObject = new SetpointCommandNormalized(objectAddress);
                    break;

                case TypeIdentifier.C_SE_NC_1:
                    informationObject = new SetpointCommandShort(objectAddress);
                    break;

                case TypeIdentifier.C_TS_NA_1:
                    informationObject = new TestCommand(objectAddress);
                    break;

                case TypeIdentifier.C_RD_NA_1:
                    informationObject = new ReadCommand(objectAddress);
                    break;

                case TypeIdentifier.C_RP_NA_1:
                    informationObject = new ResetProcessCommand(objectAddress);
                    break;

                default:
                    // 没写强类型类的类型：先查运行期注册表。
                    // 表里有的（内置补录 + 用户自定义）按"格式 × 时标"通用编解码；
                    // 真不认识才抛——未知类型绝不能吞掉，否则上层会按错误偏移继续解析。
                    if (InformationObjectRegistry.TryGet(typeId, out ElementFormat format, out TimeTagKind timeTag))
                    {
                        informationObject = new GenericInformationObject(objectAddress, format, timeTag);
                        break;
                    }

                    throw new NotSupportedException(
                        $"暂不支持的 ASDU 类型标识：{typeId}({(int)typeId})。" +
                        "如需支持请在此工厂注册对应的信息对象实现，或在设置里为该类型登记编码格式。");
            }

            bytesConsumed = informationObject.Decode(buffer, startIndex, parameters);
            return informationObject;
        }
    }
}
