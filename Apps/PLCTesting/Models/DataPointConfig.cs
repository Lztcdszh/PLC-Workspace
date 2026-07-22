namespace PLCTesting.Models
{
    /// <summary>
    /// 数据点配置：描述一个要"记录到数据库 / 发布到 MQTT"的数据点。
    /// 放在 dataPoints.json 中，新增任何数据只需加一行配置，无需改代码。
    /// </summary>
    public class DataPointConfig
    {
        /// <summary>数据点名称（同时作为 PLC 标签名，也是变化事件的匹配键）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>PLC 地址，例如 DB1.DBD1012 / DB1.DBW230</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>数据类型字符串：Bool/Float/Int/Int16/Word/UInt8/DWord/Double/String</summary>
        public string DataType { get; set; } = "Float";

        /// <summary>业务描述：写入数据库 Description 列，并随 MQTT 负载发出</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>是否记录到 SQLite 变化日志</summary>
        public bool LogToDb { get; set; } = true;

        /// <summary>是否发布到 MQTT</summary>
        public bool PublishMqtt { get; set; } = true;
    }
}
