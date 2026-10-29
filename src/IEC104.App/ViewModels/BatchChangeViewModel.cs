using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using IEC104.App.Infrastructure;
using IEC104.App.Services;
using IEC104.Core.CS101;

namespace IEC104.App.ViewModels
{
    /// <summary>批量变化窗口里的一个点位条目</summary>
    public class BatchPointItem : ViewModelBase
    {
        private bool _isChecked = true;

        public BatchPointItem(PointViewModel point)
        {
            Point = point;
        }

        public PointViewModel Point { get; }

        public bool IsChecked
        {
            get => _isChecked;
            set => SetField(ref _isChecked, value);
        }

        /// <summary>是否开关量（遥信/双点）</summary>
        public bool IsSwitch => IsSwitchType(Point.Type?.Type);

        /// <summary>是否模拟量/电度（遥测）</summary>
        public bool IsMeasured => !IsSwitch;

        internal static bool IsSwitchType(TypeIdentifier? type) =>
            type is TypeIdentifier.M_SP_NA_1 or TypeIdentifier.M_SP_TA_1 or TypeIdentifier.M_SP_TB_1
                or TypeIdentifier.M_DP_NA_1 or TypeIdentifier.M_DP_TA_1 or TypeIdentifier.M_DP_TB_1;
    }

    /// <summary>
    /// 批量变化窗口 ViewModel。
    ///
    /// 需求场景（用户原话）：
    ///   "批量变化数据点，可以指定随机范围，或者固定步长，还能自动变化，
    ///    比如我想批量 1 秒变化 200 个数据点，遥测遥信都要可以。"
    ///
    /// 对应到参数就是：间隔 1000ms + 每轮 200 个点 + 随机范围/固定步长。
    /// 开关量（遥信）无论选哪种模式都按"翻转"处理——它只有 0/1 两种合法状态，
    /// 在 0~100 之间随机取值是没有意义的。
    /// </summary>
    public class BatchChangeViewModel : ViewModelBase, IDisposable
    {
        /// <summary>每个窗口一个独立驱动器，互不干扰；窗口关闭即释放</summary>
        private readonly BatchChangeService _service = new();

        private ChangePattern _pattern = ChangePattern.RandomInRange;
        private double _min = 0;
        private double _max = 100;
        private double _step = 1;
        private int _intervalMs = 1000;
        private int _batchSize = 200;
        private bool _allPoints;
        private bool _isRunning;
        private string _statusText = "勾选点位、设好参数后点“开始变化”。";

        public BatchChangeViewModel(IEnumerable<PointViewModel> points)
        {
            foreach (PointViewModel point in points ?? Enumerable.Empty<PointViewModel>())
            {
                Items.Add(new BatchPointItem(point));
            }

            StartCommand = new RelayCommand(Start, () => !IsRunning && Items.Any(i => i.IsChecked));
            StopCommand = new RelayCommand(Stop, () => IsRunning);

            SelectAllCommand = new RelayCommand(() => SetChecked(_ => true));
            ClearAllCommand = new RelayCommand(() => SetChecked(_ => false));
            InvertCommand = new RelayCommand(() => SetChecked(i => !i.IsChecked));
            SelectMeasuredCommand = new RelayCommand(() => SetChecked(i => i.IsMeasured));
            SelectSwitchCommand = new RelayCommand(() => SetChecked(i => i.IsSwitch));
        }

        public ObservableCollection<BatchPointItem> Items { get; } = new();

        /// <summary>可选模式：随机范围 / 固定步长 / 开关翻转</summary>
        public IReadOnlyList<ChangePattern> Patterns => ChangePatternText.BatchPatterns;

        public ChangePattern Pattern
        {
            get => _pattern;
            set
            {
                if (SetField(ref _pattern, value))
                {
                    OnPropertyChanged(nameof(IsRangeMode));
                    OnPropertyChanged(nameof(IsStepMode));
                    OnPropertyChanged(nameof(HintText));
                }
            }
        }

        /// <summary>随机范围模式：需要 上限/下限</summary>
        public bool IsRangeMode => _pattern == ChangePattern.RandomInRange;

        /// <summary>固定步长模式：需要 步长</summary>
        public bool IsStepMode => _pattern == ChangePattern.FixedStep;

        public double Min
        {
            get => _min;
            set => SetField(ref _min, value);
        }

