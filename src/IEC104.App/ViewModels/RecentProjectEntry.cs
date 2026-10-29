using System;
using System.Windows.Input;
using IEC104.App.Infrastructure;

namespace IEC104.App.ViewModels
{
    /// <summary>
    /// 欢迎页「最近使用」里的一条。
    ///
    /// 为什么单独建一个类型而不是直接拿 <c>RecentProjectDto</c> 绑：
    /// 磁盘上的那条只有 路径/名字/时间，界面上还要"相对时间"和"文件还在不在"，
    /// 而后者必须**在渲染那一刻**去问磁盘（用户可能在两分钟前把文件删了）。
    /// 显示用的数据就在这一层算好，视图里不做任何判断。
    /// </summary>
    public sealed class RecentProjectEntry
    {
        public RecentProjectEntry(string path, string name, DateTime lastOpenedAt, bool exists,
            Action<RecentProjectEntry> open)
        {
            Path = path;
            Name = string.IsNullOrWhiteSpace(name)
                ? System.IO.Path.GetFileNameWithoutExtension(path)
                : name;
            LastOpenedAt = lastOpenedAt;
            Exists = exists;
            OpenCommand = new RelayCommand(() => open?.Invoke(this));
        }

        public string Path { get; }

        public string Name { get; }

        public DateTime LastOpenedAt { get; }

        /// <summary>文件此刻还在不在（不在的条目点一下会提示并自动清掉）</summary>
        public bool Exists { get; }

        /// <summary>点一下打开这个工程（文件已不存在时由 ViewModel 提示并清掉）</summary>
        public ICommand OpenCommand { get; }

        public string TimeText => FormatRelativeTime(LastOpenedAt);

        /// <summary>第二行：路径；文件没了就直接说"文件已不存在"</summary>
        public string DetailText => Exists ? Path : "文件已不存在，点一下可从列表中移除";

        public string ToolTipText => Exists ? Path : $"{Path}\n\n这个文件已经被移动或删除。";

        /// <summary>
        /// 文件已不存在时整行压暗到 55%。
        /// 为什么不在 XAML 里用转换器：这条规则只有一处用，为它引一层转换器
        /// 反而要在视图里多挂一个类型；一个只读属性就够了。
        /// </summary>
        public double RowOpacity => Exists ? 1.0 : 0.55;

        /// <summary>相对时间：今天给时刻、昨天给"昨天"、一周内给星期几以外的日期</summary>
        private static string FormatRelativeTime(DateTime time)
        {
            if (time == default)
            {
                return string.Empty;
            }

            DateTime today = DateTime.Today;
            if (time.Date == today)
            {
                return time.ToString("HH:mm");
            }

            if (time.Date == today.AddDays(-1))
            {
                return "昨天";
            }

            return time.Year == today.Year
                ? time.ToString("MM-dd")
                : time.ToString("yyyy-MM-dd");
        }
    }
}
