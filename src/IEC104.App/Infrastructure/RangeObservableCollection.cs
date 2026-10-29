using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace IEC104.App.Infrastructure
{
    /// <summary>
    /// 支持批量添加的 ObservableCollection。
    ///
    /// 为什么需要它：普通的 ObservableCollection 每 Add 一个元素就发一次
    /// CollectionChanged。100 台从站、每台 2000 点、并且每加一个点都要重建标签页时，
    /// 一次"初始化数据"会产生 2000 次标签重建 + 2000 次表格换源，
    /// 实测 100 台铺 20 万点要 336 秒、内存涨到 3.4 GB（旧集合全成了垃圾等 GC）。
    ///
    /// 批量添加后只发**一次** Reset，界面重建一次就能收工。
    /// 代价是"重置"会丢掉表格的选中行/滚动位置——对铺点这种一次性操作无所谓，
    /// 真正逐条变化的场景（值变化）走的是 PropertyChanged，不经过这里。
    /// </summary>
    public class RangeObservableCollection<T> : ObservableCollection<T>
    {
        /// <summary>批量追加（只触发一次 Reset）</summary>
        public void AddRange(IEnumerable<T> items)
        {
            if (items == null)
            {
                return;
            }

            List<T> list = items as List<T> ?? new List<T>(items);
            if (list.Count == 0)
            {
                return;
            }

            CheckReentrancy();

            // 1.9.7：先按最终容量预留一次。否则 List 会按 2 倍扩容，
            // 铺 100 万点要扩到 1048576 个槽位，中途每次扩容都要"分配新数组 + 全量复制"，
            // 最狠的一次要同时握住 52 万和 105 万两个数组（约 12 MB 瞬时峰值）。
            // 预留一次就没有这个峰值，也省掉十几次整体复制。
            if (Items is List<T> backing)
            {
                int needed = backing.Count + list.Count;
                if (backing.Capacity < needed)
                {
                    backing.Capacity = needed;
                }
            }

            foreach (T item in list)
            {
                Items.Add(item);
            }

            RaiseReset();
        }

        /// <summary>整体替换（只触发一次 Reset）</summary>
        public void ReplaceAll(IEnumerable<T> items)
        {
            CheckReentrancy();
            Items.Clear();
            if (items != null)
            {
                foreach (T item in items)
                {
                    Items.Add(item);
                }
            }

            RaiseReset();
        }

        /// <summary>
        /// 先摘掉指定元素再整体 Reset。
        /// 单个 Remove 也要 O(n) 搬数组 + 一次事件，报文列表按上限裁剪时用它更省。
        /// </summary>
        public void RemoveRange(IEnumerable<T> items)
        {
            if (items == null)
            {
                return;
            }

            CheckReentrancy();
            bool removed = false;
            foreach (T item in items)
            {
                removed |= Items.Remove(item);
            }

            if (removed)
            {
                RaiseReset();
            }
        }

        private void RaiseReset()
        {
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
