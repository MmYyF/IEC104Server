using System;
using System.Diagnostics;
using Avalonia.Threading;

namespace IEC104.App.Services
{
    /// <summary>
    /// 保存防抖调度器：一堆变化 → 停下来 N 毫秒之后才真正写一次盘。
    ///
    /// 为什么不是"每次变化都写"：仿真过程中信号值每秒可能变几十次，
    /// 40 万点一次写盘 200 ms 以上，边跑边写等于把界面拖死。
    /// 而"变化过程中一次都不写"又是可以的——用户要看的是停下来那一刻的值。
    ///
    /// 三个关键设计（都是踩过的）：
    ///
    /// 1. **Bump() 不做 Stop/Start**。批量变化一次改上万点，SetValue 会调上万次；
    ///    每次都重置计时器的话，计时器本身就成了新的卡顿源。这里 Bump() 只写一个
    ///    时间戳 + 一个 if，计时器**一直跑着**，到期时自己去比时间戳。
    ///
    /// 2. **计时器不因为"还没到点"而重启**。一拍没到就原样跳过，等下一拍。
    ///    这样 1 秒变一次 + 延迟 5 秒时，计时器永远等不到"静下来 5 秒"，全程零写盘。
    ///
    /// 3. **一道最小间隔闸门**。防的是"用户连续微调十几个值，每次停手 5 秒就写 200 ms"，
    ///    手感会碎。距上次写盘不足 M 毫秒时先跳过，下一拍照样会到期，最多晚一个 M。
    ///
    /// 计时器在 UI 线程上跑，回调也在 UI 线程——写盘是同步的（见 ProjectStore 的流式写）。
    /// 这样"写完就是写完"这件事没有歧义，也不需要为"写盘时用户又改了值"做快照。
    /// </summary>
    public sealed class SaveScheduler
    {
        /// <summary>
        /// 计时器节拍。100 ms 是"比任何延迟都细、又不至于 1 tick 就空转"的折中：
        /// 用户能选的延迟最小 3 秒，100 ms 的量化误差看不出来；
        /// 而每一拍只做两次时间戳比较，40 万点跑起来也压不出噪音。
        /// </summary>
        private const int TickMilliseconds = 100;

        private readonly DispatcherTimer _timer;
        private readonly Func<int> _delayMs;
        private readonly Func<int> _minGapMs;
        private readonly Func<bool> _onIdle;

        /// <summary>最后一次"有事发生"的时刻（Stopwatch 时间戳，0 表示没有待写的改动）</summary>
        private long _lastTouched;

        /// <summary>上一次真正写盘成功的时刻（0 表示本进程还没写过）</summary>
        private long _lastWrite;

        public SaveScheduler(string name, Func<int> delayMs, Func<int> minGapMs, Func<bool> onIdle)
        {
            Name = name;
            _delayMs = delayMs;
            _minGapMs = minGapMs;
            _onIdle = onIdle;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMilliseconds) };
            _timer.Tick += (_, _) => Pump();
        }

        /// <summary>调度器名字（structure / value），只用于自检日志和排查</summary>
        public string Name { get; }

        /// <summary>计时器是否在跑（自检用：验"没工程时不空转"）</summary>
        public bool IsRunning => _timer.IsEnabled;

        /// <summary>本调度器实际写盘次数（自检用：验"变化过程中零写盘"）</summary>
        public int WriteCount { get; private set; }

        /// <summary>有过变化但还没写盘（自检用）</summary>
        public bool IsPending => _lastTouched != 0;

        /// <summary>
        /// 记一次"有事发生"。值变化每点都会调到这里，所以必须极轻：
        /// 一次时间戳赋值 + 一个 if。
        /// </summary>
        public void Bump()
        {
            _lastTouched = Stopwatch.GetTimestamp();
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }

        /// <summary>
        /// 已经写过盘了（手动保存、自动保存、打开/新建工程都算）：
        /// 清掉待写标记、把闸门的基准挪到现在、停表。
        /// </summary>
        public void MarkWritten()
        {
            _lastWrite = Stopwatch.GetTimestamp();
            _lastTouched = 0;
            _timer.Stop();
        }

        /// <summary>直接停表（关闭工程、切换工程时用：手上的改动已经不属于新工程了）</summary>
        public void Stop()
        {
            _lastTouched = 0;
            _timer.Stop();
        }

        /// <summary>一拍：到点了就写，没到点就等下一拍</summary>
        private void Pump()
        {
            if (_lastTouched == 0)
            {
                _timer.Stop();
                return;
            }

            if (ElapsedMilliseconds(_lastTouched) < _delayMs())
            {
                return;
            }

            // 闸门：刚写过盘就再等一会儿。这里**不**清 _lastTouched——
            // 说明改动还欠着，下一拍到点还会进来。
            if (_lastWrite != 0 && ElapsedMilliseconds(_lastWrite) < _minGapMs())
            {
                return;
            }

            _timer.Stop();
            _lastTouched = 0;

            try
            {
                if (_onIdle())
                {
                    _lastWrite = Stopwatch.GetTimestamp();
                    WriteCount++;
                }
            }
            catch
            {
                // 写盘失败（磁盘满、只读、文件被占用）：不在这里吞掉原因，
                // 也不能自动重试——每 5 秒往一个写不进去的磁盘上打一次，
                // 只会让界面每隔几秒卡一下。回调自己负责把原因写到状态栏，
                // 用户下一次手动保存会重新试。
                _timer.Stop();
            }
        }

        private static double ElapsedMilliseconds(long since) =>
            (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;
    }
}
