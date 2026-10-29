using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IEC104.Core.CS101;

namespace IEC104.App.Services
{
    /// <summary>
    /// 一条自定义 ASDU 类型（磁盘格式）。
    /// 用字符串存 Code / 枚举名，不用数字下标——文件是给人看、可以手改的，
    /// 而且以后枚举顺序若调整，老文件也不会串成另一个类型。
    /// </summary>
    public sealed class CustomAsduTypeDto
    {
        /// <summary>类型标识的数值（1..255）</summary>
        public byte TypeId { get; set; }

        /// <summary>规约代号，如 M_BO_TA_1</summary>
        public string Code { get; set; }

        /// <summary>中文名</summary>
        public string DisplayName { get; set; }

        /// <summary>分类：遥信 / 遥测 / 遥控 / 遥脉 / 其他（旧值"电度""档位"读取时自动折算）</summary>
        public string Category { get; set; }

        /// <summary>信息元素格式（ElementFormat 的枚举名）</summary>
        public string Format { get; set; }

        /// <summary>时标方式（TimeTagKind 的枚举名）</summary>
        public string TimeTag { get; set; }
    }

    /// <summary>
    /// 应用设置 —— 与"工作区"分开的另一类数据。
    ///
    /// 区别要记清楚：
    ///   · 工作区（WorkspaceStore）= 一个工程的点表与通道，随工程文件走，可以有很多份；
    ///   · 设置（本文件）        = 这台机器上这个人的偏好，如自定义类型、缓冲上限，只有一份。
    ///
    /// 【为什么用 JSON 而不是 SQLite】
    /// ① 数据量：几条到几十条记录，启动读一次、改设置写一次。SQLite 的事务、索引、WAL
    ///    在这里一个都用不上，换来的是一个真实的故障面（原生库加载失败、文件锁、多进程写）。
    /// ② 可读可改：现场出问题时把 settings.json 发回来就能一眼看出配了什么；
    ///    改错了也能手工修好。SQLite 得拿工具去翻。
    /// ③ 单文件发布：SQLite 要带 e_sqlite3 原生库，自包含单文件发布时得走 native 抽取，
    ///    体积和启动解压都要付代价。JSON 用 BCL 自带的能力就够了，零依赖。
    /// ④ 一致性：工作区本来就是 JSON，配置用同一套序列化选项（缩进、不转义中文），
    ///    以后加字段的迁移套路也是同一个。
    /// 真到"几千条自定义类型 + 并发写"那天再换 SQLite 也不迟，那时 API 也只有这几行。
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>设置文件版本，将来字段迁移用</summary>
        public int Version { get; set; } = 1;

        /// <summary>用户自定义的 ASDU 类型</summary>
        public List<CustomAsduTypeDto> CustomAsduTypes { get; set; } = new();

        /// <summary>报文监视窗口最多保留多少条（超出丢最旧的）</summary>
        public int TrafficBufferLimit { get; set; } = 3000;

        /// <summary>
        /// 选中的主题 Id（如 "fresh-green"）。
        ///
        /// 1.8.2 起并进本文件：以前主题单独存一个 theme.txt，理由是"换个主题不想重写整个 JSON"。
        /// 但那个理由站不住——设置文件只有几十条记录、写一次是微秒级，
        /// 而多一个文件带来的代价是：用户打开程序目录看到两个"不知道干嘛的"文件，
        /// 备份/传配置时要记得两个都带上，漏一个就是"主题没跟过来"。
        /// 现在所有偏好只有一份真相（settings.json），null 表示"还没选过"，回落到第一套主题。
        /// </summary>
        public string Theme { get; set; }

        /// <summary>
        /// 侧边栏视图：false = 从站列表（扁平，默认），true = 通道/从站树。
        ///
        /// 默认给扁平的道理：现场 99% 是一个装置 = 一个 IP = 一个从站，
        /// "通道"是 104 协议层的实现细节（一路 TCP 监听），不是用户要组织的对象。
        /// 一进来就看见两棵树、还得先建通道再建从站，等于把协议的账算到用户头上。
        ///
        /// 侧边栏底部那对按钮和设置 → 视图里的选项写的是**同一个字段**：
        /// 用户在哪儿改都算数，下次启动还是这个样子。不做"运行期临时切换、重启就忘"，
        /// 那种设计看着严谨，实际只会让用户每开一次程序就重切一次。
        /// </summary>
        public bool SidebarTreeView { get; set; }

