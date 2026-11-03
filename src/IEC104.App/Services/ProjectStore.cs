using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IEC104.App.ViewModels;
using IEC104.Core.CS101;

namespace IEC104.App.Services
{
    /// <summary>
    /// 工程文件（<c>.iec104proj</c>）的存取。
    ///
    /// 【1.10 从"工作区"正名为"工程"】
    /// 存的还是同一棵树 —— 通道（端口/链路参数/三种地址长度）→ 从站（公共地址）→
    /// 信号（地址/名称/ASDU 类型/初始值/最后值）。改名是因为用户的心智模型是
    /// "一个工程 = 一套变电站配置"，而"工作区"听起来像 IDE 的临时目录。
    ///
    /// 【两个值字段：InitValue 与 Value】
    ///   · <c>InitValue</c> —— 初始值，编辑信号时填的，是「按初始值复位」的基准；
    ///   · <c>Value</c>     —— 最后一次写盘时界面上显示的值。
    /// 为什么两个都存：打开工程时希望"看起来和上次一样"（用 Value），
    /// 但联调完交出去之前又需要能一键回到设计值（用 InitValue）。
    ///
    /// 【品质位不存】
    /// 无效/闭锁/取代是运行态开关。存进去会变成"一打开工程就是闭锁状态"，
    /// 那不是配置，是上一次调试的残留，反而让人困惑。打开时一律复位为 false。
    ///
    /// 【为什么用 JSON】
    /// 工程文件要给人看、能手工改、能进版本库。ASDU 类型保存成枚举名（M_SP_NA_1）
    /// 而不是数字，这样以后枚举顺序调整，老文件也不会串类型。
    /// </summary>
    public static class ProjectStore
    {
        /// <summary>文件格式标识（1.10 起）</summary>
        public const string FormatTag = "iec104-simulator-project";

        /// <summary>
        /// 旧格式标识。1.9.x 及以前叫"工作区"，用 <c>.iec104ws</c>。
        /// 必须继续认：现场手里有老文件的用户升级后要能直接打开，不能要求他先转换。
        /// </summary>
        public const string LegacyFormatTag = "iec104-simulator-workspace";

        /// <summary>工程文件扩展名（含点）</summary>
        public const string Extension = ".iec104proj";

        /// <summary>旧扩展名，打开对话框的过滤器要一并接受</summary>
        public const string LegacyExtension = ".iec104ws";

        /// <summary>文件里的工程显示名（可与文件名不同），供打开后回填标题</summary>
        public const string DefaultName = "未命名工程";

        /// <summary>
        /// 超过这个点位数就不再缩进排版。
        ///
        /// 缩进的代价不是"好看一点"，是实打实的字节数：一个点一行字段、每层再缩进，
        /// 实测 100 万点缩进后是 252 MB，紧凑后约 90 MB。小于两万点时人还能翻着看，
        /// 大文件就只有程序读了，那就别为"人眼看不看得懂"付三倍体积。
        /// </summary>
        private const int IndentPointThreshold = 20000;

        /// <summary>UTF-8 BOM —— 与旧版 File.WriteAllText(..., UTF8Encoding(true)) 保持一致</summary>
        private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            // 中文名称不要被转成 \uXXXX，否则手工编辑时完全看不懂
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// ASDU 类型代号的字符串缓存。
        /// <c>TypeIdentifier.ToString()</c> 每次调用都会生成一个新字符串，
        /// 一百万点就是一百万个内容完全相同的 "M_SP_NA_1"，白白多占三四十兆。
        /// </summary>
        private static readonly Dictionary<TypeIdentifier, string> TypeCodeCache = new();

        private static string TypeCodeOf(TypeIdentifier type)
        {
            if (TypeCodeCache.TryGetValue(type, out string code))
            {
                return code;
            }

            code = type.ToString();
            TypeCodeCache[type] = code;
            return code;
        }

