using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using IEC104.App.Infrastructure;
using IEC104.App.Services;

namespace IEC104.App.ViewModels
{
    /// <summary>
    /// 「检查更新」窗口的 ViewModel。
    ///
    /// 【为什么单独开个窗口，而不是弹 MessageBox】
    /// 更新说明是**一列条目**，不是一句话；还要能显示下载进度、能在失败时说清为什么。
    /// MessageBox 装不下这些，硬塞进去就是一段读不完的长文本。
    ///
    /// 【三条不能省的规矩】
    /// ① **"没问到"不能说成"已是最新"**：没网和没新版是两回事，混为一谈会让用户
    ///    以为软件真的不用更新，之后再也不会点第二次。
    /// ② **失败必须给出路**：报错文字后面永远跟着"去发布页手动下载"。
    /// ③ **下载完要校验**：见 <see cref="UpdateService.DownloadAsync"/>，
    ///    哈希对不上就拒绝安装，绝不"大概能用就先换上"。
    /// </summary>
    public sealed class UpdateViewModel : ViewModelBase
    {
        private readonly CancellationTokenSource _lifetime = new();
        private UpdateInfo _info;
        private bool _isBusy;
        private bool _hasUpdate;
        private double _progress;
        private bool _showProgress;
        private string _statusText = "正在检查更新…";
        private string _errorText = string.Empty;

        public UpdateViewModel()
        {
            DownloadCommand = new RelayCommand(() => _ = DownloadAndApplyAsync(), () => _hasUpdate && !_isBusy);
            OpenReleasePageCommand = new RelayCommand(
                () => OpenUrlRequested?.Invoke(ReleasePageUrl), () => !string.IsNullOrEmpty(ReleasePageUrl));
        }

        /// <summary>要打开网页（发布页）。由窗口层转给系统浏览器。</summary>
        public event Action<string> OpenUrlRequested;

        /// <summary>新版本已经就位，需要关掉当前进程并按新 exe 重启。</summary>
        public event Action RestartRequested;

        public ICommand DownloadCommand { get; }

        public ICommand OpenReleasePageCommand { get; }

        public ObservableCollection<string> Notes { get; } = new();

        public string CurrentVersionText => "当前版本 " + AppInfo.Version;

        public string LatestVersionText => _info == null || string.IsNullOrEmpty(_info.Version)
            ? "—"
            : _info.Version;

        public string SourceText => _info == null || string.IsNullOrEmpty(_info.SourceName)
            ? string.Empty
            : "来源：" + _info.SourceName;

        public string DateText => _info?.Date ?? string.Empty;

        public string TitleText => _info?.Title ?? string.Empty;

        public string ReleasePageUrl => !string.IsNullOrEmpty(_info?.ReleasePageUrl)
            ? _info.ReleasePageUrl
            : AppInfo.HasRepository ? AppInfo.RepositoryUrl.TrimEnd('/') + "/releases/latest" : string.Empty;

        public string StatusText
        {
            get => _statusText;
            private set => SetField(ref _statusText, value);
        }

        /// <summary>失败原因。空串表示没有错误。</summary>
        public string ErrorText
        {
            get => _errorText;
            private set
            {
                if (SetField(ref _errorText, value))
                {
                    OnPropertyChanged(nameof(HasError));
                }
            }
        }

        public bool HasError => !string.IsNullOrEmpty(_errorText);

