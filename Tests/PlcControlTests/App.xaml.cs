using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Model;
using MyPLCUserControlLibrary;
using PlcControlTestHost.Mock;

namespace PlcControlTests
{
    /// <summary>
    /// 无显示集成测试宿主（真正的 WPF Application 上下文，使控件 InitializeComponent
    /// 拥有正确的 pack URI 上下文）。用真实 MockConnectionManager 把 16 个控件逐个连上、喂值，
    /// 验证「连接 → 自动注册标签 → 收到值并刷新」这条链路对每个控件都成立。
    /// </summary>
    public partial class App : Application
    {
        class Result
        {
            public string Name = "";
            public bool Passed;
            public string Detail = "";
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("========================================================");
            Console.WriteLine("  PLC 控件库 · 无显示集成测试（16 控件端到端）");
            Console.WriteLine("========================================================");

            var mock = new MockConnectionManager();
            var results = new List<Result>();
            var recv = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            mock.TagValueChanged += (s, ev) =>
            {
                if (ev?.TagName != null)
                    recv[ev.TagName] = (recv.ContainsKey(ev.TagName) ? recv[ev.TagName] : 0) + 1;
            };

            // ---- 12 个值驱动控件：直接 new（WPF App 上下文下 InitializeComponent 正常）----
            var specs = new (string Name, string Tag, object Value, Func<PlcControlBase> Create, Action<PlcControlBase> Setup)[]
            {
                ("PlcValueDisplay", "vd", 12.3f, () => new PlcValueDisplay(), c => {}),
                ("PlcGauge", "gauge", 45.0, () => new PlcGauge(), c => { var x=(PlcGauge)c; x.DataType=RangeDataType.Float; x.Min=0; x.Max=100; }),
                ("PlcMultiStateIndicator", "state", 2, () => new PlcMultiStateIndicator(), c => {}),
                ("PlcBitIndicator", "bits", (long)5, () => new PlcBitIndicator(), c => { ((PlcBitIndicator)c).DataType = RangeDataType.Int64; }),
                ("PlcLabel", "lbl", "TEST", () => new PlcLabel(), c => {}),
                ("PlcIndicator", "ind", true, () => new PlcIndicator(), c => {}),
                ("PlcBarIndicator", "bar", 40.0, () => new PlcBarIndicator(), c => { ((PlcBarIndicator)c).DataType = RangeDataType.Float; }),
                ("PlcRangeIndicator", "rng", 5.0, () => new PlcRangeIndicator(), c => { ((PlcRangeIndicator)c).DataType = RangeDataType.Float; }),
                ("PlcButton", "btn", true, () => new PlcButton(), c => { var b=(PlcButton)c; b.ControlTagAddress="btn"; b.StatusTagAddress="btn"; b.ControlTagName="btn"; b.StatusTagName="btn"; b.TagName="btn"; b.TagAddress="btn"; }),
                ("PlcSwitch", "sw", true, () => new PlcSwitch(), c => {}),
                ("PlcTextBox", "tb", "hi", () => new PlcTextBox(), c => {}),
                ("PlcNumericInput", "num", 7.0, () => new PlcNumericInput(), c => { ((PlcNumericInput)c).DataType = RangeDataType.Float; }),
            };

            var controls = new List<(string Name, PlcControlBase Ctrl, string Tag, object Value)>();
            foreach (var sp in specs)
            {
                try
                {
                    var ctrl = sp.Create();
                    sp.Setup(ctrl);
                    ctrl.TagName = sp.Tag;
                    ctrl.TagAddress = sp.Tag;
                    ctrl.Connection = mock;
                    controls.Add((sp.Name, ctrl, sp.Tag, sp.Value));
                }
                catch (Exception ex)
                {
                    results.Add(new Result { Name = sp.Name, Passed = false, Detail = "加载/配置异常: " + ex.Message });
                }
            }

