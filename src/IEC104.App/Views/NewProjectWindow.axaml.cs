using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using IEC104.App.Services;

namespace IEC104.App.Views
{
    /// <summary>
    /// 新建工程对话框。
    ///
    /// 只有四件事：工程名、放哪儿、完整路径预览、要不要带示例信号。
    /// 不在这里让用户填通道和从站——那些属于"工程内容"，建完工程进主界面再配，
    /// 否则这个框会变成第二个配置界面，而且把"新建"这个动作拖成十几步。
    /// </summary>
    public partial class NewProjectWindow : DialogWindow
    {
        /// <summary>Windows 文件名里不允许出现的字符</summary>
        private static readonly char[] InvalidNameChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

        public NewProjectWindow()
        {
            InitializeComponent();

            FolderBox.Text = DefaultFolder();
            NameBox.Text = ProjectStore.DefaultName;

            Opened += (_, _) =>
            {
                NameBox.Focus();
                NameBox.SelectAll();
                UpdatePreview();
            };
        }

        /// <summary>用户点了"确定"（而不是"取消"或直接关窗）</summary>
        public bool Confirmed { get; private set; }

        /// <summary>确认后回传：完整路径、工程名、是否带示例信号</summary>
        public string ProjectPath { get; private set; }

        public string ProjectName { get; private set; }

        public bool WithSamplePoints => SampleBox.IsChecked == true;

        /// <summary>默认放到"文档\IEC104 工程"，没有就现建（写不进去时回落到文档根）</summary>
        private static string DefaultFolder()
        {
            string documents;
            try
            {
                documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }
            catch (Exception)
            {
                documents = null;
            }

            if (string.IsNullOrEmpty(documents))
            {
                return Path.GetTempPath();
            }

            string target = Path.Combine(documents, "IEC104 工程");
            try
            {
                Directory.CreateDirectory(target);
                return target;
            }
            catch (Exception)
            {
                // 文档目录只读（域策略、同步盘占用）时不要卡在这里，退回文档根目录
                return documents;
            }
        }

        /// <summary>用户填的工程名经过非法字符替换后的可存盘名字</summary>
        private string SanitizedName()
        {
            string raw = (NameBox.Text ?? string.Empty).Trim();
            if (raw.Length == 0)
            {
                return string.Empty;
            }

            char[] chars = raw.ToCharArray();
            for (int index = 0; index < chars.Length; index++)
            {
                if (Array.IndexOf(InvalidNameChars, chars[index]) >= 0 || char.IsControl(chars[index]))
                {
                    chars[index] = '_';
                }
            }

            return new string(chars).Trim();
        }

        private string PreviewPath()
        {
            string name = SanitizedName();
            string folder = (FolderBox.Text ?? string.Empty).Trim();

            if (name.Length == 0 || folder.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                return Path.Combine(folder, name + ProjectStore.Extension);
            }
            catch (ArgumentException)
            {
                // 文件夹里含非法字符时 Combine 会抛，预览就空着，交给确定按钮去报错
                return string.Empty;
            }
        }

        private void OnInputChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

        /// <summary>路径预览 + "同名文件会被覆盖"的红字提示</summary>
        private void UpdatePreview()
        {
            if (PathPreview == null || PathHint == null)
            {
                return;
            }

            string path = PreviewPath();
            PathPreview.Text = path.Length > 0 ? path : "（填好工程名和位置后显示）";

            if (path.Length > 0 && File.Exists(path))
            {
                PathHint.Text = "同名文件已存在，创建会覆盖它。";
                PathHint.IsVisible = true;
            }
            else
            {
                PathHint.IsVisible = false;
            }
        }

        private async void OnBrowseClick(object sender, RoutedEventArgs e)
        {
            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = "选择工程存放位置",
                    AllowMultiple = false
                });

            if (folders.Count == 0)
            {
                return;
            }

            string path = folders[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                FolderBox.Text = path;
                UpdatePreview();
            }
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            string name = SanitizedName();
            string folder = (FolderBox.Text ?? string.Empty).Trim();
            string path = PreviewPath();

            ErrorText.IsVisible = false;

            if (name.Length == 0)
            {
                Fail("工程名不能为空。");
                return;
            }

            if (folder.Length == 0 || path.Length == 0)
            {
                Fail("存放位置无效，换一个目录试试。");
                return;
            }

            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception exception)
            {
                Fail("这个位置不可写：" + exception.Message);
                return;
            }

            ProjectName = name;
            ProjectPath = path;
            Confirmed = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            ProjectPath = null;
            Close();
        }

        private void Fail(string message)
        {
            ErrorText.Text = message;
            ErrorText.IsVisible = true;
        }
    }
}
