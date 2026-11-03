using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IEC104.App.Services
{
    /// <summary>检查更新的三种结果。</summary>
    public enum UpdateStatus
    {
        /// <summary>已经是最新（某个源明确答复过）。</summary>
        UpToDate,

        /// <summary>有更新的版本。</summary>
        Newer,

        /// <summary>一个源都没问通。**不等于**没有新版本，只说这次没问到。</summary>
        Unavailable,
    }

    /// <summary>一个可下载的发布产物。</summary>
    public sealed class UpdateAsset
    {
        public string Name { get; set; } = "IEC104-Server.exe";

        public string Url { get; set; }

        public long Size { get; set; }

        /// <summary>
        /// 文件的 SHA256（小写十六进制）。空表示这个源没给——
        /// 那就去同目录取 <c>&lt;url&gt;.sha256</c>；还取不到就**不自动安装**，
        /// 只给下载页让用户手工换（装一个没校验过的 exe 比不更新更糟）。
        /// </summary>
        public string Sha256 { get; set; }
    }

    /// <summary>一个版本的完整信息（不管来自哪种源，最后都归一成这个）。</summary>
    public sealed class UpdateInfo
    {
        public string Version { get; set; } = string.Empty;

        public string Tag { get; set; } = string.Empty;

        public string Date { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public IReadOnlyList<string> Notes { get; set; } = Array.Empty<string>();

        public UpdateAsset Asset { get; set; }

        public string ReleasePageUrl { get; set; } = string.Empty;

        /// <summary>这条信息是从哪个源拿到的（界面上要显示，用户才知道是不是内网镜像）。</summary>
        public string SourceName { get; set; } = string.Empty;
    }

    /// <summary>检查结果 + 一句给人看的话。</summary>
    public sealed class UpdateCheckResult
    {
        public UpdateStatus Status { get; set; }

        public UpdateInfo Info { get; set; }

        /// <summary>失败原因（Status=Unavailable 时必填），已经是可以直接显示的中文。</summary>
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// 取文本的网络通道。
    ///
    /// 抽成接口只有一个目的：**自检不能真发请求**。真请求会慢、会受网络与防火墙影响、
    /// 还会因为 GitHub 挂了让整条自检莫名其妙变红。自检里换成返回固定文本的假实现，
    /// 就能把"解析 + 比版本 + 校验哈希"这些真正容易写错的逻辑验干净。
    /// </summary>
    public interface IUpdateTransport
    {
        Task<string> GetStringAsync(string url, CancellationToken token);

        Task<byte[]> GetBytesAsync(string url, IProgress<double> progress, CancellationToken token);
    }

    /// <summary>真网络：HttpClient，短超时，带 UA（GitHub API 不带 UA 会被拒）。</summary>
    public sealed class HttpUpdateTransport : IUpdateTransport
    {
        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient
            {
                // 15 秒：这是"顺手点一下"的动作，等太久不如直接告诉用户网络不通
                Timeout = TimeSpan.FromSeconds(15),
            };
            client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue(
                AppInfo.ProductName.Replace(" ", "-"), AppInfo.Version));
            client.DefaultRequestHeaders.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        public async Task<string> GetStringAsync(string url, CancellationToken token)
        {
            using HttpResponseMessage response = await Client.GetAsync(url, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }

        public async Task<byte[]> GetBytesAsync(string url, IProgress<double> progress, CancellationToken token)
        {
            using HttpResponseMessage response = await Client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? -1;
            await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream(total > 0 ? (int)total : 0);

            byte[] chunk = new byte[81920];
            long read = 0;
            int count;
            while ((count = await source.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, count);
                read += count;
                if (total > 0)
                {
                    progress?.Report(Math.Min(1.0, (double)read / total));
                }
            }

            progress?.Report(1.0);
            return buffer.ToArray();
        }
    }

    /// <summary>一个发布源。</summary>
    public sealed class UpdateSource
    {
        public UpdateSource(string name, string url, bool isManifest)
        {
            Name = name;
            Url = url;
            IsManifest = isManifest;
        }

        public string Name { get; }

        public string Url { get; }

        /// <summary>
        /// true = 这个地址给的是 update.json；false = GitHub API 的 releases/latest。
        /// 两种格式不同，解析器也不同，所以在源上标清楚，别靠"猜内容"。
        /// </summary>
        public bool IsManifest { get; }
    }

    /// <summary>
    /// 在线更新：检查 / 下载 / 校验。
    ///
    /// 【为什么要多个源】
    /// 主源是 GitHub Releases。但 GitHub 的 api 与 releases 在国内网络下经常连不上，
    /// 而这类仿真工具的用户大多在内网现场——只留一个源，等于对一半用户不可用。
    /// 所以按顺序试：
    ///   ① 自定义镜像（<see cref="AppInfo.MirrorUpdateUrl"/>，阿里云 OSS / 腾讯云 COS 之类，留空则不试）
    ///   ② GitHub API 的 releases/latest
    ///   ③ GitHub 「latest/download/update.json」——一个永远指向最新 Release 附件的固定地址
    ///
    /// **第一个能应答的源说了算**：它说没有新版就是没有，不再继续问后面的源。
    /// 全都问不通时返回 <see cref="UpdateStatus.Unavailable"/> 并带上每个源的原因——
    /// "没问到"和"没有新版"是两件完全不同的事，界面上必须分开说。
    ///
    /// 【update.json 长什么样】
    /// <code>
    /// {
    ///   "version": "1.11.0", "tag": "v1.11.0", "date": "2026-11-03",
    ///   "title": "本机没有那个 IP，现在能自己补上",
    ///   "notes": ["条目一", "条目二"],
    ///   "asset": { "name": "IEC104-Server.exe", "url": "https://…/IEC104-Server.exe",
    ///              "size": 45477544, "sha256": "…" }
    /// }
    /// </code>
    /// 这个文件由 <c>.github/workflows/release.yml</c> 从 tag 与 docs/CHANGELOG.md 生成，
    /// 镜像源只要把同一份放到自己的对象存储里即可，客户端不区分。
    /// </summary>
    public static class UpdateService
    {
        /// <summary>可替换的通道（自检注入假实现）。</summary>
        public static IUpdateTransport Transport { get; set; } = new HttpUpdateTransport();

        /// <summary>
        /// 依次要试的源。抽成方法是为了自检能直接断言"镜像配了就排在 API 前面"，
        /// 不用去读网络。
        /// </summary>
        public static IReadOnlyList<UpdateSource> BuildSources()
        {
            var sources = new List<UpdateSource>();

            if (!string.IsNullOrWhiteSpace(AppInfo.MirrorUpdateUrl))
            {
                sources.Add(new UpdateSource("镜像", AppInfo.MirrorUpdateUrl.Trim(), true));
            }

            if (TryParseRepository(out string owner, out string repo))
            {
                sources.Add(new UpdateSource("GitHub API",
                    $"https://api.github.com/repos/{owner}/{repo}/releases/latest", false));
                sources.Add(new UpdateSource("GitHub 附件",
                    $"{AppInfo.RepositoryUrl.TrimEnd('/')}/releases/latest/download/update.json", true));
            }

            return sources;
        }

        public static Task<UpdateCheckResult> CheckAsync(CancellationToken token) =>
            CheckAsync(BuildSources(), token);

        /// <summary>
        /// 按给定的源逐个试。单独暴露出来是给自检用的：
        /// 真源要联网、要看仓库配没配，测不了"三个源全挂"或"第一个源没新版"这种分支。
        /// </summary>
        public static async Task<UpdateCheckResult> CheckAsync(
            IReadOnlyList<UpdateSource> sources, CancellationToken token)
        {
            if (sources == null || sources.Count == 0)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateStatus.Unavailable,
                    Message = "还没配置发布源：AppInfo.RepositoryUrl 与 MirrorUpdateUrl 都是空的。",
                };
            }

            var failures = new List<string>();

            foreach (UpdateSource source in sources)
            {
                try
                {
                    string text = await Transport.GetStringAsync(source.Url, token).ConfigureAwait(true);
                    UpdateInfo info = source.IsManifest
                        ? ParseManifest(text, source.Name, ReleasePage())
                        : ParseGitHubRelease(text, source.Name);

                    if (info == null)
                    {
                        failures.Add(source.Name + "：返回内容看不懂");
                        continue;
                    }

                    info.SourceName = source.Name;

                    // 第一个能应答的源说了算
                    return ReleaseNotes.Compare(info.Version, AppInfo.Version) > 0
                        ? new UpdateCheckResult { Status = UpdateStatus.Newer, Info = info }
                        : new UpdateCheckResult { Status = UpdateStatus.UpToDate, Info = info };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures.Add(source.Name + "：" + Describe(exception));
                }
            }

            return new UpdateCheckResult
            {
                Status = UpdateStatus.Unavailable,
                Message = "都连不上（" + string.Join("；", failures) + "）。"
                    + "可以到发布页手动下载新版，覆盖旧的 exe 即可。",
            };
        }

        /// <summary>
        /// 下载到指定路径，并做**大小与 SHA256 双重校验**。
        /// 返回校验后的文件绝对路径。任何一步不对都抛异常，绝不返回一个"大概能用"的文件。
        /// </summary>
        public static async Task<string> DownloadAsync(
            UpdateInfo info, string targetPath, IProgress<double> progress, CancellationToken token)
        {
            if (info?.Asset == null || string.IsNullOrWhiteSpace(info.Asset.Url))
            {
                throw new InvalidOperationException("这个版本没有可下载的文件地址。");
            }

            string expected = info.Asset.Sha256;
            if (string.IsNullOrWhiteSpace(expected))
            {
                expected = await FetchSiblingShaAsync(info.Asset, token).ConfigureAwait(true);
            }

            if (string.IsNullOrWhiteSpace(expected))
            {
                throw new InvalidOperationException(
                    "发布方没有提供 SHA256 校验值，出于安全考虑不自动替换。请到发布页手动下载。");
            }

            byte[] bytes = await Transport.GetBytesAsync(info.Asset.Url, progress, token).ConfigureAwait(true);

            if (info.Asset.Size > 0 && bytes.LongLength != info.Asset.Size)
            {
                throw new InvalidOperationException(
                    $"下载不完整（{bytes.LongLength} 字节，应为 {info.Asset.Size} 字节）。请重试。");
            }

            string actual = Sha256Of(bytes);
            if (!string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"文件校验没通过（算得 {actual[..12]}…，应为 {expected[..12]}…），已放弃安装。请重试或到发布页手动下载。");
            }

            string directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(targetPath, bytes);
            return targetPath;
        }

        public static string Sha256Of(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        public static string Sha256OfFile(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        /// <summary>下载缓存目录：用户临时目录下按产品名分一层，退出时可整目录删掉。</summary>
        public static string CacheDirectory => Path.Combine(
            Path.GetTempPath(), AppInfo.ProductName, "update");

        public static void ClearCache()
        {
            try
            {
                if (Directory.Exists(CacheDirectory))
                {
                    Directory.Delete(CacheDirectory, true);
                }
            }
            catch (Exception)
            {
                // 清不掉不算错：下次覆盖写就是了
            }
        }

        // ── 解析 ────────────────────────────────────────────────

        /// <summary>解析 update.json（镜像与 GitHub 附件共用这一种格式）。</summary>
        public static UpdateInfo ParseManifest(string json, string sourceName, string releasePageUrl)
        {
            json = StripBom(json);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                string version = ReadString(root, "version");
                if (string.IsNullOrWhiteSpace(version))
                {
                    version = ReadString(root, "tag").TrimStart('v', 'V');
                }

                if (string.IsNullOrWhiteSpace(version))
                {
                    return null;
                }

                var notes = new List<string>();
                if (root.TryGetProperty("notes", out JsonElement notesElement))
                {
                    if (notesElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in notesElement.EnumerateArray())
                        {
                            string text = item.GetString();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                notes.Add(text.Trim());
                            }
                        }
                    }
                    else if (notesElement.ValueKind == JsonValueKind.String)
                    {
                        notes.AddRange(SplitNotes(notesElement.GetString()));
                    }
                }

                var info = new UpdateInfo
                {
                    Version = version.Trim(),
                    Tag = ReadString(root, "tag"),
                    Date = ReadString(root, "date"),
                    Title = ReadString(root, "title"),
                    Notes = notes,
                    ReleasePageUrl = releasePageUrl,
                    SourceName = sourceName,
                };

                if (root.TryGetProperty("asset", out JsonElement asset) && asset.ValueKind == JsonValueKind.Object)
                {
                    info.Asset = new UpdateAsset
                    {
                        Name = ReadString(asset, "name"),
                        Url = ReadString(asset, "url"),
                        Size = ReadLong(asset, "size"),
                        Sha256 = ReadString(asset, "sha256"),
                    };
                }

                return info;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>解析 GitHub API 的 releases/latest。</summary>
        public static UpdateInfo ParseGitHubRelease(string json, string sourceName)
        {
            json = StripBom(json);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                string tag = ReadString(root, "tag_name");
                if (string.IsNullOrWhiteSpace(tag))
                {
                    return null;
                }

                UpdateAsset asset = null;
                if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in assets.EnumerateArray())
                    {
                        string name = ReadString(item, "name");
                        if (string.Equals(name, "IEC104-Server.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            asset = new UpdateAsset
                            {
                                Name = name,
                                Url = ReadString(item, "browser_download_url"),
                                Size = ReadLong(item, "size"),
                            };
                            break;
                        }
                    }
                }

                string published = ReadString(root, "published_at");
                if (DateTime.TryParse(published, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal, out DateTime when))
                {
                    published = when.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }

                return new UpdateInfo
                {
                    Version = tag.TrimStart('v', 'V').Trim(),
                    Tag = tag,
                    Date = published,
                    Title = FirstLine(ReadString(root, "name")),
                    Notes = SplitNotes(ReadString(root, "body")),
                    Asset = asset,
                    ReleasePageUrl = ReadString(root, "html_url"),
                    SourceName = sourceName,
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// 把 Release 正文切成条目：只取 <c>- </c> 开头的行。
        /// 正文里还有标题、分隔线、安装说明，全塞进"更新说明"列表会很难看。
        /// </summary>
        public static IReadOnlyList<string> SplitNotes(string body)
        {
            var notes = new List<string>();
            if (string.IsNullOrWhiteSpace(body))
            {
                return notes;
            }

            foreach (string raw in body.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    notes.Add(line.Substring(2).Trim());
                }
            }

            return notes;
        }

        // ── 内部 ────────────────────────────────────────────────

        /// <summary>
        /// 拿 <c>&lt;exe url&gt;.sha256</c>（Release 里那个同名文本附件）。
        /// GitHub API 只有在较新的接口里才给 digest，所以宁可去取这个确定的文件。
        /// </summary>
        private static async Task<string> FetchSiblingShaAsync(UpdateAsset asset, CancellationToken token)
        {
            try
            {
                string text = await Transport.GetStringAsync(asset.Url + ".sha256", token).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                // 格式：<hash>  <filename>
                string first = text.Trim().Split(' ', '\t')[0].Trim();
                return first.Length == 64 ? first : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 去掉可能存在的 UTF-8 BOM。
        ///
        /// 谁会带 BOM：Windows PowerShell 5.1 的 `Set-Content -Encoding utf8` 会写 BOM，
        /// 而 pwsh 7 不写；镜像如果是用别的工具上传的也可能带。JSON 标准不允许 BOM，
        /// System.Text.Json 遇到开头的 U+FEFF 会直接抛——表现是"内容看不懂"，
        /// 用户看到的原因却完全对不上。一行 TrimStart 省掉一整类玄学问题。
        /// </summary>
        private static string StripBom(string text) =>
            string.IsNullOrEmpty(text) ? text : text.TrimStart('\uFEFF');

        private static bool TryParseRepository(out string owner, out string repo)
        {
            owner = null;
            repo = null;

            string url = AppInfo.RepositoryUrl?.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            // 允许 https://github.com/owner/repo、带 .git、带尾部斜杠
            string tail = url.TrimEnd('/');
            if (tail.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                tail = tail[..^4];
            }

            int slash = tail.LastIndexOf('/');
            int previous = slash > 0 ? tail.LastIndexOf('/', slash - 1) : -1;
            if (previous < 0 || slash <= previous + 1 || slash >= tail.Length - 1)
            {
                return false;
            }

            owner = tail.Substring(previous + 1, slash - previous - 1);
            repo = tail.Substring(slash + 1);
            return owner.Length > 0 && repo.Length > 0;
        }

        private static string ReleasePage() =>
            string.IsNullOrWhiteSpace(AppInfo.RepositoryUrl)
                ? string.Empty
                : AppInfo.RepositoryUrl.TrimEnd('/') + "/releases/latest";

        private static string ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        private static long ReadLong(JsonElement element, string name) =>
            element.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out long number)
                ? number
                : 0;

        private static string FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            int index = text.IndexOfAny(new[] { '\r', '\n' });
            return index < 0 ? text.Trim() : text[..index].Trim();
        }

        /// <summary>把异常翻成一句用户能行动的说明，不要把 HttpRequestException 原文丢到界面上。</summary>
        public static string Describe(Exception exception) => exception switch
        {
            TaskCanceledException => "连接超时",
            HttpRequestException request => request.StatusCode switch
            {
                HttpStatusCode.NotFound => "地址不存在（404）",
                HttpStatusCode.Forbidden => "被限流或拒绝（403）",
                null => "网络不通",
                _ => "HTTP " + (int)request.StatusCode.Value,
            },
            _ => exception.GetType().Name,
        };
    }
}
