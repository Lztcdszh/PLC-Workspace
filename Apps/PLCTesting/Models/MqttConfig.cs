using System.Text.Json.Serialization;

namespace PLCTesting.Models
{
    /// <summary>
    /// MQTT 转发配置（极简版）。
    /// 仅保留最常用的三项：是否启用、Broker 地址/端口、主题前缀。
    /// 其余行为使用内置合理默认值：
    ///   - 匿名连接（无账号/密码、无 TLS）
    ///   - 自动生成唯一 ClientId
    ///   - QoS = 1（至少一次）、Retain = false
    ///   - 标签值变化时即发布；主题按 PLC 地址命名
    ///   - 发布全部已注册标签
    /// 可序列化为 JSON 文件（mqttconfig.json）持久化。
    /// </summary>
    public class MqttConfig
    {
        /// <summary>是否启用 MQTT 转发（开机自动连接并发布）。</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Broker 服务器地址或主机名。</summary>
        public string BrokerHost { get; set; } = "127.0.0.1";

        /// <summary>Broker 端口（默认 1883）。</summary>
        public int BrokerPort { get; set; } = 1883;

        /// <summary>主题前缀，例如 "plc" → 完整主题 "plc/DB1.DBD1012"。</summary>
        public string BaseTopic { get; set; } = "plc";
    }
}
