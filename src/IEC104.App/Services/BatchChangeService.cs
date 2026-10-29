using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using IEC104.App.ViewModels;
using IEC104.Core.CS101;

namespace IEC104.App.Services
{
    /// <summary>
    /// 批量变化参数。
    ///
    /// 现场最典型的用法：接上主站后想验证"几百个遥测每秒刷一遍"时
    /// 主站能不能扛住——那就 间隔 1000ms + 每轮 200 个点 + 随机范围。
    /// </summary>
    public class BatchChangeSettings
    {
        /// <summary>变化模式：随机范围 / 固定步长 / 开关翻转</summary>
        public ChangePattern Pattern { get; set; } = ChangePattern.RandomInRange;

        /// <summary>随机范围下限（也是固定步长回绕的下限）</summary>
        public double Min { get; set; }

        /// <summary>随机范围上限（也是固定步长回绕的上限）</summary>
        public double Max { get; set; } = 100;

        /// <summary>固定步长的每次增量</summary>
        public double Step { get; set; } = 1;

        /// <summary>节拍间隔（毫秒）</summary>
        public int IntervalMs { get; set; } = 1000;

        /// <summary>每轮变化的点数。"1 秒变化 200 个点"就是 1000 + 200</summary>
        public int BatchSize { get; set; } = 200;

        /// <summary>true = 每轮所有选中点都变化（忽略 BatchSize）</summary>
        public bool AllPoints { get; set; }
    }

    /// <summary>
    /// 批量变化驱动器。
    ///
    /// 与 <see cref="AutoChangeService"/>（逐点缓变）的分工：
    ///   - 逐点缓变：每个点按自己的函数持续变化，适合"模拟真实曲线"
    ///   - 批量变化：每轮从一批点里挑 N 个改值，适合"压一压主站的接收能力"
    /// 两者互不干扰，可以同时开在不同的点上。
    ///
    /// 为什么"每轮只挑 N 个"而不是全部：主站侧每秒处理几千条变位报文
    /// 不现实，而且 104 通道有 k 窗口（默认 12）限流，全量刷会让链路一直
    /// 处于"窗口打满等确认"的状态。所以默认按点数稀释。
    /// </summary>
    public sealed class BatchChangeService : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly Random _random = new();

        private IReadOnlyList<PointViewModel> _targets = Array.Empty<PointViewModel>();
        private BatchChangeSettings _settings = new();

        /// <summary>用于无重复抽取的索引缓冲，避免每轮分配</summary>
        private int[] _indexBuffer = Array.Empty<int>();

