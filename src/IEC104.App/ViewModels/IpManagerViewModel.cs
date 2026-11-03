using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Input;
using IEC104.App.Infrastructure;
using IEC104.App.Services;

namespace IEC104.App.ViewModels
{
    /// <summary>
    /// IP 地址管理窗口的 ViewModel（工具 → IP 地址管理）。
    ///
    /// 【这个窗口存在的理由】
    /// 仿真工具要模拟"某个网段的装置"，就得让本机真的拥有那个地址——
    /// 主站是按 IP 找装置的，地址不在本机，TcpListener 会直接报 10049。
    /// 以前这件事只能靠用户自己去"网络连接 → 高级 → 添加"，
    /// 一是不好找，二是加完没人记得删，网卡上永久挂着十几个地址。
    ///
    /// 这个窗口把"加 / 删 / 记得自己加过哪些"收在一起，并且**只承认自己的账本**：
    /// 系统原有地址和 DHCP 拿到的地址一律标成"系统/DHCP"，不给删。
    /// </summary>
    public sealed class IpManagerViewModel : ViewModelBase
    {
        private readonly IpAddressService _service = IpAddressService.Shared;

        private NicInfo _selectedNic;
        private IpAddressEntry _selectedAddress;
        private string _newAddress = string.Empty;
        private string _newMask = IpMask.DefaultMask;
        private string _statusText;
        private bool _isBusy;

        public IpManagerViewModel()
        {
            RefreshCommand = new RelayCommand(Refresh, () => !_isBusy);
            AddCommand = new RelayCommand(() => _ = AddAsync(), CanAdd);
            DeleteCommand = new RelayCommand(() => _ = DeleteAsync(), CanDelete);
            CleanupCommand = new RelayCommand(() => _ = CleanupAsync(), () => !_isBusy && ManagedCount > 0);
            SetDefaultNicCommand = new RelayCommand(SetDefaultNic, () => SelectedNic != null && !_isBusy);

            Refresh();
        }

        public ObservableCollection<NicInfo> Nics { get; } = new();

        /// <summary>当前选中网卡上的全部 IPv4 地址</summary>
        public ObservableCollection<IpAddressEntry> Addresses { get; } = new();

        public ICommand RefreshCommand { get; }

        public ICommand AddCommand { get; }

        public ICommand DeleteCommand { get; }

        public ICommand CleanupCommand { get; }

        public ICommand SetDefaultNicCommand { get; }

        public NicInfo SelectedNic
        {
            get => _selectedNic;
            set
            {
                if (SetField(ref _selectedNic, value))
                {
                    RebuildAddresses();
                    OnPropertyChanged(nameof(NicDetailText));
                    OnPropertyChanged(nameof(IsDefaultNic));
                    RaiseCommands();
                }
            }
        }

        public IpAddressEntry SelectedAddress
        {
            get => _selectedAddress;
            set
            {
                if (SetField(ref _selectedAddress, value))
                {
                    RaiseCommands();
                }
            }
        }

        public string NewAddress
        {
            get => _newAddress;
            set
            {
                if (SetField(ref _newAddress, value))
                {
                    RaiseCommands();
                }
            }
        }

