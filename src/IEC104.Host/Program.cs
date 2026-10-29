using System;
using System.Collections.Generic;
using System.Threading;
using IEC104.Core.CS101;
using IEC104.Core.CS104;
using IEC104.Simulator;
using IEC104.Simulator.IoModels;

namespace IEC104.Host
{
    /// <summary>
    /// 104 从站模拟器 —— 控制台入口。
    ///
    /// 本文件演示一次完整的装配过程：
    ///   1. 创建从站业务对象（SlaveStation）
    ///   2. 注册数据点（遥信 / 遥测 / 电度 / 步位置）
    ///   3. 创建协议层服务端（Iec104Server）并绑定回调
    ///   4. 启动监听，等待主站连接
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  【与原实现的核心差异：无激活校验】
    ///  原程序（IEC104ServerSimulator）启动流程为：
    ///      读取本地许可文件 → 取设备指纹 → 联网请求授权服务器 →
    ///      RSA 验签 Token → 校验有效期 → 通过后才允许启动服务
    ///  并且从站时间基准取自授权服务器返回的 GetServerTime。
    ///
    ///  本复刻版上述步骤【全部移除】，Main 里没有任何授权相关调用，
    ///  也不依赖任何外部服务器；时间一律使用本地时钟 DateTime.Now。
    ///  这带来的直接好处是可在电力内网等隔离环境中直接部署运行。
    /// ══════════════════════════════════════════════════════════════════
    ///
    /// 用法：
    ///   运行后监听 2404 端口（104 规约默认端口），
    ///   用任意 104 主站/调试工具连接 127.0.0.1:2404 即可发起总召、遥控、校时。
    ///   按 Q 回车退出。
    /// </summary>
    internal static class Program
    {
        /// <summary>默认监听端口，IEC 60870-5-104 规约约定为 2404</summary>
        private const int DefaultPort = 2404;

        /// <summary>
        /// 当前正在交互的连接。
        /// 用于把从站的应答帧精确回给发起请求的那个主站，
        /// 而不是广播给所有连接——多主站场景下这一点很重要。
        /// </summary>
        private static Iec104Connection _activeConnection;

