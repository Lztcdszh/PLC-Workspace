using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MyPLCUserControlLibrary
{
    /// <summary>
    /// 触摸屏友好数值输入：只读显示 PLC 数值，点击弹出数字软键盘（含符号/小数/退格/清空/确定取消），
    /// 支持数据类型/格式/范围校验，可选 ConfirmWrite。适合车间一体机无物理键盘场景。继承 PlcControlBase。
    /// </summary>
    public partial class PlcNumericInput : PlcControlBase
    {
        private bool _isConnected;
        private object? _rawValue;
        private string _pending = string.Empty;

        // 软键盘按键布局：(显示文本, 命令)
        private static readonly (string Label, string Cmd)[] KeyLayout =
        {
            ("7", "7"), ("8", "8"), ("9", "9"),
            ("4", "4"), ("5", "5"), ("6", "6"),
            ("1", "1"), ("2", "2"), ("3", "3"),
            ("±", "SIGN"), ("0", "0"), (".", "."),
            ("⌫", "BS"), ("清空", "CLR"),
            ("取消", "CANCEL"), ("确定", "OK"),
        };

        public PlcNumericInput()
        {
            InitializeComponent();
            BuildKeypad();

            if (DesignerProperties.GetIsInDesignMode(this))
            {
                _isConnected = true;
                PART_Display.Text = "123.45";
                return;
            }

            PART_Display.PreviewMouseDown += (s, e) =>
            {
                if (!IsReadOnly) OpenKeypad();
            };
            PART_Display.GotFocus += (s, e) =>
            {
                if (!IsReadOnly) OpenKeypad();
            };
            PART_EditHint.PreviewMouseDown += (s, e) =>
            {
                if (!IsReadOnly) { e.Handled = true; OpenKeypad(); }
            };
            OnReadOnlyChanged();
        }

        #region 依赖属性

        public static readonly DependencyProperty DataTypeProperty =
            DependencyProperty.Register(nameof(DataType), typeof(RangeDataType), typeof(PlcNumericInput),
                new PropertyMetadata(RangeDataType.Float, (d, e) => ((PlcNumericInput)d).OnTagChanged()));

        public RangeDataType DataType
        {
            get => (RangeDataType)GetValue(DataTypeProperty);
            set => SetValue(DataTypeProperty, value);
        }

        public static readonly DependencyProperty FormatStringProperty =
            DependencyProperty.Register(nameof(FormatString), typeof(string), typeof(PlcNumericInput), new PropertyMetadata("F2"));

        public string FormatString
        {
            get => (string)GetValue(FormatStringProperty);
            set => SetValue(FormatStringProperty, value);
        }

        public static readonly DependencyProperty UnitProperty =
            DependencyProperty.Register(nameof(Unit), typeof(string), typeof(PlcNumericInput), new PropertyMetadata(string.Empty));

        public string Unit
        {
            get => (string)GetValue(UnitProperty);
            set => SetValue(UnitProperty, value);
        }

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(PlcNumericInput), new PropertyMetadata(string.Empty));

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public static readonly DependencyProperty ShowCaptionProperty =
            DependencyProperty.Register(nameof(ShowCaption), typeof(bool), typeof(PlcNumericInput), new PropertyMetadata(true));

        public bool ShowCaption
        {
            get => (bool)GetValue(ShowCaptionProperty);
            set => SetValue(ShowCaptionProperty, value);
        }

        public static readonly DependencyProperty IsReadOnlyProperty =
            DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(PlcNumericInput),
                new PropertyMetadata(false, (d, e) => ((PlcNumericInput)d).OnReadOnlyChanged()));

        /// <summary>为 true 时仅显示 PLC 值，不弹出键盘（等同于只读显示）。</summary>
        public bool IsReadOnly
        {
            get => (bool)GetValue(IsReadOnlyProperty);
            set => SetValue(IsReadOnlyProperty, value);
        }

        public static readonly DependencyProperty MinProperty =
            DependencyProperty.Register(nameof(Min), typeof(double), typeof(PlcNumericInput), new PropertyMetadata(double.NegativeInfinity));

        public double Min
        {
            get => (double)GetValue(MinProperty);
            set => SetValue(MinProperty, value);
        }

        public static readonly DependencyProperty MaxProperty =
            DependencyProperty.Register(nameof(Max), typeof(double), typeof(PlcNumericInput), new PropertyMetadata(double.PositiveInfinity));

        public double Max
        {
            get => (double)GetValue(MaxProperty);
            set => SetValue(MaxProperty, value);
        }

        public static readonly DependencyProperty ConfirmWriteProperty =
            DependencyProperty.Register(nameof(ConfirmWrite), typeof(bool), typeof(PlcNumericInput), new PropertyMetadata(false));

        public bool ConfirmWrite
        {
            get => (bool)GetValue(ConfirmWriteProperty);
            set => SetValue(ConfirmWriteProperty, value);
        }

        public static readonly DependencyProperty ConfirmTextProperty =
            DependencyProperty.Register(nameof(ConfirmText), typeof(string), typeof(PlcNumericInput), new PropertyMetadata("确认写入该数值？"));

        public string ConfirmText
        {
            get => (string)GetValue(ConfirmTextProperty);
            set => SetValue(ConfirmTextProperty, value);
        }

        public static readonly DependencyProperty DisconnectedTextProperty =
            DependencyProperty.Register(nameof(DisconnectedText), typeof(string), typeof(PlcNumericInput), new PropertyMetadata("—"));

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
            if (!_isConnected) PART_Display.Text = DisconnectedText;
            else RefreshDisplayFromTag();
        }

        protected override void OnConnectionStateChanged(bool isConnected)
        {
            _isConnected = isConnected;
            if (!isConnected)
            {
                PART_Display.Text = DisconnectedText;
            }
            else
            {
                // 重连后主动拉一次当前标签值刷新显示，
                // 避免监控线程首次回读失败时控件永远卡在"—"。
                RefreshDisplayFromTag();
            }
        }

        /// <summary>从当前标签值刷新显示（连接恢复或标签变化时调用）。</summary>
        private void RefreshDisplayFromTag()
        {
            try
            {
                var conn = ConnectionManager;
                if (conn == null || !_isConnected) return;
                string? name = SubscriptionTagName;
                if (string.IsNullOrWhiteSpace(name)) return;
                var tag = conn.GetTag(name);
                if (tag?.Value != null)
                {
                    _rawValue = tag.Value;
                    PART_Display.Text = FormatValue(tag.Value);
                }
            }
            catch { /* 静默失败，监控线程下次会再触发 OnPlcValueReceived */ }
        }

        /// <summary>只读模式时退化为中性显示外观（隐藏编辑图标、灰色边框），避免被误认为可输入。</summary>
        private void OnReadOnlyChanged()
        {
            if (PART_EditHint != null)
                PART_EditHint.Visibility = IsReadOnly ? Visibility.Collapsed : Visibility.Visible;
            if (PART_Host != null)
                PART_Host.Style = (Style)(IsReadOnly
                    ? FindResource("PlcCardBorder")
                    : FindResource("PlcInputBorder"));
        }

        protected override void OnPlcValueReceived(object? value)
        {
            if (!_isConnected) return;
            if (value == null)
            {
                // 监控线程读失败时不要用 null 覆盖显示（保留旧值或"写入失败"短提示），
                // 等下次回读成功再刷新。
                return;
            }
            _rawValue = value;
            PART_Display.Text = FormatValue(value);
        }

        #endregion

        #region 软键盘

        private void BuildKeypad()
        {
            if (PART_Keys == null) return;
            int cols = 3;
            int row = 0, col = 0;
            foreach (var key in KeyLayout)
            {
                var btn = new Button
                {
                    Content = key.Label,
                    Margin = new Thickness(3),
                    FontSize = 16,
                    // 深色软键盘：普通键深灰底浅字，OK 绿底白字，CANCEL 稍亮一档以示区分
                    Background = key.Cmd == "OK" ? PlcPalette.SuccessBrush
                               : key.Cmd == "CANCEL" ? PlcPalette.SurfaceAltBrush
                               : PlcPalette.BackgroundBrush,
                    Foreground = key.Cmd == "OK" ? Brushes.White : PlcPalette.TextBrush,
                    BorderThickness = new Thickness(1),
                    BorderBrush = PlcPalette.BorderStrongBrush,
                    Tag = key.Cmd,
                };
                btn.Click += OnKeyClick;
                Grid.SetRow(btn, row);
                Grid.SetColumn(btn, col);
                PART_Keys.Children.Add(btn);
                col++;
                if (col >= cols) { col = 0; row++; }
            }
        }

        private void OpenKeypad()
        {
            if (!_isConnected || string.IsNullOrWhiteSpace(TagName)) return;
            _pending = _rawValue != null ? Convert.ToString(_rawValue, CultureInfo.CurrentCulture) ?? string.Empty : string.Empty;
            PART_Pending.Text = _pending;
            PART_Keypad.IsOpen = true;
        }

        private void OnKeyClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string cmd = btn.Tag as string ?? string.Empty;
            switch (cmd)
            {
                case "BS":
                    if (_pending.Length > 0) _pending = _pending.Substring(0, _pending.Length - 1);
                    break;
                case "CLR":
                    _pending = string.Empty;
                    break;
                case "SIGN":
                    if (_pending.StartsWith("-")) _pending = _pending.Substring(1);
                    else if (_pending.Length > 0) _pending = "-" + _pending;
                    break;
                case "CANCEL":
                    PART_Keypad.IsOpen = false;
                    return;
                case "OK":
                    _ = CommitWrite();
                    return;
                default: // 数字或小数点
                    if (cmd == "." && _pending.Contains(".")) break;
                    if (cmd == "." && _pending.Length == 0) _pending = "0";
                    _pending += cmd;
                    break;
            }
            PART_Pending.Text = _pending;
        }

        private async Task CommitWrite()
        {
            if (string.IsNullOrWhiteSpace(_pending))
            {
                PART_Keypad.IsOpen = false;
                return;
            }
            if (!ParseValue(_pending, out object? value) || value == null)
            {
                PART_Pending.Text = "格式错误";
                return;
            }
            double d = Convert.ToDouble(value, CultureInfo.CurrentCulture);
            if (d < Min || d > Max)
            {
                PART_Pending.Text = "超出范围";
                return;
            }

            if (ConfirmWrite)
            {
                bool ok = await PlcUi.ConfirmAsync("操作确认", ConfirmText);
                if (!ok) { PART_Keypad.IsOpen = false; return; }
            }

            bool okWrite = await WriteTagAsync(TagName, value);
            PART_Keypad.IsOpen = false;
            if (okWrite)
            {
                // 写入成功：立刻把 PART_Display 同步成用户输入的值，
                // 避免在监控线程回读前显示旧值或"—"。
                _rawValue = value;
                PART_Display.Text = FormatValue(value);
            }
            else
            {
                // 写入失败：显示明确的"写入失败"提示（1.5 秒后回退到当前 PLC 值），
                // 而不是用断线占位符"—"覆盖，避免与真正断线状态混淆。
                PART_Display.Text = "写入失败";
                _ = RestoreDisplayAfterDelay(1500);
            }
        }

        private async Task RestoreDisplayAfterDelay(int ms)
        {
            try
            {
                // 不带 ConfigureAwait(false)，回到原 SynchronizationContext(UI 线程)继续，
                // 避免 Dispatcher.BeginInvoke 触发 CS4014 警告，也减少一次跨线程封送。
                await Task.Delay(ms);
                if (PART_Display.Text == "写入失败")
                    RefreshDisplayFromTag();
            }
            catch { }
        }

        #endregion

        #region 显示

        private string FormatValue(object? val)
        {
            if (val == null) return DisconnectedText;
            try
            {
                if (!string.IsNullOrWhiteSpace(FormatString) && val is IFormattable f)
                    return f.ToString(FormatString, CultureInfo.CurrentCulture);
                return Convert.ToString(val, CultureInfo.CurrentCulture) ?? DisconnectedText;
            }
            catch { return Convert.ToString(val, CultureInfo.CurrentCulture) ?? DisconnectedText; }
        }

        #endregion

        #region 文本解析

        private bool ParseValue(string text, out object? value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            try
            {
                switch (DataType)
                {
                    case RangeDataType.Int8:    if (sbyte.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out sbyte sb)) { value = sb; return true; } break;
                    case RangeDataType.UInt8:   if (byte.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out byte b)) { value = b; return true; } break;
                    case RangeDataType.Int16:   if (short.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out short s)) { value = s; return true; } break;
                    case RangeDataType.UInt16:  if (ushort.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out ushort us)) { value = us; return true; } break;
                    case RangeDataType.Int32:   if (int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int i)) { value = i; return true; } break;
                    case RangeDataType.UInt32:  if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out uint ui)) { value = ui; return true; } break;
                    case RangeDataType.Int64:   if (long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out long l)) { value = l; return true; } break;
                    case RangeDataType.UInt64:  if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out ulong ul)) { value = ul; return true; } break;
                    case RangeDataType.Float:   if (float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out float f)) { value = f; return true; } break;
                    case RangeDataType.Double:  if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double d)) { value = d; return true; } break;
                }
                return false;
            }
            catch { return false; }
        }

        #endregion
    }
}
