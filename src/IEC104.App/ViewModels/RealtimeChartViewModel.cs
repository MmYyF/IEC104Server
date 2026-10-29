using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using IEC104.App.Controls;
using IEC104.App.Infrastructure;

namespace IEC104.App.ViewModels
{
    /// <summary>曲线勾选项：一个点位 + 是否绘制 + 自己的历史缓冲</summary>
    public class ChartPointItem : ViewModelBase
    {
        private bool _isChecked;

        public ChartPointItem(PointViewModel point, Color color)
        {
            Point = point;
            Color = color;
        }

        public PointViewModel Point { get; }

        public Color Color { get; }

        /// <summary>同色画刷（界面绑 Background 用，省得写值转换器）</summary>
        public IBrush Brush => new SolidColorBrush(Color);

        /// <summary>历史采样值（环形丢弃，只保留最近 MaxSamples 个）</summary>
        public List<double> History { get; } = new();

        public const int MaxSamples = 120;

        public string DisplayText => $"{Point.ObjectAddress}  {Point.Name}";

        public bool IsChecked
        {
            get => _isChecked;
            set => SetField(ref _isChecked, value);
        }

        public void Append(double value)
        {
            History.Add(value);
            if (History.Count > MaxSamples)
            {
                History.RemoveAt(0);
            }
        }

        public void Clear() => History.Clear();
    }

    /// <summary>
    /// 实时曲线窗口 ViewModel。
    ///
    /// 复刻说明：对应原程序 RealtimeChartingWindow（原版用 ScottPlot 绘制）。
    /// 这里按固定节拍把选中点位的当前值采样进环形缓冲，再由 LineChart 自绘，
    /// 结论一致：能看出遥测量的变化趋势、能对照缓变的配置是否生效。
    /// </summary>
    public class RealtimeChartViewModel : ViewModelBase
    {
        /// <summary>曲线配色（避开红绿，避免和电力“涨红跌绿”习惯混淆）</summary>
        private static readonly Color[] Palette =
        {
            Color.Parse("#FF5A1F"),
            Color.Parse("#2F80ED"),
            Color.Parse("#7B61FF"),
            Color.Parse("#00A3A3"),
            Color.Parse("#9C6ADE"),
            Color.Parse("#B8860B")
        };

        private readonly DispatcherTimer _timer;
        private bool _isRunning;
        private int _intervalMs = 1000;
        private IReadOnlyList<ChartSeries> _series = Array.Empty<ChartSeries>();

        public RealtimeChartViewModel(IEnumerable<PointViewModel> points)
        {
            Items = new ObservableCollection<ChartPointItem>();
            int index = 0;
            foreach (PointViewModel point in points ?? Enumerable.Empty<PointViewModel>())
            {
                Items.Add(new ChartPointItem(point, Palette[index % Palette.Length])
                {
                    // 默认勾选前几个，打开窗口就能看到曲线，不用先去打勾
                    IsChecked = index < 3
                });
                index++;
            }

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_intervalMs) };
            _timer.Tick += (_, _) => Sample();
        }

        public ObservableCollection<ChartPointItem> Items { get; }

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (SetField(ref _isRunning, value))
                {
                    OnPropertyChanged(nameof(StartButtonText));
                }
            }
        }

        public string StartButtonText => _isRunning ? "暂停采样" : "开始采样";

        /// <summary>采样间隔（毫秒）</summary>
        public int IntervalMs
        {
            get => _intervalMs;
            set
            {
                int clamped = Math.Clamp(value, 100, 10000);
                if (SetField(ref _intervalMs, clamped))
                {
                    _timer.Interval = TimeSpan.FromMilliseconds(clamped);
                }
            }
        }

        /// <summary>交给 LineChart 绘制的数据</summary>
        public IReadOnlyList<ChartSeries> Series
        {
            get => _series;
            private set => SetField(ref _series, value);
        }

        public string StatusText =>
            $"{(IsRunning ? "采集中" : "已暂停")} · 间隔 {_intervalMs} ms · " +
            $"已选 {Items.Count(i => i.IsChecked)} 条曲线 · 每条保留最近 {ChartPointItem.MaxSamples} 个采样点";

        public bool HasSelection => Items.Any(i => i.IsChecked);

        public void Toggle()
        {
            if (_isRunning)
            {
                _timer.Stop();
                IsRunning = false;
            }
            else
            {
                // 没勾任何点位就别空转
                if (!HasSelection)
                {
                    return;
                }
                _timer.Start();
                IsRunning = true;
            }
            OnPropertyChanged(nameof(StatusText));
        }

        public void ClearHistory()
        {
            foreach (ChartPointItem item in Items)
            {
                item.Clear();
            }
            Series = Array.Empty<ChartSeries>();
            OnPropertyChanged(nameof(StatusText));
        }

        /// <summary>勾选变化后立即重绘（不必等到下一个采样点）</summary>
        public void RefreshSeries()
        {
            List<ChartSeries> list = new();
            foreach (ChartPointItem item in Items.Where(i => i.IsChecked))
            {
                list.Add(new ChartSeries(item.DisplayText, item.Color, item.History.ToArray()));
            }
            Series = list;
            OnPropertyChanged(nameof(StatusText));
        }

        /// <summary>立即采样一次（供手动刷新/自检填充数据用）</summary>
        public void SampleOnce() => Sample();

        private void Sample()
        {
            foreach (ChartPointItem item in Items)
            {
                if (!item.IsChecked)
                {
                    continue;
                }
                item.Append(item.Point.Value);
            }
            RefreshSeries();
        }

        public void Stop()
        {
            _timer.Stop();
            IsRunning = false;
        }
    }
}
