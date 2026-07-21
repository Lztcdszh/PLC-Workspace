using MyPLCManagerLibrary.Interfaces;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace PLCTesting.ViewModels
{
    /// <summary>
    /// 参数设置窗口的视图模型：承载连接实例与「还原参数」指令。
    /// 与 MainWindowViewModel 共享同一个 IConnectionManager（PLC1）。
    /// </summary>
    public class ParameterSettingsViewModel : BindableBase
    {
        private readonly IConnectionManager _connection;

        public IConnectionManager Connection => _connection;

        public DelegateCommand ResetParametersCommand { get; }

        public ParameterSettingsViewModel(IConnectionManager connection)
        {
            _connection = connection;
            ResetParametersCommand = new DelegateCommand(ExecuteResetParameters);
        }

        private async void ExecuteResetParameters()
        {
            var result = MessageBox.Show(
                "确认要将所有参数恢复为默认值吗？\n此操作将写入 PLC 并覆盖当前设定。",
                "参数复位确认",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.OK) return;

            if (_connection == null || !_connection.IsConnected)
            {
                MessageBox.Show("PLC 未连接，无法执行复位。", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                await _connection.WriteTagAsync("参数复位", true);
                await Task.Delay(1000);
                await _connection.WriteTagAsync("参数复位", false);

                MessageBox.Show("复位指令已发送。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"复位失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
