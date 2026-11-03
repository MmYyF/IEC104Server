using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace IEC104.App.Services
{
    /// <summary>
    /// 一条更新说明（一个版本）。
    ///
    /// 字段刻意做得很少：版本号、一句话标题、日期、若干条目。
    /// 因为在"关于 → 更新说明"里，用户只想知道两件事：
    /// **我这一版有什么变化**、**上一版又改了什么**。写成小作文没人看。
    /// </summary>
    public sealed class ReleaseNote
    {
        public ReleaseNote(string version, string title, string date, params string[] items)
        {
            Version = version;
            Title = title;
            Date = date;
            Items = items ?? Array.Empty<string>();
        }

        /// <summary>版本号，如 "1.9.1"</summary>
        public string Version { get; }

        /// <summary>这一版的主题，一句话</summary>
        public string Title { get; }

        /// <summary>发布日期文本（yyyy-MM-dd），空串表示不展示</summary>
        public string Date { get; }

        /// <summary>改动条目</summary>
        public IReadOnlyList<string> Items { get; }
    }

    /// <summary>
    /// 更新说明的数据源：**编译时嵌入的 <c>docs/CHANGELOG.md</c>**。
    ///
    /// 【为什么要改成读文件】
    /// 1.10 之前这份说明是写死在 C# 里的 `new("1.9.1", ...)` 列表。接上在线更新之后，
    /// 同一段文字要出现在三个地方：程序里的"关于 → 更新说明"、GitHub Release 正文、
    /// 在线更新用的 `update.json`。三处各写一遍，必然漂移——发版当天对不上是最常见的翻车方式。
    /// 现在只留 `docs/CHANGELOG.md` 一份：编译时嵌进程序（离线也看得见），
    /// CI 再从**同一个文件**抽出对应版本，写进 Release 正文与 `update.json`。
    ///
    /// 【为什么要结构化】
    /// 判断"有没有新版本"要拿版本号做数值比较（1.10 > 1.9，字典序会判反）。
    /// 所以版本、日期、条目在文件里是分开的字段，而不是一坨 HTML——
    /// 结构化数据才能既渲染界面、又做版本比较。
    ///
    /// 【解析格式】见文件开头的说明：
    /// <code>
    /// ## 1.11.0 一句话标题
    /// 2026-11-03
    /// - 条目
    /// </code>
    /// 代码块（```）里的示例不参与解析——否则文件里那段格式说明会变成一条假版本。
    /// </summary>
    public static class ReleaseNotes
    {
        /// <summary>嵌入资源的逻辑名（csproj 里用 LogicalName 固定，不依赖目录结构）</summary>
        private const string ResourceName = "CHANGELOG.md";

        /// <summary>
        /// 全部版本，**从新到旧**（顺序跟 CHANGELOG.md 一致）。
        /// 界面按这个顺序铺，第一项就是最新版。
        /// </summary>
        public static IReadOnlyList<ReleaseNote> All { get; } = Load();

        /// <summary>最新一版的版本号（列表第一项）</summary>
        public static string LatestVersion => All.Count > 0 ? All[0].Version : AppInfo.Version;

        /// <summary>
        /// 是否有比当前运行的版本更新的版本。
        /// 接了在线更新之后，"要不要提示升级"仍然以这一条为准——
        /// 在线拿到的版本号会先并进 <see cref="All"/> 再比较，界面不用自己算。
        /// </summary>
        public static bool HasNewerThanRunning => Compare(LatestVersion, AppInfo.Version) > 0;

        /// <summary>比较两个版本号（按段比数字，不怕 1.10 和 1.9 的字典序坑）</summary>
        public static int Compare(string left, string right)
        {
            int[] a = ParseVersion(left);
            int[] b = ParseVersion(right);
            for (int index = 0; index < Math.Max(a.Length, b.Length); index++)
            {
                int x = index < a.Length ? a[index] : 0;
                int y = index < b.Length ? b[index] : 0;
                if (x != y)
                {
                    return x < y ? -1 : 1;
                }
            }
            return 0;
        }

        /// <summary>
        /// 按 <c>docs/CHANGELOG.md</c> 的约定切段。抽成 public 是为了让自检能直接喂字符串，
        /// 不必依赖"嵌入资源读没读到"——两件事要分开验。
        /// </summary>
        public static IReadOnlyList<ReleaseNote> ParseMarkdown(string markdown)
        {
            var notes = new List<ReleaseNote>();
            if (string.IsNullOrEmpty(markdown))
            {
                return notes;
            }

            string version = null;
            string title = string.Empty;
            string date = string.Empty;
            bool dateFilled = false;
            bool inCodeFence = false;
            var items = new List<string>();

            void Flush()
            {
                if (version != null)
                {
                    notes.Add(new ReleaseNote(version, title, date, items.ToArray()));
                }
            }

            foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();

                // 代码块里的"格式示例"必须跳过：里面有 `## <版本号> <标题>` 这种写法，
                // 不识别围栏就会解析出一条版本号为 "<版本号>" 的假记录。
                if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    inCodeFence = !inCodeFence;
                    continue;
                }

                if (inCodeFence)
                {
                    continue;
                }

                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    Flush();

                    string head = line.Substring(3).Trim();
                    int space = head.IndexOf(' ');
                    version = space > 0 ? head.Substring(0, space) : head;
                    title = space > 0 ? head.Substring(space + 1).Trim() : string.Empty;
                    date = string.Empty;
                    dateFilled = false;
                    items = new List<string>();
                    continue;
                }

                if (version == null || line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    items.Add(line.Substring(2).Trim());
                    continue;
                }

                // 段头之后、条目之前的第一个非空行是日期
                if (!dateFilled)
                {
                    date = line.Trim();
                    dateFilled = true;
                }
            }

            Flush();
            return notes;
        }

        private static IReadOnlyList<ReleaseNote> Load()
        {
            IReadOnlyList<ReleaseNote> parsed = ParseMarkdown(ReadEmbedded());
            if (parsed.Count > 0)
            {
                return parsed;
            }

            // 资源没嵌进去（csproj 改坏了）或文件为空：界面仍要能开，给一条说明而不是空列表，
            // 免得"关于"页取 All[0] 时抛异常。这是兜底，不是正常路径。
            return new[]
            {
                new ReleaseNote(AppInfo.Version, "这一版没有更新说明",
                    string.Empty,
                    "docs/CHANGELOG.md 没读到或为空，请检查 csproj 里的 EmbeddedResource 配置。"),
            };
        }

        private static string ReadEmbedded()
        {
            try
            {
                using Stream stream = typeof(ReleaseNotes).Assembly.GetManifestResourceStream(ResourceName);
                if (stream == null)
                {
                    return string.Empty;
                }

                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (Exception)
            {
                // 读资源失败不该让整个程序起不来。返回空串 → 走上面的兜底文案。
                return string.Empty;
            }
        }

        private static int[] ParseVersion(string version)
        {
            string[] parts = (version ?? string.Empty).Split('.', StringSplitOptions.RemoveEmptyEntries);
            int[] values = new int[parts.Length];
            for (int index = 0; index < parts.Length; index++)
            {
                values[index] = int.TryParse(parts[index], out int value) ? value : 0;
            }
            return values;
        }
    }
}
