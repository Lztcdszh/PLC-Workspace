using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace MyPLCUserControlLibrary
{
    /// <summary>
    /// 条件判断类型
    /// </summary>
    public enum ConditionType
    {
        Equal,          // 等于
        NotEqual,       // 不等于
        GreaterThan,    // 大于
        GreaterOrEqual, // 大于等于
        LessThan,       // 小于
        LessOrEqual,    // 小于等于
        Between,        // 介于（包含边界）
        BetweenExclusive, // 介于（不包含边界）
        Outside,        // 超出范围（不包含边界）
        OutsideInclusive // 超出范围（包含边界）
    }

    /// <summary>
    /// 数值类型枚举
    /// </summary>
    public enum RangeDataType
    {
        Int8,      // sbyte
        UInt8,     // byte
        Int16,     // short
        UInt16,    // ushort
        Int32,     // int
        UInt32,    // uint
        Int64,     // long
        UInt64,    // ulong
        Float,     // float
        Double,    // double
    }

    /// <summary>
    /// 多条件逻辑运算符
    /// </summary>
    public enum ConditionLogic
    {
        And,        // 所有条件满足
        Or          // 任一条件满足
    }

    /// <summary>
    /// PLC 范围指示灯 - 根据标签数值范围改变颜色
    /// </summary>
    public partial class PlcRangeIndicator : PlcControlBase
    {
        private DispatcherTimer? _blinkTimer;
        private bool _blinkState;
        private object? _currentRawValue;
        private bool _isCurrentlySatisfied;

        public PlcRangeIndicator()
        {
            InitializeComponent();

            // 默认尺寸
            if (double.IsNaN(this.Width)) this.Width = 24;
            if (double.IsNaN(this.Height)) this.Height = 24;

            // 默认边框
            BorderBrush = Brushes.Gray;
            BorderThickness = new Thickness(1);

            // 默认文本样式
            TextFontSize = 10;
            TextForeground = Brushes.White;
            ShowValue = false;

            UpdateVisual();
        }

        #region 依赖属性

        // 数据类型（连接/标签/自动注册由 PlcControlBase 提供）
        public static readonly DependencyProperty DataTypeProperty =
            DependencyProperty.Register(nameof(DataType), typeof(RangeDataType), typeof(PlcRangeIndicator),
                new PropertyMetadata(RangeDataType.Float, (d, e) => ((PlcRangeIndicator)d).OnTagChanged()));

        public RangeDataType DataType
        {
            get => (RangeDataType)GetValue(DataTypeProperty);
            set => SetValue(DataTypeProperty, value);
        }

        // 自动注册
        // 条件类型
        public static readonly DependencyProperty ConditionProperty =
            DependencyProperty.Register(nameof(Condition), typeof(ConditionType), typeof(PlcRangeIndicator),
                new PropertyMetadata(ConditionType.Equal, (d, e) => ((PlcRangeIndicator)d).EvaluateCondition()));

        public ConditionType Condition
        {
            get => (ConditionType)GetValue(ConditionProperty);
            set => SetValue(ConditionProperty, value);
        }

        // 比较值1（用于等于、不等于、大于、小于等）
        public static readonly DependencyProperty CompareValueProperty =
            DependencyProperty.Register(nameof(CompareValue), typeof(string), typeof(PlcRangeIndicator),
                new PropertyMetadata("0", (d, e) => ((PlcRangeIndicator)d).EvaluateCondition()));

        public string CompareValue
        {
            get => (string)GetValue(CompareValueProperty);
            set => SetValue(CompareValueProperty, value);
        }

        // 比较值2（用于 Between 范围）
        public static readonly DependencyProperty CompareValue2Property =
            DependencyProperty.Register(nameof(CompareValue2), typeof(string), typeof(PlcRangeIndicator),
                new PropertyMetadata("0", (d, e) => ((PlcRangeIndicator)d).EvaluateCondition()));

        public string CompareValue2
        {
            get => (string)GetValue(CompareValue2Property);
            set => SetValue(CompareValue2Property, value);
        }

        // 满足条件时的颜色
        public static readonly DependencyProperty TrueBrushProperty =
            DependencyProperty.Register(nameof(TrueBrush), typeof(Brush), typeof(PlcRangeIndicator),
                new PropertyMetadata(new SolidColorBrush(Colors.LimeGreen), (d, e) => ((PlcRangeIndicator)d).UpdateVisual()));

        public Brush TrueBrush
        {
            get => (Brush)GetValue(TrueBrushProperty);
            set => SetValue(TrueBrushProperty, value);
        }

        // 不满足条件时的颜色
        public static readonly DependencyProperty FalseBrushProperty =
            DependencyProperty.Register(nameof(FalseBrush), typeof(Brush), typeof(PlcRangeIndicator),
                new PropertyMetadata(new SolidColorBrush(Colors.DarkGray), (d, e) => ((PlcRangeIndicator)d).UpdateVisual()));

        public Brush FalseBrush
        {
            get => (Brush)GetValue(FalseBrushProperty);
            set => SetValue(FalseBrushProperty, value);
        }

        // 闪烁启用
        public static readonly DependencyProperty EnableBlinkingProperty =
            DependencyProperty.Register(nameof(EnableBlinking), typeof(bool), typeof(PlcRangeIndicator),
                new PropertyMetadata(false, (d, e) => ((PlcRangeIndicator)d).UpdateBlinking()));

        public bool EnableBlinking
        {
            get => (bool)GetValue(EnableBlinkingProperty);
            set => SetValue(EnableBlinkingProperty, value);
        }

        // 闪烁间隔（毫秒）
        public static readonly DependencyProperty BlinkIntervalProperty =
            DependencyProperty.Register(nameof(BlinkInterval), typeof(int), typeof(PlcRangeIndicator),
                new PropertyMetadata(500, (d, e) => ((PlcRangeIndicator)d).UpdateBlinking()));

        public int BlinkInterval
        {
            get => (int)GetValue(BlinkIntervalProperty);
            set => SetValue(BlinkIntervalProperty, value);
        }

        // 当前使用的画刷（内部）
        private static readonly DependencyPropertyKey CurrentBrushPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(CurrentBrush), typeof(Brush), typeof(PlcRangeIndicator),
                new PropertyMetadata(Brushes.Gray));

        public static readonly DependencyProperty CurrentBrushProperty = CurrentBrushPropertyKey.DependencyProperty;

        public Brush CurrentBrush
        {
            get => (Brush)GetValue(CurrentBrushProperty);
            private set => SetValue(CurrentBrushPropertyKey, value);
        }

        // 当前值文本（用于显示数值）
        public static readonly DependencyProperty DisplayTextProperty =
            DependencyProperty.Register(nameof(DisplayText), typeof(string), typeof(PlcRangeIndicator),
                new PropertyMetadata(string.Empty));

        public string DisplayText
        {
            get => (string)GetValue(DisplayTextProperty);
            set => SetValue(DisplayTextProperty, value);
        }

        // 是否显示数值
        public static readonly DependencyProperty ShowValueProperty =
            DependencyProperty.Register(nameof(ShowValue), typeof(bool), typeof(PlcRangeIndicator),
                new PropertyMetadata(false, (d, e) => ((PlcRangeIndicator)d).UpdateDisplay()));

        public bool ShowValue
        {
            get => (bool)GetValue(ShowValueProperty);
            set => SetValue(ShowValueProperty, value);
        }

        // 文本字体大小
        public static readonly DependencyProperty TextFontSizeProperty =
            DependencyProperty.Register(nameof(TextFontSize), typeof(double), typeof(PlcRangeIndicator),
                new PropertyMetadata(10.0));

        public double TextFontSize
        {
            get => (double)GetValue(TextFontSizeProperty);
            set => SetValue(TextFontSizeProperty, value);
        }

        // 文本颜色
        public static readonly DependencyProperty TextForegroundProperty =
            DependencyProperty.Register(nameof(TextForeground), typeof(Brush), typeof(PlcRangeIndicator),
                new PropertyMetadata(Brushes.White));

        public Brush TextForeground
        {
            get => (Brush)GetValue(TextForegroundProperty);
            set => SetValue(TextForegroundProperty, value);
        }

        // 边框颜色
        public static readonly DependencyProperty BorderBrushProperty =
            DependencyProperty.Register(nameof(BorderBrush), typeof(Brush), typeof(PlcRangeIndicator),
                new PropertyMetadata(Brushes.Gray));

        public Brush BorderBrush
        {
            get => (Brush)GetValue(BorderBrushProperty);
            set => SetValue(BorderBrushProperty, value);
        }

        // 边框厚度
        public static readonly DependencyProperty BorderThicknessProperty =
            DependencyProperty.Register(nameof(BorderThickness), typeof(Thickness), typeof(PlcRangeIndicator),
                new PropertyMetadata(new Thickness(1)));

        public Thickness BorderThickness
        {
            get => (Thickness)GetValue(BorderThicknessProperty);
            set => SetValue(BorderThicknessProperty, value);
        }

        // 工具提示文本
        public static readonly DependencyProperty ToolTipTextProperty =
            DependencyProperty.Register(nameof(ToolTipText), typeof(string), typeof(PlcRangeIndicator),
                new PropertyMetadata(string.Empty));

        public string ToolTipText
        {
            get => (string)GetValue(ToolTipTextProperty);
            set => SetValue(ToolTipTextProperty, value);
        }

        // 自动工具提示
        public static readonly DependencyProperty AutoToolTipProperty =
            DependencyProperty.Register(nameof(AutoToolTip), typeof(bool), typeof(PlcRangeIndicator),
                new PropertyMetadata(true, (d, e) => ((PlcRangeIndicator)d).UpdateToolTip()));

        public bool AutoToolTip
        {
            get => (bool)GetValue(AutoToolTipProperty);
            set => SetValue(AutoToolTipProperty, value);
        }

        // 多条件支持（可选扩展）
        public static readonly DependencyProperty LogicProperty =
            DependencyProperty.Register(nameof(Logic), typeof(ConditionLogic), typeof(PlcRangeIndicator),
                new PropertyMetadata(ConditionLogic.And));

        public ConditionLogic Logic
        {
            get => (ConditionLogic)GetValue(LogicProperty);
            set => SetValue(LogicProperty, value);
        }

        #endregion

        #region 连接与订阅

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

        /// <summary>连接状态变化时重新评估条件。</summary>
        protected override void OnConnectionStateChanged(bool isConnected)
        {
            EvaluateCondition();
        }

        /// <summary>基类在 UI 线程回调：收到匹配标签值后评估条件并更新显示。</summary>
        protected override void OnPlcValueReceived(object? value)
        {
            _currentRawValue = value;
            EvaluateCondition();
            UpdateDisplay();
            UpdateToolTip();
        }

        #endregion

        #region 条件评估

        /// <summary>
        /// 评估当前值是否满足条件
        /// </summary>
        private void EvaluateCondition()
        {
            if (_currentRawValue == null)
            {
                _isCurrentlySatisfied = false;
                UpdateVisual();
                return;
            }

            try
            {
                double currentValue = ConvertToDouble(_currentRawValue);
                double compareVal1 = ParseCompareValue(CompareValue);
                double compareVal2 = ParseCompareValue(CompareValue2);

                _isCurrentlySatisfied = Condition switch
                {
                    ConditionType.Equal => Math.Abs(currentValue - compareVal1) < 1e-10,
                    ConditionType.NotEqual => Math.Abs(currentValue - compareVal1) > 1e-10,
                    ConditionType.GreaterThan => currentValue > compareVal1,
                    ConditionType.GreaterOrEqual => currentValue >= compareVal1,
                    ConditionType.LessThan => currentValue < compareVal1,
                    ConditionType.LessOrEqual => currentValue <= compareVal1,
                    ConditionType.Between => currentValue >= compareVal1 && currentValue <= compareVal2,
                    ConditionType.BetweenExclusive => currentValue > compareVal1 && currentValue < compareVal2,
                    ConditionType.Outside => currentValue < compareVal1 || currentValue > compareVal2,
                    ConditionType.OutsideInclusive => currentValue <= compareVal1 || currentValue >= compareVal2,
                    _ => false,
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PlcRangeIndicator] EvaluateCondition error: {ex.Message}");
                _isCurrentlySatisfied = false;
            }

            UpdateVisual();
        }

        /// <summary>
        /// 将对象转换为 double 用于比较
        /// </summary>
        private double ConvertToDouble(object? value)
        {
            if (value == null) return 0;
            try
            {
                return Convert.ToDouble(value);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 解析比较值字符串
        /// </summary>
        private double ParseCompareValue(string valueStr)
        {
            if (string.IsNullOrWhiteSpace(valueStr))
                return 0;

            return DataType switch
            {
                RangeDataType.Int8 => sbyte.Parse(valueStr, CultureInfo.InvariantCulture),
                RangeDataType.UInt8 => byte.Parse(valueStr, CultureInfo.InvariantCulture),
                RangeDataType.Int16 => short.Parse(valueStr, CultureInfo.InvariantCulture),
                RangeDataType.UInt16 => ushort.Parse(valueStr, CultureInfo.InvariantCulture),
                RangeDataType.Int32 => int.Parse(valueStr, CultureInfo.InvariantCulture),
                RangeDataType.UInt32 => uint.Parse(valueStr, CultureInfo.InvariantCulture),
                RangeDataType.Int64 => long.Parse(valueStr, CultureInfo.InvariantCulture),
                RangeDataType.UInt64 => ulong.Parse(valueStr, CultureInfo.InvariantCulture),
                RangeDataType.Float => float.Parse(valueStr, CultureInfo.InvariantCulture),
                RangeDataType.Double => double.Parse(valueStr, CultureInfo.InvariantCulture),
                _ => double.Parse(valueStr, CultureInfo.InvariantCulture),
            };
        }

        #endregion

        #region 视觉更新

        private void UpdateVisual()
        {
            CurrentBrush = _isCurrentlySatisfied ? TrueBrush : FalseBrush;
            UpdateBlinking();

            if (PART_Ellipse != null)
            {
                PART_Ellipse.Fill = CurrentBrush;
            }
        }

        private void UpdateBlinking()
        {
            bool shouldBlink = EnableBlinking && _isCurrentlySatisfied;

            if (shouldBlink)
            {
                if (_blinkTimer == null)
                {
                    _blinkTimer = new DispatcherTimer();
                    _blinkTimer.Interval = TimeSpan.FromMilliseconds(BlinkInterval);
                    _blinkTimer.Tick += (s, e) =>
                    {
                        _blinkState = !_blinkState;
                        if (PART_Ellipse != null)
                        {
                            PART_Ellipse.Opacity = _blinkState ? 0.3 : 1.0;
                        }
                    };
                    _blinkTimer.Start();
                    _blinkState = false;
                    if (PART_Ellipse != null) PART_Ellipse.Opacity = 1.0;
                }
                else if (_blinkTimer.Interval != TimeSpan.FromMilliseconds(BlinkInterval))
                {
                    _blinkTimer.Interval = TimeSpan.FromMilliseconds(BlinkInterval);
                }
            }
            else
            {
                if (_blinkTimer != null)
                {
                    _blinkTimer.Stop();
                    _blinkTimer = null;
                }
                if (PART_Ellipse != null) PART_Ellipse.Opacity = 1.0;
            }
        }

        private void UpdateDisplay()
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
                return DataType switch
                {
                    RangeDataType.Float when value is float f => f.ToString("F2", CultureInfo.CurrentCulture),
                    RangeDataType.Double when value is double d => d.ToString("F2", CultureInfo.CurrentCulture),
                    _ => ConvertToDouble(value).ToString(CultureInfo.CurrentCulture),
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

        #region 标签变更处理

        protected override void OnTagChanged()
        {
            base.OnTagChanged();        // 基类：退订并按新标签重新注册/订阅
            EvaluateCondition();        // 数据类型/地址变更后重算
            UpdateDisplay();
        }

        #endregion

        #region 资源清理

        protected override void OnUnloadedInternal()
        {
            _blinkTimer?.Stop();
            _blinkTimer = null;
        }

        #endregion

        #endregion  // 连接与订阅
    }

    #region 转换器

    /// <summary>
    /// 布尔值到可见性的转换器
    /// </summary>
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
                return boolValue ? Visibility.Visible : Visibility.Collapsed;
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility visibility)
                return visibility == Visibility.Visible;
            return false;
        }
    }

    /// <summary>
    /// 范围颜色转换器（可选）
    /// </summary>
    public class RangeColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 可以在 XAML 中使用此转换器根据条件动态选择颜色
            return Binding.DoNothing;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    #endregion
}