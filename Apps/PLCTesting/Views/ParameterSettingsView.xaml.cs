using MyPLCManagerLibrary.Interfaces;
using PLCTesting.ViewModels;
using System.Windows;

namespace PLCTesting.Views
{
    /// <summary>
    /// 参数设置独立窗口（从主界面拆分，避免主界面拥挤）。
    /// </summary>
    public partial class ParameterSettingsView : Window
    {
        public ParameterSettingsView(IConnectionManager connection)
        {
            InitializeComponent();
            DataContext = new ParameterSettingsViewModel(connection);
        }
    }
}
