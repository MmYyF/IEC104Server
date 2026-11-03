using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using IEC104.App.Infrastructure;
using IEC104.App.Services;

namespace IEC104.App.ViewModels
{
    /// <summary>批量加地址预览里的一行</summary>
    public sealed class IpBatchPreviewItem
    {
        public int Index { get; init; }

        public string Address { get; init; }

        /// <summary>"待添加" / "已有（跳过）"</summary>
        public string Status { get; init; }

        /// <summary>true = 这次会真的加进去</summary>
        public bool WillAdd { get; init; }
    }

    /// <summary>
    /// 批量给一块网卡加地址。
    ///
    /// 【为什么要有】现场一台机器可能要模拟一整个网段的装置，地址是连续的
    /// （222.111.112.247 ~ 251 这种）。一个一个填五次不算灾难，填五十次就是折磨，
    /// 而且 UAC 会弹五十次。
    ///
    /// 落地的关键只有一条：**整批走一次提权**（见 IpAddressService.EnsureManyAsync）。
    ///
    /// 默认起始地址取"这块网卡上最大的那个地址 + 1"——用户八成就是想挨着往下加，
    /// 让他自己去翻上一个地址是多少，纯属多余。
    /// </summary>
    public sealed class IpBatchAddViewModel : ViewModelBase
    {
        /// <summary>一次最多加多少个。一个 /24 可用 254 个，超过这个数基本是填错了</summary>
        public const int MaxCount = 254;

        private readonly IpAddressService _service = IpAddressService.Shared;
        private readonly NicInfo _nic;

        private string _startIp;
        private int _ipStep = 1;
        private int _count = 5;
        private string _mask = IpMask.DefaultMask;
        private string _errorText;
        private string _statusText;
        private bool _isBusy;
        private bool _isDone;
        private int _addedCount;
        private int _skippedCount;
        private int _failedCount;

        public IpBatchAddViewModel(NicInfo nic = null)
        {
            _nic = nic ?? _service.ListNics().FirstOrDefault();
            _startIp = SuggestStartIp();

            AddCommand = new RelayCommand(() => _ = AddAllAsync(), CanAdd);
            RegenerateCommand = new RelayCommand(RebuildPreview);

            RebuildPreview();
        }

        public ICommand AddCommand { get; }

        public ICommand RegenerateCommand { get; }

        /// <summary>网卡名与当前首地址，标题下面一行说明"加到哪里"</summary>
        public string NicText =>
            _nic == null
                ? "本机没有可用网卡"
                : $"{_nic.Name} · {_nic.SummaryText} · {_nic.StatusText}";

        public string NicId => _nic?.Id;

        public bool HasNic => _nic != null;

        /// <summary>起始地址（末段超 255 自动进位到前一段）</summary>
        public string StartIp
        {
            get => _startIp;
            set { if (SetField(ref _startIp, value)) { RebuildPreview(); } }
        }

        /// <summary>地址递增步长。填 1 就是连续地址</summary>
        public int IpStep
        {
            get => _ipStep;
            set { if (SetField(ref _ipStep, Math.Clamp(value, 1, 65535))) { RebuildPreview(); } }
        }

        /// <summary>要加几个</summary>
        public int Count
        {
            get => _count;
            set { if (SetField(ref _count, Math.Clamp(value, 1, MaxCount))) { RebuildPreview(); } }
        }

        public string Mask
        {
            get => _mask;
            set
            {
                if (SetField(ref _mask, value))
                {
                    OnPropertyChanged(nameof(MaskHintText));
                }
            }
        }

        public string MaskHintText
        {
            get
            {
                int prefix = IpMask.MaskToPrefix(_mask);
                return prefix < 0 ? "掩码无效" : $"前缀长度 /{prefix}";
            }
        }

        public ObservableCollection<IpBatchPreviewItem> Preview { get; } = new();

