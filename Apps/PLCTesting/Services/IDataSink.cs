using PLCTesting.Models;
using System.Threading.Tasks;

namespace PLCTesting.Services
{
    /// <summary>
    /// 变化日志写入抽象：解耦"数据接入"与具体存储（SQLite / 文件 / 其他数据库）。
    /// 任何实现了该接口的服务都能作为日志落盘目标，DataIngestionService 不感知具体实现。
    /// </summary>
    public interface IChangeLogSink
    {
        /// <summary>写入一条变化记录。</summary>
        void Record(ChangeLogRecord record);
    }

    /// <summary>
    /// 消息发布抽象：解耦"数据接入"与具体消息中间件（MQTT / HTTP / 串口 等）。
    /// DataIngestionService 只调用该接口，不知道底层是 MQTTnet 还是别的。
    /// </summary>
    public interface IMessagePublisher
    {
        /// <summary>
        /// 发布一条数据点。
        /// </summary>
        /// <param name="name">数据点名称</param>
        /// <param name="address">主题末段 / 路由键</param>
        /// <param name="value">任意值</param>
        /// <param name="description">业务描述</param>
        /// <param name="dataType">类型字符串</param>
        Task PublishAsync(string name, string address, object value, string description, string dataType);
    }

    /// <summary>
    /// 数据点来源抽象：解耦"数据处理服务"与具体的数据点注册实现。
    /// PlcChangeLogger / MqttPublisherService 只通过该接口查询"哪些点要入库/发布"及元数据，
    /// 不依赖具体的 DataPointRegistry（可替换为数据库驱动、远程配置等）。
    /// </summary>
    public interface IDataPointSource
    {
        /// <summary>已配置的数据点集合（只读）。</summary>
        System.Collections.Generic.IReadOnlyList<DataPointConfig> Points { get; }

        /// <summary>该点是否应写入数据库。</summary>
        bool ShouldLog(string name);

        /// <summary>该点是否应发布到消息中间件。</summary>
        bool ShouldPublish(string name);

        /// <summary>按名称或地址查找数据点配置（不区分大小写）。</summary>
        bool TryGet(string name, out DataPointConfig? config);
    }
}
