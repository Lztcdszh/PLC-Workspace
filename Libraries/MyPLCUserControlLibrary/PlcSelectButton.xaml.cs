using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MyPLCUserControlLibrary
{
    /// <summary>
    /// PLC 选定按钮：点击时把预设的 <see cref="WriteValue"/> 写入 <see cref="WriteTagName"/>/<see cref="WriteTagAddress"/>
    /// 指向的 PLC 标签（功能等价于 PlcTextBox 的写入，但以按钮形式触发）。
    /// 同时订阅该目标标签，当其当前值等于 <see cref="WriteValue"/> 时高亮（<see cref="IsSelected"/>），
    /// 直观表达「该号位即当前选定目标」。
    /// 纯前端控件，连接/订阅/自动注册/写入均复用 PlcControlBase，不依赖任何后台代码。
    /// </summary>
    public partial class PlcSelectButton : PlcControlBase
    {
        private bool _isPressed;

        public enum PlcSelectValueType
        {
            Int16,
            UInt16,
            Int32,
            UInt32,
            Float,
        }

        public PlcSelectButton()
        {
            InitializeComponent();

            if (DesignerProperties.GetIsInDesignMode(this))
            {
                Width = 56;
                Height = 20;
                ButtonText = "选定";
                return;
            }

            if (double.IsNaN(Width)) Width = 56;
            if (double.IsNaN(Height)) Height = 20;
            FontSize = 10;

            MouseLeftButtonDown += OnMouseLeftButtonDown;
            MouseLeftButtonUp += OnMouseLeftButtonUp;
            MouseLeave += OnMouseLeave;
        }

        #region 依赖属性

        // 写入目标标签地址（底层地址）
        public static readonly DependencyProperty WriteTagAddressProperty =
            DependencyProperty.Register(nameof(WriteTagAddress), typeof(string), typeof(PlcSelectButton),
                new PropertyMetadata(string.Empty, OnWriteTagChanged));

        public string WriteTagAddress
        {
            get => (string)GetValue(WriteTagAddressProperty);
            set => SetValue(WriteTagAddressProperty, value);
        }

        // 写入目标标签名（用于订阅与自动注册，优先于地址）
        public static readonly DependencyProperty WriteTagNameProperty =
            DependencyProperty.Register(nameof(WriteTagName), typeof(string), typeof(PlcSelectButton),
                new PropertyMetadata(string.Empty, OnWriteTagChanged));

        public string WriteTagName
        {
            get => (string)GetValue(WriteTagNameProperty);
            set => SetValue(WriteTagNameProperty, value);
        }

        // 点击时写入的预设值（如号位位置值）
        public static readonly DependencyProperty WriteValueProperty =
            DependencyProperty.Register(nameof(WriteValue), typeof(object), typeof(PlcSelectButton),
                new PropertyMetadata(null));

        public object? WriteValue
        {
            get => GetValue(WriteValueProperty);
            set => SetValue(WriteValueProperty, value);
        }

        // 目标标签数据类型（影响自动注册标签类型与比较精度）
        public static readonly DependencyProperty ValueTypeProperty =
            DependencyProperty.Register(nameof(ValueType), typeof(PlcSelectValueType), typeof(PlcSelectButton),
                new PropertyMetadata(PlcSelectValueType.Int16, OnWriteTagChanged));

        public PlcSelectValueType ValueType
        {
            get => (PlcSelectValueType)GetValue(ValueTypeProperty);
            set => SetValue(ValueTypeProperty, value);
        }

        // 按钮文字
        public static readonly DependencyProperty ButtonTextProperty =
            DependencyProperty.Register(nameof(ButtonText), typeof(string), typeof(PlcSelectButton),
                new PropertyMetadata("选定"));

        public string ButtonText
        {
            get => (string)GetValue(ButtonTextProperty);
            set => SetValue(ButtonTextProperty, value);
        }

        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(PlcSelectButton),
                new PropertyMetadata(new CornerRadius(3)));

        public CornerRadius CornerRadius
        {
            get => (CornerRadius)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        // 当前是否为「已选定」状态（目标 PLC 值 == 写入值）
        private static readonly DependencyPropertyKey IsSelectedPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(IsSelected), typeof(bool), typeof(PlcSelectButton),
                new PropertyMetadata(false));

        public static readonly DependencyProperty IsSelectedProperty = IsSelectedPropertyKey.DependencyProperty;

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            private set => SetValue(IsSelectedPropertyKey, value);
        }

        #endregion

        #region 标签变更处理（基于 PlcControlBase）

        private static void OnWriteTagChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (PlcSelectButton)d;
            control.OnTagChanged();
        }

        /// <summary>有效写入标签名：优先 WriteTagName，其次 WriteTagAddress。</summary>
        private string? EffectiveWriteTag =>
            !string.IsNullOrWhiteSpace(WriteTagName) ? WriteTagName.Trim() :
            !string.IsNullOrWhiteSpace(WriteTagAddress) ? WriteTagAddress.Trim() : null;

        protected override string? SubscriptionTagName => EffectiveWriteTag;

        protected override PLCTag? CreateDefaultTag()
        {
            string? addr = WriteTagAddress?.Trim();
            if (string.IsNullOrWhiteSpace(addr)) return null;
            string name = EffectiveWriteTag ?? addr;

            switch (ValueType)
            {
                case PlcSelectValueType.UInt16: return PLCTag.Create<ushort>(name, addr, (ushort)0, name);
                case PlcSelectValueType.Int32:  return PLCTag.Create<int>(name, addr, 0, name);
                case PlcSelectValueType.UInt32: return PLCTag.Create<uint>(name, addr, 0u, name);
                case PlcSelectValueType.Float:  return PLCTag.Create<float>(name, addr, 0f, name);
                default:                        return PLCTag.Create<short>(name, addr, (short)0, name);
            }
        }

        protected override void OnTagChanged()
        {
            if (DesignerProperties.GetIsInDesignMode(this)) return;

            Unsubscribe();
            if (ConnectionManager != null && !string.IsNullOrWhiteSpace(EffectiveWriteTag))
            {
                AutoRegisterTags();
                Subscribe();
            }
        }

        protected override void OnPlcValueReceived(object? value)
        {
            if (WriteValue == null)
            {
                IsSelected = false;
                return;
            }
            try
            {
                long target = Convert.ToInt64(WriteValue);
                long current = Convert.ToInt64(value);
                IsSelected = target == current;
            }
            catch
            {
                IsSelected = false;
            }
        }

        #endregion

        #region 鼠标事件（点击写入）

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsEnabled || _isPressed) return;
            _isPressed = true;
            CaptureMouse();
            UpdateVisualFeedback(true);
        }

        private async void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isPressed) return;
            _isPressed = false;
            ReleaseMouseCapture();
            UpdateVisualFeedback(false);

            await WriteAsync();
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            if (_isPressed)
            {
                _isPressed = false;
                ReleaseMouseCapture();
                UpdateVisualFeedback(false);
            }
        }

        private async Task WriteAsync()
        {
            string? tag = EffectiveWriteTag;
            if (string.IsNullOrWhiteSpace(tag) || WriteValue == null)
            {
                LogDebug(nameof(WriteAsync), "写入目标或值为空，未执行");
                return;
            }

            // XAML 直接写数字字面量时 WriteValue 为 string；绑定传入时可能为 int/short 等。
            // 统一转换为目标标签底层类型，避免 TagService 对 short 标签执行 (short)value 强制拆箱失败。
            object? valueToWrite = WriteValue;
            try
            {
                PLCTag? existing = ConnectionManager?.GetTag(tag);
                Type targetType = existing?.DataType ?? MapValueType(ValueType);
                if (WriteValue is string s)
                {
                    valueToWrite = string.IsNullOrWhiteSpace(s) ? null : Convert.ChangeType(s, targetType);
                }
                else if (WriteValue.GetType() != targetType)
                {
                    valueToWrite = Convert.ChangeType(WriteValue, targetType);
                }
            }
            catch (Exception ex)
            {
                LogDebug(nameof(WriteAsync), $"写入值类型转换失败，退回原值: {ex.Message}");
                valueToWrite = WriteValue;
            }

            if (valueToWrite == null)
            {
                LogDebug(nameof(WriteAsync), "转换后写入值为空，未执行");
                return;
            }

            bool ok = await WriteTagAsync(tag, valueToWrite);
            LogDebug(nameof(WriteAsync), ok ? "写入成功" : "写入失败/未连接");
        }

        private static Type MapValueType(PlcSelectValueType t) => t switch
        {
            PlcSelectValueType.UInt16 => typeof(ushort),
            PlcSelectValueType.Int32  => typeof(int),
            PlcSelectValueType.UInt32 => typeof(uint),
            PlcSelectValueType.Float  => typeof(float),
            _ => typeof(short),
        };

        private void UpdateVisualFeedback(bool isPressed)
        {
            if (Template.FindName("Overlay", this) is Border overlay)
            {
                overlay.BeginAnimation(Border.OpacityProperty,
                    new DoubleAnimation(isPressed ? 0.35 : 0, TimeSpan.FromMilliseconds(120)));
            }
        }

        #endregion
    }
}
