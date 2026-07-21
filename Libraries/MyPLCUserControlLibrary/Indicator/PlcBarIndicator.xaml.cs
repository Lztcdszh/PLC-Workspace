using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyPLCUserControlLibrary
{
    /// <summary>
    /// 柱状控件方向
    /// </summary>
    public enum BarOrientation
    {
        Horizontal,
        Vertical
    }

    /// <summary>
    /// PLC 柱状控件 - 根据标签数值显示填充比例，支持三色区间
    /// </summary>
    public partial class PlcBarIndicator : PlcControlBase
    {
        private object? _currentRawValue;
        private double _currentFillRatio = 0d;   // 0~1

        public PlcBarIndicator()
        {
            InitializeComponent();

            // 默认尺寸
            if (double.IsNaN(Width)) Width = 100;
            if (double.IsNaN(Height)) Height = 24;

            // 默认外观
            BackgroundBrush = Brushes.LightGray;
            BorderBrush = Brushes.Gray;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(3);
            LowBrush = Brushes.Red;
            FillBrush = Brushes.Yellow;
            HighBrush = Brushes.LimeGreen;

            // 默认阈值
            LowThreshold = 30;
            HighThreshold = 70;

            // 文本样式
            TextFontSize = 10;
            TextForeground = Brushes.Black;
            ShowValue = false;

            // 初始颜色（未连接时显示默认）
            CurrentFillBrush = FillBrush;

            // 重要：等待控件加载完成后再更新尺寸，否则 ActualWidth 为 0
            Loaded += (s, e) => UpdateFillDimensions();
            SizeChanged += (s, e) => UpdateFillDimensions();
        }

        #region 依赖属性

        // 数据类型（连接/标签/自动注册由 PlcControlBase 提供）
        public static readonly DependencyProperty DataTypeProperty =
            DependencyProperty.Register(nameof(DataType), typeof(RangeDataType), typeof(PlcBarIndicator),
                new PropertyMetadata(RangeDataType.Float, (d, e) => ((PlcBarIndicator)d).OnTagChanged()));

        public RangeDataType DataType
        {
            get => (RangeDataType)GetValue(DataTypeProperty);
            set => SetValue(DataTypeProperty, value);
        }

        // 最小值（对应填充比例 0）
        public static readonly DependencyProperty MinimumProperty =
            DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(PlcBarIndicator),
                new PropertyMetadata(0d, (d, e) => ((PlcBarIndicator)d).RecalculateFillRatioAndColor()));

        public double Minimum
        {
            get => (double)GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        // 最大值（对应填充比例 1）
        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(PlcBarIndicator),
                new PropertyMetadata(100d, (d, e) => ((PlcBarIndicator)d).RecalculateFillRatioAndColor()));

        public double Maximum
        {
            get => (double)GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        // 方向
        public static readonly DependencyProperty OrientationProperty =
            DependencyProperty.Register(nameof(Orientation), typeof(BarOrientation), typeof(PlcBarIndicator),
                new PropertyMetadata(BarOrientation.Horizontal, (d, e) => ((PlcBarIndicator)d).UpdateFillDimensions()));

        public BarOrientation Orientation
        {
            get => (BarOrientation)GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        // 低值阈值（小于此值使用 LowBrush）
        public static readonly DependencyProperty LowThresholdProperty =
            DependencyProperty.Register(nameof(LowThreshold), typeof(double), typeof(PlcBarIndicator),
                new PropertyMetadata(30d, (d, e) => ((PlcBarIndicator)d).UpdateFillColor()));

        public double LowThreshold
        {
            get => (double)GetValue(LowThresholdProperty);
            set => SetValue(LowThresholdProperty, value);
        }

        // 高值阈值（大于此值使用 HighBrush）
        public static readonly DependencyProperty HighThresholdProperty =
            DependencyProperty.Register(nameof(HighThreshold), typeof(double), typeof(PlcBarIndicator),
                new PropertyMetadata(70d, (d, e) => ((PlcBarIndicator)d).UpdateFillColor()));

        public double HighThreshold
        {
            get => (double)GetValue(HighThresholdProperty);
            set => SetValue(HighThresholdProperty, value);
        }

        // 低值区画刷
        public static readonly DependencyProperty LowBrushProperty =
            DependencyProperty.Register(nameof(LowBrush), typeof(Brush), typeof(PlcBarIndicator),
                new PropertyMetadata(Brushes.Red, (d, e) => ((PlcBarIndicator)d).UpdateFillColor()));

        public Brush LowBrush
        {
            get => (Brush)GetValue(LowBrushProperty);
            set => SetValue(LowBrushProperty, value);
        }

        // 正常范围画刷
        public static readonly DependencyProperty FillBrushProperty =
            DependencyProperty.Register(nameof(FillBrush), typeof(Brush), typeof(PlcBarIndicator),
                new PropertyMetadata(Brushes.Yellow, (d, e) => ((PlcBarIndicator)d).UpdateFillColor()));

        public Brush FillBrush
        {
            get => (Brush)GetValue(FillBrushProperty);
            set => SetValue(FillBrushProperty, value);
        }

        // 高值区画刷
        public static readonly DependencyProperty HighBrushProperty =
            DependencyProperty.Register(nameof(HighBrush), typeof(Brush), typeof(PlcBarIndicator),
                new PropertyMetadata(Brushes.LimeGreen, (d, e) => ((PlcBarIndicator)d).UpdateFillColor()));

        public Brush HighBrush
        {
            get => (Brush)GetValue(HighBrushProperty);
            set => SetValue(HighBrushProperty, value);
        }

        // 当前使用的填充画刷（只读）
        private static readonly DependencyPropertyKey CurrentFillBrushPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(CurrentFillBrush), typeof(Brush), typeof(PlcBarIndicator),
                new PropertyMetadata(Brushes.Yellow));

        public static readonly DependencyProperty CurrentFillBrushProperty = CurrentFillBrushPropertyKey.DependencyProperty;

        public Brush CurrentFillBrush
        {
            get => (Brush)GetValue(CurrentFillBrushProperty);
            private set => SetValue(CurrentFillBrushPropertyKey, value);
        }

        // 背景画刷
        public static readonly DependencyProperty BackgroundBrushProperty =
            DependencyProperty.Register(nameof(BackgroundBrush), typeof(Brush), typeof(PlcBarIndicator),
                new PropertyMetadata(Brushes.LightGray));

        public Brush BackgroundBrush
        {
            get => (Brush)GetValue(BackgroundBrushProperty);
            set => SetValue(BackgroundBrushProperty, value);
        }

        // 边框画刷
        public static readonly DependencyProperty BorderBrushProperty =
            DependencyProperty.Register(nameof(BorderBrush), typeof(Brush), typeof(PlcBarIndicator),
                new PropertyMetadata(Brushes.Gray));

        public Brush BorderBrush
        {
            get => (Brush)GetValue(BorderBrushProperty);
            set => SetValue(BorderBrushProperty, value);
        }

        // 边框厚度
        public static readonly DependencyProperty BorderThicknessProperty =
            DependencyProperty.Register(nameof(BorderThickness), typeof(Thickness), typeof(PlcBarIndicator),
                new PropertyMetadata(new Thickness(1)));

        public Thickness BorderThickness
        {
            get => (Thickness)GetValue(BorderThicknessProperty);
            set => SetValue(BorderThicknessProperty, value);
        }

        // 圆角半径
        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(PlcBarIndicator),
                new PropertyMetadata(new CornerRadius(3)));

        public CornerRadius CornerRadius
        {
            get => (CornerRadius)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        // 是否显示数值文本
        public static readonly DependencyProperty ShowValueProperty =
            DependencyProperty.Register(nameof(ShowValue), typeof(bool), typeof(PlcBarIndicator),
                new PropertyMetadata(false, (d, e) => ((PlcBarIndicator)d).UpdateDisplayText()));

        public bool ShowValue
        {
            get => (bool)GetValue(ShowValueProperty);
            set => SetValue(ShowValueProperty, value);
        }

        // 显示文本
        public static readonly DependencyProperty DisplayTextProperty =
            DependencyProperty.Register(nameof(DisplayText), typeof(string), typeof(PlcBarIndicator),
                new PropertyMetadata(string.Empty));

        public string DisplayText
        {
            get => (string)GetValue(DisplayTextProperty);
            set => SetValue(DisplayTextProperty, value);
        }

        // 文本字体大小
        public static readonly DependencyProperty TextFontSizeProperty =
            DependencyProperty.Register(nameof(TextFontSize), typeof(double), typeof(PlcBarIndicator),
                new PropertyMetadata(10d));

        public double TextFontSize
        {
            get => (double)GetValue(TextFontSizeProperty);
            set => SetValue(TextFontSizeProperty, value);
        }

        // 文本颜色
        public static readonly DependencyProperty TextForegroundProperty =
            DependencyProperty.Register(nameof(TextForeground), typeof(Brush), typeof(PlcBarIndicator),
                new PropertyMetadata(Brushes.Black));

        public Brush TextForeground
        {
            get => (Brush)GetValue(TextForegroundProperty);
            set => SetValue(TextForegroundProperty, value);
        }

        // 工具提示文本
        public static readonly DependencyProperty ToolTipTextProperty =
            DependencyProperty.Register(nameof(ToolTipText), typeof(string), typeof(PlcBarIndicator),
                new PropertyMetadata(string.Empty));

        public string ToolTipText
        {
            get => (string)GetValue(ToolTipTextProperty);
            set => SetValue(ToolTipTextProperty, value);
        }

        // 自动工具提示
        public static readonly DependencyProperty AutoToolTipProperty =
            DependencyProperty.Register(nameof(AutoToolTip), typeof(bool), typeof(PlcBarIndicator),
                new PropertyMetadata(true, (d, e) => ((PlcBarIndicator)d).UpdateToolTip()));

        public bool AutoToolTip
        {
            get => (bool)GetValue(AutoToolTipProperty);
            set => SetValue(AutoToolTipProperty, value);
        }

        #endregion

        #region 连接与订阅（与原逻辑一致）

        #region PLC 基类钩子

        /// <summary>按当前数据类型创建指定名称的标签（供自动注册使用）。</summary>
        private PLCTag? CreateTagByDataType(string tagName)
        {
            return DataType switch
            {
                RangeDataType.Int8 => PLCTag.Create<sbyte>(tagName, TagAddress, (sbyte)0, tagName),
                RangeDataType.UInt8 => PLCTag.Create<byte>(tagName, TagAddress, (byte)0, tagName),
                RangeDataType.Int16 => PLCTag.Create<short>(tagName, TagAddress, (short)0, tagName),
                RangeDataType.UInt16 => PLCTag.Create<ushort>(tagName, TagAddress, (ushort)0, tagName),
                RangeDataType.Int32 => PLCTag.Create<int>(tagName, TagAddress, 0, tagName),
                RangeDataType.UInt32 => PLCTag.Create<uint>(tagName, TagAddress, 0u, tagName),
                RangeDataType.Int64 => PLCTag.Create<long>(tagName, TagAddress, 0L, tagName),
                RangeDataType.UInt64 => PLCTag.Create<ulong>(tagName, TagAddress, 0ul, tagName),
                RangeDataType.Float => PLCTag.Create<float>(tagName, TagAddress, 0.0f, tagName),
                RangeDataType.Double => PLCTag.Create<double>(tagName, TagAddress, 0.0, tagName),
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

        /// <summary>连接状态变化时重新评估数值与颜色。</summary>
        protected override void OnConnectionStateChanged(bool isConnected)
        {
            RecalculateFillRatioAndColor();
        }

        /// <summary>基类在 UI 线程回调：收到匹配标签值后更新填充与显示。</summary>
        protected override void OnPlcValueReceived(object? value)
        {
            _currentRawValue = value;
            RecalculateFillRatioAndColor();
            UpdateDisplayText();
            UpdateToolTip();
        }

        #endregion

        #endregion

        #region 数值与填充逻辑

        private double GetCurrentDoubleValue()
        {
            if (_currentRawValue == null) return 0;

            try
            {
                return DataType switch
                {
                    RangeDataType.Int8 => Convert.ToDouble((sbyte)_currentRawValue),
                    RangeDataType.UInt8 => Convert.ToDouble((byte)_currentRawValue),
                    RangeDataType.Int16 => Convert.ToDouble((short)_currentRawValue),
                    RangeDataType.UInt16 => Convert.ToDouble((ushort)_currentRawValue),
                    RangeDataType.Int32 => Convert.ToDouble((int)_currentRawValue),
                    RangeDataType.UInt32 => Convert.ToDouble((uint)_currentRawValue),
                    RangeDataType.Int64 => Convert.ToDouble((long)_currentRawValue),
                    RangeDataType.UInt64 => Convert.ToDouble((ulong)_currentRawValue),
                    RangeDataType.Float => Convert.ToDouble((float)_currentRawValue),
                    RangeDataType.Double => (double)_currentRawValue,
                    _ => Convert.ToDouble(_currentRawValue),
                };
            }
            catch
            {
                return 0;
            }
        }

        private void RecalculateFillRatioAndColor()
        {
            double curVal = GetCurrentDoubleValue();
            double min = Minimum;
            double max = Maximum;

            if (max <= min)
            {
                _currentFillRatio = 0;
            }
            else
            {
                double ratio = (curVal - min) / (max - min);
                _currentFillRatio = Math.Max(0, Math.Min(1, ratio));
            }

            UpdateFillDimensions();
            UpdateFillColor();
        }

        private void UpdateFillColor()
        {
            double curVal = GetCurrentDoubleValue();
            Brush newBrush;
            if (curVal < LowThreshold)
                newBrush = LowBrush;
            else if (curVal > HighThreshold)
                newBrush = HighBrush;
            else
                newBrush = FillBrush;

            CurrentFillBrush = newBrush;
            // 注意：CurrentFillBrush 通过绑定自动更新 UI，无需手动设置 PART_FillBorder.Background
        }

        private void UpdateFillDimensions()
        {
            if (PART_FillBorder == null || PART_BackgroundBorder == null) return;

            double totalWidth = PART_BackgroundBorder.ActualWidth;
            double totalHeight = PART_BackgroundBorder.ActualHeight;

            if (totalWidth <= 0 || totalHeight <= 0) return; // 尚未布局完成

            if (Orientation == BarOrientation.Horizontal)
            {
                double fillWidth = totalWidth * _currentFillRatio;
                PART_FillBorder.Width = fillWidth;
                PART_FillBorder.Height = totalHeight;
                PART_FillBorder.HorizontalAlignment = HorizontalAlignment.Left;
                PART_FillBorder.VerticalAlignment = VerticalAlignment.Top;
            }
            else // Vertical
            {
                double fillHeight = totalHeight * _currentFillRatio;
                PART_FillBorder.Height = fillHeight;
                PART_FillBorder.Width = totalWidth;
                PART_FillBorder.HorizontalAlignment = HorizontalAlignment.Left;
                PART_FillBorder.VerticalAlignment = VerticalAlignment.Bottom; // 从下往上填充
            }
        }

        #endregion

        #region 文本与工具提示

        private void UpdateDisplayText()
        {
            if (ShowValue && _currentRawValue != null)
            {
                DisplayText = FormatValue(_currentRawValue);
            }
            else
            {
                DisplayText = string.Empty;
            }
        }

        private string FormatValue(object value)
        {
            try
            {
                double d = GetCurrentDoubleValue();
                return DataType switch
                {
                    RangeDataType.Float => d.ToString("F2", CultureInfo.CurrentCulture),
                    RangeDataType.Double => d.ToString("F2", CultureInfo.CurrentCulture),
                    _ => d.ToString(CultureInfo.CurrentCulture),
                };
            }
            catch
            {
                return value.ToString() ?? string.Empty;
            }
        }

        private void UpdateToolTip()
        {
            if (AutoToolTip && _currentRawValue != null)
            {
                ToolTipText = string.IsNullOrWhiteSpace(TagName)
                    ? $"{TagAddress} = {FormatValue(_currentRawValue)}"
                    : $"{TagName} ({TagAddress}) = {FormatValue(_currentRawValue)}";
            }
        }

        #endregion

        #region 标签变更与清理

        protected override void OnTagChanged()
        {
            base.OnTagChanged();                 // 基类：退订并按新标签重新注册/订阅
            RecalculateFillRatioAndColor();      // 数据类型/地址变更后重算填充与颜色
            UpdateDisplayText();
        }

        #endregion
    }
}