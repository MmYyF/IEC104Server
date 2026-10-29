using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using IEC104.App.Infrastructure;
using IEC104.Core.CS101;

namespace IEC104.App.ViewModels
{
    /// <summary>
    /// “初始化数据”里的一行：一种 ASDU 类型的批量生成规则。
    /// </summary>
    public class InitDataRow : ViewModelBase
    {
        private bool _enabled;
        private int _startAddress;
        private int _count;
        private string _namePrefix;
        private double _startValue;
        private double _step;

        public InitDataRow(TypeIdentifier type, string typeName, string prefix,
            int startAddress, int count, double startValue, double step, bool enabled = false)
        {
            Type = type;
            TypeName = typeName;
            NamePrefix = prefix;
            StartAddress = startAddress;
            Count = count;
            StartValue = startValue;
            Step = step;
            Enabled = enabled;
        }

        public TypeIdentifier Type { get; }

        /// <summary>类型显示名（M_SP_NA_1 单点遥信 之类）</summary>
        public string TypeName { get; }

        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public int StartAddress
        {
            get => _startAddress;
            set
            {
                if (SetField(ref _startAddress, value))
                {
                    OnPropertyChanged(nameof(RangeText));
                }
            }
        }

        public int Count
        {
            get => _count;
            set
            {
                if (SetField(ref _count, value))
                {
                    OnPropertyChanged(nameof(RangeText));
                }
            }
        }

        public string NamePrefix
        {
            get => _namePrefix;
            set => SetField(ref _namePrefix, value);
        }

        public double StartValue
        {
            get => _startValue;
            set => SetField(ref _startValue, value);
        }

        /// <summary>每个点的递增量：遥测模拟不同负荷，电度模拟读数累加</summary>
        public double Step
        {
            get => _step;
            set => SetField(ref _step, value);
        }

        /// <summary>预览生成后的点号范围</summary>
        public string RangeText => Count <= 0
            ? "-"
            : $"{StartAddress} … {StartAddress + Count - 1}";
    }

    /// <summary>
    /// 初始化数据窗口 ViewModel。
    ///
    /// 复刻说明：对应原程序 InitDataWindow。
    /// 现场点表动辄几百个点，逐条新增不现实，这个窗口就是用来
    /// "按类型批量铺一遍初始点表"的：给定起始点号、数量、命名前缀和初值步进，
    /// 一次性把点位灌进去。
    /// </summary>
    public class InitDataViewModel : ViewModelBase
    {
        private string _statusText = "勾选左侧“生成”列后点击“开始生成”。";

        public InitDataViewModel(SlaveViewModel slave)
        {
            Slave = slave;
            Rows = BuildDefaultRows();
        }

        public SlaveViewModel Slave { get; }

        public ObservableCollection<InitDataRow> Rows { get; }

        /// <summary>目标从站说明</summary>
        public string TargetText => Slave == null
            ? "（未选择从站）"
            : $"{Slave.Name}（CA={Slave.CommonAddress}）当前 {Slave.Points.Count} 个信息对象";

        public string StatusText
        {
            get => _statusText;
            private set => SetField(ref _statusText, value);
        }

        /// <summary>
        /// 默认规则。点号段按国内远动常用习惯划分：
        /// 1xxx 遥信 / 2xxx 遥信 / 4xxx 遥测 / 6xxx 电度 / 1xxxx 档位。
        /// </summary>
        private static ObservableCollection<InitDataRow> BuildDefaultRows()
        {
            return new ObservableCollection<InitDataRow>
            {
                new(TypeIdentifier.M_SP_NA_1, "M_SP_NA_1 单点遥信", "遥信", 1, 16, 0, 0, enabled: false),
                new(TypeIdentifier.M_DP_NA_1, "M_DP_NA_1 双点遥信", "断路器", 1001, 8, 1, 0, enabled: false),
                new(TypeIdentifier.M_ME_NC_1, "M_ME_NC_1 短浮点遥测", "遥测", 4001, 12, 0, 10, enabled: false),
                new(TypeIdentifier.M_ME_NA_1, "M_ME_NA_1 归一化遥测", "遥测归一", 4101, 6, 0, 0.1, enabled: false),
                new(TypeIdentifier.M_IT_NA_1, "M_IT_NA_1 电度", "电度", 6401, 4, 0, 100, enabled: false),
                new(TypeIdentifier.M_ST_NA_1, "M_ST_NA_1 档位", "档位", 10001, 4, 1, 1, enabled: false)
            };
        }

