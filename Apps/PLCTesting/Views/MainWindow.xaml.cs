using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace PLCTesting.Views
{
    public partial class MainWindow : Window
    {
        

        public MainWindow()
        {
            InitializeComponent();

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