        public double Max
        {
            get => _max;
            set => SetField(ref _max, value);
        }

        public double Step
        {
            get => _step;
            set => SetField(ref _step, value);
        }

        /// <summary>节拍间隔（毫秒）</summary>
        public int IntervalMs
        {
            get => _intervalMs;
            set => SetField(ref _intervalMs, Math.Clamp(value, 100, 60000));
        }

        /// <summary>每轮变化点数</summary>
        public int BatchSize
        {
            get => _batchSize;
            set => SetField(ref _batchSize, Math.Clamp(value, 1, 100000));
        }

        /// <summary>每轮全部变化（忽略每轮点数）</summary>
        public bool AllPoints
        {
            get => _allPoints;
            set => SetField(ref _allPoints, value);
        }

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (SetField(ref _isRunning, value))
                {
                    (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(StartButtonText));
                }
            }
        }

        public string StartButtonText => IsRunning ? "变化中…" : "开始变化";

        /// <summary>已执行轮数（自检/诊断用）</summary>
        public long Rounds => _service.RoundCount;

        /// <summary>累计改值点数（自检/诊断用）</summary>
        public long ChangedPoints => _service.ChangedCount;

        public string StatusText
        {
            get => _statusText;
            private set => SetField(ref _statusText, value);
        }

        public string HintText => _pattern switch
        {
            ChangePattern.RandomInRange =>
                "遥测在 [下限, 上限] 内随机取值；遥信/双点按随机翻转处理（只有 0/1、1/2 两种合法状态）。",
            ChangePattern.FixedStep =>
                "遥测每次加一个步长，超过上限自动回绕到下限；步长填负数则递减。遥信按翻转处理。",
            _ =>
                "每轮把选中的开关量在 合/分 之间翻转；模拟量在上限与下限之间跳变。"
        };

        /// <summary>选中数量提示</summary>
        public string SelectionText =>
            $"已勾选 {Items.Count(i => i.IsChecked)} / {Items.Count} 个点位" +
            $"（遥测 {Items.Count(i => i.IsChecked && i.IsMeasured)}，" +
            $"遥信 {Items.Count(i => i.IsChecked && i.IsSwitch)}）";

        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand SelectAllCommand { get; }
        public ICommand ClearAllCommand { get; }
        public ICommand InvertCommand { get; }
        public ICommand SelectMeasuredCommand { get; }
        public ICommand SelectSwitchCommand { get; }

        private void Start()
        {
            List<PointViewModel> targets = Items.Where(i => i.IsChecked).Select(i => i.Point).ToList();
            if (targets.Count == 0)
            {
                StatusText = "请先勾选要变化的点位。";
                return;
            }

            BatchChangeSettings settings = new()
            {
                Pattern = _pattern,
                Min = _min,
                Max = _max,
                Step = _step,
                IntervalMs = _intervalMs,
                BatchSize = _batchSize,
                AllPoints = _allPoints
            };

            try
            {
                _service.RoundCompleted = (round, changed, elapsed) =>
                {
                    StatusText =
                        $"第 {round} 轮 · 本轮变化 {changed} 个点 · 耗时 {elapsed:F1} ms · " +
                        $"累计 {_service.ChangedCount} 次 · 节拍 {_intervalMs} ms";
                };
                _service.Start(targets, settings);
                IsRunning = true;
                StatusText = $"已启动：每 {_intervalMs} ms 变化 " +
                             $"{(_allPoints ? "全部" : _batchSize.ToString())} 个点（共 {targets.Count} 个参与）。";
            }
            catch (Exception exception)
            {
                StatusText = "启动失败：" + exception.Message;
            }
        }

        private void Stop()
        {
            _service.Stop();
            IsRunning = false;
            StatusText = $"已停止 · 共执行 {_service.RoundCount} 轮，累计变化 {_service.ChangedCount} 个点值。";
        }

        private void SetChecked(Func<BatchPointItem, bool> selector)
        {
            foreach (BatchPointItem item in Items)
            {
                item.IsChecked = selector(item);
            }
            OnPropertyChanged(nameof(SelectionText));
            (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        /// <summary>勾选变化后刷新统计（界面上点勾选框时调用）</summary>
        public void RefreshSelection()
        {
            OnPropertyChanged(nameof(SelectionText));
            (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        /// <summary>窗口关闭时务必停表并释放定时器</summary>
        public void Dispose() => _service.Dispose();
    }
}
