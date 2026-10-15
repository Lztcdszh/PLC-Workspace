using System.Text.Json.Serialization;

namespace PLCTesting.Models
{
    /// <summary>MQTT 发布方式。</summary>
    public enum MqttPublishMode
    {
        /// <summary>仅在标签值发生变化时发布（变化触发）。</summary>
        OnChange = 0,

        /// <summary>按固定周期定时发布：值未变化也会重发，用于心跳/刷新监视端数据。</summary>
        Periodic = 1,

        /// <summary>变化即发 + 定时周期发布，两者同时生效（推荐）。</summary>
        OnChangeAndPeriodic = 2
    }

    /// <summary>
    /// MQTT 转发配置（极简版）。
    /// 仅保留最常用的几项：是否启用、Broker 地址/端口、主题前缀、发布方式与定时周期。
    /// 其余行为使用内置合理默认值：
    ///   - 匿名连接（无账号/密码、无 TLS）
    ///   - 自动生成唯一 ClientId
    ///   - QoS = 1（至少一次）、Retain = false
    ///   - 主题按 PLC 地址命名
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

        /// <summary>
        /// 发布方式：变化时 / 定时 / 两者都要。
        /// 默认「变化时 + 定时」，保证监视端既有实时性又有刷新节拍。
        /// </summary>
        public MqttPublishMode PublishMode { get; set; } = MqttPublishMode.OnChangeAndPeriodic;

        /// <summary>定时发布的周期（毫秒），有效范围 200 ~ 3600000。</summary>
        public int PublishIntervalMs { get; set; } = 1000;

        #region 派生属性（不参与序列化，仅便于代码内判断）

        /// <summary>是否启用「变化时发布」。</summary>
        [JsonIgnore]
        public bool IsOnChange =>
            PublishMode == MqttPublishMode.OnChange ||
            PublishMode == MqttPublishMode.OnChangeAndPeriodic;

        /// <summary>是否启用「定时发布」。</summary>
        [JsonIgnore]
        public bool IsPeriodic =>
            PublishMode == MqttPublishMode.Periodic ||
            PublishMode == MqttPublishMode.OnChangeAndPeriodic;

        /// <summary>定时周期（毫秒），已做上下限收敛。</summary>
        [JsonIgnore]
        public int EffectiveIntervalMs =>
            PublishIntervalMs < 200 ? 200 : (PublishIntervalMs > 3_600_000 ? 3_600_000 : PublishIntervalMs);

        #endregion
    }
}