        public string ErrorText
        {
            get => _errorText;
            private set => SetField(ref _errorText, value);
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
                    RaiseCommands();
                }
            }
        }

        /// <summary>已经执行过一轮：按钮变"关闭"，防止用户以为没生效又点一次</summary>
        public bool IsDone
        {
            get => _isDone;
            private set
            {
                if (SetField(ref _isDone, value))
                {
                    OnPropertyChanged(nameof(AddButtonText));
                    RaiseCommands();
                }
            }
        }

        public string AddButtonText => _isDone ? "已完成" : "添加";

        /// <summary>结果回给上一层：真的加进去了几个</summary>
        public int AddedCount => _addedCount;

        public string SummaryText
        {
            get
            {
                if (Preview.Count == 0)
                {
                    return string.Empty;
                }

                int willAdd = Preview.Count(item => item.WillAdd);
                int exists = Preview.Count - willAdd;
                return exists == 0
                    ? $"共 {Preview.Count} 个，全部待添加"
                    : $"共 {Preview.Count} 个：待添加 {willAdd} 个，本机已有 {exists} 个（会跳过）";
            }
        }

        /// <summary>按当前参数重算预览；任何非法输入都在 ErrorText 里说清楚</summary>
        public void RebuildPreview()
        {
            Preview.Clear();
            ErrorText = null;

            if (!HasNic)
            {
                ErrorText = "本机没有可用网卡，无法添加地址。";
                OnPropertyChanged(nameof(SummaryText));
                return;
            }

            if (!IpAddressService.TryParseIpToUInt32(_startIp, out uint baseIp))
            {
                ErrorText = $"起始地址“{_startIp}”不是合法的 IPv4 地址。";
                OnPropertyChanged(nameof(SummaryText));
                return;
            }

            if (IpMask.MaskToPrefix(_mask) < 0)
            {
                ErrorText = "掩码不是有效的连续值（例如 255.255.255.0）。";
                OnPropertyChanged(nameof(SummaryText));
                return;
            }

            HashSet<string> local = _service.SnapshotLocalAddresses();

            for (int i = 0; i < _count; i++)
            {
                long value = baseIp + (long)_ipStep * i;
                if (value > uint.MaxValue)
                {
                    ErrorText = $"第 {i + 1} 个地址已超出 IPv4 范围（{_startIp} + 步长 {_ipStep} × {i}）。";
                    break;
                }

                string ip = IpAddressService.UInt32ToIp((uint)value);
                bool exists = local.Contains(ip);

                Preview.Add(new IpBatchPreviewItem
                {
                    Index = i + 1,
                    Address = ip,
                    Status = exists ? "已有（跳过）" : "待添加",
                    WillAdd = !exists
                });
            }

            OnPropertyChanged(nameof(SummaryText));
            RaiseCommands();
        }

        private bool CanAdd() => !_isBusy && !_isDone && HasNic && Preview.Count > 0
            && string.IsNullOrEmpty(_errorText);

        private async System.Threading.Tasks.Task AddAllAsync()
        {
            if (!CanAdd())
            {
                return;
            }

            IsBusy = true;
            StatusText = $"正在添加 {Preview.Count} 个地址…";

            try
            {
                IReadOnlyList<IpChangeResult> results = await _service
                    .EnsureManyAsync(Preview.Select(item => item.Address).ToList(), NicId, _mask, "批量添加")
                    .ConfigureAwait(true);

                _addedCount = results.Count(item => item.Ok && !item.AlreadyExists);
                _skippedCount = results.Count(item => item.AlreadyExists);
                _failedCount = results.Count(item => !item.Ok);

                IsDone = true;

                string firstError = results.FirstOrDefault(item => !item.Ok)?.Message;
                StatusText = _failedCount == 0
                    ? $"已添加 {_addedCount} 个，跳过 {_skippedCount} 个（本机已有）。"
                    : $"已添加 {_addedCount} 个，跳过 {_skippedCount} 个，失败 {_failedCount} 个。{firstError}";

                OnPropertyChanged(nameof(AddedCount));
            }
            catch (Exception exception)
            {
                StatusText = "批量添加失败：" + exception.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>给自检用的同步入口（按钮走命令是同步返回的，断言会在异步之前跑）</summary>
        public System.Threading.Tasks.Task AddAllForTestAsync() => AddAllAsync();

        private void RaiseCommands()
        {
            (AddCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RegenerateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// 建议的起始地址：这块网卡上**最大的那个地址 + 1**。
        /// 没有地址就退到 192.168.1.10（和批量建站的默认值一致）。
        /// </summary>
        private string SuggestStartIp()
        {
            uint max = 0;
            bool any = false;

            foreach (IpAddressEntry entry in _nic?.Addresses ?? Array.Empty<IpAddressEntry>())
            {
                if (IpAddressService.TryParseIpToUInt32(entry.Address, out uint value) && (!any || value > max))
                {
                    max = value;
                    any = true;
                }
            }

            if (!any || max >= uint.MaxValue)
            {
                return "192.168.1.10";
            }

            return IpAddressService.UInt32ToIp(max + 1);
        }
    }
}