        public bool HasUpdate
        {
            get => _hasUpdate;
            private set
            {
                if (SetField(ref _hasUpdate, value))
                {
                    OnPropertyChanged(nameof(DownloadButtonText));
                    (DownloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetField(ref _isBusy, value))
                {
                    (DownloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool ShowProgress
        {
            get => _showProgress;
            private set => SetField(ref _showProgress, value);
        }

        /// <summary>0~100，给 ProgressBar 用。</summary>
        public double ProgressValue
        {
            get => _progress;
            private set => SetField(ref _progress, value);
        }

        public string DownloadButtonText => _hasUpdate ? "下载并安装" : "已是最新";

        /// <summary>窗口打开时调用一次。</summary>
        public async Task CheckAsync()
        {
            IsBusy = true;
            HasUpdate = false;
            ErrorText = string.Empty;
            StatusText = "正在检查更新…";

            try
            {
                UpdateCheckResult result = await UpdateService.CheckAsync(_lifetime.Token).ConfigureAwait(true);
                Apply(result);
            }
            catch (OperationCanceledException)
            {
                StatusText = "已取消";
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>把检查结果铺到界面（自检也走这一条，保证测的是真路径）。</summary>
        public void Apply(UpdateCheckResult result)
        {
            _info = result?.Info;
            Notes.Clear();

            // 上一次检查留下的错误必须清掉：窗口一打开会自动查一次（那会儿可能没配源），
            // 用户再点一次检查成功后，界面上若还挂着红字，等于告诉他"明明成功了却失败了"
            ErrorText = string.Empty;

            switch (result?.Status)
            {
                case UpdateStatus.Newer:
                    HasUpdate = true;
                    StatusText = "发现新版本 " + _info.Version;
                    if (_info.Notes != null)
                    {
                        foreach (string note in _info.Notes)
                        {
                            Notes.Add(note);
                        }
                    }

                    if (Notes.Count == 0)
                    {
                        Notes.Add("（这个发布源没提供更新说明，可以点「查看发布页」看详情）");
                    }

                    break;

                case UpdateStatus.UpToDate:
                    HasUpdate = false;
                    StatusText = "已经是最新版本";
                    break;

                default:
                    HasUpdate = false;
                    StatusText = "没能检查到更新";
                    ErrorText = result?.Message ?? "未知原因。";
                    break;
            }

            OnPropertyChanged(nameof(LatestVersionText));
            OnPropertyChanged(nameof(SourceText));
            OnPropertyChanged(nameof(DateText));
            OnPropertyChanged(nameof(TitleText));
            OnPropertyChanged(nameof(ReleasePageUrl));
            OnPropertyChanged(nameof(DownloadButtonText));
            (OpenReleasePageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        public async Task DownloadAndApplyAsync()
        {
            if (!_hasUpdate || _info == null)
            {
                return;
            }

            string executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
            {
                ErrorText = "拿不到当前程序路径，无法自动替换。请到发布页手动下载。";
                return;
            }

            IsBusy = true;
            ShowProgress = true;
            ProgressValue = 0;
            ErrorText = string.Empty;
            StatusText = "正在下载…";

            try
            {
                string target = Path.Combine(
                    UpdateService.CacheDirectory,
                    AppInfo.ProductName + "-" + _info.Version + ".exe");

                var progress = new Progress<double>(value =>
                {
                    ProgressValue = Math.Round(value * 100);
                    StatusText = "正在下载… " + ProgressValue.ToString("0") + "%";
                });

                // 下载内含大小与 SHA256 双重校验；不通过会抛，绝不留一个可疑文件
                await UpdateService.DownloadAsync(_info, target, progress, _lifetime.Token)
                    .ConfigureAwait(true);

                StatusText = "正在替换程序文件…";
                UpdateInstaller.Apply(target, executable);

                StatusText = "更新完成，正在重启…";
                RestartRequested?.Invoke();
            }
            catch (OperationCanceledException)
            {
                StatusText = "已取消";
            }
            catch (Exception exception)
            {
                StatusText = "更新失败";
                ErrorText = exception.Message;
            }
            finally
            {
                IsBusy = false;
                ShowProgress = false;
            }
        }

        public void Cancel() => _lifetime.Cancel();

        // ── 自检用 ──────────────────────────────────────────────

        /// <summary>自检入口：直接执行"下载 + 校验"，写到指定路径（不替换、不重启）。</summary>
        public async Task<string> DownloadForTestAsync(string targetPath)
        {
            if (_info == null)
            {
                throw new InvalidOperationException("还没有检查结果。");
            }

            return await UpdateService.DownloadAsync(_info, targetPath, null, _lifetime.Token)
                .ConfigureAwait(true);
        }
    }
}
