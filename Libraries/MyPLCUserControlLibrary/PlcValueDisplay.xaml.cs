using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyPLCUserControlLibrary
{
    /// <summary>
    /// 只读数值显示控件：订阅一个 PLC 标签并格式化显示其数值，支持标签 / 单位 / 数据类型 / 格式字符串。
    /// 未连接或尚无数据时自动显示占位符（默认 "—"）。继承 PlcControlBase，复用连接与订阅逻辑。
    /// </summary>
    public partial class PlcValueDisplay : PlcControlBase
    {
        private bool _isConnected;
        private object? _rawValue;

        public PlcValueDisplay()
        {
            InitializeComponent();
        }

        #region 依赖属性

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(PlcValueDisplay), new PropertyMetadata(string.Empty));

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public static readonly DependencyProperty UnitProperty =
            DependencyProperty.Register(nameof(Unit), typeof(string), typeof(PlcValueDisplay), new PropertyMetadata(string.Empty));

        public string Unit
        {
            get => (string)GetValue(UnitProperty);
            set => SetValue(UnitProperty, value);
        }

        public static readonly DependencyProperty DataTypeProperty =
            DependencyProperty.Register(nameof(DataType), typeof(RangeDataType), typeof(PlcValueDisplay),
                new PropertyMetadata(RangeDataType.Float, (d, e) => ((PlcValueDisplay)d).OnTagChanged()));

        public RangeDataType DataType
        {
            get => (RangeDataType)GetValue(DataTypeProperty);
            set => SetValue(DataTypeProperty, value);
        }

        public static readonly DependencyProperty FormatStringProperty =
            DependencyProperty.Register(nameof(FormatString), typeof(string), typeof(PlcValueDisplay), new PropertyMetadata("F2"));

        public string FormatString
        {
            get => (string)GetValue(FormatStringProperty);
            set => SetValue(FormatStringProperty, value);
        }

        public static readonly DependencyProperty DisplayTextProperty =
            DependencyProperty.Register(nameof(DisplayText), typeof(string), typeof(PlcValueDisplay), new PropertyMetadata("—"));

        public string DisplayText
        {
            get => (string)GetValue(DisplayTextProperty);
            set => SetValue(DisplayTextProperty, value);
        }

        public static readonly DependencyProperty DisconnectedTextProperty =
            DependencyProperty.Register(nameof(DisconnectedText), typeof(string), typeof(PlcValueDisplay), new PropertyMetadata("—"));

        public string DisconnectedText
        {
            get => (string)GetValue(DisconnectedTextProperty);
            set => SetValue(DisconnectedTextProperty, value);
        }

        public static readonly DependencyProperty ShowLabelProperty =
            DependencyProperty.Register(nameof(ShowLabel), typeof(bool), typeof(PlcValueDisplay), new PropertyMetadata(true));

        public bool ShowLabel
        {
            get => (bool)GetValue(ShowLabelProperty);
            set => SetValue(ShowLabelProperty, value);
        }

        public static readonly DependencyProperty ShowUnitProperty =
            DependencyProperty.Register(nameof(ShowUnit), typeof(bool), typeof(PlcValueDisplay), new PropertyMetadata(true));

        public bool ShowUnit
        {
            get => (bool)GetValue(ShowUnitProperty);
            set => SetValue(ShowUnitProperty, value);
        }

        public static readonly DependencyProperty LabelFontSizeProperty =
            DependencyProperty.Register(nameof(LabelFontSize), typeof(double), typeof(PlcValueDisplay), new PropertyMetadata(12d));

        public double LabelFontSize
        {
            get => (double)GetValue(LabelFontSizeProperty);
            set => SetValue(LabelFontSizeProperty, value);
        }

        public static readonly DependencyProperty ValueFontSizeProperty =
            DependencyProperty.Register(nameof(ValueFontSize), typeof(double), typeof(PlcValueDisplay), new PropertyMetadata(28d));

        public double ValueFontSize
        {
            get => (double)GetValue(ValueFontSizeProperty);
            set => SetValue(ValueFontSizeProperty, value);
        }

        public static readonly DependencyProperty UnitFontSizeProperty =
            DependencyProperty.Register(nameof(UnitFontSize), typeof(double), typeof(PlcValueDisplay), new PropertyMetadata(14d));

        public double UnitFontSize
        {
            get => (double)GetValue(UnitFontSizeProperty);
            set => SetValue(UnitFontSizeProperty, value);
        }

        public static readonly DependencyProperty LabelForegroundProperty =
            DependencyProperty.Register(nameof(LabelForeground), typeof(Brush), typeof(PlcValueDisplay), new PropertyMetadata(new SolidColorBrush(Colors.Gray)));

        public Brush LabelForeground
        {
            get => (Brush)GetValue(LabelForegroundProperty);
            set => SetValue(LabelForegroundProperty, value);
        }

        public static readonly DependencyProperty ValueForegroundProperty =
            DependencyProperty.Register(nameof(ValueForeground), typeof(Brush), typeof(PlcValueDisplay), new PropertyMetadata(new SolidColorBrush(Colors.Black)));

        public Brush ValueForeground
        {
            get => (Brush)GetValue(ValueForegroundProperty);
            set => SetValue(ValueForegroundProperty, value);
        }

        public static readonly DependencyProperty UnitForegroundProperty =
            DependencyProperty.Register(nameof(UnitForeground), typeof(Brush), typeof(PlcValueDisplay), new PropertyMetadata(new SolidColorBrush(Colors.DimGray)));

        public Brush UnitForeground
        {
            get => (Brush)GetValue(UnitForegroundProperty);
            set => SetValue(UnitForegroundProperty, value);
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

        /// <summary>只读显示控件：与其他控件共用 TagName 时，沿用已注册（通常是可写控件声明的）类型，自身不抢占类型。</summary>
        protected override bool IsWriteCapableControl => false;

        protected override void OnConnectionChangedInternal(IConnectionManager? oldConn, IConnectionManager? newConn)
        {
            _isConnected = newConn?.IsConnected ?? false;
            UpdateDisplay();
        }

        protected override void OnConnectionStateChanged(bool isConnected)
        {
            _isConnected = isConnected;
            UpdateDisplay();
        }

        protected override void OnPlcValueReceived(object? value)
        {
            _rawValue = value;
            UpdateDisplay();
        }

        #endregion

        #region 显示

        private void UpdateDisplay()
        {
            if (!_isConnected || _rawValue == null)
            {
                DisplayText = DisconnectedText;
                return;
            }
            DisplayText = FormatValue(_rawValue);
        }

        private string FormatValue(object? val)
        {
            if (val == null) return DisconnectedText;
            try
            {
                if (!string.IsNullOrWhiteSpace(FormatString))
                    return string.Format(CultureInfo.CurrentCulture, "{0:" + FormatString + "}", val);
                return Convert.ToString(val, CultureInfo.CurrentCulture) ?? DisconnectedText;
            }
            catch
            {
                return Convert.ToString(val, CultureInfo.CurrentCulture) ?? DisconnectedText;
            }
        }

        #endregion
    }
}
