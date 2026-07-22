using MyPLCManagerLibrary.Interfaces;
using PLCTesting.Models;
using PLCTesting.Services;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace PLCTesting.ViewModels
{
    /// <summary>
    /// MQTT 配置窗口视图模型（极简版）：编辑一份配置副本，保存时写回服务并持久化；
    /// 提供测试连接、启动/停止转发。
    /// </summary>
    public class MqttConfigViewModel : BindableBase
    {
        private readonly MqttPublisherService _service;

        private MqttConfig _config;
        public MqttConfig Config
        {
            get => _config;
            set => SetProperty(ref _config, value);
        }

        public MqttPublisherService Service => _service;

        private string _testResult = "";
        public string TestResult
        {
            get => _testResult;
            set => SetProperty(ref _testResult, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        public DelegateCommand SaveCommand { get; }
        public DelegateCommand TestCommand { get; }
        public DelegateCommand TogglePublishCommand { get; }

        public MqttConfigViewModel(MqttPublisherService service, IConnectionManager connection)
        {
            _service = service;
            Config = Clone(service.Config);

            SaveCommand = new DelegateCommand(async () => await ExecuteSaveAsync());
            TestCommand = new DelegateCommand(async () => await ExecuteTestAsync());
            TogglePublishCommand = new DelegateCommand(async () => await ExecuteTogglePublishAsync());
        }

        private async Task ExecuteSaveAsync()
        {
            IsBusy = true;
            try
            {
                // 覆盖服务配置并持久化
                _service.Config = Clone(Config);
                _service.SaveConfig();
                await _service.ApplyConfigAsync();

                MessageBox.Show("MQTT 配置已保存并应用。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ExecuteTestAsync()
        {
            IsBusy = true;
            TestResult = "正在测试连接…";
            try
            {
                var ok = await _service.TestConnectionAsync(Config);
                TestResult = ok
                    ? "✅ 连接成功！Broker 可达。"
                    : $"❌ 连接失败：{_service.LastError}";
            }
            catch (Exception ex)
            {
                TestResult = $"❌ 测试异常：{ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ExecuteTogglePublishAsync()
        {
            IsBusy = true;
            try
            {
                if (_service.IsRunning)
                    await _service.StopAsync();
                else
                    await _service.StartAsync();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static MqttConfig Clone(MqttConfig src)
        {
            return new MqttConfig
            {
                Enabled = src.Enabled,
                BrokerHost = src.BrokerHost,
                BrokerPort = src.BrokerPort,
                BaseTopic = src.BaseTopic
            };
        }
    }
}