        /// <summary>
        /// 信号表格标签的分组方式：false = 按 ASDU 类型（默认），true = 按电力大类。
        ///
        /// 两种问法对应两种分法（"回了几个 M_ME_NC_1"vs"这个站有几个遥测"），
        /// 默认按类型是因为点表本来就是按类型铺的、和报文里的 TypeID 一一对得上；
        /// 但用户一旦习惯按大类看，就没有理由每次启动都给他翻回去。
        /// </summary>
        public bool PointsGroupByCategory { get; set; }

        // ── 工程相关（1.10）────────────────────────────────────
        //
        // 注意：这里存的是"这台机器上打开过什么工程"，不是工程内容本身。
        // 工程内容在 .iec104proj 文件里，跟着文件走；下面这几项跟着人走。

        /// <summary>
        /// 启动时是否先显示欢迎页。
        ///
        /// 默认**开**：工程会自动恢复（左侧从站列表已经有内容），但主区域先停在欢迎页，
        /// 让用户决定"继续改上个工程"还是"新建/打开别的"。关掉这个开关，
        /// 启动就直接进上次工程的点表。
        /// </summary>
        public bool ShowWelcomeOnStartup { get; set; } = true;

        /// <summary>上次打开的工程路径，启动时据此自动恢复</summary>
        public string LastProjectPath { get; set; }

        /// <summary>最近打开的工程（最多 <see cref="MaxRecentProjects"/> 条，最新的在最前）</summary>
        public List<RecentProjectDto> RecentProjects { get; set; } = new();

        /// <summary>
        /// 自动保存开关（1.10）。
        /// 只控制"结构改动"要不要在编辑过程中自动落盘；信号值不受它控制——
        /// 值的变化只要停下来就会自动写一次（见 ValueSaveDelaySeconds）。
        /// </summary>
        public bool AutoSaveEnabled { get; set; }

        /// <summary>结构改动停止操作后多久自动保存（秒）</summary>
        public int AutoSaveDelaySeconds { get; set; } = 5;

        /// <summary>
        /// 值停止变化多久后落盘（秒）。
        ///
        /// 这是"只保存最后一次值"的实现方式：每次值变化都重置计时器，
        /// 缓变驱动每秒改一次的话计时器永远不到期，全程零写盘；
        /// 人一停手，N 秒后写一次。
        /// </summary>
        public int ValueSaveDelaySeconds { get; set; } = 5;

        /// <summary>两次写盘之间的最小间隔（秒），防连续微调时反复写</summary>
        public int MinSaveIntervalSeconds { get; set; } = 3;

