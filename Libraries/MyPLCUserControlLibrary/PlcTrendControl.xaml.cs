using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MyPLCUserControlLibrary
{
    public partial class PlcTrendControl : PlcControlBase
    {
        private object? _currentRawValue;
        private DispatcherTimer? _samplingTimer;
        private Queue<double> _dataQueue = new Queue<double>();
        private bool _isPaused = false;
        private double _currentMinY = 0;
        private double _currentMaxY = 100;

        private double _plotWidth, _plotHeight;

        // 多曲线：主序列使用 _dataQueue（保持不变），以下为附加序列
        private readonly List<TrendBuffer> _extraSeries = new();

        private sealed class TrendBuffer
        {
            public Queue<double> Data { get; } = new Queue<double>();
            public Brush? Color { get; set; }
            public string Name { get; set; } = string.Empty;
            public RangeDataType DataType { get; set; }
        }

        public PlcTrendControl()
        {
            InitializeComponent();

            // 必须在解析 XAML 集合属性元素（<PlcTrendControl.Series>…）之前确保集合非 null，
            // 否则 XAML 解析器往集合加子项时会抛 “集合属性 Series 为 null”。
            Series ??= new ObservableCollection<PlcTrendSeries>();

            // 右键上下文菜单的数据上下文指向本控件，使菜单项的勾选项能双向绑定到依赖属性
            if (TrendContextMenu != null)
                TrendContextMenu.DataContext = this;
            ContextMenuOpening += TrendContextMenu_OnOpening;

            Width = 420;
            Height = 260;
            Background = Brushes.White;
            BorderBrush = new SolidColorBrush(Color.FromRgb(220, 223, 230));
            BorderThickness = new Thickness(1);
            StrokeBrush = new SolidColorBrush(Color.FromRgb(64, 158, 255));
            StrokeThickness = 2.2;
            GridBrush = new SolidColorBrush(Color.FromRgb(236, 240, 245));
            AxisBrush = new SolidColorBrush(Color.FromRgb(144, 147, 153));
            SamplingInterval = 1000;
            MaxDataPoints = 60;
            MinY = 0;
            MaxY = 100;
            AutoScaleY = true;
            ShowAxisLabels = true;
            SmoothCurve = true;
            ShowArea = true;
            ShowDataPoints = false;
            HighlightLast = true;
            ShowLegend = true;
            ShowStats = true;

            Loaded += (s, e) => UpdatePlotArea();
            SizeChanged += (s, e) => UpdatePlotArea();
            PART_PlotArea.MouseMove += OnMouseMove;
            PART_PlotArea.MouseLeave += (s, e) => HideMouseTrack();
        }

        #region 依赖属性

        // 数据类型（连接/标签/自动注册由 PlcControlBase 提供）
        public static readonly DependencyProperty DataTypeProperty =
            DependencyProperty.Register(nameof(DataType), typeof(RangeDataType), typeof(PlcTrendControl),
                new PropertyMetadata(RangeDataType.Float, (d, e) => ((PlcTrendControl)d).OnTagChanged()));

        public RangeDataType DataType
        {
            get => (RangeDataType)GetValue(DataTypeProperty);
            set => SetValue(DataTypeProperty, value);
        }

        public static readonly DependencyProperty SamplingIntervalProperty =
            DependencyProperty.Register(nameof(SamplingInterval), typeof(int), typeof(PlcTrendControl),
                new PropertyMetadata(1000, (d, e) => ((PlcTrendControl)d).RestartSampling()));

        public int SamplingInterval
        {
            get => (int)GetValue(SamplingIntervalProperty);
            set => SetValue(SamplingIntervalProperty, value);
        }

        public static readonly DependencyProperty MaxDataPointsProperty =
            DependencyProperty.Register(nameof(MaxDataPoints), typeof(int), typeof(PlcTrendControl),
                new PropertyMetadata(60, (d, e) => ((PlcTrendControl)d).OnMaxPointsChanged()));

        public int MaxDataPoints
        {
            get => (int)GetValue(MaxDataPointsProperty);
            set => SetValue(MaxDataPointsProperty, value);
        }

        public static readonly DependencyProperty StrokeBrushProperty =
            DependencyProperty.Register(nameof(StrokeBrush), typeof(Brush), typeof(PlcTrendControl),
                new PropertyMetadata(Brushes.SteelBlue, (d, e) => ((PlcTrendControl)d).OnStrokeChanged()));

        public Brush StrokeBrush
        {
            get => (Brush)GetValue(StrokeBrushProperty);
            set => SetValue(StrokeBrushProperty, value);
        }

        public static readonly DependencyProperty StrokeThicknessProperty =
            DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(PlcTrendControl),
                new PropertyMetadata(2.0, (d, e) => ((PlcTrendControl)d).RedrawCurveAndArea()));

        public double StrokeThickness
        {
            get => (double)GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        public static readonly DependencyProperty GridBrushProperty =
            DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(PlcTrendControl),
                new PropertyMetadata(Brushes.LightGray, (d, e) => ((PlcTrendControl)d).DrawGridAndAxis()));

        public Brush GridBrush
        {
            get => (Brush)GetValue(GridBrushProperty);
            set => SetValue(GridBrushProperty, value);
        }

        public static readonly DependencyProperty AxisBrushProperty =
            DependencyProperty.Register(nameof(AxisBrush), typeof(Brush), typeof(PlcTrendControl),
                new PropertyMetadata(Brushes.DarkGray, (d, e) => ((PlcTrendControl)d).DrawGridAndAxis()));

        public Brush AxisBrush
        {
            get => (Brush)GetValue(AxisBrushProperty);
            set => SetValue(AxisBrushProperty, value);
        }

        public static readonly DependencyProperty MinYProperty =
            DependencyProperty.Register(nameof(MinY), typeof(double), typeof(PlcTrendControl),
                new PropertyMetadata(0d, (d, e) => ((PlcTrendControl)d).OnYRangeChanged()));

        public double MinY
        {
            get => (double)GetValue(MinYProperty);
            set => SetValue(MinYProperty, value);
        }

        public static readonly DependencyProperty MaxYProperty =
            DependencyProperty.Register(nameof(MaxY), typeof(double), typeof(PlcTrendControl),
                new PropertyMetadata(100d, (d, e) => ((PlcTrendControl)d).OnYRangeChanged()));

        public double MaxY
        {
            get => (double)GetValue(MaxYProperty);
            set => SetValue(MaxYProperty, value);
        }

        public static readonly DependencyProperty AutoScaleYProperty =
            DependencyProperty.Register(nameof(AutoScaleY), typeof(bool), typeof(PlcTrendControl),
                new PropertyMetadata(true, (d, e) => ((PlcTrendControl)d).OnYRangeChanged()));

        public bool AutoScaleY
        {
            get => (bool)GetValue(AutoScaleYProperty);
            set => SetValue(AutoScaleYProperty, value);
        }

        public static readonly DependencyProperty ShowAxisLabelsProperty =
            DependencyProperty.Register(nameof(ShowAxisLabels), typeof(bool), typeof(PlcTrendControl),
                new PropertyMetadata(true, (d, e) => ((PlcTrendControl)d).DrawGridAndAxis()));

        public bool ShowAxisLabels
        {
            get => (bool)GetValue(ShowAxisLabelsProperty);
            set => SetValue(ShowAxisLabelsProperty, value);
        }

        public static readonly DependencyProperty BackgroundProperty =
            DependencyProperty.Register(nameof(Background), typeof(Brush), typeof(PlcTrendControl),
                new PropertyMetadata(Brushes.White));

        public new Brush Background
        {
            get => (Brush)GetValue(BackgroundProperty);
            set => SetValue(BackgroundProperty, value);
        }

        public static readonly DependencyProperty BorderBrushProperty =
            DependencyProperty.Register(nameof(BorderBrush), typeof(Brush), typeof(PlcTrendControl),
                new PropertyMetadata(Brushes.Gray));

        public Brush BorderBrush
        {
            get => (Brush)GetValue(BorderBrushProperty);
            set => SetValue(BorderBrushProperty, value);
        }

        public static readonly DependencyProperty BorderThicknessProperty =
            DependencyProperty.Register(nameof(BorderThickness), typeof(Thickness), typeof(PlcTrendControl),
                new PropertyMetadata(new Thickness(1)));

        public Thickness BorderThickness
        {
            get => (Thickness)GetValue(BorderThicknessProperty);
            set => SetValue(BorderThicknessProperty, value);
        }

        public static readonly DependencyProperty ToolTipTextProperty =
            DependencyProperty.Register(nameof(ToolTipText), typeof(string), typeof(PlcTrendControl),
                new PropertyMetadata(string.Empty));

        public string ToolTipText
        {
            get => (string)GetValue(ToolTipTextProperty);
            set => SetValue(ToolTipTextProperty, value);
        }

        public static readonly DependencyProperty IsPausedProperty =
            DependencyProperty.Register(nameof(IsPaused), typeof(bool), typeof(PlcTrendControl),
                new PropertyMetadata(false, (d, e) => ((PlcTrendControl)d).SetPaused((bool)e.NewValue)));

        public bool IsPaused
        {
            get => (bool)GetValue(IsPausedProperty);
            set => SetValue(IsPausedProperty, value);
        }

        // ===== 新增：标题 =====
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(PlcTrendControl),
                new PropertyMetadata("趋势曲线", (d, e) => ((PlcTrendControl)d).OnTitleChanged()));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        // ===== 新增：单位 =====
        public static readonly DependencyProperty UnitProperty =
            DependencyProperty.Register(nameof(Unit), typeof(string), typeof(PlcTrendControl),
                new PropertyMetadata(string.Empty, (d, e) => ((PlcTrendControl)d).OnUnitChanged()));

        public string Unit
        {
            get => (string)GetValue(UnitProperty);
            set => SetValue(UnitProperty, value);
        }

        // ===== 新增：平滑曲线（贝塞尔） =====
        public static readonly DependencyProperty SmoothCurveProperty =
            DependencyProperty.Register(nameof(SmoothCurve), typeof(bool), typeof(PlcTrendControl),
                new PropertyMetadata(true, (d, e) => ((PlcTrendControl)d).RedrawCurveAndArea()));

        public bool SmoothCurve
        {
            get => (bool)GetValue(SmoothCurveProperty);
            set => SetValue(SmoothCurveProperty, value);
        }

        // ===== 新增：渐变面积填充 =====
        public static readonly DependencyProperty ShowAreaProperty =
            DependencyProperty.Register(nameof(ShowArea), typeof(bool), typeof(PlcTrendControl),
                new PropertyMetadata(true, (d, e) => ((PlcTrendControl)d).RedrawCurveAndArea()));

        public bool ShowArea
        {
            get => (bool)GetValue(ShowAreaProperty);
            set => SetValue(ShowAreaProperty, value);
        }

        // ===== 新增：数据点标记 =====
        public static readonly DependencyProperty ShowDataPointsProperty =
            DependencyProperty.Register(nameof(ShowDataPoints), typeof(bool), typeof(PlcTrendControl),
                new PropertyMetadata(false, (d, e) => ((PlcTrendControl)d).RedrawCurveAndArea()));

        public bool ShowDataPoints
        {
            get => (bool)GetValue(ShowDataPointsProperty);
            set => SetValue(ShowDataPointsProperty, value);
        }

        // ===== 新增：最新点光晕高亮 =====
        public static readonly DependencyProperty HighlightLastProperty =
            DependencyProperty.Register(nameof(HighlightLast), typeof(bool), typeof(PlcTrendControl),
                new PropertyMetadata(true, (d, e) => ((PlcTrendControl)d).RedrawCurveAndArea()));

        public bool HighlightLast
        {
            get => (bool)GetValue(HighlightLastProperty);
            set => SetValue(HighlightLastProperty, value);
        }

        // ===== 新增：图例显示 =====
        public static readonly DependencyProperty ShowLegendProperty =
            DependencyProperty.Register(nameof(ShowLegend), typeof(bool), typeof(PlcTrendControl),
                new PropertyMetadata(true, (d, e) => ((PlcTrendControl)d).BuildLegend()));

        public bool ShowLegend
        {
            get => (bool)GetValue(ShowLegendProperty);
            set => SetValue(ShowLegendProperty, value);
        }

        // ===== 新增：底部统计栏 =====
        public static readonly DependencyProperty ShowStatsProperty =
            DependencyProperty.Register(nameof(ShowStats), typeof(bool), typeof(PlcTrendControl),
                new PropertyMetadata(true, (d, e) => ((PlcTrendControl)d).OnShowStatsChanged()));

        public bool ShowStats
        {
            get => (bool)GetValue(ShowStatsProperty);
            set => SetValue(ShowStatsProperty, value);
        }

        /// <summary>附加曲线集合（多曲线）。主曲线仍由 TagName/TagAddress 决定；
        /// 集合中的每条序列将叠加绘制并出现在图例中。修改集合会重建缓冲与图例。</summary>
        public static readonly DependencyProperty SeriesProperty =
            DependencyProperty.Register(nameof(Series), typeof(ObservableCollection<PlcTrendSeries>), typeof(PlcTrendControl),
                new PropertyMetadata(null, (d, e) => ((PlcTrendControl)d).RebuildSeries()));

        public ObservableCollection<PlcTrendSeries>? Series
        {
            get => (ObservableCollection<PlcTrendSeries>?)GetValue(SeriesProperty);
            set => SetValue(SeriesProperty, value);
        }

        /// <summary>是否允许右键上下文菜单（属性设置）。默认 true；设为 false 可在运行期禁止操作员修改曲线属性。</summary>
        public static readonly DependencyProperty AllowPropertyMenuProperty =
            DependencyProperty.Register(nameof(AllowPropertyMenu), typeof(bool), typeof(PlcTrendControl),
                new PropertyMetadata(true));

        public bool AllowPropertyMenu
        {
            get => (bool)GetValue(AllowPropertyMenuProperty);
            set => SetValue(AllowPropertyMenuProperty, value);
        }

        // ===== 报警：超过报警值时曲线变色 =====

        /// <summary>是否启用报警。启用后超过 <see cref="AlarmValue"/> 的曲线段以 <see cref="AlarmBrush"/> 显示，并绘制报警参考线。</summary>
        public static readonly DependencyProperty AlarmEnabledProperty =
            DependencyProperty.Register(nameof(AlarmEnabled), typeof(bool), typeof(PlcTrendControl),
                new PropertyMetadata(false, (d, e) => ((PlcTrendControl)d).OnAlarmChanged()));

        public bool AlarmEnabled
        {
            get => (bool)GetValue(AlarmEnabledProperty);
            set => SetValue(AlarmEnabledProperty, value);
        }

        /// <summary>报警阈值。数值超过此值即触发变色。</summary>
        public static readonly DependencyProperty AlarmValueProperty =
            DependencyProperty.Register(nameof(AlarmValue), typeof(double), typeof(PlcTrendControl),
                new PropertyMetadata(80d, (d, e) => ((PlcTrendControl)d).OnAlarmChanged()));

        public double AlarmValue
        {
            get => (double)GetValue(AlarmValueProperty);
            set => SetValue(AlarmValueProperty, value);
        }

        /// <summary>报警色（超限段曲线、参考线、超限当前值文字）。默认红色 #F56C6C。</summary>
        public static readonly DependencyProperty AlarmBrushProperty =
            DependencyProperty.Register(nameof(AlarmBrush), typeof(Brush), typeof(PlcTrendControl),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(245, 108, 108)), (d, e) => ((PlcTrendControl)d).OnAlarmChanged()));

        public Brush AlarmBrush
        {
            get => (Brush)GetValue(AlarmBrushProperty);
            set => SetValue(AlarmBrushProperty, value);
        }

        #endregion

        #region 属性变更回调

        private void OnTitleChanged()
        {
            if (PART_TitleText != null) PART_TitleText.Text = Title;
        }

        private void OnUnitChanged()
        {
            if (PART_UnitText != null) PART_UnitText.Text = Unit;
        }

        private void OnStrokeChanged()
        {
            BuildLegend();
            RedrawCurveAndArea();
        }

        private void OnShowStatsChanged()
        {
            if (PART_Footer != null)
                PART_Footer.Visibility = ShowStats ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnAlarmChanged()
        {
            RedrawCurveAndArea();
            RefreshLastValueColor();
        }

        /// <summary>按报警状态刷新“当前值”大字颜色：超限红色，否则曲线色。</summary>
        private void RefreshLastValueColor()
        {
            if (PART_LastValueText == null) return;
            if (_dataQueue.Count > 0 && AlarmEnabled && _dataQueue.Last() >= AlarmValue)
                PART_LastValueText.Foreground = AlarmBrush;
            else
                PART_LastValueText.Foreground = StrokeBrush;
        }

        #endregion

        #region 连接与标签管理

        #region PLC 基类钩子

        /// <summary>按当前数据类型创建指定名称的标签（供自动注册使用）。</summary>
        private PLCTag? CreateTagByDataType(string tagName)
        {
            return CreateTagByDataType(tagName, TagAddress, DataType);
        }

        /// <summary>按指定地址与数据类型创建标签（用于附加序列）。</summary>
        private PLCTag? CreateTagByDataType(string tagName, string address, RangeDataType dt)
        {
            return dt switch
            {
                RangeDataType.Int8 => PLCTag.Create<sbyte>(tagName, address, (sbyte)0, tagName),
                RangeDataType.UInt8 => PLCTag.Create<byte>(tagName, address, (byte)0, tagName),
                RangeDataType.Int16 => PLCTag.Create<short>(tagName, address, (short)0, tagName),
                RangeDataType.UInt16 => PLCTag.Create<ushort>(tagName, address, (ushort)0, tagName),
                RangeDataType.Int32 => PLCTag.Create<int>(tagName, address, 0, tagName),
                RangeDataType.UInt32 => PLCTag.Create<uint>(tagName, address, 0u, tagName),
                RangeDataType.Int64 => PLCTag.Create<long>(tagName, address, 0L, tagName),
                RangeDataType.UInt64 => PLCTag.Create<ulong>(tagName, address, 0ul, tagName),
                RangeDataType.Float => PLCTag.Create<float>(tagName, address, 0.0f, tagName),
                RangeDataType.Double => PLCTag.Create<double>(tagName, address, 0.0, tagName),
                _ => null,
            };
        }

        /// <summary>按当前数据类型创建自动注册标签（基类默认 bool，此处按 RangeDataType 创建）。</summary>
        protected override PLCTag? CreateDefaultTag()
        {
            if (string.IsNullOrWhiteSpace(TagAddress)) return null;
            string effectiveName = string.IsNullOrWhiteSpace(TagName) ? TagAddress : TagName;
            return CreateTagByDataType(effectiveName);
        }

        /// <summary>连接建立/断开时重启或停止采样。</summary>
        protected override void OnConnectionChangedInternal(IConnectionManager? oldConn, IConnectionManager? newConn)
        {
            RestartSampling();
        }

        /// <summary>连接状态变化时重启或停止采样。</summary>
        protected override void OnConnectionStateChanged(bool isConnected)
        {
            if (isConnected) RestartSampling();
            else StopSampling();
        }

        /// <summary>基类在 UI 线程回调：收到匹配标签值后记录并更新提示。</summary>
        protected override void OnPlcValueReceived(object? value)
        {
            _currentRawValue = value;
            UpdateToolTip();
        }

        #endregion

        #region 多曲线支持

        /// <summary>在主序列之外，注册所有可见附加序列的标签。</summary>
        protected override void AutoRegisterTags()
        {
            base.AutoRegisterTags();   // 注册主序列
            if (ConnectionManager == null || !AutoRegister || Series == null) return;
            foreach (var s in Series)
            {
                if (!s.Visible) continue;
                if (string.IsNullOrWhiteSpace(s.TagAddress) && string.IsNullOrWhiteSpace(s.TagName)) continue;
                string name = string.IsNullOrWhiteSpace(s.TagName) ? s.TagAddress : s.TagName;
                if (!ContainsTagSafe(name))
                {
                    try
                    {
                        var tag = CreateTagByDataType(name, s.TagAddress, s.DataType);
                        if (tag != null)
                        {
                            tag.IsMonitored = true;
                            ConnectionManager.AddTag(tag);
                        }
                    }
                    catch { }
                }
            }
        }

        private void RebuildSeries()
        {
            _extraSeries.Clear();
            if (Series != null)
            {
                foreach (var s in Series)
                {
                    if (!s.Visible) continue;
                    if (string.IsNullOrWhiteSpace(s.TagAddress) && string.IsNullOrWhiteSpace(s.TagName)) continue;
                    string name = string.IsNullOrWhiteSpace(s.TagName) ? s.TagAddress : s.TagName;
                    _extraSeries.Add(new TrendBuffer
                    {
                        Name = name,
                        DataType = s.DataType,
                        Color = s.Color ?? StrokeBrush,
                    });
                }
            }
            BuildLegend();
            RestartSampling();
            ClearData();
        }

        private void BuildLegend()
        {
            if (PART_Legend == null) return;
            PART_Legend.Children.Clear();
            if (!ShowLegend)
            {
                PART_Legend.Visibility = Visibility.Collapsed;
                return;
            }
            // 仅在存在附加序列（多曲线）时显示图例；单曲线由标题栏已足够表达
            if (_extraSeries.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(TagName) || !string.IsNullOrWhiteSpace(TagAddress))
                    AddLegendItem(string.IsNullOrWhiteSpace(TagName) ? TagAddress : TagName, StrokeBrush);
                foreach (var b in _extraSeries)
                    AddLegendItem(b.Name, b.Color ?? StrokeBrush);
            }
            PART_Legend.Visibility = PART_Legend.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddLegendItem(string name, Brush color)
        {
            // 胶囊状图例项：圆点 + 名称
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 1, 8, 1),
                Margin = new Thickness(0, 0, 6, 0)
            };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = color,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });
            panel.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(96, 98, 102)),
                VerticalAlignment = VerticalAlignment.Center
            });
            border.Child = panel;
            PART_Legend.Children.Add(border);
        }

        #endregion

        #region 采样与数据管理

        private void RestartSampling()
        {
            StopSampling();
            StartSampling();
        }

        private void StartSampling()
        {
            if (ConnectionManager == null || string.IsNullOrWhiteSpace(TagName) || !ConnectionManager.IsConnected)
                return;

            if (_samplingTimer == null)
            {
                _samplingTimer = new DispatcherTimer();
                _samplingTimer.Tick += SamplingTimer_Tick;
            }
            _samplingTimer.Interval = TimeSpan.FromMilliseconds(SamplingInterval);
            _samplingTimer.Start();
        }

        private void StopSampling()
        {
            if (_samplingTimer != null)
            {
                _samplingTimer.Stop();
                _samplingTimer = null;
            }
        }

        private void SamplingTimer_Tick(object? sender, EventArgs e)
        {
            if (_isPaused) return;

            double? currentValue = ReadCurrentValue();
            if (currentValue.HasValue)
            {
                AddDataPoint(currentValue.Value);   // 主序列并触发重绘
            }

            // 附加序列采样
            foreach (var buf in _extraSeries)
            {
                double? v = ReadSeriesValue(buf.Name, buf.DataType);
                if (v.HasValue)
                {
                    buf.Data.Enqueue(v.Value);
                    while (buf.Data.Count > MaxDataPoints)
                        buf.Data.Dequeue();
                }
            }

            if (AutoScaleY) AutoScaleYRange();   // 依据全部序列重算量程
            RedrawCurveAndArea();                // 重绘主序列 + 附加序列
        }

        private double? ReadCurrentValue()
        {
            if (ConnectionManager == null || string.IsNullOrWhiteSpace(TagName))
                return null;

            try
            {
                PLCTag? tag = ConnectionManager.GetTag(TagName);
                if (tag == null) return null;

                object? value = tag.Value;
                if (value == null) return null;

                return ConvertToDouble(value, DataType);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PlcTrendControl] ReadCurrentValue error: {ex.Message}");
                return null;
            }
        }

        private double? ReadSeriesValue(string tagName, RangeDataType dt)
        {
            if (ConnectionManager == null || string.IsNullOrWhiteSpace(tagName))
                return null;
            try
            {
                PLCTag? tag = ConnectionManager.GetTag(tagName);
                if (tag == null) return null;
                object? value = tag.Value;
                if (value == null) return null;
                return ConvertToDouble(value, dt);
            }
            catch { return null; }
        }

        private double ConvertToDouble(object value, RangeDataType dt)
        {
            return dt switch
            {
                RangeDataType.Int8 => Convert.ToDouble((sbyte)value),
                RangeDataType.UInt8 => Convert.ToDouble((byte)value),
                RangeDataType.Int16 => Convert.ToDouble((short)value),
                RangeDataType.UInt16 => Convert.ToDouble((ushort)value),
                RangeDataType.Int32 => Convert.ToDouble((int)value),
                RangeDataType.UInt32 => Convert.ToDouble((uint)value),
                RangeDataType.Int64 => Convert.ToDouble((long)value),
                RangeDataType.UInt64 => Convert.ToDouble((ulong)value),
                RangeDataType.Float => Convert.ToDouble((float)value),
                RangeDataType.Double => (double)value,
                _ => Convert.ToDouble(value),
            };
        }

        private void AddDataPoint(double value)
        {
            _dataQueue.Enqueue(value);
            while (_dataQueue.Count > MaxDataPoints)
                _dataQueue.Dequeue();

            if (PART_LastValueText != null)
                PART_LastValueText.Text = FormatValue(value);
            RefreshLastValueColor();
            if (AutoScaleY)
                AutoScaleYRange();

            RedrawCurveAndArea();
            DrawGridAndAxis();
            UpdateStats();
        }

        private void OnMaxPointsChanged()
        {
            while (_dataQueue.Count > MaxDataPoints)
                _dataQueue.Dequeue();
            foreach (var buf in _extraSeries)
                while (buf.Data.Count > MaxDataPoints)
                    buf.Data.Dequeue();
            RedrawCurveAndArea();
        }

        private void OnYRangeChanged()
        {
            if (!AutoScaleY)
            {
                _currentMinY = MinY;
                _currentMaxY = MaxY;
                RedrawCurveAndArea();
                DrawGridAndAxis();
            }
            else
            {
                AutoScaleYRange();
            }
        }

        private void AutoScaleYRange()
        {
            var all = new List<double>(_dataQueue);
            foreach (var buf in _extraSeries)
                all.AddRange(buf.Data);

            if (all.Count == 0)
            {
                _currentMinY = MinY;
                _currentMaxY = MaxY;
                return;
            }

            double min = all.Min();
            double max = all.Max();
            if (Math.Abs(max - min) < 0.0001)
            {
                min = min - 5;
                max = max + 5;
            }
            else
            {
                double padding = (max - min) * 0.1;
                min -= padding;
                max += padding;
            }
            _currentMinY = min;
            _currentMaxY = max;
            RedrawCurveAndArea();
            DrawGridAndAxis();
        }

        public void ClearData()
        {
            _dataQueue.Clear();
            foreach (var buf in _extraSeries)
                buf.Data.Clear();
            RedrawCurveAndArea();
            DrawGridAndAxis();
            if (PART_LastValueText != null) PART_LastValueText.Text = "--";
            UpdateStats();
        }

        private void SetPaused(bool paused)
        {
            _isPaused = paused;
            if (!paused)
            {
                double? val = ReadCurrentValue();
                if (val.HasValue)
                    AddDataPoint(val.Value);
            }
        }

        private void UpdateStats()
        {
            if (!ShowStats || PART_MaxText == null) return;
            if (_dataQueue.Count == 0)
            {
                PART_MaxText.Text = "--";
                PART_MinText.Text = "--";
                PART_AvgText.Text = "--";
                return;
            }
            PART_MaxText.Text = FormatValue(_dataQueue.Max());
            PART_MinText.Text = FormatValue(_dataQueue.Min());
            PART_AvgText.Text = FormatValue(_dataQueue.Average());
        }

        #endregion

        #region 绘图（渐变面积、平滑曲线、刻度线）

        private void UpdatePlotArea()
        {
            if (PART_PlotArea == null) return;
            _plotWidth = PART_PlotArea.ActualWidth;
            _plotHeight = PART_PlotArea.ActualHeight;
            if (_plotWidth <= 0 || _plotHeight <= 0) return;

            DrawGridAndAxis();
            RedrawCurveAndArea();
            if (PART_NoDataText != null)
            {
                bool anyData = _dataQueue.Count > 0 || _extraSeries.Any(b => b.Data.Count > 0);
                PART_NoDataText.Visibility = anyData ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void DrawGridAndAxis()
        {
            if (PART_GridCanvas == null || _plotWidth <= 0 || _plotHeight <= 0) return;
            PART_GridCanvas.Children.Clear();

            int xDivisions = 10;
            int yDivisions = 5;

            var gridColor = ColorOf(GridBrush, Color.FromRgb(236, 240, 245));
            var minorBrush = new SolidColorBrush(Color.FromArgb(90, gridColor.R, gridColor.G, gridColor.B));
            minorBrush.Freeze();

            // 垂直网格线（虚线，淡）
            for (int i = 0; i <= xDivisions; i++)
            {
                double x = i * _plotWidth / xDivisions;
                PART_GridCanvas.Children.Add(new Line
                {
                    X1 = x,
                    Y1 = 0,
                    X2 = x,
                    Y2 = _plotHeight,
                    Stroke = minorBrush,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 3 }
                });
            }

            // 水平网格线（实线，稍明显）
            for (int i = 0; i <= yDivisions; i++)
            {
                double y = i * _plotHeight / yDivisions;
                PART_GridCanvas.Children.Add(new Line
                {
                    X1 = 0,
                    Y1 = y,
                    X2 = _plotWidth,
                    Y2 = y,
                    Stroke = GridBrush,
                    StrokeThickness = 1
                });
            }

            // Y轴标签 + 短刻度线
            if (ShowAxisLabels && PART_YAxisCanvas != null)
            {
                PART_YAxisCanvas.Children.Clear();
                double yStep = _plotHeight / yDivisions;
                double axisWidth = PART_YAxisCanvas.ActualWidth;
                if (axisWidth <= 0) axisWidth = 40;
                for (int i = 0; i <= yDivisions; i++)
                {
                    double yPos = i * yStep;
                    double value = _currentMaxY - (_currentMaxY - _currentMinY) * (i / (double)yDivisions);
                    string label = FormatValue(value);
                    PART_YAxisCanvas.Children.Add(new Line
                    {
                        X1 = axisWidth - 5,
                        Y1 = yPos,
                        X2 = axisWidth,
                        Y2 = yPos,
                        Stroke = AxisBrush,
                        StrokeThickness = 1
                    });
                    TextBlock tb = new TextBlock { Text = label, FontSize = 9, Foreground = AxisBrush, TextAlignment = TextAlignment.Right };
                    tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Canvas.SetLeft(tb, axisWidth - tb.DesiredSize.Width - 7);
                    Canvas.SetTop(tb, yPos - 7);
                    PART_YAxisCanvas.Children.Add(tb);
                }
            }

            // X轴标签 + 刻度线（基于实际数据点）
            if (ShowAxisLabels && PART_XAxisCanvas != null && _dataQueue.Count > 0)
            {
                PART_XAxisCanvas.Children.Clear();
                int xLabels = Math.Min(6, _dataQueue.Count);
                if (xLabels < 2) xLabels = 2;
                double step = _plotWidth / (xLabels - 1);
                for (int i = 0; i < xLabels; i++)
                {
                    double xPos = i * step;
                    int dataIndex = (int)(i * (_dataQueue.Count - 1) / (double)(xLabels - 1));
                    dataIndex = Math.Max(0, Math.Min(_dataQueue.Count - 1, dataIndex));
                    int timeOffsetSec = -(int)((_dataQueue.Count - 1 - dataIndex) * (SamplingInterval / 1000.0));
                    string label = (i == xLabels - 1) ? "0s" : (timeOffsetSec == 0 ? "0s" : $"{timeOffsetSec}s");
                    PART_XAxisCanvas.Children.Add(new Line
                    {
                        X1 = xPos,
                        Y1 = 0,
                        X2 = xPos,
                        Y2 = 5,
                        Stroke = AxisBrush,
                        StrokeThickness = 1
                    });
                    TextBlock tb = new TextBlock { Text = label, FontSize = 9, Foreground = AxisBrush, TextAlignment = TextAlignment.Center };
                    tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Canvas.SetLeft(tb, xPos - tb.DesiredSize.Width / 2);
                    Canvas.SetTop(tb, 7);
                    PART_XAxisCanvas.Children.Add(tb);
                }
            }
        }

        private void RedrawCurveAndArea()
        {
            if (PART_CurveCanvas == null || PART_AreaCanvas == null || _plotWidth <= 0 || _plotHeight <= 0) return;
            PART_CurveCanvas.Children.Clear();
            PART_AreaCanvas.Children.Clear();
            PART_MarkerCanvas?.Children.Clear();

            // 先画附加序列（在主序列之下）
            foreach (var buf in _extraSeries)
            {
                if (buf.Data.Count < 2) continue;
                DrawSeries(buf.Data.ToArray(), buf.Color ?? StrokeBrush, StrokeThickness,
                           fillArea: false, showPoints: false, highlightLast: HighlightLast);
            }

            // 再画主序列（在最上层），主序列参与报警变色
            if (_dataQueue.Count >= 2)
            {
                DrawSeries(_dataQueue.ToArray(), StrokeBrush, StrokeThickness,
                           fillArea: ShowArea, showPoints: ShowDataPoints, highlightLast: HighlightLast,
                           applyAlarm: true);
            }

            // 报警阈值参考线（画在曲线之上，最清晰）
            DrawAlarmLine();

            if (PART_NoDataText != null)
            {
                bool anyData = _dataQueue.Count > 0 || _extraSeries.Any(b => b.Data.Count > 0);
                PART_NoDataText.Visibility = anyData ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        /// <summary>绘制单条序列：面积渐变、平滑/折线、数据点、最新点光晕。applyAlarm=true 时超过报警值的曲线段用报警色叠加显示。</summary>
        private void DrawSeries(double[] data, Brush stroke, double thickness, bool fillArea, bool showPoints, bool highlightLast, bool applyAlarm = false)
        {
            if (data.Length < 2) return;

            var pts = new Point[data.Length];
            double xStep = _plotWidth / (data.Length - 1);
            for (int i = 0; i < data.Length; i++)
                pts[i] = new Point(i * xStep, MapY(data[i]));

            var color = ColorOf(stroke, Color.FromRgb(64, 158, 255));

            // ===== 面积渐变填充 =====
            if (fillArea)
            {
                var areaGeo = BuildLineGeometry(pts, SmoothCurve);
                if (areaGeo.Figures.Count > 0)
                {
                    var fig = areaGeo.Figures[0];
                    fig.Segments.Add(new LineSegment(new Point(pts[pts.Length - 1].X, _plotHeight), false));
                    fig.Segments.Add(new LineSegment(new Point(pts[0].X, _plotHeight), false));
                    fig.IsClosed = true;
                    areaGeo.Freeze();

                    var areaBrush = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0),
                        EndPoint = new Point(0, 1)
                    };
                    areaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(105, color.R, color.G, color.B), 0));
                    areaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(35, color.R, color.G, color.B), 0.55));
                    areaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(6, color.R, color.G, color.B), 1));
                    areaBrush.Freeze();

                    PART_AreaCanvas.Children.Add(new Path { Data = areaGeo, Fill = areaBrush });
                }
            }

            // ===== 曲线 =====
            var lineGeo = BuildLineGeometry(pts, SmoothCurve);
            lineGeo.Freeze();
            PART_CurveCanvas.Children.Add(new Path
            {
                Data = lineGeo,
                Stroke = stroke,
                StrokeThickness = thickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });

            // ===== 报警变色：超过报警值的曲线段用报警色叠加（裁剪到阈值线以上区域） =====
            if (applyAlarm && AlarmEnabled)
            {
                double alarmY = MapY(AlarmValue);
                // 裁剪矩形覆盖“报警值以上”的区域（Y 越小值越大），clamp 到绘图区
                double clipBottom = Math.Max(0, Math.Min(_plotHeight, alarmY));
                if (clipBottom > 0)
                {
                    var clip = new RectangleGeometry(new Rect(0, 0, _plotWidth, clipBottom));
                    clip.Freeze();
                    PART_CurveCanvas.Children.Add(new Path
                    {
                        Data = lineGeo,
                        Stroke = AlarmBrush,
                        StrokeThickness = thickness,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        Clip = clip
                    });
                }
            }

            // ===== 数据点标记 =====
            if (showPoints)
            {
                foreach (var p in pts)
                {
                    var dot = new Ellipse
                    {
                        Width = 5,
                        Height = 5,
                        Fill = Brushes.White,
                        Stroke = stroke,
                        StrokeThickness = 1.5
                    };
                    Canvas.SetLeft(dot, p.X - 2.5);
                    Canvas.SetTop(dot, p.Y - 2.5);
                    (PART_MarkerCanvas ?? PART_CurveCanvas).Children.Add(dot);
                }
            }

            // ===== 最新点光晕高亮 =====
            if (highlightLast)
            {
                var last = pts[pts.Length - 1];
                var canvas = PART_MarkerCanvas ?? PART_CurveCanvas;

                // 外层光晕
                var glow = new Ellipse
                {
                    Width = 16,
                    Height = 16,
                    Fill = new SolidColorBrush(Color.FromArgb(55, color.R, color.G, color.B))
                };
                Canvas.SetLeft(glow, last.X - 8);
                Canvas.SetTop(glow, last.Y - 8);
                canvas.Children.Add(glow);

                // 白边实心点
                var dot = new Ellipse
                {
                    Width = 9,
                    Height = 9,
                    Fill = stroke,
                    Stroke = Brushes.White,
                    StrokeThickness = 2
                };
                Canvas.SetLeft(dot, last.X - 4.5);
                Canvas.SetTop(dot, last.Y - 4.5);
                canvas.Children.Add(dot);
            }
        }

        /// <summary>由点集生成折线或 Catmull-Rom 平滑贝塞尔路径几何。</summary>
        private PathGeometry BuildLineGeometry(Point[] pts, bool smooth)
        {
            var geo = new PathGeometry();
            if (pts.Length < 2) return geo;

            var fig = new PathFigure { StartPoint = pts[0], IsFilled = true };

            if (!smooth || pts.Length == 2)
            {
                for (int i = 1; i < pts.Length; i++)
                    fig.Segments.Add(new LineSegment(pts[i], true));
            }
            else
            {
                // Catmull-Rom → 三次贝塞尔
                for (int i = 0; i < pts.Length - 1; i++)
                {
                    Point p0 = i == 0 ? pts[0] : pts[i - 1];
                    Point p1 = pts[i];
                    Point p2 = pts[i + 1];
                    Point p3 = (i + 2 < pts.Length) ? pts[i + 2] : pts[i + 1];

                    Point c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
                    Point c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
                    fig.Segments.Add(new BezierSegment(c1, c2, p2, true));
                }
            }

            geo.Figures.Add(fig);
            return geo;
        }

        private static Color ColorOf(Brush? brush, Color fallback)
        {
            return brush is SolidColorBrush scb ? scb.Color : fallback;
        }

        /// <summary>绘制报警阈值水平参考线（红色虚线 + 数值标签）。</summary>
        private void DrawAlarmLine()
        {
            if (!AlarmEnabled || PART_CurveCanvas == null || _plotWidth <= 0 || _plotHeight <= 0) return;
            double alarmY = MapY(AlarmValue);
            if (alarmY < 0 || alarmY > _plotHeight) return;   // 报警线不在当前 Y 量程内则不画

            PART_CurveCanvas.Children.Add(new Line
            {
                X1 = 0,
                Y1 = alarmY,
                X2 = _plotWidth,
                Y2 = alarmY,
                Stroke = AlarmBrush,
                StrokeThickness = 1.2,
                StrokeDashArray = new DoubleCollection { 5, 3 }
            });

            var tb = new TextBlock
            {
                Text = "报警 " + FormatValue(AlarmValue),
                FontSize = 9,
                Foreground = AlarmBrush,
                Background = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
                Padding = new Thickness(3, 0, 3, 0)
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, _plotWidth - tb.DesiredSize.Width - 2);
            Canvas.SetTop(tb, Math.Max(0, alarmY - tb.DesiredSize.Height - 1));
            PART_CurveCanvas.Children.Add(tb);
        }

        private double MapY(double value)
        {
            if (_currentMaxY <= _currentMinY) return _plotHeight / 2;
            double ratio = (value - _currentMinY) / (_currentMaxY - _currentMinY);
            ratio = Math.Max(0, Math.Min(1, ratio));
            return _plotHeight * (1 - ratio);
        }

        private string FormatValue(double value)
        {
            return DataType switch
            {
                RangeDataType.Float => value.ToString("F2", CultureInfo.CurrentCulture),
                RangeDataType.Double => value.ToString("F2", CultureInfo.CurrentCulture),
                _ => value.ToString(CultureInfo.CurrentCulture),
            };
        }

        private void UpdateToolTip()
        {
            if (_currentRawValue != null)
            {
                double val = ConvertToDouble(_currentRawValue, DataType);
                ToolTipText = string.IsNullOrWhiteSpace(TagName)
                    ? $"{TagAddress} = {FormatValue(val)}"
                    : $"{TagName} ({TagAddress}) = {FormatValue(val)}";
            }
        }

        #endregion

        #region 鼠标跟踪（十字线 + 数值提示，支持多曲线）

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_dataQueue.Count < 2) return;
            Point pos = e.GetPosition(PART_PlotArea);
            if (pos.X < 0 || pos.X > _plotWidth || pos.Y < 0 || pos.Y > _plotHeight)
            {
                HideMouseTrack();
                return;
            }

            if (PART_MouseCanvas.Visibility != Visibility.Visible)
                PART_MouseCanvas.Visibility = Visibility.Visible;

            PART_MouseCanvas.Children.Clear();

            var trackColor = Color.FromArgb(160, 144, 147, 153);

            // 垂直虚线
            PART_MouseCanvas.Children.Add(new Line
            {
                X1 = pos.X,
                Y1 = 0,
                X2 = pos.X,
                Y2 = _plotHeight,
                Stroke = new SolidColorBrush(trackColor),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 2 }
            });

            int dataIndex = (int)Math.Round(pos.X / _plotWidth * (_dataQueue.Count - 1));
            dataIndex = Math.Max(0, Math.Min(_dataQueue.Count - 1, dataIndex));

            // 汇总提示：主序列 + 各附加序列在该 X 位置的值
            var rows = new List<(string name, double value, Brush color)>();

            double mainValue = _dataQueue.ElementAt(dataIndex);
            string mainName = string.IsNullOrWhiteSpace(TagName) ? (string.IsNullOrWhiteSpace(TagAddress) ? "主曲线" : TagAddress) : TagName;
            rows.Add((mainName, mainValue, StrokeBrush));

            // 主序列高亮点
            double mainY = MapY(mainValue);
            AddTrackDot(pos.X, mainY, StrokeBrush);

            foreach (var buf in _extraSeries)
            {
                if (buf.Data.Count < 2) continue;
                int idx = (int)Math.Round(pos.X / _plotWidth * (buf.Data.Count - 1));
                idx = Math.Max(0, Math.Min(buf.Data.Count - 1, idx));
                double v = buf.Data.ElementAt(idx);
                rows.Add((buf.Name, v, buf.Color ?? StrokeBrush));
                AddTrackDot(pos.X, MapY(v), buf.Color ?? StrokeBrush);
            }

            // 提示框（列出各序列）
            var tipPanel = new StackPanel();
            foreach (var r in rows)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
                line.Children.Add(new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = r.color,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 5, 0)
                });
                line.Children.Add(new TextBlock
                {
                    Text = $"{r.name}: {FormatValue(r.value)}",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(48, 49, 51)),
                    VerticalAlignment = VerticalAlignment.Center
                });
                tipPanel.Children.Add(line);
            }

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(245, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 4, 8, 4),
                Child = tipPanel
            };

            border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double tipW = border.DesiredSize.Width;
            double tipH = border.DesiredSize.Height;
            double left = pos.X + 12;
            double top = pos.Y - tipH - 8;
            if (left + tipW > _plotWidth) left = pos.X - tipW - 12;
            if (left < 0) left = 2;
            if (top < 0) top = pos.Y + 12;
            if (top + tipH > _plotHeight) top = _plotHeight - tipH - 2;
            Canvas.SetLeft(border, left);
            Canvas.SetTop(border, top);
            PART_MouseCanvas.Children.Add(border);
        }

        private void AddTrackDot(double x, double y, Brush color)
        {
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = color,
                Stroke = Brushes.White,
                StrokeThickness = 1.5
            };
            Canvas.SetLeft(dot, x - 4);
            Canvas.SetTop(dot, y - 4);
            PART_MouseCanvas.Children.Add(dot);
        }

        private void HideMouseTrack()
        {
            if (PART_MouseCanvas != null)
                PART_MouseCanvas.Visibility = Visibility.Collapsed;
        }

        #endregion

        #region 标签变更与清理

        protected override void OnTagChanged()
        {
            base.OnTagChanged();   // 基类：退订并按新标签重新注册/订阅
            // 标签/地址变更后重建主+附加序列缓冲、图例并重启采样
            RebuildSeries();
        }

        protected override void OnUnloadedInternal()
        {
            StopSampling();
        }

        #endregion

        #region 右键上下文菜单（属性设置）

        private void TrendContextMenu_OnOpening(object? sender, ContextMenuEventArgs e)
        {
            if (!AllowPropertyMenu)
                e.Handled = true;
        }

        private void OpenSettings_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new PlcTrendSettingsDialog(this);
                var owner = Window.GetWindow(this);
                if (owner != null) dlg.Owner = owner;
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PlcTrendControl] 打开属性设置对话框失败: {ex.Message}");
            }
        }

        private void ClearData_Click(object? sender, RoutedEventArgs e)
        {
            ClearData();
        }

        #endregion

        #endregion  // 连接与标签管理
    }

    /// <summary>
    /// 趋势控件的一条附加曲线定义。主曲线由控件的 TagName/TagAddress 决定，
    /// 附加曲线通过此集合叠加显示并出现在图例中。
    /// </summary>
    public class PlcTrendSeries
    {
        public string TagName { get; set; } = string.Empty;
        public string TagAddress { get; set; } = string.Empty;
        public RangeDataType DataType { get; set; } = RangeDataType.Float;
        public Brush? Color { get; set; }
        public bool Visible { get; set; } = true;
    }
}
