using System;
using IEC104.Core.CS101;

namespace IEC104.App.ViewModels
{
    /// <summary>
    /// 一根通信报文记录 —— 用于界面上的"报文监视 / ASDU 监视"列表。
    ///
    /// 复刻说明：对应原程序的
    /// Windows.Connection.CommunicatingTrafficWindow（通信报文窗口）
    /// 与 ViewModels.MsgViewModel（消息模型）。
    /// </summary>
    public class AsduRecord
    {
        /// <summary>发生时间</summary>
        public DateTime Timestamp { get; init; } = DateTime.Now;

        /// <summary>方向：收（主站→从站）或 发（从站→主站）</summary>
        public string Direction { get; init; }

        /// <summary>类型标识代号，如 C_IC_NA_1</summary>
        public string TypeCode { get; init; }

        /// <summary>类型中文名</summary>
        public string TypeName { get; init; }

        /// <summary>传送原因（含数值）</summary>
        public string CauseText { get; init; }

        /// <summary>公共地址</summary>
        public int CommonAddress { get; init; }

        /// <summary>信息对象摘要（点号 + 值）</summary>
        public string ObjectSummary { get; init; }

        /// <summary>原始报文十六进制，便于对照规约抓包</summary>
        public string Hex { get; init; }

        public string TimeText => Timestamp.ToString("HH:mm:ss.fff");

        public bool IsReceived => Direction == "收";

        /// <summary>从一条 ASDU 构造记录（用于解析后的展示）</summary>
        public static AsduRecord FromAsdu(Asdu asdu, string direction, byte[] rawFrame)
        {
            string typeCode = asdu.TypeId.ToString();
            string objects = string.Empty;

            if (asdu.InformationObjects.Count > 0)
            {
                InformationObject first = asdu.InformationObjects[0];
                objects = asdu.InformationObjects.Count == 1
                    ? $"IOA={first.ObjectAddress}"
                    : $"IOA={first.ObjectAddress} …(共 {asdu.InformationObjects.Count} 个)";
            }

            return new AsduRecord
            {
                Direction = direction,
                TypeCode = typeCode,
                TypeName = Services.TypeIdCatalog.DisplayNameOf(asdu.TypeId),
                CauseText = $"{asdu.Cot}({(int)asdu.Cot})",
                CommonAddress = asdu.CommonAddress,
                ObjectSummary = objects,
                Hex = rawFrame == null ? string.Empty : BitConverter.ToString(rawFrame).Replace('-', ' ')
            };
        }

        /// <summary>构造一条纯文本日志记录（链路事件等）</summary>
        public static AsduRecord Note(string text)
        {
            return new AsduRecord
            {
                Direction = "—",
                TypeCode = "链路",
                TypeName = text,
                CauseText = string.Empty,
                CommonAddress = 0,
                ObjectSummary = string.Empty,
                Hex = string.Empty
            };
        }
    }
}
