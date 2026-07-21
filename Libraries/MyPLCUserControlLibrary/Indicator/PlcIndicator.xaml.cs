using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;

namespace MyPLCUserControlLibrary
{
    public partial class PlcIndicator : PlcControlBase
    {
        private DispatcherTimer? _blinkTimer;
        private bool _blinkState;

        public PlcIndicator()
        {
            LogDebug("构造函数", "开始");
            InitializeComponent();

            // 强制设置默认尺寸（外部可覆盖）
            if (double.IsNaN(this.Width)) this.Width = 16;
            if (double.IsNaN(this.Height)) this.Height = 16;

            UpdateVisual();
            LogDebug("构造函数", "完成");
        }

        #region 依赖属性（仅保留指示器特有属性，连接/标签/自动注册由基类提供）

        public static readonly DependencyProperty IsOnProperty =
            DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(PlcIndicator),
                new PropertyMetadata(false, (d, e) => ((PlcIndicator)d).UpdateVisual()));

        public bool IsOn
        {
            get => (bool)GetValue(IsOnProperty);
            set => SetValue(IsOnProperty, value);
        }

        public static readonly DependencyProperty IsBlinkingProperty =
            DependencyProperty.Register(nameof(IsBlinking), typeof(bool), typeof(PlcIndicator),
                new PropertyMetadata(false, (d, e) => ((PlcIndicator)d).UpdateBlinking()));

        public bool IsBlinking
        {
            get => (bool)GetValue(IsBlinkingProperty);
            set => SetValue(IsBlinkingProperty, value);
        }

        public static readonly DependencyProperty OnBrushProperty =
            DependencyProperty.Register(nameof(OnBrush), typeof(Brush), typeof(PlcIndicator),
                new PropertyMetadata(new SolidColorBrush(Colors.LimeGreen), (d, e) => ((PlcIndicator)d).UpdateVisual()));

        public Brush OnBrush
        {
            get => (Brush)GetValue(OnBrushProperty);
            set => SetValue(OnBrushProperty, value);
        }

        public static readonly DependencyProperty OffBrushProperty =
            DependencyProperty.Register(nameof(OffBrush), typeof(Brush), typeof(PlcIndicator),
                new PropertyMetadata(new SolidColorBrush(Colors.DarkGray), (d, e) => ((PlcIndicator)d).UpdateVisual()));

        public Brush OffBrush
        {
            get => (Brush)GetValue(OffBrushProperty);
            set => SetValue(OffBrushProperty, value);
        }

        // 保留 Diameter 属性以兼容旧代码，但不再用于尺寸控制（建议未来删除）
        public static readonly DependencyProperty DiameterProperty =
            DependencyProperty.Register(nameof(Diameter), typeof(double), typeof(PlcIndicator),
                new PropertyMetadata(16.0));

        public double Diameter
        {
            get => (double)GetValue(DiameterProperty);
            set => SetValue(DiameterProperty, value);
        }

        public static readonly DependencyProperty ToolTipTextProperty =
            DependencyProperty.Register(nameof(ToolTipText), typeof(string), typeof(PlcIndicator),
                new PropertyMetadata(string.Empty));

        public string ToolTipText
        {
            get => (string)GetValue(ToolTipTextProperty);
            set => SetValue(ToolTipTextProperty, value);
        }

        public static readonly DependencyProperty AutoToolTipProperty =
            DependencyProperty.Register(nameof(AutoToolTip), typeof(bool), typeof(PlcIndicator),
                new PropertyMetadata(true));

        public bool AutoToolTip
        {
            get => (bool)GetValue(AutoToolTipProperty);
            set => SetValue(AutoToolTipProperty, value);
        }

        // IsValid 属性已不再用于视觉显示，仅保留避免破坏现有绑定。
        public static readonly DependencyProperty IsValidProperty =
            DependencyProperty.Register(nameof(IsValid), typeof(bool), typeof(PlcIndicator),
                new PropertyMetadata(true, (d, e) => { /* 不再使用，保留空回调 */ }));

        public bool IsValid
        {
            get => (bool)GetValue(IsValidProperty);
            set => SetValue(IsValidProperty, value);
        }

        #endregion

        #region PLC 值处理（基类在 UI 线程回调）

        protected override void OnPlcValueReceived(object? val)
        {
            LogDebug(nameof(OnPlcValueReceived), $"进入, 值={val}, 类型={val?.GetType()}");
            try
            {
                bool newIsOn = false;

                if (val is bool b)
                    newIsOn = b;
                else if (val is int i)
                    newIsOn = i != 0;
                else if (val is float f)
                    newIsOn = Math.Abs(f) > 1e-6;
                else if (val is double d)
                    newIsOn = Math.Abs(d) > 1e-6;
                else if (val is string s)
                {
                    if (bool.TryParse(s, out var bb))
                        newIsOn = bb;
                    else if (int.TryParse(s, out var ii))
                        newIsOn = ii != 0;
                }

                IsOn = newIsOn;
            }
            catch (Exception ex)
            {
                LogDebug(nameof(OnPlcValueReceived), $"异常: {ex.Message}");
                // 绝不修改 IsOn，避免意外回退
            }
            LogDebug(nameof(OnPlcValueReceived), "退出");
        }

        #endregion

        #region 视觉更新

        private void UpdateVisual()
        {
            PART_Ellipse.Fill = IsOn ? OnBrush : OffBrush;
            PART_Ellipse.Opacity = IsOn ? 1.0 : 0.6;

            UpdateBlinking();
            UpdateToolTip();
        }

        private void UpdateToolTip()
        {
            if (AutoToolTip)
            {
                ToolTipText = string.IsNullOrWhiteSpace(TagName)
                    ? string.Empty
                    : $"{TagName} ({TagAddress})";
            }
        }

        private void UpdateBlinking()
        {
            if (IsBlinking && IsOn)
            {
                if (_blinkTimer == null)
                {
                    _blinkTimer = new DispatcherTimer();
                    _blinkTimer.Interval = TimeSpan.FromMilliseconds(500);
                    _blinkTimer.Tick += (s, e) =>
                    {
                        _blinkState = !_blinkState;
                        PART_Ellipse.Opacity = _blinkState ? 0.2 : 1.0;
                    };
                    _blinkTimer.Start();
                    _blinkState = false;
                    PART_Ellipse.Opacity = 1.0;
                }
            }
            else
            {
                if (_blinkTimer != null)
                {
                    _blinkTimer.Stop();
                    _blinkTimer = null;
                }
                // 恢复由 UpdateVisual 设定的透明度
                PART_Ellipse.Opacity = IsOn ? 1.0 : 0.6;
            }
        }

        #endregion

        #region 资源清理（闪烁定时器）

        protected override void OnUnloadedInternal()
        {
            LogDebug(nameof(OnUnloadedInternal), "停止闪烁定时器");
            _blinkTimer?.Stop();
            _blinkTimer = null;
        }

        #endregion
    }
}
