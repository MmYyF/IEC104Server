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
        }

        private static TEnum ParseEnum<TEnum>(string value, TEnum fallback) where TEnum : struct, Enum
            => Enum.TryParse(value, ignoreCase: true, out TEnum parsed) ? parsed : fallback;
    }
}
