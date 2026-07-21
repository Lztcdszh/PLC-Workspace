using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using PLCTesting.Models;
using PLCTesting.Services;
using Prism.Commands;
using Prism.Mvvm;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System;

namespace PLCTesting.ViewModels
{
    /// <summary>配置窗口中用于勾选的标签项。</summary>
    public class TagSelectionItem : BindableBase
    {
        public string Name { get; }
        public string Address { get; }
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
        public TagSelectionItem(string name, string address, bool selected)
        {
            Name = name;
            Address = address;
            _isSelected = selected;
        }
    }

    /// <summary>
    /// MQTT 配置窗口视图模型：编辑一份配置副本，保存时写回服务并持久化；
    /// 提供测试连接、启动/停止转发、标签勾选等功能。
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

        /// <summary>「发布全部标签」开关（包装 Config，便于联动标签列表可用性）。</summary>
        public bool PublishAllTags
        {
            get => Config.PublishAllTags;
            set
            {
                Config.PublishAllTags = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(TagsSelectable));
            }
        }

        /// <summary>标签勾选列表是否可用（勾选全部时禁用）。</summary>
        public bool TagsSelectable => !Config.PublishAllTags;

        public ObservableCollection<TagSelectionItem> AvailableTags { get; } = new();

        /// <summary>ComboBox 用的「值-显示文本」选项。</summary>
        public class EnumOption
        {
            public object Value { get; set; }
            public string Label { get; set; }
        }

        public List<EnumOption> QosOptions { get; } = new()
        {
            new EnumOption { Value = MqttQosLevel.AtMostOnce, Label = "QoS 0 · 至多一次" },
            new EnumOption { Value = MqttQosLevel.AtLeastOnce, Label = "QoS 1 · 至少一次" },
            new EnumOption { Value = MqttQosLevel.ExactlyOnce, Label = "QoS 2 · 恰好一次" },
        };

        public List<EnumOption> PublishModeOptions { get; } = new()
        {
            new EnumOption { Value = MqttPublishMode.OnChange, Label = "值变化即发布" },
            new EnumOption { Value = MqttPublishMode.Periodic, Label = "周期性发布" },
        };

        public List<EnumOption> TopicFormatOptions { get; } = new()
        {
            new EnumOption { Value = MqttTopicFormat.ByAddress, Label = "按 PLC 地址（推荐）" },
            new EnumOption { Value = MqttTopicFormat.ByName, Label = "按标签名称" },
        };

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
        public DelegateCommand SelectAllCommand { get; }
        public DelegateCommand SelectNoneCommand { get; }

        public MqttConfigViewModel(MqttPublisherService service, IConnectionManager connection)
        {
            _service = service;
            Config = Clone(service.Config);

            // 构建可选标签列表（来自连接的当前标签快照）
            var snapshot = connection.GetAllTagsSnapshot();
            foreach (var tag in snapshot.OrderBy(t => t.Address))
            {
                var selected = Config.PublishAllTags ||
                               Config.SelectedTags.Any(t =>
                                   string.Equals(t, tag.Name, StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(t, tag.Address, StringComparison.OrdinalIgnoreCase));
                AvailableTags.Add(new TagSelectionItem(tag.Name, tag.Address, selected));
            }

            SaveCommand = new DelegateCommand(async () => await ExecuteSaveAsync());
            TestCommand = new DelegateCommand(async () => await ExecuteTestAsync());
            TogglePublishCommand = new DelegateCommand(async () => await ExecuteTogglePublishAsync());
            SelectAllCommand = new DelegateCommand(() => SetAllSelected(true));
            SelectNoneCommand = new DelegateCommand(() => SetAllSelected(false));
        }

        private void SetAllSelected(bool value)
        {
            foreach (var item in AvailableTags) item.IsSelected = value;
        }

        private async Task ExecuteSaveAsync()
        {
            IsBusy = true;
            try
            {
                // 写回选中标签
                Config.SelectedTags = AvailableTags
                    .Where(i => i.IsSelected)
                    .Select(i => i.Name)
                    .ToList();

                // 覆盖服务配置并持久化
                _service.Config = Clone(Config);
                _service.SaveConfig();
                await _service.ApplyConfigAsync();

                MessageBox.Show("MQTT 配置已保存并应用。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
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
            catch (System.Exception ex)
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
                ClientId = src.ClientId,
                Username = src.Username,
                Password = src.Password,
                UseTls = src.UseTls,
                BaseTopic = src.BaseTopic,
                Qos = src.Qos,
                Retain = src.Retain,
                PublishMode = src.PublishMode,
                PublishIntervalMs = src.PublishIntervalMs,
                TopicFormat = src.TopicFormat,
                PublishAllTags = src.PublishAllTags,
                SelectedTags = new List<string>(src.SelectedTags)
            };
        }
    }
}
