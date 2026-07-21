using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyPLCUserControlLibrary
{
    /// <summary>
    /// PLC 文本标签：订阅字符串类型标签并展示其文本（如设备名称、工况描述、提示信息）。
    /// 支持前缀/后缀、最大长度省略号、标题。继承 PlcControlBase。
    /// </summary>
    public partial class PlcLabel : PlcControlBase
    {
        private bool _isConnected;

        public PlcLabel()
        {
            InitializeComponent();

            if (DesignerProperties.GetIsInDesignMode(this))
            {
                _isConnected = true;
                Caption = "状态";
                Text = "运行正常";
                return;
            }
        }

        #region 依赖属性

        public static readonly DependencyProperty CaptionProperty =
            DependencyProperty.Register(nameof(Caption), typeof(string), typeof(PlcLabel), new PropertyMetadata(string.Empty));

        public string Caption
        {
            get => (string)GetValue(CaptionProperty);
            set => SetValue(CaptionProperty, value);
        }

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(PlcLabel), new PropertyMetadata("—", (d, e) => ((PlcLabel)d).RefreshText()));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public static readonly DependencyProperty PrefixProperty =
            DependencyProperty.Register(nameof(Prefix), typeof(string), typeof(PlcLabel), new PropertyMetadata(string.Empty, (d, e) => ((PlcLabel)d).RefreshText()));

        public string Prefix
        {
            get => (string)GetValue(PrefixProperty);
            set => SetValue(PrefixProperty, value);
        }

        public static readonly DependencyProperty SuffixProperty =
            DependencyProperty.Register(nameof(Suffix), typeof(string), typeof(PlcLabel), new PropertyMetadata(string.Empty, (d, e) => ((PlcLabel)d).RefreshText()));

        public string Suffix
        {
            get => (string)GetValue(SuffixProperty);
            set => SetValue(SuffixProperty, value);
        }

        public static readonly DependencyProperty MaxLengthProperty =
            DependencyProperty.Register(nameof(MaxLength), typeof(int), typeof(PlcLabel), new PropertyMetadata(0, (d, e) => ((PlcLabel)d).RefreshText()));

        /// <summary>文本最大长度（0 表示不限制）。超出时按要求截断。</summary>
        public int MaxLength
        {
            get => (int)GetValue(MaxLengthProperty);
            set => SetValue(MaxLengthProperty, value);
        }

        public static readonly DependencyProperty EllipsisProperty =
            DependencyProperty.Register(nameof(Ellipsis), typeof(bool), typeof(PlcLabel), new PropertyMetadata(true, (d, e) => ((PlcLabel)d).RefreshText()));

        public bool Ellipsis
        {
            get => (bool)GetValue(EllipsisProperty);
            set => SetValue(EllipsisProperty, value);
        }

        public static readonly DependencyProperty StringLengthProperty =
            DependencyProperty.Register(nameof(StringLength), typeof(int), typeof(PlcLabel), new PropertyMetadata(32));

        /// <summary>自动注册字符串标签时的长度（用于底层地址解析）。</summary>
        public int StringLength
        {
            get => (int)GetValue(StringLengthProperty);
            set => SetValue(StringLengthProperty, value);
        }

        public static readonly DependencyProperty ShowCaptionProperty =
            DependencyProperty.Register(nameof(ShowCaption), typeof(bool), typeof(PlcLabel), new PropertyMetadata(true, (d, e) => ((PlcLabel)d).UpdateCaptionVisibility()));

        public bool ShowCaption
        {
            get => (bool)GetValue(ShowCaptionProperty);
            set => SetValue(ShowCaptionProperty, value);
        }

        public static readonly DependencyProperty TextFontSizeProperty =
            DependencyProperty.Register(nameof(TextFontSize), typeof(double), typeof(PlcLabel), new PropertyMetadata(16d));

        public double TextFontSize
        {
            get => (double)GetValue(TextFontSizeProperty);
            set => SetValue(TextFontSizeProperty, value);
        }

        public static readonly DependencyProperty CaptionFontSizeProperty =
            DependencyProperty.Register(nameof(CaptionFontSize), typeof(double), typeof(PlcLabel), new PropertyMetadata(11d));

        public double CaptionFontSize
        {
            get => (double)GetValue(CaptionFontSizeProperty);
            set => SetValue(CaptionFontSizeProperty, value);
        }

        public static readonly DependencyProperty ValueForegroundProperty =
            DependencyProperty.Register(nameof(ValueForeground), typeof(Brush), typeof(PlcLabel), new PropertyMetadata(new SolidColorBrush(Color.FromRgb(48, 49, 51))));

        public Brush ValueForeground
        {
            get => (Brush)GetValue(ValueForegroundProperty);
            set => SetValue(ValueForegroundProperty, value);
        }

        #endregion

        #region PlcControlBase 钩子

        protected override PLCTag? CreateDefaultTag()
        {
            if (string.IsNullOrWhiteSpace(TagAddress)) return null;
            string effectiveName = string.IsNullOrWhiteSpace(TagName) ? TagAddress : TagName;
            return PLCTag.CreateString(effectiveName, TagAddress, StringLength, effectiveName);
        }

        protected override void OnConnectionChangedInternal(IConnectionManager? oldConn, IConnectionManager? newConn)
        {
            _isConnected = newConn?.IsConnected ?? false;
            if (!_isConnected) Text = "—";
        }

        protected override void OnConnectionStateChanged(bool isConnected)
        {
            _isConnected = isConnected;
            if (!_isConnected) Text = "—";
        }

        protected override void OnPlcValueReceived(object? value)
        {
            if (!_isConnected) return;
            Text = (value?.ToString()) ?? string.Empty;
            RefreshText();
        }

        #endregion

        #region 文本处理

        private void RefreshText()
        {
            string raw = Text;
            if (string.IsNullOrEmpty(raw) || raw == "—")
            {
                PART_Text.Text = raw == "—" ? "—" : string.Empty;
                return;
            }

            string composed = Prefix + raw + Suffix;
            if (MaxLength > 0 && composed.Length > MaxLength)
            {
                composed = Ellipsis
                    ? composed.Substring(0, MaxLength - 1) + "…"
                    : composed.Substring(0, MaxLength);
            }
            PART_Text.Text = composed;
        }

        private void UpdateCaptionVisibility()
        {
            if (PART_Caption != null)
                PART_Caption.Visibility = ShowCaption ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion
    }
}