        public BatchChangeService()
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            _timer.Tick += (_, _) => Tick();
        }

        public bool IsRunning => _timer.IsEnabled;

        /// <summary>已执行轮数</summary>
        public long RoundCount { get; private set; }

        /// <summary>已改值的点数累计</summary>
        public long ChangedCount { get; private set; }

        /// <summary>每一轮结束回调（轮数、本轮点数、本轮耗时毫秒）</summary>
        public Action<long, int, double> RoundCompleted { get; set; }

        public void Start(IReadOnlyList<PointViewModel> targets, BatchChangeSettings settings)
        {
            if (targets == null || targets.Count == 0)
            {
                throw new InvalidOperationException("没有勾选任何点位。");
            }
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            if (settings.Max < settings.Min)
            {
                throw new InvalidOperationException($"上限({settings.Max})不能小于下限({settings.Min})。");
            }

            _targets = targets;
            _settings = settings;
            RoundCount = 0;
            ChangedCount = 0;

            _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(settings.IntervalMs, 100, 60000));
            _timer.Start();
        }

        public void Stop() => _timer.Stop();

        private void Tick()
        {
            if (_targets.Count == 0)
            {
                return;
            }

            DateTime startedAt = DateTime.Now;
            int changed = 0;

            foreach (PointViewModel point in PickTargets())
            {
                if (Apply(point))
                {
                    changed++;
                }
            }

            RoundCount++;
            ChangedCount += changed;
            RoundCompleted?.Invoke(RoundCount, changed, (DateTime.Now - startedAt).TotalMilliseconds);
        }

        /// <summary>
        /// 选出本轮要改的点。
        /// 全量模式直接返回；否则用"部分洗牌"（Fisher-Yates 只洗前 N 个），
        /// 复杂度 O(N) 而不是整体 O(n)，点数上千时差别明显。
        /// </summary>
        private IEnumerable<PointViewModel> PickTargets()
        {
            if (_settings.AllPoints || _settings.BatchSize >= _targets.Count)
            {
                return _targets;
            }

            int count = Math.Max(1, _settings.BatchSize);
            if (_indexBuffer.Length != _targets.Count)
            {
                _indexBuffer = Enumerable.Range(0, _targets.Count).ToArray();
            }

            for (int i = 0; i < count; i++)
            {
                int j = _random.Next(i, _indexBuffer.Length);
                (_indexBuffer[i], _indexBuffer[j]) = (_indexBuffer[j], _indexBuffer[i]);
            }

            return _indexBuffer.Take(count).Select(index => _targets[index]).ToList();
        }

        /// <summary>给一个点算新值并写回；返回值是否真的变了</summary>
        private bool Apply(PointViewModel point)
        {
            TypeIdentifier? type = point.Type?.Type;
            double current = point.Value;
            double next;

            // 开关类点位：无论选哪种模式，最终都只能是几个合法状态
            if (IsSwitchType(type))
            {
                next = FlipSwitch(type, current);
            }
            else
            {
                switch (_settings.Pattern)
                {
                    case ChangePattern.RandomInRange:
                        next = _settings.Min + _random.NextDouble() * (_settings.Max - _settings.Min);
                        break;

                    case ChangePattern.FixedStep:
                        next = current + _settings.Step;
                        // 超过上限回绕到下限，避免无限增大导致主站显示越界
                        if (_settings.Step >= 0 && next > _settings.Max && _settings.Max > _settings.Min)
                        {
                            next = _settings.Min;
                        }
                        else if (_settings.Step < 0 && next < _settings.Min && _settings.Max > _settings.Min)
                        {
                            next = _settings.Max;
                        }
                        break;

                    case ChangePattern.Toggle:
                        // 模拟量选"翻转"没有意义，退化成在上下限之间跳
                        next = Math.Abs(current - _settings.Max) < 1e-9 ? _settings.Min : _settings.Max;
                        break;

                    default:
                        return false;
                }
            }

            next = RoundForType(type, next);
            if (Math.Abs(next - current) < 1e-9)
            {
                return false;
            }

            point.Value = next;
            return true;
        }

        /// <summary>开关量翻转：单点 0/1，双点 1/2</summary>
        private static double FlipSwitch(TypeIdentifier? type, double current)
        {
            if (IsDoublePoint(type))
            {
                return current >= 2 ? 1 : 2;
            }
            return current > 0.5 ? 0 : 1;
        }

        private static bool IsSwitchType(TypeIdentifier? type) =>
            type is TypeIdentifier.M_SP_NA_1 or TypeIdentifier.M_SP_TA_1 or TypeIdentifier.M_SP_TB_1
                or TypeIdentifier.M_DP_NA_1 or TypeIdentifier.M_DP_TA_1 or TypeIdentifier.M_DP_TB_1;

        private static bool IsDoublePoint(TypeIdentifier? type) =>
            type is TypeIdentifier.M_DP_NA_1 or TypeIdentifier.M_DP_TA_1 or TypeIdentifier.M_DP_TB_1;

        /// <summary>按类型归一化：值必须落在该 ASDU 类型允许的范围内</summary>
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
                    return value > 0.5 ? 1 : 0;

                case TypeIdentifier.M_DP_NA_1:
                case TypeIdentifier.M_DP_TA_1:
                case TypeIdentifier.M_DP_TB_1:
                    return Math.Clamp(Math.Round(value), 1, 2);

                case TypeIdentifier.M_ST_NA_1:
                case TypeIdentifier.M_ST_TA_1:
                case TypeIdentifier.M_ST_TB_1:
                    return Math.Clamp(Math.Round(value), -64, 63);

                case TypeIdentifier.M_IT_NA_1:
                case TypeIdentifier.M_IT_TA_1:
                case TypeIdentifier.M_IT_TB_1:
                    return Math.Round(value);

                default:
                    return Math.Round(value, 3);
            }
        }

        public void Dispose()
        {
            _timer.Stop();
        }
    }
}
