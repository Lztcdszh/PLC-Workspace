using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using PLCTesting.Models;

namespace PLCTesting.Services
{
    /// <summary>
    /// MQTT 发布服务：订阅 PLC 连接管理器的标签变化事件，将选中标签的值转发到 MQTT Broker。
    /// 完全位于 app 层，不污染控件库；支持断线自动重连、变更/周期两种发布模式、JSON 负载。
    /// </summary>
    public class MqttPublisherService : INotifyPropertyChanged, IDisposable
    {
        private static readonly string ConfigPath =
            Path.Combine(AppContext.BaseDirectory, "mqttconfig.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly IConnectionManager _connection;
        private readonly MqttFactory _factory = new();
        private IMqttClient _client;

        private bool _isRunning;
        private bool _isConnected;
        private bool _stopRequested;
        private bool _disposed;

        private string _statusText = "未启动";
        private string _lastError = "";
        private int _publishedCount;
        private System.Timers.Timer _publishTimer;

        public MqttConfig Config { get; set; } = new();

        public bool IsRunning
        {
            get => _isRunning;
            private set { if (_isRunning != value) { _isRunning = value; Raise(nameof(IsRunning)); } }
        }

        public bool IsConnected
        {
            get => _isConnected;
            private set { if (_isConnected != value) { _isConnected = value; Raise(nameof(IsConnected)); } }
        }

        public string StatusText
        {
            get => _statusText;
            private set { if (_statusText != value) { _statusText = value; Raise(nameof(StatusText)); } }
        }

        public string LastError
        {
            get => _lastError;
            private set { if (_lastError != value) { _lastError = value; Raise(nameof(LastError)); } }
        }

        public int PublishedCount
        {
            get => _publishedCount;
            private set { if (_publishedCount != value) { _publishedCount = value; Raise(nameof(PublishedCount)); } }
        }

        public MqttPublisherService(IConnectionManager connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _client = _factory.CreateMqttClient();

            _client.ConnectedAsync += OnMqttConnected;
            _client.DisconnectedAsync += OnMqttDisconnected;

            _publishTimer = new System.Timers.Timer();
            _publishTimer.AutoReset = true;
            _publishTimer.Elapsed += async (s, e) => await PublishAllSelectedAsync();

            LoadConfig();
        }

        #region 配置读写

        public void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var cfg = JsonSerializer.Deserialize<MqttConfig>(json, JsonOptions);
                    if (cfg != null) Config = cfg;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Mqtt] 加载配置失败: {ex.Message}");
            }
        }

        public void SaveConfig()
        {
            try
            {
                var json = JsonSerializer.Serialize(Config, JsonOptions);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                LastError = $"保存配置失败：{ex.Message}";
                Debug.WriteLine(LastError);
            }
        }

        #endregion

        #region 启动 / 停止

        /// <summary>按当前 Config 启动转发（若已启用则连接 Broker 并开始发布）。</summary>
        public async Task StartAsync()
        {
            if (_isRunning) return;
            _stopRequested = false;
            _isRunning = true;
            StatusText = Config.Enabled ? "正在连接 MQTT Broker…" : "已就绪（未启用）";
            if (Config.Enabled)
            {
                _connection.TagValueChanged += OnTagValueChanged;
                await ConnectWithRetryAsync();
                ConfigureTimer();
            }
        }

        /// <summary>停止转发并断开 Broker 连接。</summary>
        public async Task StopAsync()
        {
            _stopRequested = true;
            _isRunning = false;
            _publishTimer.Stop();
            _connection.TagValueChanged -= OnTagValueChanged;
            try
            {
                if (_client.IsConnected)
                    await _client.DisconnectAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Mqtt] 断开异常: {ex.Message}");
            }
            IsConnected = false;
            StatusText = "已停止";
        }

        /// <summary>应用新的配置（保存后调用）：停止当前连接，按新配置重启。</summary>
        public async Task ApplyConfigAsync()
        {
            var wasRunning = _isRunning;
            if (wasRunning)
                await StopAsync();
            if (Config.Enabled)
                await StartAsync();
            else
                StatusText = "已就绪（未启用）";
        }

        private void ConfigureTimer()
        {
            if (Config.PublishMode == MqttPublishMode.Periodic && Config.PublishIntervalMs > 0)
            {
                _publishTimer.Interval = Config.PublishIntervalMs;
                _publishTimer.Start();
            }
            else
            {
                _publishTimer.Stop();
            }
        }

        #endregion

        #region 连接

        private MqttClientOptions BuildOptions(MqttConfig config)
        {
            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(config.BrokerHost, config.BrokerPort)
                .WithClientId(string.IsNullOrWhiteSpace(config.ClientId)
                    ? $"PLCTesting_{Environment.MachineName}"
                    : config.ClientId);

            if (!string.IsNullOrEmpty(config.Username))
                builder = builder.WithCredentials(config.Username, config.Password);

            if (config.UseTls)
                builder = builder.WithTls();

            return builder.Build();
        }

        private MqttClientOptions BuildOptions() => BuildOptions(Config);

        private async Task ConnectWithRetryAsync(int maxAttempts = 5)
        {
            for (int i = 1; i <= maxAttempts; i++)
            {
                if (_stopRequested) return;
                try
                {
                    await _client.ConnectAsync(BuildOptions(), CancellationToken.None);
                    return; // ConnectedAsync 会更新状态
                }
                catch (Exception ex)
                {
                    LastError = $"第 {i} 次连接失败：{ex.Message}";
                    StatusText = LastError;
                    if (i < maxAttempts)
                        await Task.Delay(2000);
                }
            }
        }

        private Task OnMqttConnected(MqttClientConnectedEventArgs e)
        {
            IsConnected = true;
            LastError = "";
            StatusText = $"已连接 {Config.BrokerHost}:{Config.BrokerPort}";
            // 连接/重连后发布一次当前快照，确保订阅端拿到最新值
            _ = PublishAllSelectedAsync();
            return Task.CompletedTask;
        }

        private Task OnMqttDisconnected(MqttClientDisconnectedEventArgs e)
        {
            IsConnected = false;
            if (_stopRequested)
            {
                StatusText = "已停止";
                return Task.CompletedTask;
            }

            StatusText = $"连接断开（{e.Reason}），正在后台重连…";
            _ = Task.Run(async () =>
            {
                while (!_stopRequested && !_disposed)
                {
                    try
                    {
                        await Task.Delay(3000);
                        if (_stopRequested) break;
                        if (_client.IsConnected) break;
                        StatusText = "正在尝试重连 MQTT Broker…";
                        await _client.ConnectAsync(BuildOptions(), CancellationToken.None);
                        break; // 成功由 OnMqttConnected 处理
                    }
                    catch (Exception ex)
                    {
                        LastError = $"重连失败：{ex.Message}";
                        StatusText = LastError;
                    }
                }
            });
            return Task.CompletedTask;
        }

        #endregion

        #region 发布

        /// <summary>
        /// 判断某标签是否应被发布：选中列表同时按 Name 与 Address 匹配（不区分大小写）。
        /// 这样无论配置里存的是标签名（如 "实际速度"）还是地址（如 "DB1.DBD1012"）都能正确命中，
        /// 避免因「选中键」与「匹配键」不一致导致整批不发布。
        /// </summary>
        private bool IsTagSelected(PLCTag tag)
        {
            if (Config.PublishAllTags) return true;
            return Config.SelectedTags.Any(x =>
                string.Equals(x, tag.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x, tag.Address, StringComparison.OrdinalIgnoreCase));
        }

        private string BuildTopic(PLCTag tag)
        {
            var key = Config.TopicFormat == MqttTopicFormat.ByName
                ? (string.IsNullOrWhiteSpace(tag.Name) ? tag.Address : tag.Name)
                : tag.Address;
            return $"{Config.BaseTopic.Trim('/')}/{key}";
        }

        private void OnTagValueChanged(object sender, TagService.TagChangedEventArgs e)
        {
            if (!_isRunning || !Config.Enabled || !IsConnected) return;
            if (Config.PublishMode != MqttPublishMode.OnChange) return;
            var tag = _connection.GetTag(e.TagName);
            if (tag != null && !IsTagSelected(tag)) return;
            _ = PublishTagAsync(e.TagName, e.NewValue);
        }

        private async Task PublishTagAsync(string tagName, object value)
        {
            if (!_client.IsConnected) return;
            try
            {
                var tag = _connection.GetTag(tagName);
                var address = tag?.Address ?? tagName;
                var payload = JsonSerializer.Serialize(new
                {
                    tag = tagName,
                    address,
                    value = value?.ToString(),
                    ts = DateTime.Now.ToString("O")
                });
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic(BuildTopic(tag ?? new PLCTag { Name = tagName, Address = address }))
                    .WithPayload(Encoding.UTF8.GetBytes(payload))
                    .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)(int)Config.Qos)
                    .WithRetainFlag(Config.Retain)
                    .Build();
                await _client.PublishAsync(msg, CancellationToken.None);
                PublishedCount++;
            }
            catch (Exception ex)
            {
                LastError = $"发布 {tagName} 失败：{ex.Message}";
                Debug.WriteLine(LastError);
            }
        }

        private async Task PublishAllSelectedAsync()
        {
            if (!_isRunning || !Config.Enabled || !_client.IsConnected) return;
            var tags = _connection.GetAllTagsSnapshot();
            foreach (var tag in tags)
            {
                if (!IsTagSelected(tag)) continue;
                await PublishTagAsync(tag.Name, tag.Value);
            }
        }

        /// <summary>仅用于配置界面的「测试连接」：连接后立刻断开，不改变运行态。</summary>
        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                var testClient = _factory.CreateMqttClient();
                var opts = BuildOptions();
                await testClient.ConnectAsync(opts, CancellationToken.None);
                var ok = testClient.IsConnected;
                if (ok) await testClient.DisconnectAsync();
                return ok;
            }
            catch (Exception ex)
            {
                LastError = $"测试连接失败：{ex.Message}";
                return false;
            }
        }

        /// <summary>使用指定配置测试连接，不覆盖当前运行配置。</summary>
        public async Task<bool> TestConnectionAsync(MqttConfig testConfig)
        {
            try
            {
                var testClient = _factory.CreateMqttClient();
                var opts = BuildOptions(testConfig);
                await testClient.ConnectAsync(opts, CancellationToken.None);
                var ok = testClient.IsConnected;
                if (ok) await testClient.DisconnectAsync();
                return ok;
            }
            catch (Exception ex)
            {
                LastError = $"测试连接失败：{ex.Message}";
                return false;
            }
        }

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name) =>
            Application.Current?.Dispatcher?.Invoke(() =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stopRequested = true;
            _publishTimer?.Stop();
            _publishTimer?.Dispose();
            _connection.TagValueChanged -= OnTagValueChanged;
            try { _client.DisconnectAsync().GetAwaiter().GetResult(); } catch { /* 忽略 */ }
            _client?.Dispose();
        }
    }
}