        private static void Main(string[] args)
        {
            int port = args.Length > 0 && int.TryParse(args[0], out int parsedPort)
                ? parsedPort
                : DefaultPort;

            Console.OutputEncoding = System.Text.Encoding.UTF8;
            PrintBanner();

            // ── 1. 从站业务对象 ────────────────────────────────────
            SlaveStation station = new SlaveStation(ApplicationLayerParameters.CreateForCs104())
            {
                CommonAddress = 1,
                Name = "一号模拟从站"
            };

            // 数据点变化（自发上送）时，广播给所有已连接的主站
            station.AsduReadyToSend += asdu =>
            {
                if (_activeConnection != null)
                {
                    _activeConnection.SendAsdu(asdu);
                }
                else
                {
                    _server?.BroadcastAsdu(asdu);
                }
            };

            station.ControlCommandReceived += (ioa, value) =>
                Console.WriteLine($"  [遥控] 点号 {ioa} 被置为 {value}");

            station.ClockSync.ClockReceived += time =>
                Console.WriteLine($"  [校时] 收到主站时标：{(time?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "无效时标")}");

            // ── 2. 注册数据点 ──────────────────────────────────────
            RegisterDemoPoints(station);
            Console.WriteLine($"已注册 {station.Points.Count} 个数据点");

            // ── 3. 协议层服务端 ────────────────────────────────────
            _server = new Iec104Server();
            _server.LinkEventLogged += message => Console.WriteLine($"  {message}");
            _server.AsduReceived += (connection, asdu) =>
            {
                Console.WriteLine($"  [收] {asdu}");
                try
                {
                    // 把应答定向回发起请求的这条连接
                    _activeConnection = connection;
                    station.HandleReceivedAsdu(asdu);
                }
                catch (Exception exception)
                {
                    // 单帧处理失败不应拖垮整条链路，记录后继续
                    Console.WriteLine($"  [错误] 处理 ASDU 失败：{exception.Message}");
                }
                finally
                {
                    _activeConnection = null;
                }
            };

            _server.ClientConnected += connection =>
                Console.WriteLine("  主站已接入，等待 STARTDT 激活后即可总召");

            _server.Start(port);

            // ── 4. 模拟数据变化（触发自发上送）────────────────────
            using (Timer simulationTimer = new Timer(
                _ => SimulateDataChange(station), null, 3000, 3000))
            {
                Console.WriteLine();
                Console.WriteLine("模拟器运行中。输入 Q 回车退出。");
                while (true)
                {
                    string input = Console.ReadLine();

                    // 输入流被关闭（如以服务方式后台运行）时 ReadLine 返回 null，
                    // 此时必须退出，否则会在此处空转消耗 CPU
                    if (input == null)
                    {
                        break;
                    }
                    if (input.Equals("Q", StringComparison.OrdinalIgnoreCase)
                        || input.Equals("quit", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }

            _server.Stop();
            Console.WriteLine("已退出。");
        }

        private static Iec104Server _server;

        /// <summary>
        /// 注册一批演示用数据点。
        /// 点号规划参照变电站常见约定（可按工程需要调整）：
        ///   1xxx   遥信（单点，带 CP56 时标，用于 SOE）
        ///   4001+  遥测（短浮点）
        ///   6401+  电度（累计量）
        ///   10001  步位置（变压器档位）
        /// </summary>
        private static void RegisterDemoPoints(SlaveStation station)
        {
            // ── 遥信：断路器双位置（用双点类型，能表达中间态）──────
            for (int index = 0; index < 8; index++)
            {
                station.AddPoint(new DataPoint
                {
                    ObjectAddress = 1001 + index,
                    Type = TypeIdentifier.M_DP_NA_1,
                    Name = $"断路器{index + 1}位置",
                    Value = index % 2
                });
            }

            // ── 遥信：单点告警（带 7 字节时标，可上送 SOE）─────────
            for (int index = 0; index < 8; index++)
            {
                station.AddPoint(new DataPoint
                {
                    ObjectAddress = 2001 + index,
                    Type = TypeIdentifier.M_SP_TB_1,
                    Name = $"告警信号{index + 1}",
                    Value = 0
                });
            }

            // ── 遥测：短浮点（精度最高，新工程首选）────────────────
            string[] measurements = { "1号主变有功", "1号主变无功", "110kV母线电压", "10kV母线电压", "1号线路电流" };
            for (int index = 0; index < measurements.Length; index++)
            {
                station.AddPoint(new DataPoint
                {
                    ObjectAddress = 4001 + index,
                    Type = TypeIdentifier.M_ME_NC_1,
                    Name = measurements[index],
                    Value = 100.0 + (index * 10)
                });
            }

            // ── 遥测：归一化值（演示另一种编码）────────────────────
            station.AddPoint(new DataPoint
            {
                ObjectAddress = 4101,
                Type = TypeIdentifier.M_ME_NA_1,
                Name = "2号线路有功(归一化)",
                Value = 0.5
            });

            // ── 电度：累计量，计数量召唤时上送 ─────────────────────
            for (int index = 0; index < 4; index++)
            {
                station.AddPoint(new DataPoint
                {
                    ObjectAddress = 6401 + index,
                    Type = TypeIdentifier.M_IT_NA_1,
                    Name = $"电度{index + 1}",
                    Value = 10000 + (index * 100)
                });
            }

            // ── 步位置：变压器档位 ─────────────────────────────────
            station.AddPoint(new DataPoint
            {
                ObjectAddress = 10001,
                Type = TypeIdentifier.M_ST_NA_1,
                Name = "1号主变档位",
                Value = 3
            });
        }

        /// <summary>随机挑一个遥测点做小幅变化，触发自发上送（COT=3）</summary>
        private static readonly Random RandomSource = new Random();

        private static void SimulateDataChange(SlaveStation station)
        {
            IReadOnlyList<DataPoint> points = station.Points;
            if (points.Count == 0)
            {
                return;
            }

            DataPoint target = points[RandomSource.Next(points.Count)];

            // 只在链路已激活时才变更，避免产生一堆发不出去的事件
            if (_activeConnection == null && !HasActiveConnection())
            {
                return;
            }

            switch (target.Type)
            {
                case TypeIdentifier.M_ME_NC_1:
                    target.SetValue(Math.Round(target.Value + (RandomSource.NextDouble() * 2 - 1), 2));
                    break;

                case TypeIdentifier.M_ME_NA_1:
                    target.SetValue(Math.Round(target.Value + (RandomSource.NextDouble() * 0.2 - 0.1), 3));
                    break;

                case TypeIdentifier.M_DP_NA_1:
                case TypeIdentifier.M_SP_TB_1:
                    target.SetValue(target.Value != 0 ? 0 : 1);
                    break;

                case TypeIdentifier.M_IT_NA_1:
                    target.SetValue(target.Value + RandomSource.Next(1, 10));
                    break;

                default:
                    return;
            }

            Console.WriteLine($"  [自发上送] {target}");
        }

        private static bool HasActiveConnection()
        {
            return _server != null && _server.IsRunning;
        }

        private static void PrintBanner()
        {
            Console.WriteLine("==============================================");
            Console.WriteLine(" IEC 60870-5-104 从站模拟器（复刻版）");
            Console.WriteLine(" 协议层：IEC104.Core    业务层：IEC104.Simulator");
            Console.WriteLine(" 无激活校验 / 无需联网 / 无试用期限");
            Console.WriteLine("==============================================");
        }
    }
}
