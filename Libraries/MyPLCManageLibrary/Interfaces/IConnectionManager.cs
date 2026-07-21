using MyPLCManagerLibrary.Model;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace MyPLCManagerLibrary.Interfaces
{
    /// <summary>
    /// PLC 连接状态枚举
    /// </summary>
    

    /// <summary>
    /// PLC 连接管理器接口，定义连接生命周期、标签操作、引用计数及监控的完整契约。
    /// </summary>
    public interface IConnectionManager : IDisposable, INotifyPropertyChanged
    {
        #region 基础信息

        /// <summary>
        /// 连接实例名称
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 连接配置信息
        /// </summary>
        PLCConfig ConnectionConfig { get; }

        #endregion

        #region 连接状态

        /// <summary>
        /// 当前是否已成功连接
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 当前连接状态
        /// </summary>
        ConnectionState ConnectionStatus { get; }

        /// <summary>
        /// 是否正在连接中
        /// </summary>
        bool IsConnecting { get; }
        /// <summary>
        /// 连接状态文本描述
        /// </summary>
        string ConnectionStatusText { get; }

        /// <summary>
        /// 连接状态颜色代码
        /// </summary>
        string ConnectionStatusColor { get; }

        /// <summary>
        /// 连接状态图标
        /// </summary>
        string StatusIcon { get; }
        #endregion

        #region 引用计数管理

        /// <summary>
        /// 增加引用计数
        /// </summary>
        void AddReference();

        /// <summary>
        /// 减少引用计数，当计数归零时自动释放资源
        /// </summary>
        void RemoveReference();

        /// <summary>
        /// 当前引用计数
        /// </summary>
        int ReferenceCount { get; }

        #endregion

        #region 连接操作

        /// <summary>
        /// 异步建立 PLC 连接
        /// </summary>
        Task<bool> ConnectAsync();

        /// <summary>
        /// 异步断开 PLC 连接
        /// </summary>
        Task DisconnectAsync();

        #endregion

        #region 标签管理

        /// <summary>
        /// 添加一个标签到监控列表
        /// </summary>
        void AddTag(PLCTag tag);

        /// <summary>
        /// 根据名称获取标签
        /// </summary>
        PLCTag GetTag(string name);

        /// <summary>
        /// 获取所有标签的数组副本（直接引用，非线程安全快照）
        /// </summary>
        PLCTag[] GetAllTags();

        /// <summary>
        /// 获取所有标签的线程安全快照（独立副本）
        /// </summary>
        /// <returns>标签列表副本</returns>
        List<PLCTag> GetAllTagsSnapshot();

        /// <summary>
        /// 判断指定名称的标签是否存在
        /// </summary>
        bool ContainsTag(string tagName);

        /// <summary>
        /// 当前管理的标签数量
        /// </summary>
        int TagCount { get; }

        /// <summary>
        /// 是否存在任何标签
        /// </summary>
        bool HasTags { get; }

        #endregion

        #region 标签读写

        /// <summary>
        /// 异步读取指定标签的值
        /// </summary>
        Task<object> ReadTagAsync(string tagName);

        /// <summary>
        /// 异步写入指定标签的值
        /// </summary>
        Task<bool> WriteTagAsync(string tagName, object value);

        /// <summary>
        /// 异步批量读取多个标签（串行读取）
        /// </summary>
        Task<bool> ReadMultipleTagsAsync(params string[] tagNames);

        #endregion

        #region 监控

        /// <summary>
        /// 开始周期性刷新所有标签的值
        /// </summary>
        /// <param name="interval">刷新间隔（毫秒）</param>
        void StartMonitoring(int interval = 500);

        /// <summary>
        /// 停止周期性刷新
        /// </summary>
        void StopMonitoring();

        /// <summary>
        /// 标签值变化事件
        /// </summary>
        event EventHandler<TagService.TagChangedEventArgs> TagValueChanged;

        #endregion

        #region 日志

        /// <summary>
        /// 日志输出事件
        /// </summary>
        event EventHandler<string> LogMessage;

        #endregion
    }
}