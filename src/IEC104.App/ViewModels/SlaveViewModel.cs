using System;
using System.Collections.Generic;
using System.Linq;
using IEC104.App.Infrastructure;
using IEC104.Core.CS101;
using IEC104.Simulator;
using IEC104.Simulator.IoModels;

namespace IEC104.App.ViewModels
{
    /// <summary>
    /// 批量铺点时用的"点位描述"。用它而不是直接传 PointViewModel，
    /// 是因为铺 20 万点时逐个构造 VM 会多出 20 万次中间对象的存亡，
    /// 让调用方能把参数算好、一次性交过来。
    /// </summary>
    public readonly struct PointDraft
    {
        public PointDraft(int objectAddress, TypeIdentifier type, string name, double value)
        {
            ObjectAddress = objectAddress;
            Type = type;
            Name = name;
            Value = value;
        }

        public int ObjectAddress { get; }

        public TypeIdentifier Type { get; }

        public string Name { get; }

        public double Value { get; }
    }

    /// <summary>
    /// 从站（Slave）—— 一条连接下的一台"装置"，用公共地址（Common Address）区分。
    ///
    /// 复刻说明：对应原程序 ViewModels.StationViewModel。
    /// 一个连接（端口/串口）下可以挂多个从站，主站按 ASDU 的公共地址寻址到具体从站，
    /// 这与现场"一个 104 通道下接多台 RTU"的结构一致。
    ///
    /// 性能改造（第十一轮）：
    ///   ① <see cref="FindPoint"/> 原来是 Points.FirstOrDefault 线性扫描。
    ///      "初始化数据"每铺一个点都要查一次重号，2000 点就是 200 万次比较；
    ///      现在维护 地址→VM 的字典，O(1)。
    ///   ② 新增 <see cref="AddPoints"/> 批量入口：一次构造、一次入表、一次通知。
    ///      单点 AddPoint 保留，内部走同一条批量路径。
    /// </summary>
    public class SlaveViewModel : ViewModelBase
    {
        private string _name = "新建从站";
        private int _commonAddress = 1;

        /// <summary>点号 → 界面模型。铺点、改号、遥控回写都要按点号找人，必须 O(1)</summary>
        private readonly Dictionary<int, PointViewModel> _index = new();

        /// <summary>协议层从站对象，负责真正的应答逻辑</summary>
        public SlaveStation Station { get; }

        /// <summary>
        /// 点表。
        /// 类型是 RangeObservableCollection（ObservableCollection 的子类），
        /// 批量铺点时只发一次 Reset，界面只重建一次。
        /// </summary>
        public RangeObservableCollection<PointViewModel> Points { get; } = new();

        public SlaveViewModel()
        {
            // 先按 104 的标准宽度（COT 2B / CA 2B / IOA 3B）建起来。
            // 真正挂到某条通道时，ConnectionViewModel.HookSlave 会把那条通道自己的
            // 参数实例塞进来——三条长度的权威值在通道上，不在从站上。
            Station = new SlaveStation();
            SyncStation();

            // 1.10：把"任意点值变了"从协议层一路冒泡到工程层（用于值脏标记）。
            // 这里只用一次委托，不按点订阅。
            Station.ValueChangedHook = () => ValueTouched?.Invoke();
        }

        /// <summary>
        /// 值变化上报（1.10）。由 <see cref="MainWindowViewModel"/> 在从站加入通道时注入，
        /// 用来把工程标成"值脏"（退出时要存最后值）。
        /// 为 null 表示这台从站还没挂到任何工程上（例如自检里临时造的从站）。
        /// </summary>
        internal Action ValueTouched { get; set; }