        /// <summary>
        /// 把所有勾选行的起始点号，按现有最大点号往后接续排布。
        /// 现场最常见的就是"在已有点表后面再补一批"，手算起始点号容易撞号。
        /// </summary>
        public void ContinueFromMaxAddress()
        {
            if (Slave == null)
            {
                StatusText = "请先选择一个从站。";
                return;
            }

            int next = Slave.Points.Count == 0
                ? 1
                : Slave.Points.Max(point => point.ObjectAddress) + 1;

            List<InitDataRow> selected = Rows.Where(r => r.Enabled).ToList();
            if (selected.Count == 0)
            {
                StatusText = "请先勾选要生成的行。";
                return;
            }

            foreach (InitDataRow row in selected)
            {
                row.StartAddress = next;
                next += Math.Max(0, row.Count);
            }

            StatusText = $"已接续排布：{string.Join("，", selected.Select(r => $"{r.TypeName} → {r.RangeText}"))}";
        }

        /// <summary>
        /// 执行批量生成。
        ///
        /// 关键点：点号在同一个从站内必须唯一，否则协议层索引会互相覆盖。
        /// 所以这里既跳过已存在的点号，也要防止本批次内部自己撞号。
        ///
        /// 性能（第十一轮改）：原来是"边算边加"，每加一个点都会触发一次
        /// 标签页重建 + 表格换源。100 台 × 2000 点要 336 秒、内存涨到 3.4 GB。
        /// 现在改成"先把要加的点全部算完（用 HashSet 去重），最后一次性提交"，
        /// 界面只重建一次。
        /// </summary>
        public void Generate()
        {
            if (Slave == null)
            {
                StatusText = "请先选择一个从站。";
                return;
            }

            int skipped = 0;
            List<InitDataRow> selected = Rows.Where(r => r.Enabled).ToList();
            if (selected.Count == 0)
            {
                StatusText = "没有勾选任何类型。";
                return;
            }

            // 先按总量预留，避免铺 20 万点时 List 反复扩容拷贝
            int estimate = selected.Sum(row => Math.Max(0, row.Count));
            List<PointDraft> drafts = new(estimate);
            HashSet<int> usedInBatch = new(estimate);

            foreach (InitDataRow row in selected)
            {
                int count = Math.Max(0, row.Count);
                for (int index = 0; index < count; index++)
                {
                    int address = row.StartAddress + index;
                    if (address > 0xFFFFFF)
                    {
                        skipped++;
                        continue;
                    }

                    // 点号重复一律跳过：协议层用字典索引，重复 Add 会直接覆盖旧点，
                    // 表现为“刚建的点莫名其妙不见了”，所以这里不做覆盖、只跳过并计数。
                    // FindPoint 现在是字典查找，铺 2000 点不再退化成 200 万次比较。
                    if (Slave.FindPoint(address) != null || !usedInBatch.Add(address))
                    {
                        skipped++;
                        continue;
                    }

                    string name = string.IsNullOrWhiteSpace(row.NamePrefix)
                        ? $"{row.TypeName} {index + 1}"
                        : $"{row.NamePrefix}{index + 1}";

                    drafts.Add(new PointDraft(address, row.Type, name, row.StartValue + row.Step * index));
                }
            }

            // 一次性提交：协议层登记 + 建界面模型 + 单次 Reset
            int added = Slave.AddPoints(drafts).Count;
            skipped += drafts.Count - added;

            StatusText = $"已生成 {added} 个信息对象，跳过 {skipped} 个（点号已占用或越界）。";
            OnPropertyChanged(nameof(TargetText));
        }
    }
}
