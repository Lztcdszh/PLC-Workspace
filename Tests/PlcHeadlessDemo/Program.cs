using System;
using System.Windows;
using System.Windows.Threading;
using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Model;
using PlcControlTestHost.Mock;

namespace PlcHeadlessDemo
{
    /// <summary>
    /// 无显示沙箱下的数据链路演示：复用真实的 MockConnectionManager，
    /// 把驱动 16 个 PLC 控件的那条数据流实时“流”出来，并模拟写入锁定与报警位注入。
    /// </summary>
    class Program
    {
        private static MockConnectionManager _mock;
        private static int _snapshotNo;
        private static readonly object _sync = new object();

        [STAThread]
        static void Main()
        {
            var dispatcher = Dispatcher.CurrentDispatcher;

            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("========================================================");
            Console.WriteLine("  PLC 控件库 · 数据链路实时演示（无显示沙箱）");
            Console.WriteLine("  说明：本环境无法渲染 WPF 窗口，以下演示驱动 16 个");
            Console.WriteLine("        控件的同一条数据链路（真实 MockConnectionManager）。");
            Console.WriteLine("========================================================");

            _mock = new MockConnectionManager();
            RegisterDemoTags();

            _mock.TagValueChanged += OnTagChanged;
            _mock.LogMessage += (s, m) => Console.WriteLine($"  [LOG] {m}");

            Console.WriteLine($"\n已注册标签数 : {_mock.TagCount}");
            Console.WriteLine($"连接前状态   : {_mock.ConnectionStatusText}");
            Console.WriteLine(">>> 建立仿真连接...");
            _mock.ConnectAsync().Wait();
            Console.WriteLine($"连接后状态   : {_mock.ConnectionStatusText}  (IsConnected={_mock.IsConnected})");
            Console.WriteLine("    ↓ 仿真定时器已启动，标签值开始随机游走并推送");

            // 每 1.5s 打印一次快照（对应仪表/指示灯/数值显示看到的值）
            var printer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            printer.Tick += (s, e) => PrintSnapshot();
            printer.Start();

            // 3.5s 时模拟用户在 PlcSwitch / PlcButton 上写入 pump=ON
            var writeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3500) };
            writeTimer.Tick += (s, e) =>
            {
                writeTimer.Stop();
                Console.WriteLine("\n>>> [演示] 模拟用户点击写入 pump = ON（PlcSwitch/PlcButton 写入路径）");
                _mock.WriteTagAsync("pump", true).Wait();
                Console.WriteLine("    → pump 已被“锁定”，仿真不再覆盖它（见后续快照保持 true）");
            };
            writeTimer.Start();

            // 5.5s 时注入报警位（PlcAlarmViewer 的 BitSet 评估路径）
            var alarmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5500) };
            alarmTimer.Tick += (s, e) =>
            {
                alarmTimer.Stop();
                Console.WriteLine("\n>>> [演示] 注入报警位 alarm_overload = true（PlcAlarmViewer 评估路径）");
                _mock.InjectValue("alarm_overload", true);
                var raw = _mock.GetTag("alarm_overload").Value;
                bool active = raw is bool b && b;            // 等价于 AlarmComparison.BitSet
                Console.WriteLine($"    → PlcAlarmViewer 评估: {(active ? "⚠ 报警激活（红色高亮 + 可 Ack）" : "正常")}");
            };
            alarmTimer.Start();

            // 9s 时断开并结束
            var stopper = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(9000) };
            stopper.Tick += (s, e) =>
            {
                stopper.Stop();
                Console.WriteLine("\n>>> 断开连接并结束演示");
                _mock.DisconnectAsync().Wait();
                Console.WriteLine($"    最终状态: {_mock.ConnectionStatusText}");
                dispatcher.InvokeShutdown();
            };
            stopper.Start();

            Dispatcher.Run();
            Console.WriteLine("\n演示结束。在 Windows 桌面运行 PlcControlTestHost.exe 可看到完整可视化界面。");
        }

        static void RegisterDemoTags()
        {
            // 显示核心：仪表/数值/位灯/字符串
            _mock.AddTag(PLCTag.Create<float>("temp", "temp", 25.0f));      // PlcGauge / PlcValueDisplay
            _mock.AddTag(PLCTag.Create<float>("speed", "speed", 50.0f));    // PlcTrendControl 主曲线
            _mock.AddTag(PLCTag.Create<int>("level", "level", 60));          // PlcBarIndicator
            _mock.AddTag(PLCTag.Create<short>("motor_state", "motor_state", 1)); // PlcMultiStateIndicator (1=运行)
            _mock.AddTag(PLCTag.Create<ushort>("status_word", "status_word", 0));   // PlcBitIndicator
            _mock.AddTag(PLCTag.CreateString("batch", "batch", 16, ""));  // PlcLabel
            // 输入交互
            _mock.AddTag(PLCTag.Create<bool>("pump", "pump", false));       // PlcSwitch / PlcButton
            // 报警
            _mock.AddTag(PLCTag.Create<bool>("alarm_overload", "alarm_overload", false)); // PlcAlarmViewer
        }

        static void OnTagChanged(object sender, TagService.TagChangedEventArgs e)
        {
            // 仅对“写入/报警”事件做即时提示，避免随机游走刷屏
            if (e.TagName == "pump" || e.TagName == "alarm_overload")
                Console.WriteLine($"  [EVENT] {e.TagName} <- {e.NewValue}");
        }

        static void PrintSnapshot()
        {
            lock (_sync)
            {
                _snapshotNo++;
                Console.WriteLine($"\n--- 快照 #{_snapshotNo}  (t={_snapshotNo * 1.5:F1}s) ---");
                foreach (var t in _mock.GetAllTagsSnapshot())
                {
                    string val = t.Value?.ToString() ?? "null";
                    string bar = "";
                    if (t.DataType == typeof(float) && t.Value is float f)
                        bar = "  " + BuildBar(f, 0, 100);
                    else if (t.DataType == typeof(int) && t.Value is int i)
                        bar = "  " + BuildBar(i, 0, 100);
                    Console.WriteLine($"  {t.Name,-14} = {val,-14} [{t.DataType.Name,-7}]{bar}");
                }
            }
        }

        static string BuildBar(double v, double min, double max)
        {
            int total = 20;
            double ratio = Math.Max(0, Math.Min(1, (v - min) / (max - min)));
            int filled = (int)Math.Round(ratio * total);
            return "[" + new string('#', filled) + new string('-', total - filled) + "]";
        }
    }
}
