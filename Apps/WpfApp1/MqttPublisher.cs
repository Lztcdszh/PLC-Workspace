using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace WpfApp1
{
    /// <summary>
    /// MQTT 发布客户端（仅发布）：将当前点云状态以 JSON 推送到指定主题。
    /// 基于 MQTTnet 4.3。
    /// </summary>
    public class MqttPublisher : IDisposable
    {
        private readonly MqttFactory _factory = new MqttFactory();
        private IMqttClient? _client;
        private string _topic = "wpfapp1/pointcloud/status";

        public bool IsConnected => _client != null && _client.IsConnected;

        /// <summary>状态变化回调（连接/断开/错误），字符串为可读消息</summary>
        public event Action<string>? StatusChanged;

        public async Task ConnectAsync(string host, int port, string clientId)
        {
            _client ??= _factory.CreateMqttClient();

            _client.ConnectedAsync += e =>
            {
                StatusChanged?.Invoke("已连接到 MQTT Broker");
                return Task.CompletedTask;
            };
            _client.DisconnectedAsync += e =>
            {
                StatusChanged?.Invoke("已断开 MQTT 连接");
                return Task.CompletedTask;
            };

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(host, port)
                .WithClientId(string.IsNullOrWhiteSpace(clientId) ? $"WpfApp1_{Guid.NewGuid():N}" : clientId)
                .Build();

            await _client.ConnectAsync(options, CancellationToken.None);
            StatusChanged?.Invoke($"连接成功: {host}:{port}");
        }

        public async Task DisconnectAsync()
        {
            if (_client != null && _client.IsConnected)
            {
                await _client.DisconnectAsync();
            }
            StatusChanged?.Invoke("已断开");
        }

        /// <summary>
        /// 发布 JSON 负载到当前主题。
        /// </summary>
        public async Task PublishAsync(string json, string? topic = null)
        {
            if (_client == null || !_client.IsConnected)
                throw new InvalidOperationException("MQTT 未连接");

            string useTopic = topic ?? _topic;
            if (string.IsNullOrWhiteSpace(useTopic))
                throw new InvalidOperationException("未指定发布主题");

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(useTopic)
                .WithPayload(json)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            await _client.PublishAsync(message, CancellationToken.None);
            StatusChanged?.Invoke($"已发布到主题: {useTopic}");
        }

        public void SetTopic(string topic)
        {
            if (!string.IsNullOrWhiteSpace(topic)) _topic = topic;
        }

        public void Dispose()
        {
            try
            {
                if (_client != null && _client.IsConnected)
                {
                    _client.DisconnectAsync().GetAwaiter().GetResult();
                }
                _client?.Dispose();
            }
            catch { }
        }
    }
}
