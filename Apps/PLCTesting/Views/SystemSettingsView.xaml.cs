using PLCTesting.Services;
using PLCTesting.ViewModels;
using System.Windows;

namespace PLCTesting.Views
{
    /// <summary>
    /// 系统设置窗口：开机自启动 + 后台运行。
    /// </summary>
    public partial class SystemSettingsView : Window
    {
        public SystemSettingsView(Services.AppSettingsStore store = null, Services.StartupService startup = null)
        {
            InitializeComponent();
            DataContext = new SystemSettingsViewModel(store, startup);
        }
    }
}
