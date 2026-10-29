namespace IEC104.Core.CS101
{
    /// <summary>
    /// ASDU 类型标识（TypeID）—— 决定信息对象的结构与语义。
    ///
    /// 复刻说明（源程序集：lib60870.dll 中 lib60870.CS101.TypeID，共 67 项）：
    /// 枚举成员名与数值完全对齐原实现，并补齐了 IEC 60870-5-101:2003 的注释说明。
    /// 命名规则（规约约定，务必理解，否则看报文会懵）：
    ///   M_ = 监视方向（从站 -> 主站，上送数据）
    ///   C_ = 控制方向（主站 -> 从站，下发命令）
    ///   P_ = 参数方向（主站 -> 从站，下装参数）
    ///   F_ = 文件传输（101 扩展）
    ///   NA = 无时标，TA = 带 CP24Time2a 短时标，TB/TD/TE/TF = 带 CP56Time2a 长时标
    /// </summary>
    public enum TypeIdentifier : byte
    {
        // ── 监视方向：过程信息（1..21）──────────────────────────────

        /// <summary>M_SP_NA_1 单点信息（遥信）。信息元素：SIQ（1 字节）</summary>
        M_SP_NA_1 = 1,

        /// <summary>M_SP_TA_1 带 CP24Time2a 时标的单点信息</summary>
        M_SP_TA_1 = 2,

        /// <summary>M_DP_NA_1 双点信息（遥信，含中间态）。信息元素：DIQ（1 字节）</summary>
        M_DP_NA_1 = 3,

        /// <summary>M_DP_TA_1 带 CP24Time2a 时标的双点信息</summary>
        M_DP_TA_1 = 4,

        /// <summary>M_ST_NA_1 步位置信息（变压器档位）。信息元素：VTI + QDS</summary>
        M_ST_NA_1 = 5,

        /// <summary>M_ST_TA_1 带 CP24Time2a 时标的步位置信息</summary>
        M_ST_TA_1 = 6,

        /// <summary>M_BO_NA_1 32 位比特串（成组遥信/保护压板状态）。信息元素：BSI + QDS</summary>
        M_BO_NA_1 = 7,

        /// <summary>M_BO_TA_1 带 CP24Time2a 时标的 32 位比特串</summary>
        M_BO_TA_1 = 8,

        /// <summary>M_ME_NA_1 测量值·归一化值（遥测，2 字节有符号小数）。NVA + QDS</summary>
        M_ME_NA_1 = 9,

        /// <summary>M_ME_TA_1 带 CP24Time2a 时标的归一化测量值</summary>
        M_ME_TA_1 = 10,

        /// <summary>M_ME_NB_1 测量值·标度化值（2 字节有符号整数）。SVA + QDS</summary>
        M_ME_NB_1 = 11,

        /// <summary>M_ME_TB_1 带 CP24Time2a 时标的标度化测量值</summary>
        M_ME_TB_1 = 12,

        /// <summary>M_ME_NC_1 测量值·短浮点数（IEEE 754 单精度，4 字节）</summary>
        M_ME_NC_1 = 13,

        /// <summary>M_ME_TC_1 带 CP24Time2a 时标的短浮点测量值</summary>
        M_ME_TC_1 = 14,

        /// <summary>M_IT_NA_1 累计量（电度，BCR 5 字节）</summary>
        M_IT_NA_1 = 15,

        /// <summary>M_IT_TA_1 带 CP24Time2a 时标的累计量</summary>
        M_IT_TA_1 = 16,

        /// <summary>M_EP_TA_1 继电保护设备事件（CP24 时标 + CP16 相对时间）</summary>
        M_EP_TA_1 = 17,

        /// <summary>M_EP_TB_1 继电保护设备成组启动事件</summary>
        M_EP_TB_1 = 18,

        /// <summary>M_EP_TC_1 继电保护设备成组输出电路信息</summary>
        M_EP_TC_1 = 19,

        /// <summary>M_PS_NA_1 带变位检出的成组单点信息（SCD + QDS）</summary>
        M_PS_NA_1 = 20,

        /// <summary>M_ME_ND_1 不带品质描述词的归一化测量值（省 1 字节）</summary>
        M_ME_ND_1 = 21,

        // ── 监视方向：带 CP56Time2a 长时标（30..40）─────────────────

        /// <summary>M_SP_TB_1 带 CP56Time2a 时标的单点信息</summary>
        M_SP_TB_1 = 30,

        /// <summary>M_DP_TB_1 带 CP56Time2a 时标的双点信息</summary>
        M_DP_TB_1 = 31,

        /// <summary>M_ST_TB_1 带 CP56Time2a 时标的步位置信息</summary>
        M_ST_TB_1 = 32,

        /// <summary>M_BO_TB_1 带 CP56Time2a 时标的 32 位比特串</summary>
        M_BO_TB_1 = 33,

        /// <summary>M_ME_TD_1 带 CP56Time2a 时标的归一化测量值</summary>
        M_ME_TD_1 = 34,

        /// <summary>M_ME_TE_1 带 CP56Time2a 时标的标度化测量值</summary>
        M_ME_TE_1 = 35,

        /// <summary>M_ME_TF_1 带 CP56Time2a 时标的短浮点测量值</summary>
        M_ME_TF_1 = 36,

        /// <summary>M_IT_TB_1 带 CP56Time2a 时标的累计量</summary>
        M_IT_TB_1 = 37,

        /// <summary>M_EP_TD_1 带 CP56Time2a 时标的继电保护设备事件</summary>
        M_EP_TD_1 = 38,

        /// <summary>M_EP_TE_1 带 CP56Time2a 时标的成组启动事件</summary>
        M_EP_TE_1 = 39,

        /// <summary>M_EP_TF_1 带 CP56Time2a 时标的成组输出电路信息</summary>
        M_EP_TF_1 = 40,

        // ── 控制方向：过程信息（45..51）─────────────────────────────

        /// <summary>C_SC_NA_1 单点遥控命令（SCO）</summary>
        C_SC_NA_1 = 45,

        /// <summary>C_DC_NA_1 双点遥控命令（DCO）</summary>
        C_DC_NA_1 = 46,

        /// <summary>C_RC_NA_1 步调节命令（升/降/停，RCO）</summary>
        C_RC_NA_1 = 47,

        /// <summary>C_SE_NA_1 设定值命令·归一化值</summary>
        C_SE_NA_1 = 48,

        /// <summary>C_SE_NB_1 设定值命令·标度化值</summary>
        C_SE_NB_1 = 49,

        /// <summary>C_SE_NC_1 设定值命令·短浮点数</summary>
        C_SE_NC_1 = 50,

        /// <summary>C_BO_NA_1 32 位比特串命令</summary>
        C_BO_NA_1 = 51,

        // ── 控制方向：带 CP56Time2a 长时标（58..64）─────────────────

        /// <summary>C_SC_TA_1 带 CP56Time2a 时标的单点命令</summary>
        C_SC_TA_1 = 58,

        /// <summary>C_DC_TA_1 带 CP56Time2a 时标的双点命令</summary>
        C_DC_TA_1 = 59,

        /// <summary>C_RC_TA_1 带 CP56Time2a 时标的步调节命令</summary>
        C_RC_TA_1 = 60,

        /// <summary>C_SE_TA_1 带 CP56Time2a 时标的归一化设定值命令</summary>
        C_SE_TA_1 = 61,

        /// <summary>C_SE_TB_1 带 CP56Time2a 时标的标度化设定值命令</summary>
        C_SE_TB_1 = 62,

        /// <summary>C_SE_TC_1 带 CP56Time2a 时标的短浮点设定值命令</summary>
        C_SE_TC_1 = 63,

        /// <summary>C_BO_TA_1 带 CP56Time2a 时标的 32 位比特串命令</summary>
        C_BO_TA_1 = 64,

        // ── 监视方向：系统命令（70）─────────────────────────────────

        /// <summary>M_EI_NA_1 初始化结束（从站上电/复位后主动上送，COI 1 字节）</summary>
        M_EI_NA_1 = 70,

        // ── 控制方向：系统命令（100..107）───────────────────────────

        /// <summary>C_IC_NA_1 总召唤（站召唤）。从站需以此组织全数据上送</summary>
        C_IC_NA_1 = 100,

        /// <summary>C_CI_NA_1 计数量召唤（电度召唤）</summary>
        C_CI_NA_1 = 101,

        /// <summary>C_RD_NA_1 读命令（按信息对象地址点名读取）</summary>
        C_RD_NA_1 = 102,

        /// <summary>C_CS_NA_1 时钟同步命令（主站下发 CP56Time2a，从站据此校时）</summary>
        C_CS_NA_1 = 103,

        /// <summary>C_TS_NA_1 测试命令（链路保活，从站回送同样的测试帧）</summary>
        C_TS_NA_1 = 104,

        /// <summary>C_RP_NA_1 复位进程命令</summary>
        C_RP_NA_1 = 105,

        /// <summary>C_CD_NA_1 延时获得命令（用于传输延时测定）</summary>
        C_CD_NA_1 = 106,

        /// <summary>C_TS_TA_1 带 CP56Time2a 时标的测试命令</summary>
        C_TS_TA_1 = 107,

        // ── 参数方向（110..113）─────────────────────────────────────

        /// <summary>P_ME_NA_1 测量值参数·归一化值</summary>
        P_ME_NA_1 = 110,

        /// <summary>P_ME_NB_1 测量值参数·标度化值</summary>
        P_ME_NB_1 = 111,

        /// <summary>P_ME_NC_1 测量值参数·短浮点数</summary>
        P_ME_NC_1 = 112,

        /// <summary>P_AC_NA_1 参数激活（QPA）</summary>
        P_AC_NA_1 = 113,

        // ── 文件传输（120..127）─────────────────────────────────────

        /// <summary>F_FR_NA_1 文件准备就绪</summary>
        F_FR_NA_1 = 120,

        /// <summary>F_SR_NA_1 节准备就绪</summary>
        F_SR_NA_1 = 121,

        /// <summary>F_SC_NA_1 召唤目录 / 选择文件 / 召唤文件 / 召唤节</summary>
        F_SC_NA_1 = 122,

        /// <summary>F_LS_NA_1 最后的节 / 最后的段</summary>
        F_LS_NA_1 = 123,

        /// <summary>F_AF_NA_1 认可文件 / 认可段</summary>
        F_AF_NA_1 = 124,

        /// <summary>F_SG_NA_1 段（文件数据块）</summary>
        F_SG_NA_1 = 125,

        /// <summary>F_DR_TA_1 目录（带 CP56Time2a 时标）</summary>
        F_DR_TA_1 = 126,

        /// <summary>F_SC_NB_1 扩展召唤目录（部分厂家的私有扩展）</summary>
        F_SC_NB_1 = 127
    }
}
