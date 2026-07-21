using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MyPLCUserControlLibrary
{
    /// <summary>
    /// 多状态指示控件：将 PLC 标签的整数 / 枚举值映射到一组状态定义（文本 + 颜色 + 可选闪烁），
    /// 常用于设备状态（如小车状态 Int16：0=停止/灰、1=运行/绿、2=故障/红）。
    /// 继承 PlcControlBase，复用连接与订阅逻辑。
    /// </summary>
    public partial class PlcMultiStateIndicator : PlcControlBase
    {
        private bool _isConnected;
        private Brush? _currentBrush;
        private DispatcherTimer? _blinkTimer;
        private bool _blinkOn = true;

        /// <summary>未显式配置 States 时使用的内置默认状态（停止/运行/故障）。</summary>
        private static readonly List<PlcStateDefinition> _builtinDefaults = new List<PlcStateDefinition>
        {
            new PlcStateDefinition { Value = 0, Text = "停止", Brush = new SolidColorBrush(Colors.Gray) },
            new PlcStateDefinition { Value = 1, Text = "运行", Brush = new SolidColorBrush(Colors.LimeGreen) },
            new PlcStateDefinition { Value = 2, Text = "故障", Brush = new SolidColorBrush(Colors.Red), Blink = true },
        };

        public PlcMultiStateIndicator()
        {
            InitializeComponent();

            CurrentStateText = DisconnectedText;
            CurrentStateBrush = UnknownBrush;
        }

        /// <summary>实际生效的状态表：若用户配置了 States 则使用之，否则使用内置默认值。</summary>
        private IEnumerable<PlcStateDefinition> EffectiveStates
            => (States != null && States.Count > 0) ? States : _builtinDefaults;

        #region 依赖属性

        public static readonly DependencyProperty DataTypeProperty =
            DependencyProperty.Register(nameof(DataType), typeof(RangeDataType), typeof(PlcMultiStateIndicator),
                new PropertyMetadata(RangeDataType.Int16, (d, e) => ((PlcMultiStateIndicator)d).OnTagChanged()));

        public RangeDataType DataType
        {
            get => (RangeDataType)GetValue(DataTypeProperty);
            set => SetValue(DataTypeProperty, value);
        }

        public static readonly DependencyProperty StatesProperty =
            DependencyProperty.Register(nameof(States), typeof(ObservableCollection<PlcStateDefinition>), typeof(PlcMultiStateIndicator),
                new PropertyMetadata(null));

        public ObservableCollection<PlcStateDefinition>? States
        {
            get => (ObservableCollection<PlcStateDefinition>?)GetValue(StatesProperty);
            set => SetValue(StatesProperty, value);
        }

        public static readonly DependencyProperty UnknownTextProperty =
            DependencyProperty.Register(nameof(UnknownText), typeof(string), typeof(PlcMultiStateIndicator), new PropertyMetadata("未知"));

        public string UnknownText
        {
            get => (string)GetValue(UnknownTextProperty);
            set => SetValue(UnknownTextProperty, value);
        }

        public static readonly DependencyProperty UnknownBrushProperty =
            DependencyProperty.Register(nameof(UnknownBrush), typeof(Brush), typeof(PlcMultiStateIndicator), new PropertyMetadata(new SolidColorBrush(Colors.Orange)));

        public Brush UnknownBrush
        {
            get => (Brush)GetValue(UnknownBrushProperty);
            set => SetValue(UnknownBrushProperty, value);
        }

        public static readonly DependencyProperty DisconnectedTextProperty =
            DependencyProperty.Register(nameof(DisconnectedText), typeof(string), typeof(PlcMultiStateIndicator), new PropertyMetadata("—"));

        public string DisconnectedText
        {
            get => (string)GetValue(DisconnectedTextProperty);
            set => SetValue(DisconnectedTextProperty, value);
        }

        public static readonly DependencyProperty ShowValueProperty =
            DependencyProperty.Register(nameof(ShowValue), typeof(bool), typeof(PlcMultiStateIndicator), new PropertyMetadata(false));

        public bool ShowValue
        {
            get => (bool)GetValue(ShowValueProperty);
            set => SetValue(ShowValueProperty, value);
        }

        public static readonly DependencyProperty TextFontSizeProperty =
            DependencyProperty.Register(nameof(TextFontSize), typeof(double), typeof(PlcMultiStateIndicator), new PropertyMetadata(14d));

        public double TextFontSize
        {
            get => (double)GetValue(TextFontSizeProperty);
            set => SetValue(TextFontSizeProperty, value);
        }

        public static readonly DependencyProperty ValueFontSizeProperty =
            DependencyProperty.Register(nameof(ValueFontSize), typeof(double), typeof(PlcMultiStateIndicator), new PropertyMetadata(12d));

        public double ValueFontSize
        {
            get => (double)GetValue(ValueFontSizeProperty);
            set => SetValue(ValueFontSizeProperty, value);
        }

        private static readonly DependencyPropertyKey CurrentStateTextPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(CurrentStateText), typeof(string), typeof(PlcMultiStateIndicator), new PropertyMetadata("—"));

        public static readonly DependencyProperty CurrentStateTextProperty = CurrentStateTextPropertyKey.DependencyProperty;

        public string CurrentStateText
        {
            get => (string)GetValue(CurrentStateTextProperty);
            private set => SetValue(CurrentStateTextPropertyKey, value);
        }

        private static readonly DependencyPropertyKey CurrentStateBrushPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(CurrentStateBrush), typeof(Brush), typeof(PlcMultiStateIndicator), new PropertyMetadata(null));

        public static readonly DependencyProperty CurrentStateBrushProperty = CurrentStateBrushPropertyKey.DependencyProperty;

        public Brush? CurrentStateBrush
        {
            get => (Brush?)GetValue(CurrentStateBrushProperty);
            private set => SetValue(CurrentStateBrushPropertyKey, value);
        }

        private static readonly DependencyPropertyKey CurrentStateValueDisplayPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(CurrentStateValueDisplay), typeof(string), typeof(PlcMultiStateIndicator), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty CurrentStateValueDisplayProperty = CurrentStateValueDisplayPropertyKey.DependencyProperty;

        public string CurrentStateValueDisplay
        {
            get => (string)GetValue(CurrentStateValueDisplayProperty);
            private set => SetValue(CurrentStateValueDisplayPropertyKey, value);
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
            if (!_isConnected)
                ShowDisconnected();
        }

        protected override void OnConnectionStateChanged(bool isConnected)
        {
            _isConnected = isConnected;
            if (!_isConnected)
                ShowDisconnected();
        }

        protected override void OnPlcValueReceived(object? value)
        {
            if (!_isConnected) return;

            int intVal;
            try { intVal = Convert.ToInt32(value, CultureInfo.CurrentCulture); }
            catch { intVal = int.MinValue; }

            CurrentStateValueDisplay = ShowValue ? $"({intVal})" : string.Empty;

            PlcStateDefinition? def = EffectiveStates
                .FirstOrDefault(s => s.Value == intVal);

            if (def != null)
            {
                CurrentStateText = def.Text;
                _currentBrush = def.Brush;
                CurrentStateBrush = def.Brush;
                UpdateIndicatorVisual();
                if (def.Blink) StartBlink();
                else StopBlink();
            }
            else
            {
                CurrentStateText = UnknownText;
                _currentBrush = UnknownBrush;
                CurrentStateBrush = UnknownBrush;
                UpdateIndicatorVisual();
                StopBlink();
            }
        }

        #endregion

        #region 视觉

        private void ShowDisconnected()
        {
            StopBlink();
            CurrentStateText = DisconnectedText;
            CurrentStateValueDisplay = string.Empty;
            _currentBrush = Brushes.Gray;
            CurrentStateBrush = Brushes.Gray;
            UpdateIndicatorVisual();
        }

        private void StartBlink()
        {
            if (_blinkTimer == null)
            {
                _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _blinkTimer.Tick += (s, e) =>
                {
                    _blinkOn = !_blinkOn;
                    UpdateIndicatorVisual();
                };
            }
            if (!_blinkTimer.IsEnabled)
                _blinkTimer.Start();
        }

        private void StopBlink()
        {
            _blinkTimer?.Stop();
            _blinkOn = true;
            UpdateIndicatorVisual();
        }

        private void UpdateIndicatorVisual()
        {
            if (PART_Indicator == null) return;
            PART_Indicator.Fill = (_blinkOn ? _currentBrush : Brushes.Transparent) ?? Brushes.Transparent;
        }

        #endregion

        #region 资源清理

        protected override void OnUnloadedInternal()
        {
            StopBlink();
        }

        #endregion
    }

    /// <summary>
    /// 多状态控件的一个状态定义：匹配的数值、显示文本、指示灯颜色、是否闪烁。
    /// 可在 XAML 中以 &lt;local:PlcStateDefinition .../&gt; 形式配置。
    /// </summary>
    public class PlcStateDefinition
    {
        public int Value { get; set; }
        public string Text { get; set; } = string.Empty;
        public Brush? Brush { get; set; }
        public bool Blink { get; set; }
    }
}