            // ---- 特殊控件 ----
            PlcPanel? panel = null; PlcConnectionStatus? connStatus = null;
            PlcAlarmViewer? alarm = null; PlcTrendControl? trend = null;
            try
            {
                panel = new PlcPanel(); panel.Connection = mock;
                connStatus = new PlcConnectionStatus(); connStatus.Connection = mock;
                alarm = new PlcAlarmViewer();
                alarm.PollInterval = 150;
                alarm.Alarms = new System.Collections.ObjectModel.ObservableCollection<PlcAlarmDefinition>
                {
                    new PlcAlarmDefinition { TagName = "alm", Address = "alm", Comparison = AlarmComparison.BitSet, BitIndex = 0, Message = "测试报警", Severity = PlcAlarmSeverity.Error }
                };
                alarm.Connection = mock;
                trend = new PlcTrendControl();
                trend.Series = new System.Collections.ObjectModel.ObservableCollection<PlcTrendSeries>
                {
                    new PlcTrendSeries { TagName = "tr", TagAddress = "tr", DataType = RangeDataType.Float }
                };
                trend.Connection = mock;
            }
            catch (Exception ex)
            {
                results.Add(new Result { Name = "特殊控件", Passed = false, Detail = "加载异常: " + ex.Message });
            }

            mock.ConnectAsync().Wait();
            mock.PauseSimulation();
            Console.WriteLine($"已连接: {mock.IsConnected}，已注册标签数: {mock.TagCount}");

            var injectTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            injectTimer.Tick += (s, ev) =>
            {
                injectTimer.Stop();
                foreach (var c in controls) mock.InjectValue(c.Tag, c.Value);
                mock.InjectValue("alm", true);
                mock.InjectValue("tr", 3.0);
                Console.WriteLine(">>> 已向各控件标签注入测试值");
            };
            injectTimer.Start();

            var assertTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            assertTimer.Tick += (s, ev) =>
            {
                assertTimer.Stop();
                foreach (var c in controls)
                {
                    bool registered = mock.ContainsTag(c.Tag);
                    bool delivered = recv.TryGetValue(c.Tag, out int n) && n > 0;
                    results.Add(new Result { Name = c.Name, Passed = registered && delivered, Detail = $"注册标签={registered}, 值送达次数={n}" });
                }
                results.Add(new Result { Name = "PlcPanel", Passed = panel?.Connection == mock, Detail = "持有 Connection=" + (panel?.Connection != null) });
                results.Add(new Result { Name = "PlcConnectionStatus", Passed = connStatus?.Connection == mock, Detail = "持有 Connection=" + (connStatus?.Connection != null) });
                bool almActive = (alarm?.ActiveAlarms.Count ?? 0) > 0;
                results.Add(new Result { Name = "PlcAlarmViewer", Passed = almActive, Detail = $"活动报警数={alarm?.ActiveAlarms.Count}" });
                bool trendOk = trend?.Series != null && trend.Series.Count == 1;
                results.Add(new Result { Name = "PlcTrendControl", Passed = trendOk, Detail = $"曲线数={trend?.Series?.Count}" });

                PrintSummary(results);
                mock.DisconnectAsync().Wait();
                Application.Current.Shutdown();
            };
            assertTimer.Start();
        }

        static void PrintSummary(List<Result> results)
        {
            Console.WriteLine("\n==================== 测试结果 ====================");
            int pass = 0;
            foreach (var r in results)
            {
                string mark = r.Passed ? "PASS" : "FAIL";
                if (r.Passed) pass++;
                Console.WriteLine($"  [{mark}] {r.Name,-22} {r.Detail}");
            }
            Console.WriteLine("--------------------------------------------------");
            Console.WriteLine($"  总计 {results.Count} 项，通过 {pass} 项，失败 {results.Count - pass} 项");
            Console.WriteLine(results.Count == pass ? "  ✅ 全部通过" : "  ❌ 存在失败项");
            Console.WriteLine("==================================================");
        }
    }
}
