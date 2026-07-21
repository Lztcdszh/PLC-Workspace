using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyPLCUserControlLibrary
{
    /// <summary>
    /// 通用 UI 助手：为工控上位机提供模态确认弹窗（写入前二次确认），避免误操作时写错 PLC 线圈/寄存器。
    /// </summary>
    public static class PlcUi
    {
        /// <summary>
        /// 弹出一个居中的模态确认对话框，返回用户是否确认。
        /// 在触摸一体机上比原生 MessageBox 更美观、按钮更大。
        /// </summary>
        /// <param name="title">标题</param>
        /// <param name="message">提示内容</param>
        /// <param name="okText">确认按钮文字（默认"确认"）</param>
        /// <param name="cancelText">取消按钮文字（默认"取消"）</param>
        /// <param name="owner">宿主窗口，可空</param>
        public static Task<bool> ConfirmAsync(string title, string message,
            string okText = "确认", string cancelText = "取消", Window? owner = null)
        {
            var tcs = new TaskCompletionSource<bool>();

            var dlg = new Window
            {
                Title = title,
                Width = 360,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Topmost = true,
                Background = new SolidColorBrush(Color.FromRgb(245, 247, 250)),
            };

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var txt = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(48, 49, 51)),
            };
            Grid.SetRow(txt, 0);

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
            };

            var btnOk = new Button
            {
                Content = okText,
                Width = 100,
                Height = 36,
                Margin = new Thickness(8, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(64, 158, 255)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 14,
            };
            var btnCancel = new Button
            {
                Content = cancelText,
                Width = 100,
                Height = 36,
                Margin = new Thickness(8, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(220, 223, 230)),
                Foreground = new SolidColorBrush(Color.FromRgb(48, 49, 51)),
                BorderThickness = new Thickness(0),
                FontSize = 14,
            };

            btnOk.Click += (s, e) => { dlg.DialogResult = true; dlg.Close(); };
            btnCancel.Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };

            panel.Children.Add(btnCancel);
            panel.Children.Add(btnOk);

            Grid.SetRow(panel, 1);
            grid.Children.Add(txt);
            grid.Children.Add(panel);
            dlg.Content = grid;

            dlg.Closed += (s, e) => tcs.TrySetResult(dlg.DialogResult == true);
            dlg.ShowDialog();

            return tcs.Task;
        }
    }
}
