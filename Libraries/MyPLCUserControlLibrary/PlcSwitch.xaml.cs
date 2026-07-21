using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MyPLCUserControlLibrary
{
    /// <summary>
    /// 可视化开关（拨动/点动）：订阅布尔标签显示当前状态，点击向 PLC 写入相反值。
    /// 支持开/关文本与配色，可选 ConfirmWrite 二次确认（防止误触）。继承 PlcControlBase。
    /// </summary>
    public partial class PlcSwitch : PlcControlBase
    {
        private bool _isConnected;
        private bool _isOn;
        private bool _isBusy;

        public PlcSwitch()
        {
            InitializeComponent();

            if (DesignerProperties.GetIsInDesignMode(this))
            {
                _isConnected = true;
                _isOn = true;
                UpdateVisual();
                PART_StateText.Text = OnText;
                return;
            }

            PART_Canvas.PreviewMouseDown += OnSwitchClick;
        }

        #region 依赖属性

        public static readonly DependencyProperty OnTextProperty =
            DependencyProperty.Register(nameof(OnText), typeof(string), typeof(PlcSwitch), new PropertyMetadata("ON", (d, e) => ((PlcSwitch)d).UpdateVisual()));

        public string OnText
        {
            get => (string)GetValue(OnTextProperty);
            set => SetValue(OnTextProperty, value);
        }

        public static readonly DependencyProperty OffTextProperty =
            DependencyProperty.Register(nameof(OffText), typeof(string), typeof(PlcSwitch), new PropertyMetadata("OFF", (d, e) => ((PlcSwitch)d).UpdateVisual()));

        public string OffText
        {
            get => (string)GetValue(OffTextProperty);
            set => SetValue(OffTextProperty, value);
        }

        public static readonly DependencyProperty OnColorProperty =
            DependencyProperty.Register(nameof(OnColor), typeof(Brush), typeof(PlcSwitch),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(103, 194, 58)), (d, e) => ((PlcSwitch)d).UpdateVisual()));

        public Brush OnColor
        {
            get => (Brush)GetValue(OnColorProperty);
            set => SetValue(OnColorProperty, value);
        }

        public static readonly DependencyProperty OffColorProperty =
            DependencyProperty.Register(nameof(OffColor), typeof(Brush), typeof(PlcSwitch),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(144, 147, 153)), (d, e) => ((PlcSwitch)d).UpdateVisual()));

        public Brush OffColor
        {
            get => (Brush)GetValue(OffColorProperty);
            set => SetValue(OffColorProperty, value);
        }

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(PlcSwitch), new PropertyMetadata(string.Empty));

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public static readonly DependencyProperty ConfirmWriteProperty =
            DependencyProperty.Register(nameof(ConfirmWrite), typeof(bool), typeof(PlcSwitch), new PropertyMetadata(false));

        /// <summary>写入前是否弹二次确认。</summary>
        public bool ConfirmWrite
        {
            get => (bool)GetValue(ConfirmWriteProperty);
            set => SetValue(ConfirmWriteProperty, value);
        }

        public static readonly DependencyProperty ConfirmTextProperty =
            DependencyProperty.Register(nameof(ConfirmText), typeof(string), typeof(PlcSwitch), new PropertyMetadata("确认切换该开关？"));

        public string ConfirmText
        {
            get => (string)GetValue(ConfirmTextProperty);
            set => SetValue(ConfirmTextProperty, value);
        }

        public static readonly DependencyProperty ShowStateTextProperty =
            DependencyProperty.Register(nameof(ShowStateText), typeof(bool), typeof(PlcSwitch), new PropertyMetadata(true));

        public bool ShowStateText
        {
            get => (bool)GetValue(ShowStateTextProperty);
            set => SetValue(ShowStateTextProperty, value);
        }

        #endregion

        #region PlcControlBase 钩子

        protected override PLCTag? CreateDefaultTag()
        {
            if (string.IsNullOrWhiteSpace(TagAddress)) return null;
            string effectiveName = string.IsNullOrWhiteSpace(TagName) ? TagAddress : TagName;
            return PLCTag.CreateBool(effectiveName, TagAddress, effectiveName);
        }

        protected override void OnConnectionChangedInternal(IConnectionManager? oldConn, IConnectionManager? newConn)
        {
            _isConnected = newConn?.IsConnected ?? false;
            if (!_isConnected) UpdateVisual();
        }

        protected override void OnConnectionStateChanged(bool isConnected)
        {
            _isConnected = isConnected;
            if (!_isConnected) UpdateVisual();
        }

        protected override void OnPlcValueReceived(object? value)
        {
            if (!_isConnected) return;
            try
            {
                _isOn = value switch
                {
                    bool b => b,
                    int i => i != 0,
                    float f => Math.Abs(f) > 1e-6,
                    double dbl => Math.Abs(dbl) > 1e-6,
                    string s => bool.TryParse(s, out var bb) ? bb : (int.TryParse(s, out var ii) && ii != 0),
                    _ => false,
                };
                UpdateVisual();
            }
            catch { }
        }

        #endregion

        #region 交互与视觉

        private async void OnSwitchClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!_isConnected || _isBusy || string.IsNullOrWhiteSpace(TagName)) return;

            bool target = !_isOn;
            if (ConfirmWrite)
            {
                bool ok = await PlcUi.ConfirmAsync("操作确认", ConfirmText);
                if (!ok) return;
            }

            _isBusy = true;
            try
            {
                await WriteTagAsync(TagName, target);
            }
            finally
            {
                _isBusy = false;
            }
        }

        private void UpdateVisual()
        {
            if (PART_Thumb == null || PART_Track == null || PART_StateText == null) return;

            Brush trackColor = (_isConnected && _isOn) ? OnColor : OffColor;
            PART_Track.Background = trackColor;

            double leftOn = PART_Canvas.ActualWidth - PART_Thumb.Width - 3;
            double leftOff = 3;
            Canvas.SetLeft(PART_Thumb, (_isConnected && _isOn) ? leftOn : leftOff);

            PART_StateText.Text = (_isConnected && _isOn) ? OnText : OffText;
            PART_StateText.Visibility = ShowStateText ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion
    }
}
