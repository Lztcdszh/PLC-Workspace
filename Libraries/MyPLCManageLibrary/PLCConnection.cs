using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using System.Windows;

namespace MyPLCManagerLibrary
{
    public class PLCConnection : IConnectionManager
    {
        private static readonly ConcurrentDictionary<string, PLCConnection> _connections = new();

        private readonly PLCService _plcService;
        private readonly TagService _tagService;
        private bool _disposed = false;
        private int _referenceCount = 0;
        private readonly object _lock = new object();

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<string> LogMessage;

        public PLCConfig ConnectionConfig { get; }
        public string Name => ConnectionConfig.Name;

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                if (_isConnected != value)
                {
                    _isConnected = value;
                    ExecuteOnUI(() =>
                    {
                        OnPropertyChanged(nameof(IsConnected));
                        OnPropertyChanged(nameof(ConnectionStatusText));
                        OnPropertyChanged(nameof(ConnectionStatusColor));
                        OnPropertyChanged(nameof(StatusIcon));
                    });
                    AddLog(value ? "连接成功" : "连接断开");
                }
            }
        }

        private ConnectionState _connectionState = ConnectionState.Disconnected;
        public ConnectionState ConnectionStatus
        {
            get => _connectionState;
            private set
            {
                if (_connectionState != value)
                {
                    _connectionState = value;
                    ExecuteOnUI(() =>
                    {
                        OnPropertyChanged(nameof(ConnectionStatus));
                        OnPropertyChanged(nameof(ConnectionStatusText));
                        OnPropertyChanged(nameof(ConnectionStatusColor));
                        OnPropertyChanged(nameof(StatusIcon));
                    });
                }
            }
        }

        public string ConnectionStatusText => ConnectionStatus switch
        {
            ConnectionState.Disconnected => "未连接",
            ConnectionState.Connecting => "连接中...",
            ConnectionState.Connected => "已连接",
            ConnectionState.Error => "连接错误",
            _ => "未知"
        };

        public string ConnectionStatusColor => ConnectionStatus switch
        {
            ConnectionState.Disconnected => "#dc3545",
            ConnectionState.Connecting => "#ffc107",
            ConnectionState.Connected => "#28a745",
            ConnectionState.Error => "#dc3545",
            _ => "#6c757d"
        };

        public string StatusIcon => ConnectionStatus switch
        {
            ConnectionState.Connected => "🔗",
            ConnectionState.Connecting => "⏳",
            ConnectionState.Disconnected => "❌",
            ConnectionState.Error => "⚠️",
            _ => "❓"
        };

        public bool IsConnecting => ConnectionStatus == ConnectionState.Connecting;

        private PLCConnection(PLCConfig config)
        {
            ConnectionConfig = config ?? throw new ArgumentNullException(nameof(config));
            _plcService = new PLCService(config);
            _tagService = new TagService(_plcService);

            _plcService.LogMessage += OnPLCLogMessage;
            _plcService.PropertyChanged += OnPLCPropertyChanged;

            AddLog($"PLC连接实例已创建: {Name} ({config.IP})");
        }

        #region 工厂方法 - 单例模式
        public static PLCConnection GetOrCreate(PLCConfig config)
        {
            var key = GenerateKey(config);
            return _connections.GetOrAdd(key, k =>
            {
                var connection = new PLCConnection(config);
                connection.AddLog($"创建新的PLC连接: {key}");
                _ = connection.ConnectAsync();
                return connection;
            });
        }

        public static PLCConnection GetOrCreate(string ip, string name = "S7-200 SMART", short rack = 0, short slot = 1)
        {
            var config = new PLCConfig
            {
                Name = name,
                IP = ip,
                Rack = rack,
                Slot = slot,
                AutoReconnect = true,
                MaxReconnectAttempts = 10,
                ReconnectDelayMs = 3000,
                Timeout = 5000
            };
            return GetOrCreate(config);
        }

        private static string GenerateKey(PLCConfig config) => $"{config.IP}_{config.Rack}_{config.Slot}";
        #endregion

        #region 引用计数管理
        public void AddReference()
        {
            lock (_lock)
            {
                _referenceCount++;
                AddLog($"增加引用计数，当前: {_referenceCount}");
                OnPropertyChanged(nameof(ReferenceCount));
            }
        }

        public void RemoveReference()
        {
            lock (_lock)
            {
                if (_referenceCount > 0)
                {
                    _referenceCount--;
                    AddLog($"减少引用计数，当前: {_referenceCount}");
                    OnPropertyChanged(nameof(ReferenceCount));

                    if (_referenceCount <= 0)
                    {
                        DisposeInternal();
                    }
                }
            }
        }

        public int ReferenceCount => _referenceCount;
        #endregion

        #region PLC连接管理
        public async Task<bool> ConnectAsync()
        {
            try
            {
                if (IsConnected)
                {
                    AddLog("PLC已经连接");
                    return true;
                }

                ExecuteOnUI(() => ConnectionStatus = ConnectionState.Connecting);
                AddLog($"正在连接PLC: {Name} ({ConnectionConfig.IP})");

                var result = await _plcService.ConnectAsync().ConfigureAwait(false);

                await ExecuteOnUIAsync(async () =>
                {
                    if (result)
                    {
                        IsConnected = true;
                        ConnectionStatus = ConnectionState.Connected;
                        AddLog("PLC连接成功");

                        // 启动标签监控（首轮会自动全量更新）
                        _tagService.StartMonitoring(200);

                        // 强制刷新所有标签 UI，确保立即显示（包括0值）
                        foreach (var tag in _tagService.Tags)
                        {
                            tag.RefreshUI();
                        }
                    }
                    else
                    {
                        AddLog("PLC连接失败");
                        ConnectionStatus = ConnectionState.Error;
                    }
                });

                return result;
            }
            catch (Exception ex)
            {
                AddLog($"连接PLC错误: {ex.Message}");
                ExecuteOnUI(() => ConnectionStatus = ConnectionState.Error);
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            try
            {
                if (!IsConnected)
                {
                    AddLog("PLC未连接");
                    return;
                }

                AddLog("正在断开PLC连接...");
                await _plcService.DisconnectAsync();

                ExecuteOnUI(() =>
                {
                    IsConnected = false;
                    ConnectionStatus = ConnectionState.Disconnected;
                });

                try { _tagService?.StopMonitoring(); } catch { }

                AddLog("PLC已断开");
            }
            catch (Exception ex)
            {
                AddLog($"断开连接错误: {ex.Message}");
                ExecuteOnUI(() => ConnectionStatus = ConnectionState.Error);
            }
        }
        #endregion

        #region 标签操作
        public void AddTag(PLCTag tag)
        {
            _tagService.AddTag(tag);
            AddLog($"添加标签: {tag.Name} ({tag.Address})");
        }

        public PLCTag GetTag(string name) => _tagService.GetTag(name);

        public List<PLCTag> GetAllTagsSnapshot()
        {
            var list = new List<PLCTag>();
            try
            {
                foreach (var t in _tagService.Tags)
                {
                    PLCTag copy = null;
                    var dt = t.DataType;
                    if (dt == typeof(bool))
                        copy = PLCTag.CreateBool(t.Name, t.Address, t.Description);
                    else if (dt == typeof(float))
                        copy = PLCTag.CreateFloat(t.Name, t.Address, t.Description);
                    else if (dt == typeof(int))
                        copy = PLCTag.CreateInt(t.Name, t.Address, t.Description);
                    else if (dt == typeof(short))
                        copy = PLCTag.CreateShort(t.Name, t.Address, t.Description);
                    else if (dt == typeof(ushort))
                        copy = PLCTag.CreateWord(t.Name, t.Address, t.Description);
                    else if (dt == typeof(byte))
                        copy = PLCTag.CreateByte(t.Name, t.Address, t.Description);
                    else if (dt == typeof(uint))
                        copy = PLCTag.CreateDWord(t.Name, t.Address, t.Description);
                    else if (dt == typeof(double))
                        copy = PLCTag.CreateDouble(t.Name, t.Address, t.Description);
                    else if (dt == typeof(string))
                        copy = PLCTag.CreateString(t.Name, t.Address, 256, t.Description);
                    else
                        copy = PLCTag.Create<object>(t.Name, t.Address, t.Value, t.Description);

                    if (copy != null)
                    {
                        try { copy.Value = t.Value; } catch { }
                        list.Add(copy);
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"获取标签快照失败: {ex.Message}");
                return new List<PLCTag>();
            }
            return list;
        }

        public PLCTag[] GetAllTags()
        {
            try { return _tagService.Tags.ToArray(); }
            catch { return Array.Empty<PLCTag>(); }
        }

        public bool ContainsTag(string tagName) => _tagService.GetTag(tagName) != null;
        public int TagCount => _tagService.Tags.Count;
        public bool HasTags => TagCount > 0;

        public async Task<object> ReadTagAsync(string tagName)
        {
            try
            {
                if (!IsConnected)
                {
                    AddLog("PLC未连接");
                    return null;
                }

                var tag = _tagService.GetTag(tagName);
                if (tag == null)
                {
                    AddLog($"标签 {tagName} 不存在");
                    return null;
                }

                var success = await _tagService.ReadTagAsync(tag);
                return success ? tag.Value : null;
            }
            catch (Exception ex)
            {
                AddLog($"读取标签 {tagName} 失败: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> WriteTagAsync(string tagName, object value)
        {
            try
            {
                if (!IsConnected)
                {
                    AddLog("PLC未连接");
                    return false;
                }

                var tag = _tagService.GetTag(tagName);
                if (tag == null)
                {
                    AddLog($"标签 {tagName} 不存在");
                    return false;
                }

                var result = await _tagService.WriteTagAsync(tag, value);
                if (result) AddLog($"写入标签 {tagName} 成功: {value}");
                else AddLog($"写入标签 {tagName} 失败");
                return result;
            }
            catch (Exception ex)
            {
                AddLog($"写入标签 {tagName} 失败: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> ReadMultipleTagsAsync(params string[] tagNames)
        {
            bool allSuccess = true;
            foreach (var tagName in tagNames)
            {
                var result = await ReadTagAsync(tagName);
                if (result == null) allSuccess = false;
            }
            return allSuccess;
        }

        public void StartMonitoring(int interval = 500) => _tagService.StartMonitoring(interval);
        public void StopMonitoring() => _tagService.StopMonitoring();
        #endregion

        #region 事件处理
        private void OnPLCLogMessage(object sender, string message)
        {
            try { LogMessage?.Invoke(this, message); }
            catch (Exception ex) { Debug.WriteLine($"OnPLCLogMessage 事件异常: {ex}"); }
        }

        private void OnPLCPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PLCService.IsConnected))
            {
                bool newIsConnected = _plcService.IsConnected;
                ExecuteOnUI(() =>
                {
                    bool oldIsConnected = IsConnected;
                    IsConnected = newIsConnected;
                    ConnectionStatus = newIsConnected ? ConnectionState.Connected : ConnectionState.Disconnected;

                    if (!oldIsConnected && newIsConnected)
                    {
                        AddLog("连接状态从断开变为成功，强制刷新所有标签 UI");
                        foreach (var tag in _tagService.Tags)
                        {
                            tag.RefreshUI();
                        }

                        // ✅ 重新启动标签监控循环
                        _tagService.StartMonitoring(200);
                    }
                });
            }
            else if (e.PropertyName == nameof(PLCService.Status))
            {
                ExecuteOnUI(() =>
                {
                    var status = _plcService.Status;
                    // 连接真值优先：底层已连接时，不因瞬时 Status 文本（如重试中出现的
                    // "连接中/重连中"）把枚举降级为 Connecting，避免"已连接却显示连接中"。
                    if (status == "已连接")
                        ConnectionStatus = ConnectionState.Connected;
                    else if (!_plcService.IsConnected && (status.Contains("连接中") || status.Contains("重连")))
                        ConnectionStatus = ConnectionState.Connecting;
                });
            }
        }

        public event EventHandler<TagService.TagChangedEventArgs> TagValueChanged
        {
            add { _tagService.TagValueChanged += value; }
            remove { _tagService.TagValueChanged -= value; }
        }
        #endregion

        #region 辅助方法
        private void ExecuteOnUI(Action action)
        {
            if (action == null) return;
            try
            {
                if (Application.Current == null || Application.Current.Dispatcher == null)
                {
                    action();
                    return;
                }

                if (Application.Current.Dispatcher.CheckAccess())
                    action();
                else
                    Application.Current.Dispatcher.Invoke(action);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ExecuteOnUI 异常: {ex.Message}");
            }
        }

        private async Task ExecuteOnUIAsync(Func<Task> asyncAction)
        {
            if (asyncAction == null) return;
            try
            {
                if (Application.Current == null || Application.Current.Dispatcher == null)
                {
                    await asyncAction();
                    return;
                }

                if (Application.Current.Dispatcher.CheckAccess())
                    await asyncAction();
                else
                    await Application.Current.Dispatcher.Invoke(async () => await asyncAction());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ExecuteOnUIAsync 异常: {ex.Message}");
            }
        }

        private void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var logMessage = $"[{Name}] {timestamp} - {message}";
            Debug.WriteLine(logMessage);
            try { LogMessage?.Invoke(this, logMessage); }
            catch (Exception ex) { Debug.WriteLine($"事件处理异常: {ex}"); }
        }

        private void OnPropertyChanged(string propertyName)
        {
            try { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)); }
            catch (Exception ex) { Debug.WriteLine($"PropertyChanged 事件异常: {ex}"); }
        }

        private void DisposeInternal()
        {
            if (_disposed) return;
            AddLog("开始释放PLC连接资源...");
            try
            {
                var key = GenerateKey(ConnectionConfig);
                _connections.TryRemove(key, out _);
                _tagService?.StopMonitoring();
                _plcService.LogMessage -= OnPLCLogMessage;
                _plcService.PropertyChanged -= OnPLCPropertyChanged;
                _tagService?.Dispose();
                _plcService?.Dispose();
            }
            catch (Exception ex) { AddLog($"释放资源时出错: {ex.Message}"); }
            _disposed = true;
            AddLog("PLC连接资源释放完成");
        }

        public void Dispose() => RemoveReference();
        #endregion
    }
}