using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using IEC104.App.Infrastructure;
using IEC104.App.Services;

namespace IEC104.App.ViewModels
{
    /// <summary>
    /// 缓变（自动变化）配置窗口的 ViewModel。
    ///
    /// 复刻说明：对应原程序 EaseFunctionConfigWindowViewModel。
    /// 差异：原版按“缓变函数”分组配置，这里直接列点位、逐点选模式与幅度，
    /// 现场对着点表勾一遍就能让数据动起来，操作路径更短。
    /// </summary>
    public class AutoChangeViewModel : ViewModelBase
    {
        private readonly AutoChangeService _service;
        private bool _isEnabled;
        private int _intervalMs;

        public AutoChangeViewModel(AutoChangeService service, ObservableCollection<PointViewModel> points)
        {
            _service = service;
            Points = points;

            _intervalMs = service.IntervalMs;
            _isEnabled = service.IsRunning;

            ApplySameTypeCommand = new RelayCommand(ApplySameType, () => SelectedPoint != null);
            DisableAllCommand = new RelayCommand(DisableAll);
        }

        /// <summary>点表（与主窗口共用同一个集合，改这里等于改主界面）</summary>
        public ObservableCollection<PointViewModel> Points { get; }

        private PointViewModel _selectedPoint;

        public PointViewModel SelectedPoint
        {
            get => _selectedPoint;
            set
            {
                if (SetField(ref _selectedPoint, value))
                {
                    (ApplySameTypeCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>总开关</summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (!SetField(ref _isEnabled, value))
                {
                    return;
                }

                if (value)
                {
                    _service.Start();
                }
                else
                {
                    _service.Stop();
                }
                OnPropertyChanged(nameof(StatusText));
            }
        }

        /// <summary>变化间隔（毫秒）。间隔越小上送越密，链路压力越大</summary>
        public int IntervalMs
        {
            get => _intervalMs;
            set
            {
                if (SetField(ref _intervalMs, value))
                {
                    _service.SetInterval(value);
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        public string StatusText =>
            $"{(_service.IsRunning ? "运行中" : "已停止")} · 节拍 {_service.IntervalMs} ms · " +
            $"参与变化 {Points.Count(p => p.IsAutoChanging)} / {Points.Count} 个点位";

        public ICommand ApplySameTypeCommand { get; }

        public ICommand DisableAllCommand { get; }

        /// <summary>把选中点的模式与幅度套用到同类型的所有点</summary>
        private void ApplySameType()
        {
            if (SelectedPoint?.Type == null)
            {
                return;
            }

            foreach (PointViewModel point in Points.Where(p => p.Type?.Type == SelectedPoint.Type.Type))
            {
                point.ChangePattern = SelectedPoint.ChangePattern;
                point.ChangeAmplitude = SelectedPoint.ChangeAmplitude;
            }
            OnPropertyChanged(nameof(StatusText));
        }

        private void DisableAll()
        {
            foreach (PointViewModel point in Points)
            {
                point.ChangePattern = ChangePattern.None;
                _service.Reset(point);
            }
            IsEnabled = false;
            OnPropertyChanged(nameof(StatusText));
        }

        /// <summary>刷新统计（_service 状态由窗口外部改变时调用）</summary>
        public void RefreshStatus() => OnPropertyChanged(nameof(StatusText));
    }
}
