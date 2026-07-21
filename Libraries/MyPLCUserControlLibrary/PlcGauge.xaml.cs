using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MyPLCUserControlLibrary
{
    /// <summary>
    /// 圆形仪表盘：270° 圆弧显示模拟量（压力/温度/转速/流量等），支持量程、单位、标签、
    /// 警戒/危险阈值配色、主刻度。继承 PlcControlBase，复用连接与订阅逻辑。
    /// 角度约定：0°=东，90°=南(下)，270°=北(上)；弧线从 135°(左下) 顺时针扫过顶部到 45°(右下)。
    /// </summary>
    public partial class PlcGauge : PlcControlBase
    {
        private bool _isConnected;
        private double _rawValue;
        private bool _hasValue;

        // 圆弧起点/扫角（度）
        private const double StartAngle = 135d;
        private const double SweepAngle = 270d;

        public PlcGauge()
        {
            InitializeComponent();

            if (DesignerProperties.GetIsInDesignMode(this))
            {
                // 设计态示例
                Min = 0; Max = 100; _rawValue = 65; _hasValue = true; _isConnected = true;
                UpdateArc();
                PART_ValueText.Text = "65.0";
                PART_Label.Text = "示例";
                return;
            }

            Loaded += (s, e) => { DrawTicks(); UpdateArc(); };
            SizeChanged += (s, e) => { DrawTicks(); UpdateArc(); };
        }

        #region 依赖属性

        public static readonly DependencyProperty DataTypeProperty =
            DependencyProperty.Register(nameof(DataType), typeof(RangeDataType), typeof(PlcGauge),
                new PropertyMetadata(RangeDataType.Float, (d, e) => ((PlcGauge)d).OnTagChanged()));

        public RangeDataType DataType
        {
            get => (RangeDataType)GetValue(DataTypeProperty);
            set => SetValue(DataTypeProperty, value);
        }

        public static readonly DependencyProperty MinProperty =
            DependencyProperty.Register(nameof(Min), typeof(double), typeof(PlcGauge), new PropertyMetadata(0d, (d, e) => ((PlcGauge)d).UpdateArc()));

        public double Min
        {
            get => (double)GetValue(MinProperty);
            set => SetValue(MinProperty, value);
        }

        public static readonly DependencyProperty MaxProperty =
            DependencyProperty.Register(nameof(Max), typeof(double), typeof(PlcGauge), new PropertyMetadata(100d, (d, e) => ((PlcGauge)d).UpdateArc()));

        public double Max
        {
            get => (double)GetValue(MaxProperty);
            set => SetValue(MaxProperty, value);
        }

        public static readonly DependencyProperty WarningValueProperty =
            DependencyProperty.Register(nameof(WarningValue), typeof(double), typeof(PlcGauge), new PropertyMetadata(double.NaN, (d, e) => ((PlcGauge)d).UpdateArc()));

        /// <summary>达到该值（含）时弧线变为警告色；NaN 表示不启用。</summary>
        public double WarningValue
        {
            get => (double)GetValue(WarningValueProperty);
            set => SetValue(WarningValueProperty, value);
        }

        public static readonly DependencyProperty DangerValueProperty =
            DependencyProperty.Register(nameof(DangerValue), typeof(double), typeof(PlcGauge), new PropertyMetadata(double.NaN, (d, e) => ((PlcGauge)d).UpdateArc()));

        /// <summary>达到该值（含）时弧线变为危险色；NaN 表示不启用。</summary>
        public double DangerValue
        {
            get => (double)GetValue(DangerValueProperty);
            set => SetValue(DangerValueProperty, value);
        }

        public static readonly DependencyProperty MajorTicksProperty =
            DependencyProperty.Register(nameof(MajorTicks), typeof(int), typeof(PlcGauge), new PropertyMetadata(10, (d, e) => ((PlcGauge)d).DrawTicks()));

        public int MajorTicks
        {
            get => (int)GetValue(MajorTicksProperty);
            set => SetValue(MajorTicksProperty, value);
        }

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(PlcGauge), new PropertyMetadata(string.Empty));

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public static readonly DependencyProperty UnitProperty =
            DependencyProperty.Register(nameof(Unit), typeof(string), typeof(PlcGauge), new PropertyMetadata(string.Empty));

        public string Unit
        {
            get => (string)GetValue(UnitProperty);
            set => SetValue(UnitProperty, value);
        }

        public static readonly DependencyProperty FormatStringProperty =
            DependencyProperty.Register(nameof(FormatString), typeof(string), typeof(PlcGauge), new PropertyMetadata("F1"));

        public string FormatString
        {
            get => (string)GetValue(FormatStringProperty);
            set => SetValue(FormatStringProperty, value);
        }

        public static readonly DependencyProperty ValueBrushProperty =
            DependencyProperty.Register(nameof(ValueBrush), typeof(Brush), typeof(PlcGauge), new PropertyMetadata(new SolidColorBrush(Color.FromRgb(64, 158, 255)), (d, e) => ((PlcGauge)d).UpdateArc()));

        public Brush ValueBrush
        {
            get => (Brush)GetValue(ValueBrushProperty);
            set => SetValue(ValueBrushProperty, value);
        }

        public static readonly DependencyProperty TrackBrushProperty =
            DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(PlcGauge), new PropertyMetadata(new SolidColorBrush(Color.FromRgb(233, 236, 241)), (d, e) => ((PlcGauge)d).UpdateArc()));

        public Brush TrackBrush
        {
            get => (Brush)GetValue(TrackBrushProperty);
            set => SetValue(TrackBrushProperty, value);
        }

        public static readonly DependencyProperty StrokeThicknessProperty =
            DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(PlcGauge), new PropertyMetadata(12d, (d, e) => ((PlcGauge)d).UpdateArc()));

        public double StrokeThickness
        {
            get => (double)GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        public static readonly DependencyProperty DisconnectedTextProperty =
            DependencyProperty.Register(nameof(DisconnectedText), typeof(string), typeof(PlcGauge), new PropertyMetadata("--"));

        public string DisconnectedText
        {
            get => (string)GetValue(DisconnectedTextProperty);
            set => SetValue(DisconnectedTextProperty, value);
        }

        #endregion

        #region PlcControlBase 钩子

        protected override PLCTag? CreateDefaultTag()
        {
            if (string.IsNullOrWhiteSpace(TagAddress)) return null;
            string effectiveName = string.IsNullOrWhiteSpace(TagName) ? TagAddress : TagName;
            return DataType switch
            {
                RangeDataType.Int8 => PLCTag.Create<sbyte>(effectiveName, TagAddress, (sbyte)0, effectiveName),
                RangeDataType.UInt8 => PLCTag.Create<byte>(effectiveName, TagAddress, (byte)0, effectiveName),
                RangeDataType.Int16 => PLCTag.Create<short>(effectiveName, TagAddress, (short)0, effectiveName),
                RangeDataType.UInt16 => PLCTag.Create<ushort>(effectiveName, TagAddress, (ushort)0, effectiveName),
                RangeDataType.Int32 => PLCTag.Create<int>(effectiveName, TagAddress, 0, effectiveName),
                RangeDataType.UInt32 => PLCTag.Create<uint>(effectiveName, TagAddress, 0u, effectiveName),
                RangeDataType.Int64 => PLCTag.Create<long>(effectiveName, TagAddress, 0L, effectiveName),
                RangeDataType.UInt64 => PLCTag.Create<ulong>(effectiveName, TagAddress, 0ul, effectiveName),
                RangeDataType.Float => PLCTag.Create<float>(effectiveName, TagAddress, 0.0f, effectiveName),
                RangeDataType.Double => PLCTag.Create<double>(effectiveName, TagAddress, 0.0, effectiveName),
                _ => null,
            };
        }

        protected override void OnConnectionChangedInternal(IConnectionManager? oldConn, IConnectionManager? newConn)
        {
            _isConnected = newConn?.IsConnected ?? false;
            if (!_isConnected) ShowDisconnected();
        }

        protected override void OnConnectionStateChanged(bool isConnected)
        {
            _isConnected = isConnected;
            if (!_isConnected) ShowDisconnected();
        }

        protected override void OnPlcValueReceived(object? value)
        {
            if (!_isConnected || value == null) return;
            try
            {
                _rawValue = ConvertToDouble(value);
                _hasValue = true;
                UpdateArc();
                PART_ValueText.Text = FormatValue(_rawValue);
            }
            catch { }
        }

        #endregion

        #region 几何与绘制

        private double ConvertToDouble(object value) => DataType switch
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

        private void ShowDisconnected()
        {
            _hasValue = false;
            UpdateArc();
            PART_ValueText.Text = DisconnectedText;
        }

        private Point PointForAngle(double angleDeg, double cx, double cy, double r)
        {
            double rad = angleDeg * Math.PI / 180.0;
            return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
        }

        private void UpdateArc()
        {
            if (PART_Track == null || PART_Value == null || PART_Needle == null) return;
            double w = PART_Canvas.ActualWidth;
            double h = PART_Canvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            double cx = w / 2;
            double cy = h / 2;
            double r = Math.Min(w, h) / 2 - StrokeThickness - 4;
            if (r <= 0) return;

            double startA = StartAngle;
            double endA = StartAngle + SweepAngle;

            // 背景轨道
            Point pStart = PointForAngle(startA, cx, cy, r);
            Point pEnd = PointForAngle(endA, cx, cy, r);
            PART_Track.Data = BuildArc(pStart, pEnd, r, true);
            PART_Track.Stroke = TrackBrush;
            PART_Track.StrokeThickness = StrokeThickness;

            // 数值弧
            double frac = _hasValue ? Math.Max(0, Math.Min(1, (_rawValue - Min) / (Max - Min))) : 0;
            double valA = startA + SweepAngle * frac;
            Point pVal = PointForAngle(valA, cx, cy, r);
            bool large = (valA - startA) > 180;
            PART_Value.Data = BuildArc(pStart, pVal, r, large);
            PART_Value.StrokeThickness = StrokeThickness;
            PART_Value.Stroke = PickArcBrush();

            // 指针
            Point pNeedle = PointForAngle(valA, cx, cy, r * 0.82);
            PART_Needle.X1 = cx;
            PART_Needle.Y1 = cy;
            PART_Needle.X2 = pNeedle.X;
            PART_Needle.Y2 = pNeedle.Y;

            // 中心轴帽定位到圆心
            Canvas.SetLeft(PART_Hub, cx - PART_Hub.Width / 2);
            Canvas.SetTop(PART_Hub, cy - PART_Hub.Height / 2);

            // 中心读数位置跟随
            Canvas.SetLeft(PART_Readout, cx - PART_Readout.ActualWidth / 2);
            Canvas.SetTop(PART_Readout, cy + r * 0.18);
        }

        private Brush PickArcBrush()
        {
            if (_hasValue)
            {
                if (!double.IsNaN(DangerValue) && _rawValue >= DangerValue) return PlcPalette.DangerBrush;
                if (!double.IsNaN(WarningValue) && _rawValue >= WarningValue) return PlcPalette.WarningBrush;
            }
            return ValueBrush;
        }

        private PathGeometry BuildArc(Point start, Point end, double r, bool largeArc)
        {
            var geo = new PathGeometry();
            geo.Figures.Add(new PathFigure(
                start,
                new PathSegment[] { new ArcSegment(end, new Size(r, r), 0, largeArc, SweepDirection.Clockwise, true) },
                false));
            return geo;
        }

        private void DrawTicks()
        {
            if (PART_Ticks == null) return;
            PART_Ticks.Children.Clear();
            double w = PART_Canvas.ActualWidth;
            double h = PART_Canvas.ActualHeight;
            if (w <= 0 || h <= 0) return;
            double cx = w / 2, cy = h / 2;
            double r = Math.Min(w, h) / 2 - StrokeThickness - 4;
            if (r <= 0) return;

            int ticks = Math.Max(1, MajorTicks);
            for (int i = 0; i <= ticks; i++)
            {
                double a = StartAngle + SweepAngle * (i / (double)ticks);
                Point outer = PointForAngle(a, cx, cy, r - 2);
                Point inner = PointForAngle(a, cx, cy, r - StrokeThickness * 0.5 - 2);
                PART_Ticks.Children.Add(new Line
                {
                    X1 = outer.X, Y1 = outer.Y, X2 = inner.X, Y2 = inner.Y,
                    Stroke = new SolidColorBrush(Color.FromRgb(144, 147, 153)),
                    StrokeThickness = 1.5,
                });
            }
        }

        private string FormatValue(double value)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(FormatString) && (DataType == RangeDataType.Float || DataType == RangeDataType.Double))
                    return value.ToString(FormatString, CultureInfo.CurrentCulture);
                return value.ToString("F1", CultureInfo.CurrentCulture);
            }
            catch { return value.ToString(CultureInfo.CurrentCulture); }
        }

        #endregion
    }
}
