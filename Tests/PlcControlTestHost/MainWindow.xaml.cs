using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using MyPLCUserControlLibrary;
using PlcControlTestHost.Mock;

namespace PlcControlTestHost
{
    public partial class MainWindow : Window
    {
        private readonly MockConnectionManager _mock = new();

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ConfigureTrend();
            ConfigureAlarms();

            // 将仿真连接下发给所有 PLC 控件（含 PlcPanel 内部子控件）
            foreach (var ctrl in FindVisualChildren<PlcControlBase>(this))
            {
                if (ctrl.Connection == null)
                    ctrl.Connection = _mock;
            }

            _ = _mock.ConnectAsync();
            UpdateConnectionUI();
        }

        #region 配置趋势与报警

        private void ConfigureTrend()
        {
            trendCtrl.Series = new System.Collections.ObjectModel.ObservableCollection<PlcTrendSeries>
            {
                new PlcTrendSeries { TagName = "t_sin", TagAddress = "t_sin", DataType = RangeDataType.Float, Color = Brushes.OrangeRed, Visible = true },
                new PlcTrendSeries { TagName = "t_cos", TagAddress = "t_cos", DataType = RangeDataType.Float, Color = Brushes.MediumPurple, Visible = true },
            };
        }

        private void ConfigureAlarms()
        {
            alarmViewer.Alarms = new System.Collections.ObjectModel.ObservableCollection<PlcAlarmDefinition>
            {
                new PlcAlarmDefinition { TagName = "temp", Comparison = AlarmComparison.Greater, Threshold = 80.0, Severity = PlcAlarmSeverity.Warning, Message = "温度偏高" },
                new PlcAlarmDefinition { TagName = "press", Comparison = AlarmComparison.Greater, Threshold = 90.0, Severity = PlcAlarmSeverity.Error, Message = "压力超限" },
                new PlcAlarmDefinition { TagName = "bits", Comparison = AlarmComparison.BitSet, BitIndex = 0, Severity = PlcAlarmSeverity.Error, Message = "急停触发" },
                new PlcAlarmDefinition { TagName = "run", Comparison = AlarmComparison.Equal, Threshold = false, Severity = PlcAlarmSeverity.Info, Message = "设备已停止" },
            };
        }

        #endregion

        #region 工具栏交互

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_mock.IsConnected)
                _ = _mock.DisconnectAsync();
            else
                _ = _mock.ConnectAsync();
            UpdateConnectionUI();
        }

        private void BtnSim_Click(object sender, RoutedEventArgs e)
        {
            if (_mock.IsSimulating) _mock.PauseSimulation();
            else _mock.ResumeSimulation();
            UpdateConnectionUI();
        }

        private void BtnUnlock_Click(object sender, RoutedEventArgs e)
        {
            _mock.UnlockAll();
        }

        private void UpdateConnectionUI()
        {
            btnConnect.Content = _mock.IsConnected ? "断开连接" : "连接";
            btnSim.Content = _mock.IsSimulating ? "暂停仿真" : "继续仿真";
            btnSim.IsEnabled = _mock.IsConnected;
            txtTagCount.Text = $"已注册标签：{_mock.TagCount}";
        }

        #endregion

        #region VisualTree 辅助

        public static IEnumerable<T> FindVisualChildren<T>(DependencyObject dep) where T : DependencyObject
        {
            if (dep == null) yield break;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(dep); i++)
            {
                var child = VisualTreeHelper.GetChild(dep, i);
                if (child is T t) yield return t;
                foreach (var c in FindVisualChildren<T>(child))
                    yield return c;
            }
        }

        #endregion
    }
}
