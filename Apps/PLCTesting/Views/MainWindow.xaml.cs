using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PLCTesting.Views
{
    public partial class MainWindow : Window
    {

        public MainWindow()
        {
            InitializeComponent();

            // 从磁盘加载窗口标题栏图标（部署后 Assets/app.ico 必存在）。
            // 注意：XAML 中不能用 Icon="Assets/app.ico"——WPF 会把它当作程序集“内嵌资源”解析，
            // 而该文件是作为 Content 复制到输出的，不在资源里，会导致 InitializeComponent 抛 IOException 使窗口建不出来。
            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
                if (File.Exists(iconPath))
                    this.Icon = new BitmapImage(new Uri(iconPath));
            }
            catch
            {
                // 图标为非关键项，加载失败不影响启动
            }

            Loaded += (s, e) =>
            {
                // 确保窗口不会超出当前屏幕工作区，避免在 768/900 等小屏上底部被截断
                var area = SystemParameters.WorkArea;
                if (ActualHeight > area.Height)
                    Height = area.Height - 8;
                if (ActualWidth > area.Width)
                    Width = area.Width - 8;
                MaxHeight = area.Height;
                MaxWidth = area.Width;
            };
        }

     

        private void PlcTextBox_Loaded(object sender, RoutedEventArgs e)
        {
            // 可选逻辑
        }
    }
}