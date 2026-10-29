using System;
using System.ComponentModel;
using Avalonia.Media;
using IEC104.App.Infrastructure;
using IEC104.App.Services;

namespace IEC104.App.ViewModels
{
    /// <summary>
    /// 侧边栏"从站列表"（扁平视图）里的一行。
    ///
    /// 【为什么需要这一层包装，而不是直接把 SlaveViewModel 塞进列表】
    /// 用户要的两件事天生不在从站自己身上：
    ///   ① 标签上要显示 IP 和端口 —— 这两个字段属于 ConnectionViewModel（一路 TCP 监听）；
    ///   ② 标签颜色要随"监听中 / 主站已接入"变 —— 即通道的三态，也不属于从站。
    /// 扁平列表又不能只绑 SlaveViewModel（拿不到 ①②），也不该把 IP/端口复制一份到
    /// SlaveViewModel（两份真相，改一处不同步一处）——所以就用一行"从站 + 它的通道"。
    ///
    /// 这一层只做**转发 + 改名**：所有事实仍然存在原来的对象里，
    /// 这里订阅两边的 PropertyChanged 再往上抛，界面就能跟着变。
    /// 换句话说：删掉这个类，通道和从站也照常工作。
    ///
    /// 归属通道不叫 Owner 而叫 Channel：侧栏里"通道"是用户能看见的词，
    /// Owner 会让人以为是"从站的所有者"这种没出现在界面上的概念。
    /// </summary>
    public sealed class SlaveRowViewModel : ViewModelBase, IDisposable
    {
        public SlaveRowViewModel(ConnectionViewModel channel, SlaveViewModel slave)
        {
            Channel = channel ?? throw new ArgumentNullException(nameof(channel));
            Slave = slave ?? throw new ArgumentNullException(nameof(slave));

            Channel.PropertyChanged += OnChannelPropertyChanged;
            Slave.PropertyChanged += OnSlavePropertyChanged;
        }

        /// <summary>所属通道（监听 IP / 端口 / 状态都从这儿取）</summary>
        public ConnectionViewModel Channel { get; }

        /// <summary>从站本身</summary>
        public SlaveViewModel Slave { get; }

        // ── 名称与地址 ──────────────────────────────────────────

        /// <summary>第一行：从站名 + 公共地址（与原树形列表完全一致的说法）</summary>
        public string DisplayText => Slave.DisplayText;

        /// <summary>第二行左侧：监听端点，如 "0.0.0.0:2404"</summary>
        public string EndPointText => Channel.EndPointText;

        /// <summary>第二行右侧：点表规模</summary>
        public string PointCountText => $"{Slave.PointCount} 个信息对象";

        // ── 状态（三态，与通道状态灯同一套语义）────────────────

        public ConnectionViewModel.ChannelState State => Channel.State;

        /// <summary>完整状态说法（悬停提示用）</summary>
        public string StateText => Channel.StateText;

        /// <summary>标签右上角那颗小胶囊里的短词</summary>
        public string StateShortText => State switch
        {
            ConnectionViewModel.ChannelState.Connected => "已连接",
            ConnectionViewModel.ChannelState.Listening => "监听中",
            _ => "已停止"
        };

        /// <summary>悬停提示：把"这台装置怎么才能通"一次说清</summary>
        public string ToolTipText => $"{Slave.DisplayText}\n{Channel.Name} · {Channel.EndPointText}\n{Channel.StateText}"
            + "\n\n双击可改 IP / 端口 / 公共地址";

        // ── 配色（跟着主题走，换主题后 MainWindowViewModel 会重发通知）──

        public IBrush StateBrush => Channel.StateBrush;

        public IBrush StateHaloBrush => Channel.StateHaloBrush;

        /// <summary>标签底色（浅）</summary>
        public IBrush TagBackground => State switch
        {
            ConnectionViewModel.ChannelState.Connected => ThemePalette.StateConnectedSoft,
            ConnectionViewModel.ChannelState.Listening => ThemePalette.StateListeningSoft,
            _ => ThemePalette.StateStoppedSoft
        };

        /// <summary>标签描边（深，与底色成对）</summary>
        public IBrush TagBorder => State switch
        {
            ConnectionViewModel.ChannelState.Connected => ThemePalette.StateConnected,
            ConnectionViewModel.ChannelState.Listening => ThemePalette.StateListening,
            _ => ThemePalette.StateStopped
        };

        /// <summary>标签上的强调文字/胶囊文字色（深，与底色成对）</summary>
        public IBrush TagForeground => State switch
        {
            ConnectionViewModel.ChannelState.Connected => ThemePalette.StateConnectedText,
            ConnectionViewModel.ChannelState.Listening => ThemePalette.StateListeningText,
            _ => ThemePalette.StateStoppedText
        };

        // ── 变更转发 ────────────────────────────────────────────

        private void OnChannelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ConnectionViewModel.State):
                case nameof(ConnectionViewModel.StateBrush):
                case nameof(ConnectionViewModel.StateHaloBrush):
                case nameof(ConnectionViewModel.StateText):
                case nameof(ConnectionViewModel.ClientCount):
                case nameof(ConnectionViewModel.IsOpen):
                    RaiseVisuals();
                    break;

                case nameof(ConnectionViewModel.EndPointText):
                case nameof(ConnectionViewModel.Port):
                case nameof(ConnectionViewModel.BindAddress):
                    OnPropertyChanged(nameof(EndPointText));
                    OnPropertyChanged(nameof(ToolTipText));
                    break;

                case nameof(ConnectionViewModel.Name):
                    OnPropertyChanged(nameof(ToolTipText));
                    break;
            }
        }

        private void OnSlavePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(SlaveViewModel.Name):
                case nameof(SlaveViewModel.CommonAddress):
                    OnPropertyChanged(nameof(DisplayText));
                    OnPropertyChanged(nameof(ToolTipText));
                    break;

                case nameof(SlaveViewModel.PointCount):
                    OnPropertyChanged(nameof(PointCountText));
                    break;
            }
        }

        /// <summary>
        /// 状态相关的显示全刷一遍。
        /// 通道那边是 State/ClientCount/IsOpen 三个属性分别变化、RaiseStateVisuals 一次报四个名字，
        /// 这里只做"收到任一个就全刷"，省得维护一张精确的映射表（行数量级只有几十上百行）。
        /// </summary>
        public void RaiseVisuals()
        {
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(StateShortText));
            OnPropertyChanged(nameof(StateBrush));
            OnPropertyChanged(nameof(StateHaloBrush));
            OnPropertyChanged(nameof(TagBackground));
            OnPropertyChanged(nameof(TagBorder));
            OnPropertyChanged(nameof(TagForeground));
        }

        public void Dispose()
        {
            Channel.PropertyChanged -= OnChannelPropertyChanged;
            Slave.PropertyChanged -= OnSlavePropertyChanged;
        }

        public override string ToString() => $"{DisplayText} @ {EndPointText}";
    }
}
