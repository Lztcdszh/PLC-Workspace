using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MyPLCUserControlLibrary
{
    /// <summary>
    /// 所有 PLC 用户控件的抽象基类。
    /// 集中实现「连接生命周期 / 标签订阅 / 自动注册 / 事件分发 / 资源清理 / 写入辅助 / 调试日志」
    /// 这套在每个控件中重复约 150~200 行的样板代码，子类只需关注自身可视化逻辑：
    /// <list type="bullet">
    ///   <item>override <see cref="OnPlcValueReceived"/> —— 收到 PLC 值后在 UI 线程处理（原 ProcessValue）。</item>
    ///   <item>override <see cref="CreateDefaultTag"/> —— 自动注册时创建何种类型的标签（默认 bool）。</item>
    ///   <item>override <see cref="SubscriptionTagName"/> —— 事件过滤用的状态标签名（默认 = TagName，复杂控件如 PlcButton 可覆盖）。</item>
    ///   <item>override <see cref="AutoRegisterTags"/> —— 需要注册多个地址时（如 PlcButton 的 ON/OFF/Status）。</item>
    ///   <item>override <see cref="OnConnectionChangedInternal"/> / <see cref="OnConnectionStateChanged"/> / <see cref="OnUnloadedInternal"/> —— 额外钩子。</item>
    /// </list>
    /// 公共依赖属性（Connection/TagName/TagAddress/AutoRegister）由基类提供，XAML 用法保持不变。
    /// </summary>
    public abstract class PlcControlBase : UserControl
    {
        private IConnectionManager? _connection;
        private bool _subscribed;
        private bool _isConnectionStateSubscribed;

        /// <summary>当前连接管理器（子类只读访问）。</summary>
        protected IConnectionManager? ConnectionManager => _connection;

        /// <summary>是否已订阅标签值变化事件。</summary>
        protected bool IsSubscribed => _subscribed;

        #region 依赖属性（由基类统一定义，子类直接继承）

        public static readonly DependencyProperty ConnectionProperty =
            DependencyProperty.Register(nameof(Connection), typeof(IConnectionManager), typeof(PlcControlBase),
                new PropertyMetadata(null, OnConnectionChangedStatic));

        public IConnectionManager? Connection
        {
            get => (IConnectionManager?)GetValue(ConnectionProperty);
            set => SetValue(ConnectionProperty, value);
        }

        public static readonly DependencyProperty TagNameProperty =
            DependencyProperty.Register(nameof(TagName), typeof(string), typeof(PlcControlBase),
                new PropertyMetadata(string.Empty, OnTagPropertyChangedStatic));

        /// <summary>标签名（用于订阅与自动注册）。</summary>
        public string TagName
        {
            get => (string)GetValue(TagNameProperty);
            set => SetValue(TagNameProperty, value);
        }

        public static readonly DependencyProperty TagAddressProperty =
            DependencyProperty.Register(nameof(TagAddress), typeof(string), typeof(PlcControlBase),
                new PropertyMetadata(string.Empty, OnTagPropertyChangedStatic));

        /// <summary>标签地址（仅用于自动注册时的底层地址，订阅仍使用 TagName）。</summary>
        public string TagAddress
        {
            get => (string)GetValue(TagAddressProperty);
            set => SetValue(TagAddressProperty, value);
        }

        public static readonly DependencyProperty AutoRegisterProperty =
            DependencyProperty.Register(nameof(AutoRegister), typeof(bool), typeof(PlcControlBase),
                new PropertyMetadata(true));

        /// <summary>是否允许控件在连接建立时自动把自身地址注册为监控标签。</summary>
        public bool AutoRegister
        {
            get => (bool)GetValue(AutoRegisterProperty);
            set => SetValue(AutoRegisterProperty, value);
        }

        #endregion

        #region 构造

        protected PlcControlBase()
        {
            Unloaded += OnUnloaded;
        }

        #endregion

        #region 属性变更回调（静态分发到实例虚方法）

        private static void OnConnectionChangedStatic(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PlcControlBase ctrl)
                ctrl.OnConnectionChanged((IConnectionManager?)e.OldValue, (IConnectionManager?)e.NewValue);
        }

        private static void OnTagPropertyChangedStatic(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PlcControlBase ctrl)
                ctrl.OnTagChanged();
        }

        #endregion

        #region 连接与订阅（共享实现）

        private void OnConnectionChanged(IConnectionManager? oldConn, IConnectionManager? newConn)
        {
            if (DesignerProperties.GetIsInDesignMode(this)) return;

            LogDebug(nameof(OnConnectionChanged), $"旧={oldConn?.GetHashCode()}, 新={newConn?.GetHashCode()}");

            if (oldConn != null)
            {
                Unsubscribe();
                UnsubscribeConnectionState();
            }

            _connection = newConn;

            if (_connection != null)
            {
                AutoRegisterTags();
                Subscribe();
                SubscribeConnectionState();
            }

            OnConnectionChangedInternal(oldConn, newConn);
        }

        /// <summary>连接建立/更换后的额外处理钩子（子类可选 override）。</summary>
        protected virtual void OnConnectionChangedInternal(IConnectionManager? oldConn, IConnectionManager? newConn) { }

        private void SubscribeConnectionState()
        {
            if (_connection == null || _isConnectionStateSubscribed) return;
            if (_connection is INotifyPropertyChanged notify)
            {
                notify.PropertyChanged += Connection_PropertyChanged;
                _isConnectionStateSubscribed = true;
                LogDebug(nameof(SubscribeConnectionState), "订阅连接状态");
            }
        }

        private void UnsubscribeConnectionState()
        {
            if (_connection != null && _isConnectionStateSubscribed)
            {
                if (_connection is INotifyPropertyChanged notify)
                {
                    notify.PropertyChanged -= Connection_PropertyChanged;
                    _isConnectionStateSubscribed = false;
                    LogDebug(nameof(UnsubscribeConnectionState), "取消订阅连接状态");
                }
            }
        }

        private void Connection_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(IConnectionManager.IsConnected) && e.PropertyName != "IsConnected")
                return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => Connection_PropertyChanged(sender, e));
                return;
            }

            bool isConnected = _connection?.IsConnected ?? false;
            LogDebug(nameof(Connection_PropertyChanged), $"IsConnected={isConnected}");
            OnConnectionStateChanged(isConnected);
        }

        /// <summary>连接状态变化钩子（默认无操作，子类可选 override）。</summary>
        protected virtual void OnConnectionStateChanged(bool isConnected) { }

        protected void Subscribe()
        {
            if (_connection == null || _subscribed || !ShouldSubscribe) return;
            try
            {
                _connection.TagValueChanged += Connection_TagValueChanged;
                _subscribed = true;
                LogDebug(nameof(Subscribe), "订阅标签值变化成功");

                // 初始值同步：若标签已被读取且当前值非空，立即处理一次，
                // 避免控件加载晚于首次轮询时永远收不到 TagValueChanged。
                string? subTagInit = SubscriptionTagName;
                if (!string.IsNullOrWhiteSpace(subTagInit))
                {
                    try
                    {
                        var tagInit = _connection.GetTag(subTagInit);
                        if (tagInit?.Value != null)
                        {
                            Dispatcher.BeginInvoke(() => OnPlcValueReceived(tagInit.Value));
                            LogDebug(nameof(Subscribe), $"初始值同步: {subTagInit} = {tagInit.Value}");
                        }
                    }
                    catch (Exception exInit)
                    {
                        LogDebug(nameof(Subscribe), $"初始值同步失败: {exInit.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug(nameof(Subscribe), $"失败: {ex.Message}");
            }
        }

        protected void Unsubscribe()
        {
            if (_connection != null && _subscribed)
            {
                try
                {
                    _connection.TagValueChanged -= Connection_TagValueChanged;
                    _subscribed = false;
                    LogDebug(nameof(Unsubscribe), "取消订阅标签值变化");
                }
                catch (Exception ex)
                {
                    LogDebug(nameof(Unsubscribe), $"失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 全局标签值变化回调：仅当标签名匹配 <see cref="SubscriptionTagName"/> 时，
        /// 封送到 UI 线程并调用 <see cref="OnPlcValueReceived"/>。
        /// </summary>
        private void Connection_TagValueChanged(object? sender, TagService.TagChangedEventArgs e)
        {
            string? subTag = SubscriptionTagName;
            if (string.IsNullOrWhiteSpace(subTag)) return;
            if (e?.TagName?.Equals(subTag, StringComparison.OrdinalIgnoreCase) != true) return;

            Dispatcher.BeginInvoke(() =>
            {
                LogDebug(nameof(Connection_TagValueChanged), $"收到值: {e.NewValue}");
                OnPlcValueReceived(e.NewValue);
            });
        }

        /// <summary>事件过滤所用的状态标签名。默认 = <see cref="TagName"/>；复杂控件（如 PlcButton）应覆盖为实际状态标签。</summary>
        protected virtual string? SubscriptionTagName => TagName;

        /// <summary>是否应订阅标签值变化（默认 true）。只读/只写等场景可覆盖，例如只写模式返回 false。</summary>
        protected virtual bool ShouldSubscribe => true;

        /// <summary>收到匹配标签值后在 UI 线程调用（替代各控件的 ProcessValue）。</summary>
        protected abstract void OnPlcValueReceived(object? value);

        #endregion

        #region 自动注册（共享框架，标签类型由子类决定）

        /// <summary>
        /// 自动注册标签。基类默认注册由 <see cref="CreateDefaultTag"/> 返回的单个标签；
        /// 需要注册多个地址（如 ON/OFF/Status）的控件应覆盖本方法自行注册。
        /// </summary>
        protected virtual void AutoRegisterTags()
        {
            if (_connection == null || !AutoRegister) return;

            PLCTag? tag = CreateDefaultTag();
            if (tag == null) return;

            tag.IsMonitored = IsMonitorTag;
            if (!ContainsTagSafe(tag.Name))
            {
                try
                {
                    _connection.AddTag(tag);
                    LogDebug(nameof(AutoRegisterTags), $"自动注册: {tag.Name} @ {tag.Address}");
                    if (string.IsNullOrWhiteSpace(TagName))
                        TagName = tag.Name;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[{GetType().Name}] 自动注册失败: {ex.Message}");
                }
            }
        }

        /// <summary>自动注册时创建的默认标签（默认 bool 类型）。子类按自身数据类型覆盖。</summary>
        protected virtual PLCTag? CreateDefaultTag()
        {
            if (string.IsNullOrWhiteSpace(TagAddress)) return null;
            string effectiveName = string.IsNullOrWhiteSpace(TagName) ? TagAddress : TagName;
            return PLCTag.CreateBool(effectiveName, TagAddress, effectiveName);
        }

        /// <summary>自动注册标签是否参与监控轮询（默认 true）。</summary>
        protected virtual bool IsMonitorTag => true;

        /// <summary>标签名是否存在于连接的标签表中（线程安全包装）。</summary>
        protected bool ContainsTagSafe(string tagName)
        {
            if (_connection == null) return false;
            try
            {
                return _connection.GetTag(tagName) != null;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region 标签变更处理（共享）

        /// <summary>TagName/TagAddress 变更时：取消原订阅，按新标签重新注册并订阅。</summary>
        protected void RefreshSubscriptionForTagChange()
        {
            if (DesignerProperties.GetIsInDesignMode(this)) return;

            Unsubscribe();
            if (_connection == null || string.IsNullOrWhiteSpace(TagName))
                return;

            AutoRegisterTags();
            Subscribe();
        }

        /// <summary>子类在 TagName/TagAddress 变更时的额外处理（在 RefreshSubscriptionForTagChange 之后调用）。</summary>
        protected virtual void OnTagChanged()
        {
            RefreshSubscriptionForTagChange();
        }

        #endregion

        #region 写入辅助（共享，带信号量与连接检查）

        private readonly SemaphoreSlim _writeSemaphore = new SemaphoreSlim(1, 1);
        private bool _isWriting;

        /// <summary>
        /// 线程安全的标签写入：检查连接与已连接状态，使用单一信号量串行化写入。
        /// 返回是否写入成功。子类写入统一走本方法，避免重复实现信号量逻辑。
        /// </summary>
        protected async Task<bool> WriteTagAsync(string? tagName, object value)
        {
            if (_connection == null)
            {
                LogDebug(nameof(WriteTagAsync), "连接为空，无法写入");
                return false;
            }
            if (string.IsNullOrWhiteSpace(tagName))
            {
                LogDebug(nameof(WriteTagAsync), "标签名为空，无法写入");
                return false;
            }
            if (!_connection.IsConnected)
            {
                LogDebug(nameof(WriteTagAsync), "PLC 未连接，无法写入");
                return false;
            }

            await _writeSemaphore.WaitAsync();
            try
            {
                if (_isWriting) return false;
                _isWriting = true;

                LogDebug(nameof(WriteTagAsync), $"尝试写入 {value} 到 '{tagName}'");
                bool success = await _connection.WriteTagAsync(tagName, value);
                LogDebug(nameof(WriteTagAsync), success ? "写入成功" : "写入失败");
                return success;
            }
            catch (Exception ex)
            {
                LogDebug(nameof(WriteTagAsync), $"异常: {ex.Message}");
                return false;
            }
            finally
            {
                _isWriting = false;
                _writeSemaphore.Release();
            }
        }

        #endregion

        #region 资源清理（共享）

        private void OnUnloaded(object? sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this)) return;

            LogDebug(nameof(OnUnloaded), "控件卸载，清理资源");
            Unsubscribe();
            UnsubscribeConnectionState();
            OnUnloadedInternal();
        }

        /// <summary>控件卸载时的额外清理钩子（子类可选 override，如停止定时器）。</summary>
        protected virtual void OnUnloadedInternal() { }

        #endregion

        #region 调试辅助（共享）

        [Conditional("DEBUG")]
        protected void LogDebug(string method, string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var threadId = Thread.CurrentThread.ManagedThreadId;
            Debug.WriteLine($"[{GetType().Name}] {timestamp} [T{threadId}] {method} - {message}");
        }

        #endregion
    }
}
