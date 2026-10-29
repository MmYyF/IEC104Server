using System;

namespace IEC104.App.Services
{
    /// <summary>
    /// 点位自动变化模式。
    ///
    /// 复刻说明：对应原程序的 EaseFunctionConfigWindow（缓变函数配置）+
    /// SetChangeInterval 命令。原版是把"变化曲线类型 + 周期 + 幅度"配到点位上，
    /// 由后台定时器统一驱动。这里保留同样三要素，但把曲线类型收敛为
    /// 现场最常用的五种，避免界面出现一堆用不上的参数。
    /// </summary>
    public enum ChangePattern
    {
        /// <summary>不变（默认）</summary>
        None = 0,

        /// <summary>正弦：模拟遥测量连续波动（如电压、频率）</summary>
        Sine = 1,

        /// <summary>随机游走：模拟有功/无功这类无规律负荷</summary>
        RandomWalk = 2,

        /// <summary>翻转：模拟开关量变位（合↔分、0↔1）</summary>
        Toggle = 3,

        /// <summary>递增：模拟电度表读数累加</summary>
        Increment = 4,

        /// <summary>
        /// 随机范围：每次在 [Min, Max] 之间取一个随机值。
        /// 遥信/双点会退化成"随机翻转"，因为开关量只有几个合法状态。
        /// </summary>
        RandomInRange = 5,

        /// <summary>
        /// 固定步长：每次在当前值上加减一个固定步长，超过上限回绕到下限。
        /// 遥测按步长走，遥信按翻转处理。
        /// </summary>
        FixedStep = 6
    }

    /// <summary>变化模式的界面显示名</summary>
    public static class ChangePatternText
    {
        public static string DisplayName(this ChangePattern pattern)
        {
            return pattern switch
            {
                ChangePattern.None => "不变",
                ChangePattern.Sine => "正弦波动",
                ChangePattern.RandomWalk => "随机游走",
                ChangePattern.Toggle => "开关变位",
                ChangePattern.Increment => "累计递增",
                ChangePattern.RandomInRange => "随机范围",
                ChangePattern.FixedStep => "固定步长",
                _ => "未知"
            };
        }

        /// <summary>逐点缓变可选项（不含批量专用的两种）</summary>
        public static ChangePattern[] All => new[]
        {
            ChangePattern.None,
            ChangePattern.Sine,
            ChangePattern.RandomWalk,
            ChangePattern.Toggle,
            ChangePattern.Increment
        };

        /// <summary>批量变化可选项</summary>
        public static ChangePattern[] BatchPatterns => new[]
        {
            ChangePattern.RandomInRange,
            ChangePattern.FixedStep,
            ChangePattern.Toggle
        };
    }
}
