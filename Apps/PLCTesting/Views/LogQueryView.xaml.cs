using PLCTesting.Services;
using PLCTesting.ViewModels;
using System.Windows;

namespace PLCTesting.Views
{
    /// <summary>
    /// PLC 变化日志查询窗口。
    /// </summary>
    public partial class LogQueryView : Window
    {
        public LogQueryView(ChangeLogRepository repository)
        {
            InitializeComponent();
            DataContext = new LogQueryViewModel(repository);
        }
    }
}
