using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace IEC104.App.Infrastructure
{
    /// <summary>
    /// MVVM 的 ViewModel 基类。
    ///
    /// 复刻说明：原程序有同名基类 IEC104ServerSimulator.ViewModels.ViewModelBase，
    /// 职责一致（实现 INotifyPropertyChanged）。此处用标准实现重写。
    /// </summary>
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 赋值并在值真正变化时触发通知。
        /// 只比较不相等才通知，避免界面无谓刷新（数据点高频刷新时这点很重要）。
        /// </summary>
        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
