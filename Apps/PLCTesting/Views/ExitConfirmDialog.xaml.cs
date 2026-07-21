using System.Windows;

namespace PLCTesting.Views
{
    /// <summary>主窗口关闭时的选择：后台运行 / 退出 / 取消。</summary>
    public enum ExitChoice
    {
        Cancel,
        Tray,
        Exit
    }

    public partial class ExitConfirmDialog : Window
    {
        public ExitChoice Result { get; private set; } = ExitChoice.Cancel;

        public ExitConfirmDialog()
        {
            InitializeComponent();
            // 无边框窗口：允许按住空白区域拖动
            MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };
        }

        private void BtnTray_Click(object sender, RoutedEventArgs e)
        {
            Result = ExitChoice.Tray;
            DialogResult = true;
        }

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            Result = ExitChoice.Exit;
            DialogResult = true;
        }
    }
}
