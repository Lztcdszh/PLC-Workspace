using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MyPLCUserControlLibrary
{
    /// <summary>
    /// 报警汇总列表：依据 <see cref="Alarms"/> 定义（标签/比较/阈值/位/消息/级别），轮询连接读取并实时评估，
    /// 维护活动报警列表（时间 / 级别 / 消息 / 确认），按级别配色，支持确认与最大条数。继承 PlcControlBase。
    /// </summary>
    public partial class PlcAlarmViewer : PlcControlBase
    {
        private readonly DispatcherTimer _pollTimer = new DispatcherTimer();
        private bool _isConnected;

        public PlcAlarmViewer()
        {
            InitializeComponent();

            // 必须在解析 XAML 集合属性元素（<PlcAlarmViewer.Alarms>…）之前确保集合非 null，
            // 否则 XAML 解析器往集合加子项时会抛 “集合属性 Alarms 为 null”。
            Alarms ??= new ObservableCollection<PlcAlarmDefinition>();

            if (DesignerProperties.GetIsInDesignMode(this))
            {
                ActiveAlarms.Add(new AlarmEntry { Time = DateTime.Now.AddMinutes(-2), Severity = PlcAlarmSeverity.Warning, Message = "1#皮带堵料", Acked = false });
                ActiveAlarms.Add(new AlarmEntry { Time = DateTime.Now.AddMinutes(-1), Severity = PlcAlarmSeverity.Error, Message = "急停按钮触发", Acked = false });
                return;
            }

            _pollTimer.Tick += PollTimer_Tick;
            Loaded += (s, e) => UpdateEmptyHint();
        }

        #region 依赖属性

        public static readonly DependencyProperty AlarmsProperty =
            DependencyProperty.Register(nameof(Alarms), typeof(ObservableCollection<PlcAlarmDefinition>), typeof(PlcAlarmViewer),
                new PropertyMetadata(null, (d, e) => ((PlcAlarmViewer)d).OnAlarmsChanged()));

        /// <summary>报警定义集合（在 XAML 或 ViewModel 中配置）。</summary>
        public ObservableCollection<PlcAlarmDefinition>? Alarms
        {
            get => (ObservableCollection<PlcAlarmDefinition>?)GetValue(AlarmsProperty);
            set => SetValue(AlarmsProperty, value);
        }

        public static readonly DependencyProperty MaxItemsProperty =
            DependencyProperty.Register(nameof(MaxItems), typeof(int), typeof(PlcAlarmViewer), new PropertyMetadata(100, (d, e) => ((PlcAlarmViewer)d).TrimItems()));

        public int MaxItems
        {
            get => (int)GetValue(MaxItemsProperty);
            set => SetValue(MaxItemsProperty, value);
        }

        public static readonly DependencyProperty PollIntervalProperty =
            DependencyProperty.Register(nameof(PollInterval), typeof(int), typeof(PlcAlarmViewer),
                new PropertyMetadata(500, (d, e) => ((PlcAlarmViewer)d).ApplyPollInterval()));

        /// <summary>报警轮询间隔（毫秒）。</summary>
        public int PollInterval
        {
            get => (int)GetValue(PollIntervalProperty);
            set => SetValue(PollIntervalProperty, value);
        }

        public static readonly DependencyProperty ShowAckProperty =
            DependencyProperty.Register(nameof(ShowAck), typeof(bool), typeof(PlcAlarmViewer), new PropertyMetadata(true));

        public bool ShowAck
        {
            get => (bool)GetValue(ShowAckProperty);
            set => SetValue(ShowAckProperty, value);
        }

        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(nameof(Header), typeof(string), typeof(PlcAlarmViewer), new PropertyMetadata("报警列表"));

        public string Header
        {
            get => (string)GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        #endregion

        #region 活动报警集合

        /// <summary>当前活动（且未消除）的报警列表，供 ItemsControl 绑定。</summary>
        public ObservableCollection<AlarmEntry> ActiveAlarms { get; } = new ObservableCollection<AlarmEntry>();

        #endregion

        #region PlcControlBase 钩子

        protected override bool ShouldSubscribe => false;
        protected override PLCTag? CreateDefaultTag() => null;
        protected override void OnPlcValueReceived(object? value) { }

        protected override void AutoRegisterTags()
        {
            if (ConnectionManager == null || !AutoRegister) return;
            if (Alarms == null) return;
            foreach (var def in Alarms)
            {
                if (string.IsNullOrWhiteSpace(def.Address)) continue;
                string name = string.IsNullOrWhiteSpace(def.TagName) ? def.Address : def.TagName;
                def.TagName = name; // 规范化，便于后续读取
                if (!ContainsTagSafe(name))
                {
                    try
                    {
                        var tag = CreateTagByDataType(name, def.Address, def.DataType);
                        tag.IsMonitored = true;
                        ConnectionManager.AddTag(tag);
                    }
                    catch { }
                }
            }
        }

        protected override void OnConnectionStateChanged(bool isConnected)
        {
            _isConnected = isConnected;
            if (isConnected)
            {
                ApplyPollInterval();
                _pollTimer.Start();
            }
            else
            {
                _pollTimer.Stop();
                ActiveAlarms.Clear();
                UpdateEmptyHint();
            }
        }

        #endregion

        #region 评估逻辑

        private void PollTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isConnected || ConnectionManager == null || Alarms == null) return;

            foreach (var def in Alarms)
            {
                if (string.IsNullOrWhiteSpace(def.TagName)) continue;
                object? raw = null;
                try
                {
                    var tag = ConnectionManager.GetTag(def.TagName);
                    raw = tag?.Value;
                }
                catch { }

                bool active = raw != null && Evaluate(def, raw);
                var existing = ActiveAlarms.FirstOrDefault(a => a.Source == def.TagName);
                if (active)
                {
                    if (existing == null)
                    {
                        ActiveAlarms.Insert(0, new AlarmEntry
                        {
                            Time = DateTime.Now,
                            Severity = def.Severity,
                            Message = def.Message,
                            Source = def.TagName,
                            Acked = false,
                        });
                        TrimItems();
                    }
                }
                else
                {
                    if (existing != null)
                        ActiveAlarms.Remove(existing);
                }
            }
            UpdateEmptyHint();
        }

        private bool Evaluate(PlcAlarmDefinition def, object value)
        {
            try
            {
                switch (def.Comparison)
                {
                    case AlarmComparison.Greater:
                        return Convert.ToDouble(value, CultureInfo.CurrentCulture) > Convert.ToDouble(def.Threshold, CultureInfo.CurrentCulture);
                    case AlarmComparison.GreaterOrEqual:
                        return Convert.ToDouble(value, CultureInfo.CurrentCulture) >= Convert.ToDouble(def.Threshold, CultureInfo.CurrentCulture);
                    case AlarmComparison.Less:
                        return Convert.ToDouble(value, CultureInfo.CurrentCulture) < Convert.ToDouble(def.Threshold, CultureInfo.CurrentCulture);
                    case AlarmComparison.LessOrEqual:
                        return Convert.ToDouble(value, CultureInfo.CurrentCulture) <= Convert.ToDouble(def.Threshold, CultureInfo.CurrentCulture);
                    case AlarmComparison.Equal:
                        return Convert.ToDouble(value, CultureInfo.CurrentCulture) == Convert.ToDouble(def.Threshold, CultureInfo.CurrentCulture);
                    case AlarmComparison.NotEqual:
                        return Convert.ToDouble(value, CultureInfo.CurrentCulture) != Convert.ToDouble(def.Threshold, CultureInfo.CurrentCulture);
                    case AlarmComparison.BitSet:
                        return (Convert.ToInt64(value, CultureInfo.CurrentCulture) >> def.BitIndex & 1) == 1;
                    case AlarmComparison.BitClear:
                        return (Convert.ToInt64(value, CultureInfo.CurrentCulture) >> def.BitIndex & 1) == 0;
                    default:
                        return false;
                }
            }
            catch { return false; }
        }

        private PLCTag CreateTagByDataType(string name, string address, RangeDataType dt)
        {
            return dt switch
            {
                RangeDataType.Int8 => PLCTag.Create<sbyte>(name, address, (sbyte)0, name),
                RangeDataType.UInt8 => PLCTag.Create<byte>(name, address, (byte)0, name),
                RangeDataType.Int16 => PLCTag.Create<short>(name, address, (short)0, name),
                RangeDataType.UInt16 => PLCTag.Create<ushort>(name, address, (ushort)0, name),
                RangeDataType.Int32 => PLCTag.Create<int>(name, address, 0, name),
                RangeDataType.UInt32 => PLCTag.Create<uint>(name, address, 0u, name),
                RangeDataType.Int64 => PLCTag.Create<long>(name, address, 0L, name),
                RangeDataType.UInt64 => PLCTag.Create<ulong>(name, address, 0ul, name),
                RangeDataType.Float => PLCTag.Create<float>(name, address, 0.0f, name),
                RangeDataType.Double => PLCTag.Create<double>(name, address, 0.0, name),
                _ => PLCTag.Create<float>(name, address, 0.0f, name),
            };
        }

        #endregion

        #region 辅助

        private void OnAlarmsChanged()
        {
            // 报警定义变更：清空当前活动项，待下次轮询重建
            ActiveAlarms.Clear();
            UpdateEmptyHint();
        }

        private void TrimItems()
        {
            while (ActiveAlarms.Count > MaxItems)
                ActiveAlarms.RemoveAt(ActiveAlarms.Count - 1);
        }

        private void ApplyPollInterval()
        {
            if (PollInterval > 0)
                _pollTimer.Interval = TimeSpan.FromMilliseconds(PollInterval);
        }

        private void UpdateEmptyHint()
        {
            if (PART_EmptyHint != null)
                PART_EmptyHint.Visibility = ActiveAlarms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AckButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is AlarmEntry entry)
                entry.Acked = true;
        }

        protected override void OnUnloadedInternal()
        {
            _pollTimer.Stop();
        }

        #endregion
    }

    /// <summary>报警比较方式。</summary>
    public enum AlarmComparison
    {
        Greater, GreaterOrEqual, Less, LessOrEqual, Equal, NotEqual, BitSet, BitClear,
    }

    /// <summary>单条报警定义（在 XAML / ViewModel 中配置）。</summary>
    public class PlcAlarmDefinition
    {
        public string TagName { get; set; } = string.Empty;   // 自动注册后会被规范化为有效名
        public string Address { get; set; } = string.Empty;
        public RangeDataType DataType { get; set; } = RangeDataType.Float;
        public AlarmComparison Comparison { get; set; } = AlarmComparison.Greater;
        public object? Threshold { get; set; }
        public int BitIndex { get; set; }
        public string Message { get; set; } = string.Empty;
        public PlcAlarmSeverity Severity { get; set; } = PlcAlarmSeverity.Warning;
    }

    /// <summary>活动报警条目（绑定到列表项）。</summary>
    public class AlarmEntry : INotifyPropertyChanged
    {
        private bool _acked;
        public DateTime Time { get; set; }
        public PlcAlarmSeverity Severity { get; set; }
        public string Message { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public bool Acked
        {
            get => _acked;
            set { _acked = value; OnPropertyChanged(nameof(Acked)); }
        }

        public string TimeText => Time.ToString("HH:mm:ss");
        public Brush SeverityBrush => PlcPalette.BrushForSeverity(Severity);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
