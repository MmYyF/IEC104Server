using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using IEC104.App.Services;
using IEC104.App.ViewModels;

namespace IEC104.App.Views
{
    /// <summary>
    /// 检查更新窗口。
    ///
    /// 代码后置只管三件"必须接触操作系统"的事：开浏览器、重启、关应用。
    /// 检查、下载、校验、替换的判定全在 <see cref="UpdateViewModel"/> 里，
    /// 自检可以直接喂假的检查结果，不用点按钮、不用联网。
    /// </summary>
    public partial class UpdateWindow : DialogWindow
    {
        public UpdateWindow()
        {
            InitializeComponent();

            if (DataContext is UpdateViewModel viewModel)
            {
                Hook(viewModel);
            }
        }

        public UpdateWindow(UpdateViewModel viewModel) : this()
        {
            DataContext = viewModel;
            Hook(viewModel);
        }

        public UpdateViewModel ViewModel => DataContext as UpdateViewModel;

        private void Hook(UpdateViewModel viewModel)
        {
            if (viewModel == null)
            {
                return;
            }

            viewModel.OpenUrlRequested += OpenExternal;
            viewModel.RestartRequested += RestartAndExit;
        }

        /// <summary>窗口一出来就开始查，不用用户再点一次。</summary>
        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            _ = ViewModel?.CheckAsync();
        }

        protected override void OnClosed(EventArgs e)
        {
            // 关窗时取消在途请求：下载一半的文件留着也没用，让 CancellationToken 把它断掉
            ViewModel?.Cancel();
            base.OnClosed(e);
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        private static void OpenExternal(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception)
            {
                // 没有默认浏览器时什么也做不了，静默即可：这不是用户要完成的主任务
            }
        }

        /// <summary>新 exe 已就位：起新进程，然后关掉自己。</summary>
        private void RestartAndExit()
        {
            string executable = Environment.ProcessPath;
            try
            {
                if (!string.IsNullOrEmpty(executable))
                {
                    UpdateInstaller.Restart(executable);
                }
            }
            catch (Exception)
            {
                // 起不来就只关自己，用户再双击一次即可（文件已经是新的了）
            }

            Close();

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // 走正常退出流程：值脏时该存的存，别绕过 OnExit
                desktop.Shutdown();
            }
        }
    }
}
