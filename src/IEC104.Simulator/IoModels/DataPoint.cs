using System;
using IEC104.Core.CS101;

namespace IEC104.Simulator.IoModels
{
    /// <summary>
    /// 从站数据点模型 —— 模拟现场 RTU/装置内部的一个"点"。
    ///
    /// 复刻说明（对应原实现 IEC104ServerSimulator.ViewModels 下的
    /// SinglePointInformationViewModel / MeasuredValueShortViewModel 等一系列 Model+ViewModel）：
    /// 原实现为每一种 ASDU 类型都建了 Model 与 ViewModel 两个类（MVVM 双层），
    /// 且每种还按"是否带 CP24/CP56 时标"再分出变体，因此类型数量很多。
    /// 这里复刻时收敛为"一个数据点持有当前值 + 类型标签"的形式：
    ///   - 优点：新增点位无需新增类，配置驱动
    ///   - 代价：不同类型的值统一用 double/string 承载，需在取值时按 Type 判断
    /// 取舍理由：模拟器场景更关心"能发什么数据"，而非每个类型的强类型封装。
    /// 若要移植到正式产品，建议保留原实现的强类型模型，避免类型混用出错。
    /// </summary>
    public class DataPoint
    {
        /// <summary>信息对象地址（点号）</summary>
        public int ObjectAddress { get; set; }

        /// <summary>该点对应的 ASDU 类型，决定上送时如何编码</summary>
        public TypeIdentifier Type { get; set; }

        /// <summary>点名称，仅用于界面显示与日志，不参与报文</summary>
        public string Name { get; set; }

        /// <summary>当前值。遥信用 0/1；遥测用工程值；步位置用档位</summary>
        public double Value { get; set; }

        /// <summary>是否无效（对应 QDS/SIQ 的 IV 位）</summary>
        public bool IsInvalid { get; set; }

        /// <summary>是否被封锁（BL 位）。封锁后仍上送但标注不再刷新</summary>
        public bool IsBlocked { get; set; }

        /// <summary>是否被人工置数（SB 位）</summary>
        public bool IsSubstituted { get; set; }

        /// <summary>最近一次变化的时间，用于生成带时标 ASDU</summary>
        public DateTime LastChangedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// 所属从站 —— 值变化后由它决定要不要自发上送（COT=3）。
        ///
        /// 【1.9.7 内存优化：为什么不用 event】
        /// 原实现是 <c>public event Action&lt;DataPoint,double&gt; ValueChanged</c>，
        /// 由 <see cref="SlaveStation.AddPoint"/> 逐点 <c>+=</c> 订阅。
        /// 问题是每个点都会因此多出一个委托对象：委托 ≥32 字节 + 一条订阅链节点，
        /// 一百万点就是 32 MB 的纯开销——而且它不承载任何"每个点各不同"的信息，
        /// 所有点订阅的都是同一个 SlaveStation 上的同一个方法。
        ///
        /// 改成反向引用后：DataPoint 只多一个 8 字节的对象引用（原先的 event 字段也是 8 字节，
        /// 等于字段本身没变大），委托对象全省掉；调用也从"多播委托遍历"变成一次直接调用。
        /// 语义完全不变——一台从站的点只属于这一台从站，不存在一个点通知多个从站的情况。
        /// </summary>
        internal SlaveStation Owner { get; set; }

        /// <summary>
        /// 设置新值。只有真正发生变化时才通知从站上送，
        /// 避免刷新同值时产生大量无意义的上送报文。
        /// </summary>
        public void SetValue(double newValue)
        {
            if (newValue == Value)
            {
                return;
            }

            Value = newValue;
            LastChangedAt = DateTime.Now;
            Owner?.OnPointValueChanged(this, newValue);
        }

        /// <summary>构造本点对应的品质描述词</summary>
        public QualityDescriptor BuildQuality()
        {
            return new QualityDescriptor
            {
                Invalid = IsInvalid,
                Blocked = IsBlocked,
                Substituted = IsSubstituted
            };
        }

        /// <summary>构造单点品质（遥信用）</summary>
        public SinglePointQuality BuildSinglePointQuality()
        {
            return new SinglePointQuality
            {
                Value = Value != 0,
                Invalid = IsInvalid,
                Blocked = IsBlocked,
                Substituted = IsSubstituted
            };
        }

        public override string ToString()
            => $"{Name}(IOA={ObjectAddress}) {Type} = {Value}";
    }
}
