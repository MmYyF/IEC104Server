namespace IEC104.Core.CS101
{
    /// <summary>
    /// 传送原因（CauseOfTransmission, COT）—— 说明本帧 ASDU 为何被发出。
    ///
    /// 复刻说明（源程序集：lib60870.dll 中 lib60870.CS101.CauseOfTransmission，共 42 项）：
    /// 成员与数值对齐原实现。COT 是从站"应答语义"的核心，
    /// 例如总召应答必须用 INTERROGATED_BY_STATION(20) 或对应分组值，
    /// 用错主站会直接丢弃该帧——这是联调阶段最高频的问题之一。
    ///
    /// 数值 &gt;= 44 的是规约定义的否定确认（从站告知主站"我没法处理"）。
    /// </summary>
    public enum CauseOfTransmission : byte
    {
        // ── 常规原因 ────────────────────────────────────────────────

        /// <summary>周期 / 循环上送（per/cyc）</summary>
        PerCyc = 1,

        /// <summary>背景扫描（back）</summary>
        BackgroundScan = 2,

        /// <summary>突发 / 自发上送（spont）—— 变位遥信、越限遥测走这个</summary>
        Spontaneous = 3,

        /// <summary>初始化完成（init）</summary>
        Initialized = 4,

        /// <summary>请求或被请求（req）</summary>
        Request = 5,

        /// <summary>激活（act）—— 主站下发命令用</summary>
        Activation = 6,

        /// <summary>激活确认（actcon）—— 从站收到命令后的第一响应</summary>
        ActivationCon = 7,

        /// <summary>停止激活（deact）</summary>
        Deactivation = 8,

        /// <summary>停止激活确认（deactcon）</summary>
        DeactivationCon = 9,

        /// <summary>激活终止（actterm）—— 从站完成命令后的结束响应</summary>
        ActivationTermination = 10,

        /// <summary>远方命令引起的返送信息（retrem）</summary>
        ReturnInfoRemote = 11,

        /// <summary>当地命令引起的返送信息（retloc）</summary>
        ReturnInfoLocal = 12,

        /// <summary>文件传输（file）</summary>
        FileTransfer = 13,

        /// <summary>认证（14）</summary>
        Authentication = 14,

        /// <summary>会话密钥维护（15）</summary>
        MaintenanceOfAuthSessionKey = 15,

        /// <summary>用户角色与更新密钥维护（16）</summary>
        MaintenanceOfUserRoleAndUpdateKey = 16,

        // ── 总召响应（20..36）───────────────────────────────────────

        /// <summary>响应站召唤（总召）。总召数据的标准回答原因</summary>
        InterrogatedByStation = 20,

        /// <summary>响应第 1 组召唤</summary>
        InterrogatedByGroup1 = 21,

        /// <summary>响应第 2 组召唤</summary>
        InterrogatedByGroup2 = 22,

        /// <summary>响应第 3 组召唤</summary>
        InterrogatedByGroup3 = 23,

        /// <summary>响应第 4 组召唤</summary>
        InterrogatedByGroup4 = 24,

        /// <summary>响应第 5 组召唤</summary>
        InterrogatedByGroup5 = 25,

        /// <summary>响应第 6 组召唤</summary>
        InterrogatedByGroup6 = 26,

        /// <summary>响应第 7 组召唤</summary>
        InterrogatedByGroup7 = 27,

        /// <summary>响应第 8 组召唤</summary>
        InterrogatedByGroup8 = 28,

        /// <summary>响应第 9 组召唤</summary>
        InterrogatedByGroup9 = 29,

        /// <summary>响应第 10 组召唤</summary>
        InterrogatedByGroup10 = 30,

        /// <summary>响应第 11 组召唤</summary>
        InterrogatedByGroup11 = 31,

        /// <summary>响应第 12 组召唤</summary>
        InterrogatedByGroup12 = 32,

        /// <summary>响应第 13 组召唤</summary>
        InterrogatedByGroup13 = 33,

        /// <summary>响应第 14 组召唤</summary>
        InterrogatedByGroup14 = 34,

        /// <summary>响应第 15 组召唤</summary>
        InterrogatedByGroup15 = 35,

        /// <summary>响应第 16 组召唤</summary>
        InterrogatedByGroup16 = 36,

        // ── 计数量召唤响应（37..41）─────────────────────────────────

        /// <summary>响应计数量总召唤</summary>
        RequestedByGeneralCounter = 37,

        /// <summary>响应第 1 组计数量召唤</summary>
        RequestedByGroup1Counter = 38,

        /// <summary>响应第 2 组计数量召唤</summary>
        RequestedByGroup2Counter = 39,

        /// <summary>响应第 3 组计数量召唤</summary>
        RequestedByGroup3Counter = 40,

        /// <summary>响应第 4 组计数量召唤</summary>
        RequestedByGroup4Counter = 41,

        // ── 否定确认（44..47）───────────────────────────────────────

        /// <summary>未知的类型标识——从站不认识该 TypeID</summary>
        UnknownTypeId = 44,

        /// <summary>未知的传送原因</summary>
        UnknownCauseOfTransmission = 45,

        /// <summary>未知的 ASDU 公共地址</summary>
        UnknownCommonAddressOfAsdu = 46,

        /// <summary>未知的信息对象地址</summary>
        UnknownInformationObjectAddress = 47
    }

    /// <summary>
    /// 传送原因的辅助判别方法。
    /// 之所以单独放一个静态类：总召/计数量召唤的判断在总召处理器里会被反复用到，
    /// 散落在各处容易出现"某处漏判分组召唤"的问题。
    /// </summary>
    public static class CauseOfTransmissionExtensions
    {
        /// <summary>是否为站总召或分组召唤的响应原因</summary>
        public static bool IsInterrogation(this CauseOfTransmission cot)
        {
            return cot >= CauseOfTransmission.InterrogatedByStation
                && cot <= CauseOfTransmission.InterrogatedByGroup16;
        }

        /// <summary>是否为计数量召唤的响应原因</summary>
        public static bool IsCounterInterrogation(this CauseOfTransmission cot)
        {
            return cot >= CauseOfTransmission.RequestedByGeneralCounter
                && cot <= CauseOfTransmission.RequestedByGroup4Counter;
        }

        /// <summary>是否为否定确认（从站拒绝/无法处理）</summary>
        public static bool IsNegativeConfirmation(this CauseOfTransmission cot)
        {
            return cot >= CauseOfTransmission.UnknownTypeId
                && cot <= CauseOfTransmission.UnknownInformationObjectAddress;
        }

        /// <summary>取总召组号：站总召返回 0，分组召唤返回 1..16，非总召返回 null</summary>
        public static int? GetInterrogationGroup(this CauseOfTransmission cot)
        {
            if (cot == CauseOfTransmission.InterrogatedByStation)
            {
                return 0;
            }
            if (cot > CauseOfTransmission.InterrogatedByStation
                && cot <= CauseOfTransmission.InterrogatedByGroup16)
            {
                return (int)cot - (int)CauseOfTransmission.InterrogatedByStation;
            }
            return null;
        }
    }
}