        public string NewMask
        {
            get => _newMask;
            set
            {
                if (SetField(ref _newMask, value))
                {
                    OnPropertyChanged(nameof(MaskHintText));
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetField(ref _statusText, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetField(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(IsIdle));
                    RaiseCommands();
                }
            }
        }

        public bool IsIdle => !_isBusy;

        /// <summary>当前进程有没有管理员权限（没提权时提示会弹一次授权）</summary>
        public bool IsElevated => IpAddressService.IsElevated();

        public string ElevationText => IsElevated
            ? "已具备管理员权限，改动立即生效"
            : "普通权限：改动时会弹一次管理员授权（点“是”即可）";

        public string NicDetailText
        {
            get
            {
                if (SelectedNic == null)
                {
                    return "先在左边选一块网卡";
                }

                string speed = SelectedNic.SpeedMbps > 0 ? $" · {SelectedNic.SpeedMbps} Mbps" : string.Empty;
                string mac = string.IsNullOrEmpty(SelectedNic.MacAddress) ? string.Empty : $" · {SelectedNic.MacAddress}";
                string kind = SelectedNic.IsVirtual ? " · 虚拟网卡" : string.Empty;
                return $"{SelectedNic.StatusText}{speed}{mac}{kind}";
            }
        }

        public bool IsDefaultNic =>
            SelectedNic != null
            && string.Equals(AppSettingsStore.Current.DefaultNicId, SelectedNic.Id, StringComparison.OrdinalIgnoreCase);

        /// <summary>账本里的条数（决定"清理"能不能点）</summary>
        public int ManagedCount => AppSettingsStore.Current.ManagedAddresses?.Count ?? 0;

        public string ManagedSummaryText
        {
            get
            {
                int count = ManagedCount;
                return count == 0 ? "本工具没有添加过地址" : $"本工具添加过 {count} 个地址";
            }
        }

        public string MaskHintText
        {
            get
            {
                int prefix = IpMask.MaskToPrefix(NewMask);
                return prefix < 0 ? "掩码不是连续的有效值（如 255.255.255.0）" : $"前缀长度 /{prefix}";
            }
        }

        // ── 行为 ────────────────────────────────────────────────

        /// <summary>重新枚举网卡与地址。任何改动之后都调它，界面永远拿系统现状说话</summary>
        public void Refresh()
        {
            string keep = _selectedNic?.Id;
            string keepAddress = _selectedAddress?.Address;

            IReadOnlyList<NicInfo> nics = _service.ListNics();
            _service.MarkManaged(nics);

            Nics.Clear();
            foreach (NicInfo nic in nics)
            {
                Nics.Add(nic);
            }

            if (Nics.Count == 0)
            {
                SelectedNic = null;
                StatusText = "没有找到可用网卡（回环除外）。";
            }
            else
            {
                SelectedNic = Nics.FirstOrDefault(nic =>
                                   string.Equals(nic.Id, keep, StringComparison.OrdinalIgnoreCase))
                               ?? Nics.FirstOrDefault(nic =>
                                   string.Equals(nic.Id, AppSettingsStore.Current.DefaultNicId, StringComparison.OrdinalIgnoreCase))
                               ?? Nics[0];
            }

            if (!string.IsNullOrEmpty(keepAddress))
            {
                SelectedAddress = Addresses.FirstOrDefault(item =>
                    string.Equals(item.Address, keepAddress, StringComparison.OrdinalIgnoreCase));
            }

            OnPropertyChanged(nameof(ManagedCount));
            OnPropertyChanged(nameof(ManagedSummaryText));
            RaiseCommands();
        }

        private void RebuildAddresses()
        {
            Addresses.Clear();
            if (SelectedNic != null)
            {
                // 本工具加的排最前：用户来这个窗口，八成是要处理自己加的那些
                foreach (IpAddressEntry entry in SelectedNic.Addresses
                             .OrderByDescending(item => item.IsManaged)
                             .ThenBy(item => item.Address, StringComparer.OrdinalIgnoreCase))
                {
                    Addresses.Add(entry);
                }
            }
            SelectedAddress = null;
        }

        private bool CanAdd()
        {
            if (_isBusy || SelectedNic == null)
            {
                return false;
            }

            return IPAddress.TryParse((_newAddress ?? string.Empty).Trim(), out IPAddress parsed)
                && parsed.AddressFamily == AddressFamily.InterNetwork
                && IpMask.MaskToPrefix(_newMask) >= 0;
        }

        private bool CanDelete() => !_isBusy && _selectedAddress?.IsManaged == true;

        private async Task AddAsync()
        {
            if (!CanAdd())
            {
                StatusText = "地址或掩码不合法。";
                return;
            }

            string address = _newAddress.Trim();
            IsBusy = true;
            StatusText = $"正在添加 {address}…";
            try
            {
                IpChangeResult result = await _service
                    .EnsureAsync(address, SelectedNic?.Id, _newMask, "手动添加")
                    .ConfigureAwait(true);

                StatusText = result.Ok
                    ? (result.AlreadyExists ? $"本机已有 {address}，未重复添加。" : $"已添加 {address}。")
                    : $"添加失败：{result.Message}";

                if (result.Ok)
                {
                    NewAddress = string.Empty;
                    Refresh();
                }
            }
            catch (Exception exception)
            {
                StatusText = "添加失败：" + exception.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task DeleteAsync()
        {
            IpAddressEntry entry = _selectedAddress;
            if (entry == null || !entry.IsManaged)
            {
                return;
            }

            ManagedAddress record = AppSettingsStore.Current.ManagedAddresses
                .FirstOrDefault(item => string.Equals(item.Address, entry.Address, StringComparison.OrdinalIgnoreCase));
            if (record == null)
            {
                StatusText = $"{entry.Address} 不在账本里（不是本工具加的），不能从这里删。";
                return;
            }

            IsBusy = true;
            StatusText = $"正在删除 {entry.Address}…";
            try
            {
                IReadOnlyList<IpChangeResult> results = await _service
                    .RemoveManagedAsync(new[] { record })
                    .ConfigureAwait(true);

                IpChangeResult first = results?.FirstOrDefault();
                StatusText = first == null
                    ? $"{entry.Address} 已从账本移除（网卡已不存在）。"
                    : first.Ok
                        ? $"已删除 {entry.Address}。"
                        : $"删除失败：{first.Message}";

                Refresh();
            }
            catch (Exception exception)
            {
                StatusText = "删除失败：" + exception.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>一键清理：把账本里记着、而且现在还在系统上的地址全部删掉</summary>
        private async Task CleanupAsync()
        {
            List<ManagedAddress> targets = AppSettingsStore.Current.ManagedAddresses
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Address))
                .ToList();

            if (targets.Count == 0)
            {
                StatusText = "账本是空的，没有可清理的地址。";
                _service.PruneLedger();
                Refresh();
                return;
            }

            IsBusy = true;
            StatusText = $"正在清理 {targets.Count} 个地址…";
            try
            {
                IReadOnlyList<IpChangeResult> results = await _service
                    .RemoveManagedAsync(targets)
                    .ConfigureAwait(true);

                int ok = results?.Count(item => item.Ok) ?? 0;
                int failed = (results?.Count ?? 0) - ok;
                StatusText = failed == 0
                    ? $"已清理 {ok} 个地址。"
                    : $"清理完成：成功 {ok} 个，失败 {failed} 个（见下方列表的失败提示）。";

                Refresh();
            }
            catch (Exception exception)
            {
                StatusText = "清理失败：" + exception.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void SetDefaultNic()
        {
            if (SelectedNic == null)
            {
                return;
            }

            AppSettingsStore.Current.DefaultNicId = SelectedNic.Id;
            AppSettingsStore.Save();
            StatusText = $"已把 {SelectedNic.Name} 设为默认通信网卡（新建通道时自动带出）。";
            OnPropertyChanged(nameof(IsDefaultNic));
        }

        private void RaiseCommands()
        {
            (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AddCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CleanupCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SetDefaultNicCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// 给自检用的同步入口：直接走一遍"添加"的判定与执行，避免自检去点按钮。
        ///
        /// （RelayCommand.Execute 是同步的，内部 `_ = AddAsync()` 之后就返回了，
        /// 自检在下一行断言时异步还没跑完 —— 这正是之前踩过的"断言假绿"。
        /// 所以给一个可以 await 的入口。）
        /// </summary>
        public Task AddForTestAsync(string address, string mask)
        {
            NewAddress = address;
            NewMask = mask;
            return AddAsync();
        }

        /// <summary>同上，给自检的清理入口</summary>
        public Task CleanupForTestAsync() => CleanupAsync();
    }
}