        /// <summary>
        /// 保存工程。
        ///
        /// 【1.9.7 内存优化：为什么不再 Serialize(对象树) —— 这一条是"大工程保存时卡死/闪退"的根因】
        /// 旧实现三步都是"整份物化"：
        ///   ① `Connections.Select(ToDto).ToList()` —— 先造出与点位数等量的 PointDto 对象；
        ///   ② `JsonSerializer.Serialize(...)` —— 再拼成一整个字符串（UTF-16，字符数 ≈ 字节数）；
        ///   ③ `File.WriteAllText(path, json)` —— 还要把这个字符串再编码成一份 UTF-8 字节数组。
        /// 100 万点时 ①②③ 叠起来要多占近 800 MB，而此刻程序本来已经握着 480 MB 的点表——
        /// 峰值直接顶到 1.3 GB，32 位进程或内存吃紧的机器上就是 OutOfMemory。
        ///
        /// 现在直接用 <see cref="Utf8JsonWriter"/> 往文件流里写：不建 DTO 树、不拼字符串、
        /// 不额外编码，额外占用是一块 64 KB 的写缓冲。
        /// </summary>
        public static void Save(MainWindowViewModel viewModel, string path, string projectName)
        {
            bool indented = CountPoints(viewModel) <= IndentPointThreshold;

            using FileStream stream = new(
                path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
            stream.Write(Utf8Bom, 0, Utf8Bom.Length);

            JsonWriterOptions writerOptions = new()
            {
                Indented = indented,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            using (Utf8JsonWriter writer = new(stream, writerOptions))
            {
                writer.WriteStartObject();
                writer.WriteString(nameof(ProjectDocument.Format), FormatTag);
                writer.WriteNumber(nameof(ProjectDocument.Version), 1);
                writer.WriteString(nameof(ProjectDocument.Name),
                    string.IsNullOrWhiteSpace(projectName) ? DefaultName : projectName);
                writer.WriteString(nameof(ProjectDocument.SavedAt), DateTime.Now);
                writer.WriteStartArray(nameof(ProjectDocument.Connections));

                foreach (ConnectionViewModel connection in viewModel.Connections)
                {
                    WriteConnection(writer, connection);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }
        }

        private static int CountPoints(MainWindowViewModel viewModel)
        {
            int total = 0;
            foreach (ConnectionViewModel connection in viewModel.Connections)
            {
                foreach (SlaveViewModel slave in connection.Slaves)
                {
                    total += slave.Points.Count;
                }
            }
            return total;
        }

        private static void WriteConnection(Utf8JsonWriter writer, ConnectionViewModel connection)
        {
            writer.WriteStartObject();
            WriteStringIfNotNull(writer, nameof(ConnectionDto.Name), connection.Name);
            writer.WriteString(nameof(ConnectionDto.Protocol), connection.Protocol.ToString());
            writer.WriteNumber(nameof(ConnectionDto.Port), connection.Port);
            WriteStringIfNotNull(writer, nameof(ConnectionDto.BindAddress), connection.BindAddress);
            WriteStringIfNotNull(writer, nameof(ConnectionDto.SerialPortName), connection.SerialPortName);
            writer.WriteNumber(nameof(ConnectionDto.BaudRate), connection.BaudRate);
            writer.WriteNumber(nameof(ConnectionDto.K), connection.K);
            writer.WriteNumber(nameof(ConnectionDto.W), connection.W);
            writer.WriteNumber(nameof(ConnectionDto.T1Seconds), connection.T1Seconds);
            writer.WriteNumber(nameof(ConnectionDto.T2Seconds), connection.T2Seconds);
            writer.WriteNumber(nameof(ConnectionDto.T3Seconds), connection.T3Seconds);
            writer.WriteNumber(nameof(ConnectionDto.CauseOfTransmissionLength), connection.CauseOfTransmissionLength);
            writer.WriteNumber(nameof(ConnectionDto.CommonAddressLength), connection.CommonAddressLength);
            writer.WriteNumber(nameof(ConnectionDto.InformationObjectAddressLength), connection.InformationObjectAddressLength);
            WriteStringIfNotNull(writer, nameof(ConnectionDto.NicId), connection.NicId);
            if (connection.AutoAddAddress)
            {
                // 默认关的开关：只在开的时候写，老版本读新文件也不会多出一堆 false
                writer.WriteBoolean(nameof(ConnectionDto.AutoAddAddress), true);
            }

            writer.WriteStartArray(nameof(ConnectionDto.Slaves));
            foreach (SlaveViewModel slave in connection.Slaves)
            {
                WriteSlave(writer, slave);
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        private static void WriteSlave(Utf8JsonWriter writer, SlaveViewModel slave)
        {
            writer.WriteStartObject();
            WriteStringIfNotNull(writer, nameof(SlaveDto.Name), slave.Name);
            writer.WriteNumber(nameof(SlaveDto.CommonAddress), slave.CommonAddress);

            writer.WriteStartArray(nameof(SlaveDto.Points));
            foreach (PointViewModel point in slave.Points)
            {
                writer.WriteStartObject();
                writer.WriteNumber(nameof(PointDto.Address), point.ObjectAddress);
                WriteStringIfNotNull(writer, nameof(PointDto.Name), point.Name);
                writer.WriteString(nameof(PointDto.Type), TypeCodeOf(point.Type?.Type ?? TypeIdentifier.M_SP_NA_1));

                // 顺序固定：先初始值，后最后值。两个字段都是必写的（不像品质位只在 true 时写），
                // 因为 0 也是一个合法取值，省略会让读侧分不清"没这个字段"和"值就是 0"。
                writer.WriteNumber(nameof(PointDto.InitValue), point.InitValue);
                writer.WriteNumber(nameof(PointDto.Value), point.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        /// <summary>值为 null 时整个字段省略（读侧会回落到默认名），与旧选项 WhenWritingNull 一致</summary>
        private static void WriteStringIfNotNull(Utf8JsonWriter writer, string name, string value)
        {
            if (value != null)
            {
                writer.WriteString(name, value);
            }
        }

        /// <summary>
        /// 载入工程，返回点位数。
        /// 采用的是"先清空再重建"：直接把对象树整体换掉，避免旧点位残留在协议层索引里。
        ///
        /// 1.9.7：改成直接从文件流反序列化。旧实现 <c>File.ReadAllText</c> 会先把
        /// 整份 JSON 读成一个字符串（252 MB 的 UTF-8 在 .NET 里是 252M 个 char ≈ 500 MB 堆），
        /// 再交给反序列化器——光这一步就让"打开大工程"多占半 GB。
        /// </summary>
        /// <param name="documentName">回传文件里记录的工程名（可能为 null，调用方自行回落）</param>
        public static int Load(MainWindowViewModel viewModel, string path, out string documentName)
        {
            ProjectDocument document;
            using (FileStream stream = new(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
            {
                document = JsonSerializer.Deserialize<ProjectDocument>(stream, Options)
                    ?? throw new InvalidDataException("工程文件为空或格式不正确。");
            }

            // 两种标识都放行：老文件是 "...-workspace"。格式本身没变（字段一直是兼容添加的），
            // 所以不需要转换，读进来即可；下次保存会自动写成新标识。
            if (!string.Equals(document.Format, FormatTag, StringComparison.Ordinal)
                && !string.Equals(document.Format, LegacyFormatTag, StringComparison.Ordinal))
            {
                throw new InvalidDataException("该文件不是 IEC104 仿真器的工程文件。");
            }

            // 先停掉并断开所有连接，否则旧 socket 还挂在旧从站上
            foreach (ConnectionViewModel existing in viewModel.Connections.ToList())
            {
                existing.Close();
            }
            viewModel.Connections.Clear();

            documentName = document.Name;
            int pointCount = 0;
            foreach (ConnectionDto connectionDto in document.Connections ?? new List<ConnectionDto>())
            {
                ConnectionViewModel connection = new()
                {
                    Name = connectionDto.Name ?? "连接",
                    Protocol = connectionDto.Protocol == nameof(ConnectionProtocol.Iec101)
                        ? ConnectionProtocol.Iec101
                        : ConnectionProtocol.Iec104,
                    Port = connectionDto.Port,
                    BindAddress = connectionDto.BindAddress ?? "0.0.0.0",
                    SerialPortName = connectionDto.SerialPortName ?? "COM1",
                    BaudRate = connectionDto.BaudRate,
                    K = connectionDto.K,
                    W = connectionDto.W,
                    T1Seconds = connectionDto.T1Seconds,
                    T2Seconds = connectionDto.T2Seconds,
                    T3Seconds = connectionDto.T3Seconds,
                    // 老文件里没有这三个字段（反序列化后是 0），
                    // 0 不是合法宽度，落到默认值 2/2/3 即可，不能直接塞进去
                    CauseOfTransmissionLength = IsValidLength(connectionDto.CauseOfTransmissionLength, 1, 2)
                        ? connectionDto.CauseOfTransmissionLength : 2,
                    CommonAddressLength = IsValidLength(connectionDto.CommonAddressLength, 1, 2)
                        ? connectionDto.CommonAddressLength : 2,
                    InformationObjectAddressLength = IsValidLength(connectionDto.InformationObjectAddressLength, 1, 3)
                        ? connectionDto.InformationObjectAddressLength : 3,
                    NicId = connectionDto.NicId,
                    AutoAddAddress = connectionDto.AutoAddAddress
                };

                foreach (SlaveDto slaveDto in connectionDto.Slaves ?? new List<SlaveDto>())
                {
                    SlaveViewModel slave = new()
                    {
                        Name = slaveDto.Name ?? "从站",
                        CommonAddress = slaveDto.CommonAddress
                    };

                    // 性能（第十二轮改）：原来是一个点一次 slave.AddPoint(...)。
                    // 单点入口内部会发一次集合 Reset，而 Reset 会触发主窗口重建标签页
                    // （RebuildPointTabs 要遍历整张点表）——20 万点就是 20 万次整表遍历，
                    // 打开工程直接变成"打不开"。现在先攒成 PointDraft 列表，
                    // 一台从站只调一次 AddPoints，只发一次 Reset。
                    List<PointDto> pointDtos = slaveDto.Points ?? new List<PointDto>();
                    List<PointDraft> drafts = new(pointDtos.Count);
                    List<PointDto> accepted = new(pointDtos.Count);
                    HashSet<int> seen = new(pointDtos.Count);

                    foreach (PointDto pointDto in pointDtos)
                    {
                        // 文件里万一有重号，先在这里剔掉：AddPoints 会跳过重号，
                        // 跳过后两个列表的下标就对不上了
                        if (!seen.Add(pointDto.Address))
                        {
                            continue;
                        }

                        // 用 Value（最后值）建点，打开后的画面和上次退出时一致
                        drafts.Add(new PointDraft(
                            pointDto.Address, ParseType(pointDto.Type), pointDto.Name ?? string.Empty,
                            EffectiveValue(pointDto)));
                        accepted.Add(pointDto);
                    }

                    List<PointViewModel> created = slave.AddPoints(drafts);
                    int apply = Math.Min(created.Count, accepted.Count);
                    for (int index = 0; index < apply; index++)
                    {
                        PointDto pointDto = accepted[index];
                        // 初始值单独赋：它是复位基准，不参与"变位"判定，所以放在建点之后写
                        created[index].InitValue = EffectiveInitValue(pointDto);
                        pointCount++;
                    }

                    // 顺序与原版一致：先入集合，再挂事件（报文监视才收得到）
                    connection.Slaves.Add(slave);
                    connection.HookSlave(slave);
                }

                viewModel.Connections.Add(connection);
            }

            viewModel.SelectedConnection = viewModel.Connections.FirstOrDefault();
            return pointCount;
        }

        /// <summary>
        /// 最后值：用 Value。老文件里 Value 与初始值是同一个字段，
        /// 直接拿来当"上次显示的值"正好。
        /// </summary>
        private static double EffectiveValue(PointDto dto) => dto.Value;

        /// <summary>
        /// 初始值：新文件读 InitValue；老文件没有这个字段（null）就回落到 Value。
        /// 回落而不是给 0——否则老工程一打开，"按初始值复位"会把所有点清零。
        /// </summary>
        private static double EffectiveInitValue(PointDto dto) => dto.InitValue ?? dto.Value;

        /// <summary>类型名解析失败时退回单点遥信，至少不让整份工程打不开</summary>
        private static TypeIdentifier ParseType(string name)
        {
            return Enum.TryParse(name, out TypeIdentifier type) ? type : TypeIdentifier.M_SP_NA_1;
        }

        /// <summary>
        /// 文件里的报文长度是否是合法宽度。
        /// 老文件没有这个字段，反序列化得到 0——直接用会让编解码整体错位。
        /// </summary>
        private static bool IsValidLength(int value, int min, int max) => value >= min && value <= max;

        // 说明：1.9.7 起不再有 ToDto(ConnectionViewModel) / ToDto(SlaveViewModel)。
        // 那两条把整个对象树物化成 DTO 列表的路径，正是"大工程保存时内存翻倍"的来源。
        // 现在写盘走 Utf8JsonWriter 直接遍历 ViewModel（见 Save / WriteConnection / WriteSlave），
        // DTO 类本身保留 —— 它们仍是反序列化的目标类型，也是磁盘格式的唯一定义。

        // ── 磁盘格式 ────────────────────────────────────────────

        internal sealed class ProjectDocument
        {
            public string Format { get; set; }
            public int Version { get; set; }

            /// <summary>工程显示名（1.10 新增）。老文件没有 → null，调用方回落到文件名</summary>
            public string Name { get; set; }

            public DateTime SavedAt { get; set; }
            public List<ConnectionDto> Connections { get; set; }
        }

        internal sealed class ConnectionDto
        {
            public string Name { get; set; }
            public string Protocol { get; set; }
            public int Port { get; set; }
            public string BindAddress { get; set; }
            public string SerialPortName { get; set; }
            public int BaudRate { get; set; }
            public int K { get; set; }
            public int W { get; set; }
            public int T1Seconds { get; set; }
            public int T2Seconds { get; set; }
            public int T3Seconds { get; set; }

            /// <summary>传送原因长度（字节）。老文件没有此字段，读取时按 0 处理并回落到 2</summary>
            public int CauseOfTransmissionLength { get; set; }

            /// <summary>公共地址长度（字节），同上回落到 2</summary>
            public int CommonAddressLength { get; set; }

            /// <summary>信息体地址长度（字节），同上回落到 3</summary>
            public int InformationObjectAddressLength { get; set; }

            /// <summary>
            /// 通信网卡 GUID（1.11 新增）。老文件没有此字段 → null → 启动不做自动添加。
            ///
            /// 存 GUID 不存名字：网卡名可以改，中英文系统的默认名也不同。
            /// 换台机器打开时 GUID 大概率对不上，界面会显示"网卡已不存在"并让用户重选。
            /// </summary>
            public string NicId { get; set; }

            /// <summary>启动时自动把 BindAddress 加到 NicId 那块网卡（1.11 新增，默认关）</summary>
            public bool AutoAddAddress { get; set; }

            public List<SlaveDto> Slaves { get; set; }
        }

        internal sealed class SlaveDto
        {
            public string Name { get; set; }
            public int CommonAddress { get; set; }
            public List<PointDto> Points { get; set; }
        }

        internal sealed class PointDto
        {
            public int Address { get; set; }
            public string Name { get; set; }
            public string Type { get; set; }

            /// <summary>
            /// 初始值（1.10 新增）。可空是刻意的：老文件里没有这个字段，
            /// 反序列化得到 null，读侧据此回落到 <see cref="Value"/>。
            /// 做成非空 double 就无法区分"没有这个字段"和"初始值就是 0"。
            /// </summary>
            public double? InitValue { get; set; }

            /// <summary>最后一次写盘时界面显示的值</summary>
            public double Value { get; set; }
        }
    }
}
