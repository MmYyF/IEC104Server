using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using IEC104.App.ViewModels;
using IEC104.Core.CS101;

namespace IEC104.App.Services
{
    /// <summary>
    /// 点位自动变化驱动器。
    ///
    /// 复刻说明：对应原程序 MainViewModel 里挂的缓变定时器，
    /// 以及 SetChangeInterval 命令设置的“变化间隔”。
    ///
    /// 工作方式：一个 DispatcherTimer 按 interval 打节拍，每个节拍里遍历所有
    /// 配了变化模式的点位，按模式算出新值写回。写值走
    /// PointViewModel.Value → DataPoint.SetValue → 从站自发上送（COT=3），
    /// 所以主站侧看到的就是真实的变化报文，和手改值完全等价。
    ///
    /// 为什么要节流：104 通道 k=12 意味着未确认的 I 帧最多 12 帧，
    /// 每秒几十次的变位上送会把窗口打满、导致链路反复阻塞。
    /// 因此这里默认 1 秒一个节拍，且按点位各自的变化周期再稀释。
    /// </summary>
    public sealed class AutoChangeService : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly Random _random = new();

        /// <summary>点位 → 上次变化时刻</summary>
        private readonly Dictionary<PointViewModel, DateTime> _lastTick = new();

        /// <summary>由 MainWindowViewModel 提供当前全部点位</summary>
        private Func<IEnumerable<SlaveViewModel>> _slavesProvider;

        public AutoChangeService()
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IntervalMs) };
            _timer.Tick += (_, _) => Tick();
        }

        /// <summary>全局节拍（毫秒），对应原版“变化间隔”设置</summary>
        public int IntervalMs { get; private set; } = 1000;

        public bool IsRunning => _timer.IsEnabled;

        public void Attach(Func<IEnumerable<SlaveViewModel>> slavesProvider)
        {
            _slavesProvider = slavesProvider;
        }

        public void Start()
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }

        public void Stop()
        {
            if (_timer.IsEnabled)
            {
                _timer.Stop();
            }
        }

        /// <summary>调整节拍；运行中会立即生效</summary>
        public void SetInterval(int milliseconds)
        {
            IntervalMs = Math.Clamp(milliseconds, 100, 60000);
            _timer.Interval = TimeSpan.FromMilliseconds(IntervalMs);
        }

        /// <summary>把某个点位的变化状态清零（停止参与时调用）</summary>
        public void Reset(PointViewModel point)
        {
            if (point != null)
            {
                _lastTick.Remove(point);
            }
        }

        /// <summary>
        /// 立刻按一拍执行（不走定时器）。
        /// 用途只有一个：规模压测里要"确定性地跑 5 拍并计时"，
        /// 靠等 DispatcherTimer 触发既不准也会被别的消息插队。
        /// </summary>
        internal void TickNow() => Tick();

        private void Tick()
        {
            if (_slavesProvider == null)
            {
                return;
            }

            DateTime now = DateTime.Now;
            foreach (SlaveViewModel slave in _slavesProvider())
            {
                // 不复制一份 .ToList()：原来每拍都要把 20 万个点位引用搬进新 List，
                // 一秒一拍就是 1.6 MB/秒 的纯垃圾。这里只改点位自己的值、
                // 不增删集合元素，直接遍历是安全的。
                foreach (PointViewModel point in slave.Points)
                {
                    if (point.ChangePattern == ChangePattern.None)
                    {
                        continue;
                    }

                    if (_lastTick.TryGetValue(point, out DateTime last)
                        && (now - last).TotalMilliseconds < IntervalMs)
                    {
                        continue;
                    }
                    _lastTick[point] = now;
                    Apply(point);
                }
            }
        }

        /// <summary>按模式算出一个新值并写回点位</summary>
        private void Apply(PointViewModel point)
        {
            double current = point.Value;
            double amplitude = point.ChangeAmplitude;
            double next;

            switch (point.ChangePattern)
            {
                case ChangePattern.Sine:
                    // 相位步进 36°：一个完整周期约 10 个节拍
                    point.SinePhase += Math.PI / 5;
                    if (point.SinePhase > Math.PI * 2)
                    {
                        point.SinePhase -= Math.PI * 2;
                    }
                    next = point.BaselineValue + amplitude * Math.Sin(point.SinePhase);
                    break;

                case ChangePattern.RandomWalk:
                    next = current + (_random.NextDouble() * 2 - 1) * amplitude;
                    // 不让它无限漂走：相对基准值限幅到 ±10 倍幅度
                    next = Math.Clamp(next,
                        point.BaselineValue - amplitude * 10,
                        point.BaselineValue + amplitude * 10);
                    break;

                case ChangePattern.Toggle:
                    next = IsBooleanType(point.Type?.Type) && current <= 0.5 ? 1 : 0;
                    // 双点遥信要取 0/1/2，避免出现非法值 3
                    if (IsDoublePointType(point.Type?.Type))
                    {
                        next = current >= 1 ? 1 : 2;
                    }
                    break;

                case ChangePattern.Increment:
                    next = current + Math.Abs(amplitude);
                    break;

                default:
                    return;
            }

            point.Value = RoundForType(point.Type?.Type, next);
        }

        /// <summary>按 ASDU 类型做合理取整：遥信/档位只能是整数，电度用整数累计</summary>
        private static double RoundForType(TypeIdentifier? type, double value)
        {
            if (type == null)
            {
                return value;
            }

            switch (type.Value)
            {
                case TypeIdentifier.M_SP_NA_1:
                case TypeIdentifier.M_SP_TA_1:
                case TypeIdentifier.M_SP_TB_1:
                    return value > 0 ? 1 : 0;

                case TypeIdentifier.M_DP_NA_1:
                case TypeIdentifier.M_DP_TA_1:
                case TypeIdentifier.M_DP_TB_1:
                    return Math.Round(value);

                case TypeIdentifier.M_ST_NA_1:
                case TypeIdentifier.M_ST_TA_1:
                case TypeIdentifier.M_ST_TB_1:
                    // 档位信息在 104 里是 -64..63 的整数
                    return Math.Clamp(Math.Round(value), -64, 63);

                case TypeIdentifier.M_IT_NA_1:
                case TypeIdentifier.M_IT_TA_1:
                case TypeIdentifier.M_IT_TB_1:
                    return Math.Round(value);

                default:
                    return Math.Round(value, 3);
            }
        }

        private static bool IsBooleanType(TypeIdentifier? type) =>
            type is TypeIdentifier.M_SP_NA_1
                or TypeIdentifier.M_SP_TA_1
                or TypeIdentifier.M_SP_TB_1;

        private static bool IsDoublePointType(TypeIdentifier? type) =>
            type is TypeIdentifier.M_DP_NA_1
                or TypeIdentifier.M_DP_TA_1
                or TypeIdentifier.M_DP_TB_1;

        public void Dispose()
        {
            _timer.Stop();
            _lastTick.Clear();
        }
    }
}
