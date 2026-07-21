using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PLCTesting.Models
{
    /// <summary>MQTT 服务质量等级（数值与 MQTTnet 协议一致）。</summary>
    public enum MqttQosLevel
    {
        AtMostOnce = 0,
        AtLeastOnce = 1,
        ExactlyOnce = 2
    }

    /// <summary>发布模式：仅在值变化时发布，或周期性发布全部选中标签。</summary>
    public enum MqttPublishMode
    {
        OnChange = 0,
        Periodic = 1
    }

    /// <summary>主题命名方式：使用 PLC 地址（稳定，无中文）或标签名称。</summary>
    public enum MqttTopicFormat
    {
        ByAddress = 0,
        ByName = 1
    }

    /// <summary>
    /// MQTT 转发配置，可序列化为 JSON 文件（mqttconfig.json）持久化。
    /// </summary>
    public class MqttConfig
    {
        /// <summary>是否启用 MQTT 转发（开机自动连接并发布）。</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Broker 服务器地址或主机名。</summary>
        public string BrokerHost { get; set; } = "127.0.0.1";

        /// <summary>Broker 端口（1883 非加密 / 8883 TLS）。</summary>
        public int BrokerPort { get; set; } = 1883;

        /// <summary>客户端标识，Broker 端需唯一。</summary>
        public string ClientId { get; set; } = "PLCTestingClient";

        /// <summary>用户名（留空表示匿名）。</summary>
        public string Username { get; set; } = "";

        /// <summary>密码。</summary>
        public string Password { get; set; } = "";

        /// <summary>是否使用 TLS 加密连接。</summary>
        public bool UseTls { get; set; } = false;

        /// <summary>主题前缀，例如 "plc" → 完整主题 "plc/DB1.DBD1012"。</summary>
        public string BaseTopic { get; set; } = "plc";

        /// <summary>发布服务质量等级。</summary>
        public MqttQosLevel Qos { get; set; } = MqttQosLevel.AtLeastOnce;

        /// <summary>是否保留最后一条消息（Retain）。</summary>
        public bool Retain { get; set; } = false;

        /// <summary>发布模式：变更发布 / 周期发布。</summary>
        public MqttPublishMode PublishMode { get; set; } = MqttPublishMode.OnChange;

        /// <summary>周期发布间隔（毫秒），仅 PublishMode=Periodic 生效。</summary>
        public int PublishIntervalMs { get; set; } = 1000;

        /// <summary>主题命名方式。</summary>
        public MqttTopicFormat TopicFormat { get; set; } = MqttTopicFormat.ByAddress;

        /// <summary>发布全部已注册标签（为 true 时忽略 SelectedTags）。</summary>
        public bool PublishAllTags { get; set; } = true;

        /// <summary>指定发布的标签名称集合（PublishAllTags=false 时生效）。</summary>
        public List<string> SelectedTags { get; set; } = new();
    }
}
