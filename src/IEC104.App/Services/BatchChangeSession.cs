using System;
using System.Collections.Generic;
using IEC104.App.ViewModels;

namespace IEC104.App.Services
{
    /// <summary>
    /// 批量变化的**应用级会话**：驱动器跟着主窗口走，不跟批量变化窗口走。
    ///
    /// 【为什么要有这一层】1.10.1 之前 <see cref="BatchChangeService"/> 是
    /// 批量变化窗口 ViewModel 的私有字段，窗口一关就 Dispose —— 用户的原话是
    /// "设置了规则和数据点后这个界面不能关掉，关掉后数据就不变化了"。
    /// 这个行为在实现上是"顺手释放"，在用户眼里就是"这个功能有毛病"：
    /// 谁会盯着一屏参数看十分钟？他要去主界面看报文、看点位表在变。
    ///
    /// 所以把所有权上提一层：
    ///   · 会话归 MainWindowViewModel，窗口只是它的一层视图；
    ///   · 关窗 = 关视图，运行状态、轮次、参数都留在会话里；
    ///   · 重新打开窗口能看到"正在跑、第几轮"，停止按钮在窗口、工具条、状态栏都有。
    ///
    /// 【仍然只有一个驱动器】同一时刻只允许一批点在变化。同时开两个窗口各跑一套，
    /// 状态栏和工具条就没法说清"现在到底在变什么"，而现场也不需要——他压的是同一条链路。
    /// 重新点「开始变化」就是用新的点位和参数**替换**掉上一批，不叠加。
    /// </summary>
    public sealed class BatchChangeSession : IDisposable
    {
        private readonly BatchChangeService _service = new();

        public BatchChangeSession()
        {
            // 每轮结束都通知一次：状态栏那行"第 N 轮"要跟着跳，
            // 否则用户看不出它到底还在不在变（这是关窗后唯一能确认的地方）。
            _service.RoundCompleted = (round, changed, elapsed) =>
            {
                LastRoundChanged = changed;
                LastRoundElapsedMs = elapsed;
                StateChanged?.Invoke();
            };
        }

        /// <summary>启动 / 停止 / 每轮结束都会触发，视图据此刷新提示</summary>
        public event Action StateChanged;

        public bool IsRunning => _service.IsRunning;

        public long RoundCount => _service.RoundCount;

        public long ChangedCount => _service.ChangedCount;

        /// <summary>上一轮实际变化的点数</summary>
        public int LastRoundChanged { get; private set; }

        /// <summary>上一轮耗时（毫秒）</summary>
        public double LastRoundElapsedMs { get; private set; }

        /// <summary>当前节拍（毫秒），停止后仍保留，便于窗口重开时回填</summary>
        public int IntervalMs { get; private set; }

        /// <summary>当前每轮点数</summary>
        public int BatchSize { get; private set; }

        /// <summary>是否每轮全部变化</summary>
        public bool AllPoints { get; private set; }

        /// <summary>参与变化的点位数</summary>
        public int TargetCount { get; private set; }

        /// <summary>这批点位来自哪个从站（停止提示里带上，避免"不知道停的是谁"）</summary>
        public string TargetSlaveName { get; private set; } = string.Empty;

        public void Start(
            IReadOnlyList<PointViewModel> targets,
            BatchChangeSettings settings,
            string slaveName = null)
        {
            // 异常直接抛给调用方（窗口里会显示在状态行），这里不吞
            _service.Start(targets, settings);

            IntervalMs = settings.IntervalMs;
            BatchSize = settings.BatchSize;
            AllPoints = settings.AllPoints;
            TargetCount = targets?.Count ?? 0;
            LastRoundChanged = 0;
            LastRoundElapsedMs = 0;

            if (!string.IsNullOrWhiteSpace(slaveName))
            {
                TargetSlaveName = slaveName;
            }

            StateChanged?.Invoke();
        }

        public void Stop()
        {
            if (!_service.IsRunning)
            {
                return;
            }

            _service.Stop();
            StateChanged?.Invoke();
        }

        public void Dispose()
        {
            _service.Dispose();
            StateChanged = null;
        }
    }
}
