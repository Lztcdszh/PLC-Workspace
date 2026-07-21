using MyPLCManagerLibrary.Interfaces;
using PLCTesting.Services;
using PLCTesting.ViewModels;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PLCTesting.Views
{
    /// <summary>
    /// MQTT 配置窗口。
    /// </summary>
    public partial class MqttConfigView : Window
    {
        public MqttConfigView(MqttPublisherService service, IConnectionManager connection)
        {
            InitializeComponent();
            DataContext = new MqttConfigViewModel(service, connection);
        }
    }

    /// <summary>bool → 画刷转换器（用于连接状态指示点）。</summary>
    public class BoolToBrushConverter : IValueConverter
    {
        public Brush TrueBrush { get; set; } = Brushes.Green;
        public Brush FalseBrush { get; set; } = Brushes.Gray;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? TrueBrush : FalseBrush;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>bool → 文本转换器（用于启动/停止按钮文案）。</summary>
    public class BoolToTextConverter : IValueConverter
    {
        public string TrueText { get; set; } = "开";
        public string FalseText { get; set; } = "关";

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? TrueText : FalseText;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