        public string Name
        {
            get => _name;
            set
            {
                if (SetField(ref _name, value))
                {
                    Station.Name = value;
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public int CommonAddress
        {
            get => _commonAddress;
            set
            {
                if (SetField(ref _commonAddress, value))
                {
                    Station.CommonAddress = value;
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        /// <summary>树/列表上的显示文本</summary>
        public string DisplayText => $"{_name} (CA={_commonAddress})";

        /// <summary>点位数量</summary>
        public int PointCount => Points.Count;

        /// <summary>
        /// 添加单个点位。
        /// 会把界面模型同步下沉到协议层（Station.AddPoint），
        /// 这样主站总召时才能拿到这个点。
        /// </summary>
        public PointViewModel AddPoint(int objectAddress, TypeIdentifier type, string name, double value)
        {
            List<PointViewModel> created = AddPoints(new[] { new PointDraft(objectAddress, type, name, value) });
            return created.Count > 0 ? created[0] : FindPoint(objectAddress);
        }

        /// <summary>
        /// 批量添加点位（铺点表的主入口）。
        ///
        /// 三件事一次做完：协议层登记 → 建界面模型 → 单次通知界面。
        /// 点号重复的条目直接跳过（协议层用字典索引，重复 Add 会覆盖旧点，
        /// 表现为"刚建的点莫名不见了"，所以宁可不加也不覆盖）。
        /// </summary>
        public List<PointViewModel> AddPoints(IReadOnlyList<PointDraft> drafts)
        {
            if (drafts == null || drafts.Count == 0)
            {
                return new List<PointViewModel>(0);
            }

            List<PointViewModel> created = new(drafts.Count);
            DateTime now = DateTime.Now;

            // 1.9.7：两个字典 + 点表数组一次性预留容量，避免铺点过程中反复扩容。
            // 100 万点若靠 2 倍扩容长起来，光"新旧两套桶同时存在"的瞬时峰值就有几十 MB。
            int targetCapacity = _index.Count + drafts.Count;
            _index.EnsureCapacity(targetCapacity);
            Station.EnsurePointCapacity(targetCapacity);

            for (int index = 0; index < drafts.Count; index++)
            {
                PointDraft draft = drafts[index];
                if (_index.ContainsKey(draft.ObjectAddress))
                {
                    continue;
                }

                DataPoint point = new()
                {
                    ObjectAddress = draft.ObjectAddress,
                    Type = draft.Type,
                    Name = draft.Name,
                    Value = draft.Value,
                    // 1.10：建点时初始值默认等于给的初值。
                    // 所有铺点入口（手建、批量铺点、初始化数据、载入工程）都走这里，
                    // 所以"初始值"总有个合理起点；载入工程时会被文件里的 InitValue 覆盖。
                    InitValue = draft.Value,
                    LastChangedAt = now
                };

                // 注意顺序：先建对象并赋初值，再交给 Station 订阅 ValueChanged。
                // 反过来的话，初值写入会被当成一次"变位"，铺点瞬间向主站喷一路 COT=3。
                Station.AddPoint(point);

                PointViewModel vm = new(point, Station)
                {
                    // 1.9.7：原先注入的是 Func<PointViewModel,int,bool> 委托（每点一个闭包），
                    // 现在只存一个从站引用，改点号时由本类处理，
                    // 见 PointViewModel.Owner 的注释。
                    Owner = this
                };

                _index[draft.ObjectAddress] = vm;
                created.Add(vm);
            }

            if (created.Count > 0)
            {
                // 单次 Reset：标签页与表格只重建一次，这是 20 万点能不卡的关键
                Points.AddRange(created);
                OnPropertyChanged(nameof(PointCount));
            }

            return created;
        }

        /// <summary>
        /// 内联改点号的入口：先查范围与重号，再交给 <see cref="ChangePointAddress"/> 重建索引。
        /// 直接调 ChangePointAddress 也能用，但它失败时静默返回，
        /// 界面分不清"改成功了"和"被忽略了"，所以这里包一层给出明确结果。
        /// </summary>
        internal bool TryChangePointAddress(PointViewModel point, int newAddress)
        {
            if (point == null || newAddress < 1 || newAddress > 0xFFFFFF)
            {
                return false;
            }

            PointViewModel existing = FindPoint(newAddress);
            if (existing != null && !ReferenceEquals(existing, point))
            {
                return false;
            }

            ChangePointAddress(point, newAddress);
            return true;
        }

        /// <summary>按点号取点（字典查找，O(1)）</summary>
        public PointViewModel FindPoint(int objectAddress)
        {
            return _index.TryGetValue(objectAddress, out PointViewModel point) ? point : null;
        }

        /// <summary>
        /// 删除点位。
        /// 必须同时从协议层摘除，否则主站总召仍会看到这个点（"界面删了但报文里还有"）。
        /// </summary>
        public void RemovePoint(PointViewModel point)
        {
            if (point == null)
            {
                return;
            }

            Station.RemovePoint(point.ObjectAddress);
            _index.Remove(point.ObjectAddress);
            Points.Remove(point);
            OnPropertyChanged(nameof(PointCount));
        }

        /// <summary>清空点表</summary>
        public void ClearPoints()
        {
            Station.ClearPoints();
            _index.Clear();
            Points.Clear();
            OnPropertyChanged(nameof(PointCount));
        }

        /// <summary>
        /// 改点号：协议层用字典索引，改号必须"先删后加"，不能原地改。
        /// 这是很容易踩的坑——原地改 ObjectAddress 会导致旧键指向同一个对象，
        /// 总召时同一个点被上送两次。
        /// </summary>
        public void ChangePointAddress(PointViewModel point, int newAddress)
        {
            if (point == null || point.ObjectAddress == newAddress || FindPoint(newAddress) != null)
            {
                return;
            }

            DataPoint dataPoint = point.Point;
            int oldAddress = dataPoint.ObjectAddress;
            Station.RemovePoint(oldAddress);
            dataPoint.ObjectAddress = newAddress;
            Station.AddPoint(dataPoint);
            _index.Remove(oldAddress);
            _index[newAddress] = point;
            point.NotifyAddressChanged();
            point.RefreshFromPoint();
        }

        /// <summary>把界面上的从站参数同步到协议层</summary>
        private void SyncStation()
        {
            Station.Name = _name;
            Station.CommonAddress = _commonAddress;
        }

        /// <summary>主站遥控改值后，刷新对应点位的界面显示</summary>
        internal void RefreshPoint(int objectAddress)
        {
            FindPoint(objectAddress)?.RefreshFromPoint();
        }

        public override string ToString() => DisplayText;
    }
}
