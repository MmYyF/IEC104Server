using System;
using System.Collections.Generic;
using System.Linq;
using IEC104.App.Infrastructure;
using IEC104.App.Services;

namespace IEC104.App.ViewModels
{
    /// <summary>
    /// "监听地址 + 通信网卡 + 启动时自动添加"这一组的编辑模型。
    ///
    /// 【为什么单独抽出来】这组参数在三个地方出现：新建从站、编辑从站的通道端点、批量建站。
    /// 每处复制一份 XAML 的后果是"这里少一个勾、那里少一个提示"，
    /// 而它们说的其实是同一件事（参考 LinkParametersPanel 的教训）。
    /// 所以模型一份、面板一份（Views/NicPickerPanel.axaml），三处各放一个即可。
    ///
    /// 模型只负责"描述计划"，不执行添加 —— 真正的添加发生在启动通道时。
    /// 这样对话框里不会突然弹 UAC，用户点"确定"之前系统状态一点没变。
    /// </summary>
    public sealed class NicPickerModel : ViewModelBase
    {
        private string _bindAddress = "0.0.0.0";
        private NicInfo _selectedNic;
        private bool _autoAddAddress;

        public NicPickerModel()
        {
            // 枚举一次就够：对话框活几十秒，没必要每次输入都去问一遍系统
            Nics = IpAddressService.Shared.ListNics();

            // 默认带出设置里的默认通信网卡，再退到第一块已连接的物理网卡
            _selectedNic =
                Nics.FirstOrDefault(nic => string.Equals(nic.Id, AppSettingsStore.Current.DefaultNicId, StringComparison.OrdinalIgnoreCase))
                ?? Nics.FirstOrDefault(nic => nic.IsUp && !nic.IsVirtual)
                ?? Nics.FirstOrDefault();
        }

        /// <summary>本机网卡（构造时枚举的快照）</summary>
        public IReadOnlyList<NicInfo> Nics { get; }

        public bool HasNic => Nics.Count > 0;

        /// <summary>正在配置的监听地址。由宿主（SlaveEditModel / 批量建站）在地址变化时写入</summary>
        public string BindAddress
        {
            get => _bindAddress;
            set
            {
                if (SetField(ref _bindAddress, value))
                {
                    OnPropertyChanged(nameof(CanAutoAdd));
                    OnPropertyChanged(nameof(PlanText));
                    OnPropertyChanged(nameof(PlanNeedsAttention));
                }
            }
        }

        public NicInfo SelectedNic
        {
            get => _selectedNic;
            set
            {
                if (SetField(ref _selectedNic, value))
                {
                    OnPropertyChanged(nameof(NicId));
                    OnPropertyChanged(nameof(PlanText));
                    OnPropertyChanged(nameof(PlanNeedsAttention));
                    Changed?.Invoke();
                }
            }
        }

        /// <summary>网卡 GUID（工程文件里存的就是这个）</summary>
        public string NicId
        {
            get => _selectedNic?.Id;
            set
            {
                // 空 = 这条通道还没配过网卡（老工程全是这样）。
                // 这时**不要**把已经带出来的默认网卡清掉：界面上留一块"空白下拉"，
                // 用户勾上"启动时自动添加"之后还得自己去选，等于白给一个坑。
                // 保留默认值，点确定时就落这块网卡——想换随时能改。
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                NicInfo match = Nics.FirstOrDefault(nic =>
                    string.Equals(nic.Id, value, StringComparison.OrdinalIgnoreCase));

                // GUID 在本机找不到（换了机器 / 网卡被删）：同样保留默认网卡，
                // 用户点确定时就把这条配置指向一块真存在的网卡，而不是带着一个死 GUID 走。
                if (match == null)
                {
                    return;
                }

                if (!ReferenceEquals(match, _selectedNic))
                {
                    SelectedNic = match;
                }
            }
        }

        /// <summary>启动通道时若本机没有这个地址，先加进去</summary>
        public bool AutoAddAddress
        {
            get => _autoAddAddress;
            set
            {
                if (SetField(ref _autoAddAddress, value))
                {
                    OnPropertyChanged(nameof(PlanText));
                    OnPropertyChanged(nameof(PlanNeedsAttention));
                    Changed?.Invoke();
                }
            }
        }

        /// <summary>地址不是 0.0.0.0 时才有"自动添加"这回事</summary>
        public bool CanAutoAdd
        {
            get
            {
                string ip = (_bindAddress ?? string.Empty).Trim();
                return ip.Length > 0 && ip != "0.0.0.0" && ip != "*";
            }
        }

        public string PlanText => IpAddressService.DescribePlan(_bindAddress, NicId, _autoAddAddress).Text;

        /// <summary>
        /// 要不要显示 PlanText 那一行。
        /// 批量建站里"地址"是一整批（每行一个），说一句"本机没有 xxx"会误导成只有一个地址，
        /// 那种场合改由预览表格逐行给状态，这里把这句话关掉。
        /// </summary>
        public bool ShowPlanText { get; init; } = true;

        /// <summary>计划里有需要用户先解决的问题（本机没有地址又没开自动添加 / 地址不合法）</summary>
        public bool PlanNeedsAttention =>
            IpAddressService.DescribePlan(_bindAddress, NicId, _autoAddAddress).Warning;

        /// <summary>网卡或开关变了（宿主据此刷新自己的提示）</summary>
        public event Action Changed;
    }
}
