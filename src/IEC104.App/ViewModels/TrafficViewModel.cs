using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using IEC104.App.Infrastructure;
using IEC104.App.Services;

namespace IEC104.App.ViewModels
{
    /// <summary>
    /// 通信报文监视（独立窗口）的 ViewModel。
    ///
    /// 复刻说明：对应原程序 CommunicatingTrafficWindow ——
    /// 主窗口里那两页监视适合"边操作边看"，这个独立窗口适合
    /// 一边放监视、一边在主界面改点值，或者干脆拉到第二块屏上盯报文。
    ///
    /// 与主窗口共用同一份 Records 集合（不是拷贝），所以两边看到的内容一致；
    /// 这里只做过滤视图，避免每来一条报文就整体重建几千行。
    /// </summary>
    public class TrafficViewModel : ViewModelBase
    {
        /// <summary>过滤视图的行数上限，跟报文缓冲上限保持一致（见设置 → 报文监视）</summary>
        private static int MaxRows => AppSettingsStore.Current.TrafficBufferLimit;

        private readonly ObservableCollection<AsduRecord> _source;
        private string _keyword = string.Empty;
        private int _directionIndex;
        private AsduRecord _selectedRecord;

        public TrafficViewModel(ObservableCollection<AsduRecord> source)
        {
            _source = source ?? new ObservableCollection<AsduRecord>();

            // 先按现有内容铺一遍（打开窗口时能看到历史）
            List<AsduRecord> initial = new(MaxRows);
            for (int index = _source.Count - 1; index >= 0 && initial.Count < MaxRows; index--)
            {
                AsduRecord record = _source[index];
                if (Matches(record))
                {
                    initial.Add(record);
                }
            }
            Records.ReplaceAll(initial);

            _source.CollectionChanged += OnSourceChanged;
        }

        /// <summary>
        /// 过滤后的视图，新的在最上面。
        ///
        /// 性能（第十二轮改）：原来是普通 ObservableCollection，Rebuild 里
        /// Clear + 3000 次 Add —— 也就是每次批量刷新都发 3001 次集合事件，
        /// 报文窗口开着的时候光通知就能把 UI 线程按住。现在 ReplaceAll 只发一次 Reset。
        /// </summary>
        public RangeObservableCollection<AsduRecord> Records { get; } = new();

        /// <summary>方向：0 全部 / 1 接收 / 2 发送</summary>
        public int DirectionIndex
        {
            get => _directionIndex;
            set
            {
                if (SetField(ref _directionIndex, value))
                {
                    Rebuild();
                }
            }
        }

        public string Keyword
        {
            get => _keyword;
            set
            {
                if (SetField(ref _keyword, value ?? string.Empty))
                {
                    Rebuild();
                }
            }
        }

        public AsduRecord SelectedRecord
        {
            get => _selectedRecord;
            set
            {
                if (SetField(ref _selectedRecord, value))
                {
                    OnPropertyChanged(nameof(SelectedHex));
                }
            }
        }

        /// <summary>选中报文对应的原始字节，便于和抓包工具比对</summary>
        public string SelectedHex => _selectedRecord?.Hex ?? "（未选中报文）";

        public string CountText => $"共 {Records.Count} 条（源 {_source.Count} 条）";

        /// <summary>清空（只清源集合，主界面同步清空）</summary>
        public void Clear()
        {
            _source.Clear();
            Records.Clear();
            OnPropertyChanged(nameof(CountText));
        }

        private void OnSourceChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                // 插到最前面：报文监视要"最新的在最上面"。
                // 先攒成一个"新在前、旧在后"的整体再一次性替换，不要逐条 Insert(0)：
                // 每条 Insert(0) 都要把已有行整体后移一位并各发一次集合事件。
                List<AsduRecord> incoming = new();
                foreach (AsduRecord record in e.NewItems.OfType<AsduRecord>())
                {
                    if (Matches(record))
                    {
                        incoming.Add(record);
                    }
                }

                if (incoming.Count == 0)
                {
                    return;
                }

                List<AsduRecord> merged = new(Math.Min(MaxRows, incoming.Count + Records.Count));
                for (int index = incoming.Count - 1; index >= 0 && merged.Count < MaxRows; index--)
                {
                    merged.Add(incoming[index]);
                }
                for (int index = 0; index < Records.Count && merged.Count < MaxRows; index++)
                {
                    merged.Add(Records[index]);
                }

                Records.ReplaceAll(merged);
                OnPropertyChanged(nameof(CountText));
            }
            else if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                // 源集合是批量刷新的（主窗口每 150 ms 合并一次，只发 Reset），
                // Reset 必须重建视图而不是清空——早先只 Clear，结果报文窗口
                // 开了几分钟还是一条不显示。
                Rebuild();
            }
        }

        private void Rebuild()
        {
            // 倒着扫源集合，避免 Reverse() 每刷一次就复制一份整表
            List<AsduRecord> view = new(MaxRows);
            for (int index = _source.Count - 1; index >= 0 && view.Count < MaxRows; index--)
            {
                AsduRecord record = _source[index];
                if (Matches(record))
                {
                    view.Add(record);
                }
            }

            Records.ReplaceAll(view);
            OnPropertyChanged(nameof(CountText));
        }

        private bool Matches(AsduRecord record)
        {
            if (record == null)
            {
                return false;
            }

            // 方向过滤
            if (_directionIndex == 1 && !record.IsReceived)
            {
                return false;
            }
            if (_directionIndex == 2 && record.IsReceived)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(_keyword))
            {
                return true;
            }

            string keyword = _keyword.Trim();
            return Contains(record.TypeCode, keyword)
                || Contains(record.TypeName, keyword)
                || Contains(record.CauseText, keyword)
                || Contains(record.ObjectSummary, keyword)
                || Contains(record.Hex, keyword)
                || record.CommonAddress.ToString() == keyword;
        }

        private static bool Contains(string value, string keyword) =>
            !string.IsNullOrEmpty(value)
            && value.Contains(keyword, StringComparison.OrdinalIgnoreCase);

        public void Detach()
        {
            _source.CollectionChanged -= OnSourceChanged;
        }
    }
}
