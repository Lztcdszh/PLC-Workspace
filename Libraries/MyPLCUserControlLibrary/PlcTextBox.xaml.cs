using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MyPLCUserControlLibrary
{
    /// <summary>
    /// PLC 数值输入/显示控件，支持多种数值数据类型（有符号/无符号整数、浮点数），
    /// 可配置为只读、只写或读写模式。连接/订阅/自动注册由 PlcControlBase 统一处理。
    /// </summary>
    public partial class PlcTextBox : PlcControlBase
    {
        private TextBox? _textBox;
        private bool _isUpdatingFromPlc; // 防止因 PLC 更新而触发写入操作
        private string _lastGoodText = string.Empty;

        /// <summary>
        /// PLC 数据类型枚举（扩展了有符号/无符号整数及浮点类型）
        /// </summary>
        public enum PlcDataType
        {
            Int8,      // sbyte
            UInt8,     // byte
            Int16,     // short
            UInt16,    // ushort (原 Word)
            Int32,     // int (原 Int)
            UInt32,    // uint (原 DWord)
            Int64,     // long
            UInt64,    // ulong
            Float,     // float (原 Float)
            Double,    // double
        }

        /// <summary>
        /// 访问模式枚举
        /// </summary>
        public enum PlcAccessMode
        {
            ReadOnly,   // 只读，不可编辑
            WriteOnly,  // 只写，不显示当前值（可编辑）
            ReadWrite   // 读写，显示并可编辑
        }

        public PlcTextBox()
        {
            InitializeComponent();

            if (DesignerProperties.GetIsInDesignMode(this))
            {
                // 设计时预览
                this.Width = 100;
                this.Height = 26;
                this.Background = Brushes.White;
                this.Content = "123.45";
                return;
            }

            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_textBox == null)
            {
                _textBox = GetTemplateChild("PART_TextBox") as TextBox;
                if (_textBox != null)
                {
                    _textBox.LostFocus += TextBox_LostFocus;
                    _textBox.KeyDown += TextBox_KeyDown;
                }
            }
            UpdateReadOnlyState();
            UpdateDisplayText();
        }

        #region 依赖属性（仅保留控件特有属性，连接/标签/自动注册由基类提供）

        public static readonly DependencyProperty DataTypeProperty =
            DependencyProperty.Register(nameof(DataType), typeof(PlcDataType), typeof(PlcTextBox),
                new PropertyMetadata(PlcDataType.Float, OnDataTypeChanged));

        /// <summary>
        /// PLC 数据类型，影响值的解析和格式化
        /// </summary>
        public PlcDataType DataType
        {
            get => (PlcDataType)GetValue(DataTypeProperty);
            set => SetValue(DataTypeProperty, value);
        }

        public static readonly DependencyProperty AccessModeProperty =
            DependencyProperty.Register(nameof(AccessMode), typeof(PlcAccessMode), typeof(PlcTextBox),
                new PropertyMetadata(PlcAccessMode.ReadWrite, OnAccessModeChanged));

        /// <summary>
        /// 访问模式：只读、只写、读写
        /// </summary>
        public PlcAccessMode AccessMode
        {
            get => (PlcAccessMode)GetValue(AccessModeProperty);
            set => SetValue(AccessModeProperty, value);
        }

        public static readonly DependencyProperty FormatStringProperty =
            DependencyProperty.Register(nameof(FormatString), typeof(string), typeof(PlcTextBox),
                new PropertyMetadata(null, OnFormatStringChanged));

        /// <summary>
        /// 格式化字符串，用于显示数值（例如 "F2", "N0", "X" 等）
        /// </summary>
        public string? FormatString
        {
            get => (string)GetValue(FormatStringProperty);
            set => SetValue(FormatStringProperty, value);
        }

        public static readonly DependencyProperty CurrentValueProperty =
            DependencyProperty.Register(nameof(CurrentValue), typeof(object), typeof(PlcTextBox),
                new PropertyMetadata(null, OnCurrentValueChanged));

        /// <summary>
        /// 当前 PLC 值（object 类型，根据 DataType 自动转换）
        /// </summary>
        public object? CurrentValue
        {
            get => GetValue(CurrentValueProperty);
            set => SetValue(CurrentValueProperty, value);
        }

        // 内部使用的显示文本（用于绑定到 TextBox，单向）
        private static readonly DependencyPropertyKey DisplayTextPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(DisplayText), typeof(string), typeof(PlcTextBox),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty DisplayTextProperty = DisplayTextPropertyKey.DependencyProperty;

        public string DisplayText
        {
            get => (string)GetValue(DisplayTextProperty);
            private set => SetValue(DisplayTextPropertyKey, value);
        }

        // 从模板继承的常用属性（直接传递给内部 TextBox）
        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(PlcTextBox),
                new PropertyMetadata(new CornerRadius(2)));

        public CornerRadius CornerRadius
        {
            get => (CornerRadius)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        #endregion

        #region 属性变更回调

        private static void OnDataTypeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (PlcTextBox)d;
            control.UpdateDisplayText(); // 重新格式化显示
        }

        private static void OnFormatStringChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (PlcTextBox)d;
            control.UpdateDisplayText();
        }

        private static void OnAccessModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (PlcTextBox)d;
            control.UpdateReadOnlyState();
            control.RefreshSubscriptionForTagChange(); // 根据模式调整订阅（只写模式不订阅）
        }

        private static void OnCurrentValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (PlcTextBox)d;
            control.UpdateDisplayText();
        }

        #endregion

        #region 基类的可覆盖钩子

        /// <summary>只写模式不参与监控与订阅。</summary>
        protected override bool IsMonitorTag => AccessMode != PlcAccessMode.WriteOnly;

        /// <summary>只写模式不订阅值变化事件。</summary>
        protected override bool ShouldSubscribe => AccessMode != PlcAccessMode.WriteOnly;

        /// <summary>按当前数据类型创建自动注册用的标签（基类的默认实现为 bool，此处覆盖）。</summary>
        protected override PLCTag? CreateDefaultTag()
        {
            if (string.IsNullOrWhiteSpace(TagAddress)) return null;
            string effectiveName = string.IsNullOrWhiteSpace(TagName) ? TagAddress : TagName;

            switch (DataType)
            {
                case PlcDataType.Int8:    return PLCTag.Create<sbyte>(effectiveName, TagAddress, (sbyte)0, effectiveName);
                case PlcDataType.UInt8:   return PLCTag.Create<byte>(effectiveName, TagAddress, (byte)0, effectiveName);
                case PlcDataType.Int16:   return PLCTag.Create<short>(effectiveName, TagAddress, (short)0, effectiveName);
                case PlcDataType.UInt16:  return PLCTag.Create<ushort>(effectiveName, TagAddress, (ushort)0, effectiveName);
                case PlcDataType.Int32:   return PLCTag.Create<int>(effectiveName, TagAddress, 0, effectiveName);
                case PlcDataType.UInt32:  return PLCTag.Create<uint>(effectiveName, TagAddress, 0u, effectiveName);
                case PlcDataType.Int64:   return PLCTag.Create<long>(effectiveName, TagAddress, 0L, effectiveName);
                case PlcDataType.UInt64:  return PLCTag.Create<ulong>(effectiveName, TagAddress, 0ul, effectiveName);
                case PlcDataType.Float:   return PLCTag.Create<float>(effectiveName, TagAddress, 0.0f, effectiveName);
                case PlcDataType.Double:  return PLCTag.Create<double>(effectiveName, TagAddress, 0.0, effectiveName);
                default:                  return null;
            }
        }

        protected override void OnPlcValueReceived(object? val)
        {
            if (AccessMode == PlcAccessMode.WriteOnly)
                return; // 只写模式忽略读取的值

            try
            {
                object? converted = ConvertFromPlcValue(val);
                CurrentValue = converted;
            }
            catch (Exception ex)
            {
                LogDebug(nameof(OnPlcValueReceived), $"异常: {ex.Message}");
                // 保留旧值
            }
        }

        protected override void OnTagChanged()
        {
            UpdateDisplayText();
            base.OnTagChanged();
        }

        #endregion

        #region 值转换

        /// <summary>
        /// 将 PLC 原始值转换为当前数据类型对应的 .NET 类型
        /// </summary>
        private object? ConvertFromPlcValue(object? val)
        {
            if (val == null) return null;

            try
            {
                switch (DataType)
                {
                    case PlcDataType.Int8:
                        if (val is byte b8) return (sbyte)b8;
                        return Convert.ToSByte(val);
                    case PlcDataType.UInt8:
                        return Convert.ToByte(val);
                    case PlcDataType.Int16:
                        if (val is ushort u16) return (short)u16;
                        return Convert.ToInt16(val);
                    case PlcDataType.UInt16:
                        return Convert.ToUInt16(val);
                    case PlcDataType.Int32:
                        if (val is uint u32) return (int)u32;
                        return Convert.ToInt32(val);
                    case PlcDataType.UInt32:
                        return Convert.ToUInt32(val);
                    case PlcDataType.Int64:
                        if (val is ulong u64) return (long)u64;
                        return Convert.ToInt64(val);
                    case PlcDataType.UInt64:
                        return Convert.ToUInt64(val);
                    case PlcDataType.Float:
                        return Convert.ToSingle(val);
                    case PlcDataType.Double:
                        return Convert.ToDouble(val);
                    default:
                        return val;
                }
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region UI 更新与事件

        private void UpdateReadOnlyState()
        {
            if (_textBox == null) return;
            _textBox.IsReadOnly = (AccessMode == PlcAccessMode.ReadOnly);
            if (AccessMode == PlcAccessMode.WriteOnly)
            {
                DisplayText = string.Empty;
            }
        }

        private void UpdateDisplayText()
        {
            if (AccessMode == PlcAccessMode.WriteOnly)
            {
                DisplayText = string.Empty;
                return;
            }

            string newText = CurrentValue == null ? string.Empty : FormatValue(CurrentValue);
            DisplayText = newText;
            // 保存最后一次有效显示，用于恢复（当用户输入无效时）
            _lastGoodText = newText;
        }

        private string FormatValue(object value)
        {
            if (value == null) return string.Empty;
            try
            {
                if (!string.IsNullOrEmpty(FormatString))
                {
                    // 尝试使用格式字符串格式化（适用于 IFormattable 类型）
                    if (value is IFormattable formattable)
                        return formattable.ToString(FormatString, CultureInfo.CurrentCulture);
                }
                // 默认转换
                return value.ToString() ?? string.Empty;
            }
            catch
            {
                return value.ToString() ?? string.Empty;
            }
        }

        private void TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingFromPlc) return;
            if (AccessMode == PlcAccessMode.ReadOnly) return;

            string newText = _textBox?.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(newText))
            {
                // 空文本：可能表示清除，根据情况处理
                return;
            }

            // 尝试解析并写入
            _ = TryWriteAsync(newText);
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _textBox?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                e.Handled = true;
            }
        }

        private async Task TryWriteAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(TagName))
            {
                RestoreLastGoodText();
                return;
            }

            if (!ParseValue(text, out object? value))
            {
                // 解析失败，恢复原值
                RestoreLastGoodText();
                return;
            }

            bool success = await WriteTagAsync(TagName, value!);
            if (!success)
            {
                // 写入失败，恢复原值
                RestoreLastGoodText();
            }
            // 写入成功，等待值变化事件更新 CurrentValue，无需立即更新
        }

        /// <summary>
        /// 将用户输入的文本解析为当前数据类型对应的 .NET 对象
        /// </summary>
        private bool ParseValue(string text, out object? value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            try
            {
                switch (DataType)
                {
                    case PlcDataType.Int8:
                        if (sbyte.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out sbyte sb))
                        { value = sb; return true; }
                        break;
                    case PlcDataType.UInt8:
                        if (byte.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out byte b))
                        { value = b; return true; }
                        break;
                    case PlcDataType.Int16:
                        if (short.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out short s))
                        { value = s; return true; }
                        break;
                    case PlcDataType.UInt16:
                        if (ushort.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out ushort us))
                        { value = us; return true; }
                        break;
                    case PlcDataType.Int32:
                        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int i))
                        { value = i; return true; }
                        break;
                    case PlcDataType.UInt32:
                        if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out uint ui))
                        { value = ui; return true; }
                        break;
                    case PlcDataType.Int64:
                        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out long l))
                        { value = l; return true; }
                        break;
                    case PlcDataType.UInt64:
                        if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out ulong ul))
                        { value = ul; return true; }
                        break;
                    case PlcDataType.Float:
                        if (float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out float f))
                        { value = f; return true; }
                        break;
                    case PlcDataType.Double:
                        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double d))
                        { value = d; return true; }
                        break;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private void RestoreLastGoodText()
        {
            if (_textBox != null)
            {
                _isUpdatingFromPlc = true;
                try
                {
                    DisplayText = _lastGoodText;
                }
                finally
                {
                    _isUpdatingFromPlc = false;
                }
            }
        }

        #endregion
    }
}