        /// <summary>最近工程列表的条数上限</summary>
        public const int MaxRecentProjects = 5;
    }

    /// <summary>最近工程列表里的一条（磁盘格式）</summary>
    public sealed class RecentProjectDto
    {
        public string Path { get; set; }

        public string Name { get; set; }

        public DateTime LastOpenedAt { get; set; }
    }

    /// <summary>
    /// 设置文件的读写。
    ///
    /// 【位置：默认与 exe 同目录（便携）】
    /// 1.8.1 起，settings.json 默认就放在 IEC104-Server.exe 旁边——
    /// 整个文件夹拷到另一台机器，自定义类型和主题都跟着走，不需要"再配一次"。
    ///
    /// 之前放在 %APPDATA%\IEC104Simulator，理由是"程序可能被放在只读目录或共享盘上，
    /// 配置写不进去最难解释"。这个顾虑是真的，但用"永远不写 exe 目录"来回避是过度补偿：
    /// 现场的常态是把工具解压到某个目录直接用，把配置藏在 AppData 里反而让人找不到、
    /// 也没法连工具带配置一起发给人复现。
    ///
    /// 现在的做法（见 <see cref="PortableFolder"/>）：
    ///   · 默认写 exe 目录；
    ///   · 启动时探一次可写性，只读时自动回退到 %APPDATA%，并把这句原因显示在设置页；
    ///   · 旧版本存在 %APPDATA% 的设置/主题，首次运行自动复制过来（原件保留）。
    /// 也就是说：便携是默认，只读是例外，例外一定会说出来。
    /// </summary>
    public static class AppSettingsStore
    {
        private const string StorageFile = "settings.json";

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            // 中文名不要被转成 \uXXXX，否则手工编辑时完全看不懂
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>当前生效的设置（只读使用；写入走 <see cref="Save"/>）</summary>
        public static AppSettings Current { get; private set; } = new();

        /// <summary>设置文件的完整路径（设置界面里展示给用户）</summary>
        public static string FilePath => Path.Combine(FolderPath, StorageFile);

        /// <summary>
        /// 设置文件所在目录 —— exe 目录（只读时是 %APPDATA% 的回退目录）。
        /// 崩溃日志（crash.log）也在这里；主题选择已并入本文件，不再单独占一个文件。
        /// 见 <see cref="PortableFolder"/>。
        /// </summary>
        public static string FolderPath => PortableFolder.Path;

        /// <summary>位置说明（迁移过 / 回退过才非空），设置页原样显示</summary>
        public static string LocationNote => PortableFolder.Note;

        /// <summary>设置变化后触发（报文缓冲上限等即时生效项要据此刷新）</summary>
        public static event Action Changed;

        /// <summary>最近一次读写失败的原因，界面用它提示；正常时为 null</summary>
        public static string LastError { get; private set; }

        /// <summary>启动时调用一次：读文件 → 解析 → 把自定义类型装进目录</summary>
        public static void Initialize()
        {
            Load();
            ApplyToCatalog();
        }

        /// <summary>从磁盘读；文件不存在或损坏都回落到默认设置，绝不让程序起不来</summary>
        public static void Load()
        {
            LastError = null;
            try
            {
                if (!File.Exists(FilePath))
                {
                    Current = new AppSettings();
                    return;
                }

                string json = File.ReadAllText(FilePath);
                AppSettings parsed = JsonSerializer.Deserialize<AppSettings>(json, Options);
                Current = parsed ?? new AppSettings();
                Normalize(Current);
            }
            catch (Exception exception)
            {
                // 配置读坏了不该阻断启动：用默认值继续，并把原因留给设置界面显示
                LastError = "读取设置失败：" + exception.Message;
                Current = new AppSettings();
            }
        }

        /// <summary>把当前设置写回磁盘</summary>
        public static bool Save()
        {
            LastError = null;
            try
            {
                Directory.CreateDirectory(FolderPath);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options),
                    new System.Text.UTF8Encoding(true));
                return true;
            }
            catch (Exception exception)
            {
                LastError = "保存设置失败：" + exception.Message;
                return false;
            }
        }

        /// <summary>把自定义类型同步进类型目录 + Core 注册表</summary>
        public static void ApplyToCatalog()
        {
            List<TypeDescriptor> descriptors = new();

            foreach (CustomAsduTypeDto dto in Current.CustomAsduTypes ?? new List<CustomAsduTypeDto>())
            {
                if (dto == null || dto.TypeId == 0 || string.IsNullOrWhiteSpace(dto.DisplayName))
                {
                    continue;
                }

                descriptors.Add(new TypeDescriptor
                {
                    Type = (TypeIdentifier)dto.TypeId,
                    Code = string.IsNullOrWhiteSpace(dto.Code) ? ((TypeIdentifier)dto.TypeId).ToString() : dto.Code.Trim(),
                    DisplayName = dto.DisplayName.Trim(),
                    // 旧设置里可能写着"电度""档位"这两个已废弃的分类名，
                    // 读进来先折算成现行口径，避免编辑时被静默改成"遥信"、页签里掉进"其他"
                    Category = string.IsNullOrWhiteSpace(dto.Category)
                        ? "其他"
                        : TypeIdCatalog.NormalizeCategory(dto.Category),
                    IsCustom = true,
                    Format = ParseEnum(dto.Format, ElementFormat.SinglePoint),
                    TimeTag = ParseEnum(dto.TimeTag, TimeTagKind.None)
                });
            }

            TypeIdCatalog.ReplaceCustomTypes(descriptors);
        }

        /// <summary>把目录里的自定义类型写回设置对象（设置界面保存时调用）</summary>
        public static void CaptureFromCatalog()
        {
            Current.CustomAsduTypes = TypeIdCatalog.CustomTypes
                .Select(descriptor => new CustomAsduTypeDto
                {
                    TypeId = (byte)descriptor.Type,
                    Code = descriptor.Code,
                    DisplayName = descriptor.DisplayName,
                    Category = descriptor.Category,
                    Format = descriptor.Format.ToString(),
                    TimeTag = descriptor.TimeTag.ToString()
                })
                .ToList();
        }

        /// <summary>恢复默认设置（清空自定义类型、缓冲上限回 3000）</summary>
        public static void Reset()
        {
            Current = new AppSettings();
            ApplyToCatalog();
            Save();
            Changed?.Invoke();
        }

        /// <summary>通知外部"设置变了"（保存后调用）</summary>
        public static void NotifyChanged() => Changed?.Invoke();

        // ── 工程历史（1.10）────────────────────────────────────

        /// <summary>
        /// 记住"这个工程被打开/保存过"：更新 LastProjectPath 与最近列表。
        /// 写盘失败不影响主流程（工程本身已经存好了），只是下次启动恢复不了。
        /// </summary>
        public static void RememberProject(string path, string name)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            Current.LastProjectPath = path;

            List<RecentProjectDto> recent = Current.RecentProjects ??= new List<RecentProjectDto>();

            // 同一个文件只留最新一条：按路径去重（忽略大小写，Windows 上同一文件可能大小写不同）
            recent.RemoveAll(item => item != null && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
            recent.Insert(0, new RecentProjectDto
            {
                Path = path,
                Name = string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileNameWithoutExtension(path) : name,
                LastOpenedAt = DateTime.Now
            });

            // 裁掉多余的：列表在欢迎页上展示，撑长了两屏也放不下
            while (recent.Count > AppSettings.MaxRecentProjects)
            {
                recent.RemoveAt(recent.Count - 1);
            }

            Save();
        }

        /// <summary>
        /// 忘掉"当前工程"（关闭工程、或上次的工程文件已经不存在了）。
        /// 只清 LastProjectPath，**不动最近列表**——用户可能只是关掉当前这个，
        /// 下次还想从最近列表里点开，把历史一并清了反而添乱。
        /// </summary>
        public static void ForgetCurrentProject()
        {
            if (string.IsNullOrEmpty(Current.LastProjectPath))
            {
                return;
            }

            Current.LastProjectPath = null;
            Save();
        }

        /// <summary>从最近列表里剔掉一条（点开时发现文件已不存在）</summary>
        public static void RemoveRecentProject(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Current.RecentProjects == null)
            {
                return;
            }

            int removed = Current.RecentProjects.RemoveAll(
                item => item != null && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));

            if (removed > 0)
            {
                if (string.Equals(Current.LastProjectPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    Current.LastProjectPath = null;
                }

                Save();
            }
        }

        /// <summary>清空最近列表</summary>
        public static void ClearRecentProjects()
        {
            Current.RecentProjects = new List<RecentProjectDto>();
            Save();
        }

        private static void Normalize(AppSettings settings)
        {
            settings.CustomAsduTypes ??= new List<CustomAsduTypeDto>();

            // 空白的主题 Id 一律按"没选过"处理：回落到第一套主题的是 ThemeService，
            // 这里只负责别让一个 "" 或一串空格流传下去
            if (string.IsNullOrWhiteSpace(settings.Theme))
            {
                settings.Theme = null;
            }

            // 缓冲上限：太小会把联调要看的报文冲掉，太大则界面会卡（每条都要占一行控件）
            if (settings.TrafficBufferLimit < 500)
            {
                settings.TrafficBufferLimit = 500;
            }
            else if (settings.TrafficBufferLimit > 50000)
            {
                settings.TrafficBufferLimit = 50000;
            }

            // 工程相关的三个秒数：夹到合理区间，别让手改坏的配置文件把程序变成"每秒写一次盘"
            settings.AutoSaveDelaySeconds = Math.Clamp(settings.AutoSaveDelaySeconds, 3, 60);
            settings.ValueSaveDelaySeconds = Math.Clamp(settings.ValueSaveDelaySeconds, 3, 60);
            settings.MinSaveIntervalSeconds = Math.Clamp(settings.MinSaveIntervalSeconds, 1, 60);

            // 最近工程：去掉空条目和重复路径，再裁到上限
            settings.RecentProjects ??= new List<RecentProjectDto>();
            List<RecentProjectDto> recent = new(settings.RecentProjects.Count);
            HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
            foreach (RecentProjectDto item in settings.RecentProjects)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Path))
                {
                    continue;
                }

                if (!seenPaths.Add(item.Path))
                {
                    continue;
                }

                recent.Add(item);
                if (recent.Count >= AppSettings.MaxRecentProjects)
                {
                    break;
                }
            }
            settings.RecentProjects = recent;
        }

        private static TEnum ParseEnum<TEnum>(string value, TEnum fallback) where TEnum : struct, Enum
            => Enum.TryParse(value, ignoreCase: true, out TEnum parsed) ? parsed : fallback;
    }
}
