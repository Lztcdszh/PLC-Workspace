using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Model;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyPLCUserControlLibrary
{
    /// <summary>状态条方向：横向（默认）或竖向。</summary>
    public enum StatusBarOrientation
    {
        /// <summary>横向条（默认）。</summary>
        Horizontal,
        /// <summary>竖向条（用于上下走向的轨道/转弯连接）。</summary>
        Vertical
    }

    /// <summary>激活判定模式。</summary>
    public enum StatusBarActiveMode
    {
        /// <summary>等于模式（默认）：PLC 值 == ActiveValue 即激活（表示某站到位）。</summary>
        Equals,
        /// <summary>区间模式：Low &lt; PLC 值 &lt; High 即激活（如“7↔8 之间在途”）。</summary>
        Between
    }

    /// <summary>
    /// 条状位置状态显示控件：绑定一个 PLC 标签，以横向/竖向条（Bar）形式高亮显示位置状态。
    /// 激活判定由 <see cref="ActiveMode"/> 决定：
    ///  - <see cref="StatusBarActiveMode.Equals"/>（默认）：值 == <see cref="ActiveValue"/>，表示“某站到位”；
    ///  - <see cref="StatusBarActiveMode.Between"/>：Low &lt; 值 &lt; High，表示“两站之间在途”。
    /// 可按 <see cref="Blink"/> 设定闪烁。支持 <see cref="Orientation"/>=Vertical 的竖向布局。
    /// 纯前端控件，连接/订阅/自动注册/写入均复用 <see cref="PlcControlBase"/>，不依赖后台代码。
    /// </summary>
    public partial class PlcStatusBar : PlcControlBase
    {
        #region 依赖属性

        /// <summary>激活状态（PLC 值 == ActiveValue）时条的颜色。</summary>
        public static readonly DependencyProperty ActiveBrushProperty =
            DependencyProperty.Register(nameof(ActiveBrush), typeof(Brush), typeof(PlcStatusBar),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00))));
        public Brush ActiveBrush
        {
            get => (Brush)GetValue(ActiveBrushProperty);
            set => SetValue(ActiveBrushProperty, value);
        }

        /// <summary>未激活时条的颜色（底色）。</summary>
        public static readonly DependencyProperty InactiveBrushProperty =
            DependencyProperty.Register(nameof(InactiveBrush), typeof(Brush), typeof(PlcStatusBar),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0xDC, 0xDF, 0xE6))));
        public Brush InactiveBrush
        {
            get => (Brush)GetValue(InactiveBrushProperty);
            set => SetValue(InactiveBrushProperty, value);
        }

        /// <summary>条的高度（像素）。</summary>
        public static readonly DependencyProperty BarHeightProperty =
            DependencyProperty.Register(nameof(BarHeight), typeof(double), typeof(PlcStatusBar), new PropertyMetadata(6.0));
        public double BarHeight
        {
            get => (double)GetValue(BarHeightProperty);
            set => SetValue(BarHeightProperty, value);
        }

        /// <summary>条的圆角。</summary>
        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(PlcStatusBar),
                new PropertyMetadata(new CornerRadius(3)));
        public CornerRadius CornerRadius
        {
            get => (CornerRadius)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        /// <summary>是否在激活时闪烁（默认 true）。</summary>
        public static readonly DependencyProperty BlinkProperty =
            DependencyProperty.Register(nameof(Blink), typeof(bool), typeof(PlcStatusBar), new PropertyMetadata(true));
        public bool Blink
        {
            get => (bool)GetValue(BlinkProperty);
            set => SetValue(BlinkProperty, value);
        }

        /// <summary>判定为“激活”的 PLC 值（默认 1，即非零/到位）。用于 <see cref="ActiveMode"/>=Equals。</summary>
        public static readonly DependencyProperty ActiveValueProperty =
            DependencyProperty.Register(nameof(ActiveValue), typeof(object), typeof(PlcStatusBar), new PropertyMetadata((object)1));
        public object ActiveValue
        {
            get => GetValue(ActiveValueProperty);
            set => SetValue(ActiveValueProperty, value);
        }

        /// <summary>激活判定模式：Equals（默认，到位）或 Between（两站之间在途）。</summary>
        public static readonly DependencyProperty ActiveModeProperty =
            DependencyProperty.Register(nameof(ActiveMode), typeof(StatusBarActiveMode), typeof(PlcStatusBar),
                new PropertyMetadata(StatusBarActiveMode.Equals));
        public StatusBarActiveMode ActiveMode
        {
            get => (StatusBarActiveMode)GetValue(ActiveModeProperty);
            set => SetValue(ActiveModeProperty, value);
        }

        /// <summary>区间模式下限（不包含）。用于 <see cref="ActiveMode"/>=Between。</summary>
        public static readonly DependencyProperty ActiveLowProperty =
            DependencyProperty.Register(nameof(ActiveLow), typeof(object), typeof(PlcStatusBar), new PropertyMetadata((object)0));
        public object ActiveLow
        {
            get => GetValue(ActiveLowProperty);
            set => SetValue(ActiveLowProperty, value);
        }

        /// <summary>区间模式上限（不包含）。用于 <see cref="ActiveMode"/>=Between。</summary>
        public static readonly DependencyProperty ActiveHighProperty =
            DependencyProperty.Register(nameof(ActiveHigh), typeof(object), typeof(PlcStatusBar), new PropertyMetadata((object)0));
        public object ActiveHigh
        {
            get => GetValue(ActiveHighProperty);
            set => SetValue(ActiveHighProperty, value);
        }

        /// <summary>状态条方向：横向（默认）或竖向。</summary>
        public static readonly DependencyProperty OrientationProperty =
            DependencyProperty.Register(nameof(Orientation), typeof(StatusBarOrientation), typeof(PlcStatusBar),
                new PropertyMetadata(StatusBarOrientation.Horizontal));
        public StatusBarOrientation Orientation
        {
            get => (StatusBarOrientation)GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        private static readonly DependencyPropertyKey IsActivePropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(IsActive), typeof(bool), typeof(PlcStatusBar), new PropertyMetadata(false));
        public static readonly DependencyProperty IsActiveProperty = IsActivePropertyKey.DependencyProperty;
        /// <summary>只读：当前是否处于激活（到位）状态。</summary>
        public bool IsActive => (bool)GetValue(IsActiveProperty);
        private void SetIsActive(bool v) => SetValue(IsActivePropertyKey, v);

        #endregion

        public PlcStatusBar()
        {
            InitializeComponent();
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Center;
        }

        /// <summary>自动注册为字（Word）类型标签，对应位置状态寄存器（如 DB1.DBW301）。</summary>
        protected override PLCTag? CreateDefaultTag()
        {
            if (string.IsNullOrWhiteSpace(TagAddress)) return null;
            string name = string.IsNullOrWhiteSpace(TagName) ? TagAddress : TagName;
            return PLCTag.CreateWord(name, TagAddress, name);
        }

        /// <summary>
        /// 收到 PLC 值后在 UI 线程处理：
        ///  - Equals 模式：值 == ActiveValue 即视为该位置到位（激活）；
        ///  - Between 模式：Low &lt; 值 &lt; High 即视为“两站之间在途”（激活）。
        /// </summary>
        protected override void OnPlcValueReceived(object? value)
        {
            if (value == null) { SetIsActive(false); return; }
            try
            {
                if (ActiveMode == StatusBarActiveMode.Between)
                {
                    double low = Convert.ToDouble(ActiveLow);
                    double high = Convert.ToDouble(ActiveHigh);
                    double current = Convert.ToDouble(value);
                    // 严格介于（不含两端），避免与两端车站自身的“到位”判定重叠
                    SetIsActive(current > low && current < high);
                }
                else
                {
                    long active = Convert.ToInt64(ActiveValue);
                    long current = Convert.ToInt64(value);
                    SetIsActive(active == current);
                }
            }
            catch
            {
                SetIsActive(false);
            }
        }
    }
}
