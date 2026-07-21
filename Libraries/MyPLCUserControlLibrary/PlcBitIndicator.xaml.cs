using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MyPLCUserControlLibrary
{
    /// <summary>
    /// 状态字位灯：将整型/字标签按位展开为 N 个指示灯（默认 8 位，可选 16 位），
    /// 用于展示设备状态字、报警位组等。支持正反序、亮灭配色、位号显示。继承 PlcControlBase。
    /// </summary>
    public partial class PlcBitIndicator : PlcControlBase
    {
        private readonly List<Ellipse> _lamps = new();
        private bool _isConnected;

        public PlcBitIndicator()
        {
            InitializeComponent();

            if (DesignerProperties.GetIsInDesignMode(this))
            {
                BitCount = 8;
                _isConnected = true;
                BuildLamps();
                UpdateBits(0b10110010);
                return;
            }

            Loaded += (s, e) => BuildLamps();
        }

        #region 依赖属性

        public static readonly DependencyProperty BitCountProperty =
            DependencyProperty.Register(nameof(BitCount), typeof(int), typeof(PlcBitIndicator),
                new PropertyMetadata(8, (d, e) => ((PlcBitIndicator)d).BuildLamps()));

        /// <summary>展示的位数（通常 8 或 16）。</summary>
        public int BitCount
        {
            get => (int)GetValue(BitCountProperty);
            set => SetValue(BitCountProperty, value);
        }

        public static readonly DependencyProperty OrientationProperty =
            DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(PlcBitIndicator),
                new PropertyMetadata(Orientation.Horizontal));

        public Orientation Orientation
        {
            get => (Orientation)GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        public static readonly DependencyProperty OnColorProperty =
            DependencyProperty.Register(nameof(OnColor), typeof(Brush), typeof(PlcBitIndicator),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(103, 194, 58)), (d, e) => ((PlcBitIndicator)d).RefreshLampColors()));

        public Brush OnColor
        {
            get => (Brush)GetValue(OnColorProperty);
            set => SetValue(OnColorProperty, value);
        }

        public static readonly DependencyProperty OffColorProperty =
            DependencyProperty.Register(nameof(OffColor), typeof(Brush), typeof(PlcBitIndicator),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(82, 90, 102)), (d, e) => ((PlcBitIndicator)d).RefreshLampColors()));

        public Brush OffColor
        {
            get => (Brush)GetValue(OffColorProperty);
            set => SetValue(OffColorProperty, value);
        }

        public static readonly DependencyProperty ShowBitIndexProperty =
            DependencyProperty.Register(nameof(ShowBitIndex), typeof(bool), typeof(PlcBitIndicator), new PropertyMetadata(true, (d, e) => ((PlcBitIndicator)d).BuildLamps()));

        public bool ShowBitIndex
        {
            get => (bool)GetValue(ShowBitIndexProperty);
            set => SetValue(ShowBitIndexProperty, value);
        }

        public static readonly DependencyProperty ReverseOrderProperty =
            DependencyProperty.Register(nameof(ReverseOrder), typeof(bool), typeof(PlcBitIndicator), new PropertyMetadata(false));

        /// <summary>为 true 时，bit0 显示在右侧（低位在右），更符合阅读习惯。</summary>
        public bool ReverseOrder
        {
            get => (bool)GetValue(ReverseOrderProperty);
            set => SetValue(ReverseOrderProperty, value);
        }

        public static readonly DependencyProperty DataTypeProperty =
            DependencyProperty.Register(nameof(DataType), typeof(RangeDataType), typeof(PlcBitIndicator),
                new PropertyMetadata(RangeDataType.UInt16, (d, e) => ((PlcBitIndicator)d).OnTagChanged()));

        public RangeDataType DataType
        {
            get => (RangeDataType)GetValue(DataTypeProperty);
            set => SetValue(DataTypeProperty, value);
        }

        public static readonly DependencyProperty LampSizeProperty =
            DependencyProperty.Register(nameof(LampSize), typeof(double), typeof(PlcBitIndicator), new PropertyMetadata(16d, (d, e) => ((PlcBitIndicator)d).BuildLamps()));

        public double LampSize
        {
            get => (double)GetValue(LampSizeProperty);
            set => SetValue(LampSizeProperty, value);
        }

        #endregion

        #region PlcControlBase 钩子

        protected override PLCTag? CreateDefaultTag()
        {
            if (string.IsNullOrWhiteSpace(TagAddress)) return null;
            string effectiveName = string.IsNullOrWhiteSpace(TagName) ? TagAddress : TagName;
            return DataType switch
            {
                RangeDataType.UInt8 => PLCTag.Create<byte>(effectiveName, TagAddress, (byte)0, effectiveName),
                RangeDataType.UInt16 => PLCTag.Create<ushort>(effectiveName, TagAddress, (ushort)0, effectiveName),
                RangeDataType.UInt32 => PLCTag.Create<uint>(effectiveName, TagAddress, 0u, effectiveName),
                RangeDataType.Int8 => PLCTag.Create<sbyte>(effectiveName, TagAddress, (sbyte)0, effectiveName),
                RangeDataType.Int16 => PLCTag.Create<short>(effectiveName, TagAddress, (short)0, effectiveName),
                RangeDataType.Int32 => PLCTag.Create<int>(effectiveName, TagAddress, 0, effectiveName),
                _ => PLCTag.Create<ushort>(effectiveName, TagAddress, (ushort)0, effectiveName),
            };
        }

        protected override void OnConnectionChangedInternal(IConnectionManager? oldConn, IConnectionManager? newConn)
        {
            _isConnected = newConn?.IsConnected ?? false;
            if (!_isConnected) ClearLamps();
        }

        protected override void OnConnectionStateChanged(bool isConnected)
        {
            _isConnected = isConnected;
            if (!_isConnected) ClearLamps();
        }

        protected override void OnPlcValueReceived(object? value)
        {
            if (!_isConnected || value == null) return;
            try
            {
                long bits = Convert.ToInt64(value);
                UpdateBits(bits);
            }
            catch { }
        }

        #endregion

        #region 灯阵构建与刷新

        private void BuildLamps()
        {
            if (PART_Lamps == null) return;
            PART_Lamps.Children.Clear();
            _lamps.Clear();

            int count = Math.Max(1, Math.Min(64, BitCount));
            for (int i = 0; i < count; i++)
            {
                var cell = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Margin = new Thickness(3),
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                var lamp = new Ellipse
                {
                    Width = LampSize,
                    Height = LampSize,
                    Fill = OffColor,
                    Stroke = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                    StrokeThickness = 1,
                };
                cell.Children.Add(lamp);
                _lamps.Add(lamp);

                if (ShowBitIndex)
                {
                    cell.Children.Add(new TextBlock
                    {
                        Text = i.ToString(),
                        FontSize = 9,
                        Foreground = new SolidColorBrush(Color.FromRgb(117, 120, 126)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    });
                }
                PART_Lamps.Children.Add(cell);
            }
        }

        private void UpdateBits(long bits)
        {
            int count = _lamps.Count;
            for (int k = 0; k < count; k++)
            {
                int bitIndex = ReverseOrder ? (count - 1 - k) : k;
                bool on = ((bits >> bitIndex) & 0x1) == 1;
                _lamps[k].Fill = on ? OnColor : OffColor;
            }
        }

        private void ClearLamps()
        {
            foreach (var lamp in _lamps)
                lamp.Fill = OffColor;
        }

        private void RefreshLampColors()
        {
            // 颜色变更时，按当前值重画（简单起见全部熄灭重设）
            ClearLamps();
        }

        #endregion
    }
}
