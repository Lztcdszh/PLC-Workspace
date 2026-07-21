using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MyPLCUserControlLibrary
{
    /// <summary>
    /// PLC 按钮控件，支持分离的控制地址和状态地址，且可为 ON/OFF 状态指定不同控制地址。
    /// 每个地址可设置独立的 TagName，便于导出识别。
    /// </summary>
    public partial class PlcButton : PlcControlBase
    {
        private bool _isPressed;
        private string? _effectiveStatusTag;      // 实际使用的状态标签（标签名）

        // 用于 Momentary 模式，记录按下时选择的标签名（释放时使用同一标签名）
        private string? _momentaryTagName;

        public enum PlcButtonMode
        {
            Momentary,   // 点动：按下写入 True，释放写入 False（标签名根据当前状态选择）
            Toggle,      // 切换：每次点击翻转当前值
            SetOn,       // 置位：点击写入 True
            SetOff       // 复位：点击写入 False
        }

        public PlcButton()
        {
            InitializeComponent();

            if (DesignerProperties.GetIsInDesignMode(this))
            {
                // 设计时简单预览
                this.Width = 80;
                this.Height = 30;
                this.Background = Brushes.LightGray;
                this.Content = "Button";
                this.BorderBrush = Brushes.DarkGray;
                this.BorderThickness = new Thickness(1);
                this.CornerRadius = new CornerRadius(3);
                return;
            }

            if (double.IsNaN(this.Width)) this.Width = 80;
            if (double.IsNaN(this.Height)) this.Height = 30;

            this.MouseLeftButtonDown += OnMouseLeftButtonDown;
            this.MouseLeftButtonUp += OnMouseLeftButtonUp;
            this.MouseLeave += OnMouseLeave;
            this.IsEnabledChanged += OnIsEnabledChanged;

            UpdateEffectiveStatusTag();
            UpdateVisual();
        }

        #region 依赖属性

        // 注意：Connection / TagName / AutoRegister 由基类 PlcControlBase 提供。

        // 统一控制地址（如果未分别指定 ON/OFF 地址，则使用此地址）
        public static readonly DependencyProperty ControlTagAddressProperty =
            DependencyProperty.Register(nameof(ControlTagAddress), typeof(string), typeof(PlcButton),
                new PropertyMetadata(string.Empty, OnTagPropertyChanged));

        public string ControlTagAddress
        {
            get => (string)GetValue(ControlTagAddressProperty);
            set => SetValue(ControlTagAddressProperty, value);
        }

        // ControlTagAddress 对应的显示名称（也是标签名）
        public static readonly DependencyProperty ControlTagNameProperty =
            DependencyProperty.Register(nameof(ControlTagName), typeof(string), typeof(PlcButton),
                new PropertyMetadata(string.Empty, OnTagPropertyChanged));

        public string ControlTagName
        {
            get => (string)GetValue(ControlTagNameProperty);
            set => SetValue(ControlTagNameProperty, value);
        }

        // ON 状态控制地址（写入 True 时使用，或在 Momentary 模式下当前状态为 false 时使用）
        public static readonly DependencyProperty OnControlTagAddressProperty =
            DependencyProperty.Register(nameof(OnControlTagAddress), typeof(string), typeof(PlcButton),
                new PropertyMetadata(string.Empty, OnTagPropertyChanged));

        public string OnControlTagAddress
        {
            get => (string)GetValue(OnControlTagAddressProperty);
            set => SetValue(OnControlTagAddressProperty, value);
        }

        // OnControlTagAddress 对应的显示名称（标签名）
        public static readonly DependencyProperty OnControlTagNameProperty =
            DependencyProperty.Register(nameof(OnControlTagName), typeof(string), typeof(PlcButton),
                new PropertyMetadata(string.Empty, OnTagPropertyChanged));

        public string OnControlTagName
        {
            get => (string)GetValue(OnControlTagNameProperty);
            set => SetValue(OnControlTagNameProperty, value);
        }

        // OFF 状态控制地址（写入 False 时使用，或在 Momentary 模式下当前状态为 true 时使用）
        public static readonly DependencyProperty OffControlTagAddressProperty =
            DependencyProperty.Register(nameof(OffControlTagAddress), typeof(string), typeof(PlcButton),
                new PropertyMetadata(string.Empty, OnTagPropertyChanged));

        public string OffControlTagAddress
        {
            get => (string)GetValue(OffControlTagAddressProperty);
            set => SetValue(OffControlTagAddressProperty, value);
        }

        // OffControlTagAddress 对应的显示名称（标签名）
        public static readonly DependencyProperty OffControlTagNameProperty =
            DependencyProperty.Register(nameof(OffControlTagName), typeof(string), typeof(PlcButton),
                new PropertyMetadata(string.Empty, OnTagPropertyChanged));

        public string OffControlTagName
        {
            get => (string)GetValue(OffControlTagNameProperty);
            set => SetValue(OffControlTagNameProperty, value);
        }

        // 状态地址（读取源）
        public static readonly DependencyProperty StatusTagAddressProperty =
            DependencyProperty.Register(nameof(StatusTagAddress), typeof(string), typeof(PlcButton),
                new PropertyMetadata(string.Empty, OnTagPropertyChanged));

        public string StatusTagAddress
        {
            get => (string)GetValue(StatusTagAddressProperty);
            set => SetValue(StatusTagAddressProperty, value);
        }

        // StatusTagAddress 对应的显示名称（标签名）
        public static readonly DependencyProperty StatusTagNameProperty =
            DependencyProperty.Register(nameof(StatusTagName), typeof(string), typeof(PlcButton),
                new PropertyMetadata(string.Empty, OnTagPropertyChanged));

        public string StatusTagName
        {
            get => (string)GetValue(StatusTagNameProperty);
            set => SetValue(StatusTagNameProperty, value);
        }

        public static readonly DependencyProperty ModeProperty =
            DependencyProperty.Register(nameof(Mode), typeof(PlcButtonMode), typeof(PlcButton),
                new PropertyMetadata(PlcButtonMode.Momentary));

        public PlcButtonMode Mode
        {
            get => (PlcButtonMode)GetValue(ModeProperty);
            set => SetValue(ModeProperty, value);
        }

        public static readonly DependencyProperty CurrentValueProperty =
            DependencyProperty.Register(nameof(CurrentValue), typeof(bool), typeof(PlcButton),
                new PropertyMetadata(false, OnCurrentValueChanged));

        public bool CurrentValue
        {
            get => (bool)GetValue(CurrentValueProperty);
            set => SetValue(CurrentValueProperty, value);
        }

        public static readonly DependencyProperty OnBrushProperty =
            DependencyProperty.Register(nameof(OnBrush), typeof(Brush), typeof(PlcButton),
                new PropertyMetadata(new SolidColorBrush(Colors.LimeGreen), OnBrushChanged));

        public Brush OnBrush
        {
            get => (Brush)GetValue(OnBrushProperty);
            set => SetValue(OnBrushProperty, value);
        }

        public static readonly DependencyProperty OffBrushProperty =
            DependencyProperty.Register(nameof(OffBrush), typeof(Brush), typeof(PlcButton),
                new PropertyMetadata(new SolidColorBrush(Colors.DarkGray), OnBrushChanged));

        public Brush OffBrush
        {
            get => (Brush)GetValue(OffBrushProperty);
            set => SetValue(OffBrushProperty, value);
        }

        public static readonly DependencyProperty OnContentProperty =
            DependencyProperty.Register(nameof(OnContent), typeof(object), typeof(PlcButton),
                new PropertyMetadata("ON", OnContentChanged));

        public object OnContent
        {
            get => GetValue(OnContentProperty);
            set => SetValue(OnContentProperty, value);
        }

        public static readonly DependencyProperty OffContentProperty =
            DependencyProperty.Register(nameof(OffContent), typeof(object), typeof(PlcButton),
                new PropertyMetadata("OFF", OnContentChanged));

        public object OffContent
        {
            get => GetValue(OffContentProperty);
            set => SetValue(OffContentProperty, value);
        }

        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(PlcButton),
                new PropertyMetadata(new CornerRadius(3)));

        public CornerRadius CornerRadius
        {
            get => (CornerRadius)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        private static readonly DependencyPropertyKey IsPressedPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(IsPressed), typeof(bool), typeof(PlcButton),
                new PropertyMetadata(false));

        public static readonly DependencyProperty IsPressedProperty = IsPressedPropertyKey.DependencyProperty;

        public bool IsPressed
        {
            get => (bool)GetValue(IsPressedProperty);
            private set => SetValue(IsPressedPropertyKey, value);
        }

        #endregion

        #region 属性变更回调

        private static void OnTagPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (PlcButton)d;
            control.OnTagChanged();
        }

        private static void OnCurrentValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (PlcButton)d;
            control.UpdateVisual();
        }

        private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (PlcButton)d;
            control.UpdateVisual();
        }

        private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (PlcButton)d;
            control.UpdateVisual();
        }

        #endregion

        #region 标签地址处理

        /// <summary>
        /// 更新实际使用的状态标签名：优先使用 StatusTagName，否则 StatusTagAddress。
        /// </summary>
        private void UpdateEffectiveStatusTag()
        {
            if (!string.IsNullOrWhiteSpace(StatusTagName))
                _effectiveStatusTag = StatusTagName.Trim();
            else
                _effectiveStatusTag = !string.IsNullOrWhiteSpace(StatusTagAddress) ? StatusTagAddress.Trim() : null;

            LogDebug(nameof(UpdateEffectiveStatusTag), $"状态标签名='{_effectiveStatusTag}'");
        }

        /// <summary>
        /// 根据要写入的值获取对应的控制标签名（用于 Toggle、SetOn、SetOff 模式）。
        /// 返回的字符串应作为标签名传递给 WriteTagAsync。
        /// </summary>
        private string? GetControlTagNameForValue(bool value)
        {
            if (value)
            {
                // 写入 True：优先使用 OnControlTagName，否则 OnControlTagAddress
                if (!string.IsNullOrWhiteSpace(OnControlTagName))
                    return OnControlTagName.Trim();
                if (!string.IsNullOrWhiteSpace(OnControlTagAddress))
                    return OnControlTagAddress.Trim();
            }
            else
            {
                // 写入 False：优先使用 OffControlTagName，否则 OffControlTagAddress
                if (!string.IsNullOrWhiteSpace(OffControlTagName))
                    return OffControlTagName.Trim();
                if (!string.IsNullOrWhiteSpace(OffControlTagAddress))
                    return OffControlTagAddress.Trim();
            }

            // 如果专用地址未设置，则回退到通用控制地址
            if (!string.IsNullOrWhiteSpace(ControlTagName))
                return ControlTagName.Trim();
            if (!string.IsNullOrWhiteSpace(ControlTagAddress))
                return ControlTagAddress.Trim();

            return null;
        }

        /// <summary>
        /// Momentary 模式：根据当前状态选择要写入的标签名（按下时用此标签名写入 True，释放时写入 False）。
        /// </summary>
        private string? GetMomentaryTagName()
        {
            // 根据当前状态选择专用 ON 或 OFF 地址
            if (!CurrentValue) // 当前停止，应启动
            {
                if (!string.IsNullOrWhiteSpace(OnControlTagName))
                    return OnControlTagName.Trim();
                if (!string.IsNullOrWhiteSpace(OnControlTagAddress))
                    return OnControlTagAddress.Trim();
            }
            else // 当前运行，应停止
            {
                if (!string.IsNullOrWhiteSpace(OffControlTagName))
                    return OffControlTagName.Trim();
                if (!string.IsNullOrWhiteSpace(OffControlTagAddress))
                    return OffControlTagAddress.Trim();
            }

            // 如果专用地址未设置，则回退到通用控制地址（单一地址点动）
            if (!string.IsNullOrWhiteSpace(ControlTagName))
                return ControlTagName.Trim();
            if (!string.IsNullOrWhiteSpace(ControlTagAddress))
                return ControlTagAddress.Trim();

            return null; // 无可用的地址
        }

        protected override void OnTagChanged()
        {
            if (DesignerProperties.GetIsInDesignMode(this)) return;

            LogDebug(nameof(OnTagChanged), $"OnAddr='{OnControlTagAddress}', OnName='{OnControlTagName}', OffAddr='{OffControlTagAddress}', OffName='{OffControlTagName}', CtrlAddr='{ControlTagAddress}', CtrlName='{ControlTagName}', StatusAddr='{StatusTagAddress}', StatusName='{StatusTagName}'");
            Unsubscribe(); // 取消原有订阅
            UpdateEffectiveStatusTag();
            UpdateVisual();

            if (ConnectionManager == null || string.IsNullOrWhiteSpace(_effectiveStatusTag))
                return;

            AutoRegisterTags();
            Subscribe(); // 重新订阅状态标签
        }

        #endregion

        #region 连接与订阅（基于 PlcControlBase）

        /// <summary>事件过滤用的状态标签名（覆盖基类，返回实际状态标签）。</summary>
        protected override string? SubscriptionTagName => _effectiveStatusTag;

        /// <summary>覆盖基类自动注册：注册 4 个地址（控制 / ON / OFF / 状态）。</summary>
        protected override void AutoRegisterTags()
        {
            if (ConnectionManager == null || !AutoRegister) return;
            TryAutoRegister();
        }

        /// <summary>基类回调：收到状态标签值后在 UI 线程处理。</summary>
        protected override void OnPlcValueReceived(object? value)
        {
            ProcessValue(value);
        }

        private void TryAutoRegister()
        {
            if (ConnectionManager == null || !AutoRegister) return;

            // 定义地址与对应 TagName 的映射关系，并标识是否为状态标签
            var registrations = new[]
            {
                new { TagName = ControlTagName?.Trim(), Address = ControlTagAddress?.Trim(), IsStatus = false },
                new { TagName = OnControlTagName?.Trim(), Address = OnControlTagAddress?.Trim(), IsStatus = false },
                new { TagName = OffControlTagName?.Trim(), Address = OffControlTagAddress?.Trim(), IsStatus = false },
                new { TagName = StatusTagName?.Trim(), Address = StatusTagAddress?.Trim(), IsStatus = true }
            };

            foreach (var reg in registrations)
            {
                if (string.IsNullOrWhiteSpace(reg.Address))
                    continue; // 没有地址，无法注册

                // 确定最终使用的标签名：优先专用 TagName，其次通用 TagName，最后地址
                string finalTagName;
                if (!string.IsNullOrWhiteSpace(reg.TagName))
                    finalTagName = reg.TagName;
                else if (!string.IsNullOrWhiteSpace(TagName))
                    finalTagName = TagName;
                else
                    finalTagName = reg.Address;

                string displayName = finalTagName;

                if (ContainsTagSafe(finalTagName))
                {
                    // 已存在，跳过
                }
                else
                {
                    try
                    {
                        var tag = PLCTag.CreateBool(finalTagName, reg.Address, displayName);
                        // 设置监控标志：只有状态标签参与自动轮询
                        tag.IsMonitored = reg.IsStatus;
                        ConnectionManager.AddTag(tag);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[PlcButton] Auto-register failed for {finalTagName}: {ex.Message}");
                    }
                }
            }
        }

        private void ProcessValue(object? val)
        {
            LogDebug(nameof(ProcessValue), $"值={val}, 类型={val?.GetType()}");
            try
            {
                bool newValue = val switch
                {
                    bool b => b,
                    int i => i != 0,
                    float f => Math.Abs(f) > 1e-6,
                    double d => Math.Abs(d) > 1e-6,
                    string s => bool.TryParse(s, out var bb) ? bb : (int.TryParse(s, out var ii) && ii != 0),
                    _ => false
                };
                CurrentValue = newValue;
            }
            catch (Exception ex)
            {
                LogDebug(nameof(ProcessValue), $"异常: {ex.Message}");
            }
        }

        #endregion

        #region 鼠标事件

        private async void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsEnabled || _isPressed) return;
            _isPressed = true;
            IsPressed = true;
            CaptureMouse();
            UpdateVisualFeedback(true);

            LogDebug(nameof(OnMouseLeftButtonDown), $"Mode={Mode}");

            if (Mode == PlcButtonMode.Momentary)
            {
                // 根据当前状态选择标签名并记录，然后写入 true
                _momentaryTagName = GetMomentaryTagName();
                if (string.IsNullOrWhiteSpace(_momentaryTagName))
                {
                    LogDebug(nameof(OnMouseLeftButtonDown), "无法确定 Momentary 标签名，不执行写入");
                }
                else
                {
                    await WriteToTagAsync(_momentaryTagName, true);
                }
            }
        }

        private async void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isPressed) return;
            _isPressed = false;
            IsPressed = false;
            ReleaseMouseCapture();
            UpdateVisualFeedback(false);

            LogDebug(nameof(OnMouseLeftButtonUp), $"Mode={Mode}");

            switch (Mode)
            {
                case PlcButtonMode.Momentary:
                    if (!string.IsNullOrWhiteSpace(_momentaryTagName))
                    {
                        // 使用按下时记录的标签名写入 false
                        await WriteToTagAsync(_momentaryTagName, false);
                        _momentaryTagName = null; // 清空记录
                    }
                    break;
                case PlcButtonMode.Toggle:
                    await WriteValueAsync(!CurrentValue);
                    break;
                case PlcButtonMode.SetOn:
                    await WriteValueAsync(true);
                    break;
                case PlcButtonMode.SetOff:
                    await WriteValueAsync(false);
                    break;
            }
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            if (_isPressed)
            {
                if (Mode == PlcButtonMode.Momentary)
                {
                    _isPressed = false;
                    IsPressed = false;
                    ReleaseMouseCapture();
                    if (!string.IsNullOrWhiteSpace(_momentaryTagName))
                    {
                        _ = WriteToTagAsync(_momentaryTagName, false);
                        _momentaryTagName = null;
                    }
                    LogDebug(nameof(OnMouseLeave), "鼠标离开，点动释放");
                }
                else
                {
                    _isPressed = false;
                    IsPressed = false;
                    ReleaseMouseCapture();
                }
                UpdateVisualFeedback(false);
            }
        }

        private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsEnabled && _isPressed)
            {
                _isPressed = false;
                IsPressed = false;
                ReleaseMouseCapture();
                UpdateVisualFeedback(false);
            }
        }

        private void UpdateVisualFeedback(bool isPressed)
        {
            if (Template.FindName("Overlay", this) is Border overlay)
            {
                double targetOpacity = isPressed ? 0.3 : 0;
                overlay.BeginAnimation(Border.OpacityProperty,
                    new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(150)));
            }
        }

        #endregion

        #region 写入操作

        /// <summary>
        /// 通用的写入方法，指定标签名和值（用于所有模式）。底层走基类 WriteTagAsync（含连接检查与信号量）。
        /// </summary>
        private async Task WriteToTagAsync(string tagName, bool value)
        {
            await WriteTagAsync(tagName, value);
        }

        /// <summary>
        /// 根据值选择标签名并写入（用于 Toggle/SetOn/SetOff 模式）
        /// </summary>
        private async Task WriteValueAsync(bool value)
        {
            string? tagName = GetControlTagNameForValue(value);
            if (string.IsNullOrWhiteSpace(tagName))
            {
                LogDebug(nameof(WriteValueAsync), "无法确定控制标签名，无法写入");
                return;
            }

            await WriteTagAsync(tagName, value);
        }

        #endregion

        #region 视觉更新

        private void UpdateVisual()
        {
            Background = CurrentValue ? OnBrush : OffBrush;
            Content = CurrentValue ? OnContent : OffContent;
        }

        #endregion

        #region 资源清理

        // 卸载资源（退订 / 取消连接状态订阅）已由基类 PlcControlBase.OnUnloaded 统一处理，
        // PlcButton 无额外定时器或句柄需清理。

        #endregion
    }
}