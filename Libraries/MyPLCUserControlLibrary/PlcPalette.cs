using System.Windows;
using System.Windows.Media;

namespace MyPLCUserControlLibrary
{
    /// <summary>
    /// 通用工控上位机控件库的共享配色与画笔。
    /// 所有控件统一引用此处定义的颜色，保证整库视觉一致（深色/浅色主题切换也只需改这里）。
    /// </summary>
    public static class PlcPalette
    {
        // 语义化主色
        public static readonly Color AccentColor = Color.FromRgb(64, 158, 255);   // 主题蓝
        public static readonly Color SuccessColor = Color.FromRgb(103, 194, 58);   // 运行/正常 绿
        public static readonly Color WarningColor = Color.FromRgb(230, 162, 60);   // 警告 橙
        public static readonly Color DangerColor  = Color.FromRgb(245, 108, 108);  // 故障/报警 红
        public static readonly Color InfoColor    = Color.FromRgb(144, 147, 153);  // 闲置/未知 灰
        public static readonly Color OffColor     = Color.FromRgb(82, 90, 102);    // 熄灭 深灰

        // 文本/边框/背景
        public static readonly Color TextColor        = Color.FromRgb(48, 49, 51);
        public static readonly Color TextSecondaryColor = Color.FromRgb(117, 120, 126);
        public static readonly Color BorderColor      = Color.FromRgb(220, 223, 230);
        public static readonly Color BackgroundColor  = Color.FromRgb(255, 255, 255);
        public static readonly Color PanelHeaderColor = Color.FromRgb(245, 247, 250);

        // 画笔（每次返回新实例，避免跨控件共享冻结状态问题）
        public static SolidColorBrush AccentBrush   => new SolidColorBrush(AccentColor);
        public static SolidColorBrush SuccessBrush   => new SolidColorBrush(SuccessColor);
        public static SolidColorBrush WarningBrush   => new SolidColorBrush(WarningColor);
        public static SolidColorBrush DangerBrush    => new SolidColorBrush(DangerColor);
        public static SolidColorBrush InfoBrush      => new SolidColorBrush(InfoColor);
        public static SolidColorBrush OffBrush       => new SolidColorBrush(OffColor);
        public static SolidColorBrush TextBrush      => new SolidColorBrush(TextColor);
        public static SolidColorBrush TextSecondaryBrush => new SolidColorBrush(TextSecondaryColor);
        public static SolidColorBrush BorderBrush    => new SolidColorBrush(BorderColor);
        public static SolidColorBrush BackgroundBrush => new SolidColorBrush(BackgroundColor);

        /// <summary>根据报警/状态级别返回对应语义色画笔。</summary>
        public static SolidColorBrush BrushForSeverity(PlcAlarmSeverity severity) => severity switch
        {
            PlcAlarmSeverity.Error => DangerBrush,
            PlcAlarmSeverity.Warning => WarningBrush,
            PlcAlarmSeverity.Info => InfoBrush,
            _ => InfoBrush,
        };
    }

    /// <summary>报警严重级别（与通用 SCADA 配色语义一致）。</summary>
    public enum PlcAlarmSeverity
    {
        Info = 0,
        Warning = 1,
        Error = 2,
    }
}
