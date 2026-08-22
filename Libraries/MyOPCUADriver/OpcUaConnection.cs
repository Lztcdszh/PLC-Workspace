using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using Opc.Ua;
using Opc.Ua.Client;

namespace MyOPCUADriver
{
    /// <summary>
    /// OPC UA 连接驱动，实现 <see cref="IConnectionManager"/> 契约，使其与现有 S7/Modbus 驱动平级。
    /// 用法（直接实例化，避免与 GetOrCreate 单例的 S7/Modbus 键冲突）：
    /// <code>
    ///   var cfg = new PLCConfig { Protocol = ProtocolType.OpcUa, Name = "产线UA",
    ///                             EndpointUrl = "opc.tcp://192.168.1.10:4840",
    ///                             OpcUaMode = OpcUaSecurityMode.None };
    ///   var conn = new OpcUaConnection(cfg);
    ///   await conn.ConnectAsync();
    /// </code>
    /// 标签的 Address 即为 OPC UA NodeId 字符串（例如 "ns=2;s=Demo.Static.Scalar.Int32"）。
    /// </summary>
    public sealed class OpcUaConnection : IConnectionManager
    {
        private readonly PLCConfig _config;
        private readonly ConcurrentDictionary<string, PLCTag> _tags = new(StringComparer.OrdinalIgnoreCase);
        private Session? _session;
        private Subscription? _subscription;

        private bool _isConnected;
        private bool _isConnecting;
        private ConnectionState _state = ConnectionState.Disconnected;
        private int _refCount;
        private bool _disposed;

        public OpcUaConnection(PLCConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            if (_config.Protocol != ProtocolType.OpcUa)
                throw new ArgumentException("PLCConfig.Protocol 必须为 OpcUa", nameof(config));
        }

        #region IConnectionManager 基础信息

        public string Name => _config.Name;
        public PLCConfig ConnectionConfig => _config;

        public bool IsConnected
        {
            get => _isConnected;
            private set { if (_isConnected != value) { _isConnected = value; OnPropertyChanged(nameof(IsConnected)); } }
        }

        public ConnectionState ConnectionStatus
        {
            get => _state;
            private set { if (_state != value) { _state = value; OnPropertyChanged(nameof(ConnectionStatus)); } }
        }

        public bool IsConnecting
        {
            get => _isConnecting;
            private set { if (_isConnecting != value) { _isConnecting = value; OnPropertyChanged(nameof(IsConnecting)); } }
        }

        public string ConnectionStatusText => _state switch
        {
            ConnectionState.Connected => "已连接",
            ConnectionState.Connecting => "连接中…",
            ConnectionState.Error => "错误",
            _ => "未连接"
        };

        public string ConnectionStatusColor => _state switch
        {
            ConnectionState.Connected => "#22C55E",
            ConnectionState.Connecting => "#F59E0B",
            ConnectionState.Error => "#EF4444",
            _ => "#9CA3AF"
        };

        public string StatusIcon => _state switch
        {
            ConnectionState.Connected => "🟢",
            ConnectionState.Connecting => "🟡",
            ConnectionState.Error => "🔴",
            _ => "⚪"
        };

        #endregion

        #region 引用计数

        public void AddReference() { Interlocked.Increment(ref _refCount); OnPropertyChanged(nameof(ReferenceCount)); }
        public void RemoveReference()
        {
            var n = Interlocked.Decrement(ref _refCount);
            OnPropertyChanged(nameof(ReferenceCount));
            if (n <= 0) { _ = DisconnectAsync(); Dispose(); }
        }
        public int ReferenceCount => _refCount;

        #endregion

        #region 连接操作

        public async Task<bool> ConnectAsync()
        {
            if (_disposed) return false;
            if (_isConnected && _session != null) return true;
            IsConnecting = true;
            ConnectionStatus = ConnectionState.Connecting;
            RaiseLog($"OPC UA 正在连接: {_config.EndpointUrl}");
            try
            {
                var appConfig = BuildApplicationConfiguration();
                await appConfig.Validate(ApplicationType.Client);

                var endpoint = new EndpointDescription(_config.EndpointUrl)
                {
                    SecurityMode = MapSecurityMode(_config.OpcUaMode),
                    SecurityPolicyUri = MapSecurityPolicy(_config.OpcUaMode),
                    Server = new ApplicationDescription
                    {
                        ApplicationUri = _config.EndpointUrl,
                        ApplicationType = ApplicationType.Server
                    }
                };
                var configuredEndpoint = new ConfiguredEndpoint(null, endpoint);

                IUserIdentity identity = string.IsNullOrEmpty(_config.OpcUaUser)
                    ? new UserIdentity()
                    : new UserIdentity(_config.OpcUaUser, _config.OpcUaPassword);

                _session = await Session.Create(
                    appConfig, configuredEndpoint,
                    updateBeforeConnect: true, checkDomain: false,
                    sessionName: "PLCTestingOpcUaClient",
                    sessionTimeout: 60000, identity: identity, preferredLocales: null);

                IsConnected = true;
                ConnectionStatus = ConnectionState.Connected;
                RaiseLog("OPC UA 连接成功");
                return true;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                ConnectionStatus = ConnectionState.Error;
                RaiseLog($"OPC UA 连接失败: {ex.Message}");
                return false;
            }
            finally
            {
                IsConnecting = false;
            }
        }

        public async Task DisconnectAsync()
        {
            try
            {
                StopMonitoring();
                if (_session != null)
                {
                    await Task.Run(() => _session.CloseSession(null, true));
                    _session.Dispose();
                    _session = null;
                }
            }
            catch (Exception ex) { RaiseLog($"OPC UA 断开异常: {ex.Message}"); }
            finally
            {
                IsConnected = false;
                ConnectionStatus = ConnectionState.Disconnected;
            }
        }

        private static ApplicationConfiguration BuildApplicationConfiguration()
        {
            var config = new ApplicationConfiguration
            {
                ApplicationName = "PLCTestingOpcUaClient",
                ApplicationType = ApplicationType.Client,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = "Directory",
                        StorePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PLCTestingOpcUaCerts"),
                        SubjectName = "CN=PLCTestingOpcUaClient"
                    },
                    AutoAcceptUntrustedCertificates = true,
                    RejectSHA1SignedCertificates = false,
                    MinimumCertificateKeySize = 1024
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
                TraceConfiguration = new TraceConfiguration { TraceMasks = 0 }
            };
            config.CertificateValidator = new CertificateValidator { AutoAccept = true };
            return config;
        }

        private static MessageSecurityMode MapSecurityMode(OpcUaSecurityMode mode) => mode switch
        {
            OpcUaSecurityMode.Sign => MessageSecurityMode.Sign,
            OpcUaSecurityMode.SignAndEncrypt => MessageSecurityMode.SignAndEncrypt,
            _ => MessageSecurityMode.None
        };

        private static string MapSecurityPolicy(OpcUaSecurityMode mode) =>
            mode == OpcUaSecurityMode.None ? SecurityPolicies.None : SecurityPolicies.Basic256Sha256;

        #endregion

        #region 标签管理

        public void AddTag(PLCTag tag)
        {
            if (tag == null || string.IsNullOrEmpty(tag.Name)) return;
            _tags[tag.Name] = tag;
        }

        public PLCTag? GetTag(string name) => _tags.TryGetValue(name, out var t) ? t : null;

        public PLCTag[] GetAllTags() => _tags.Values.ToArray();

        public List<PLCTag> GetAllTagsSnapshot() => _tags.Values.ToList();

        public bool ContainsTag(string tagName) => _tags.ContainsKey(tagName);

        public int TagCount => _tags.Count;

        public bool HasTags => _tags.Count > 0;

        #endregion

        #region 标签读写

        public async Task<object?> ReadTagAsync(string tagName)
        {
            var tag = GetTag(tagName);
            if (tag == null || _session == null) return null;
            try
            {
                var nodeId = new NodeId(tag.Address);
                var nodes = new ReadValueIdCollection
                {
                    new ReadValueId { NodeId = nodeId, AttributeId = Attributes.Value }
                };
                DataValueCollection results;
                DiagnosticInfoCollection di;
                await Task.Run(() => _session.Read(null, 0, TimestampsToReturn.Both, nodes, out results, out di));
                if (results.Count == 0 || results[0].Value == null) return null;
                var v = ConvertTo(results[0].Value, tag.DataType);
                tag.Value = v;
                return v;
            }
            catch (Exception ex)
            {
                RaiseLog($"OPC UA 读失败[{tagName}]: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> WriteTagAsync(string tagName, object value)
        {
            var tag = GetTag(tagName);
            if (tag == null || _session == null) return false;
            try
            {
                var nodeId = new NodeId(tag.Address);
                var toWrite = ConvertTo(value, tag.DataType);
                var values = new WriteValueCollection
                {
                    new WriteValue
                    {
                        NodeId = nodeId,
                        AttributeId = Attributes.Value,
                        Value = new DataValue(new Variant(toWrite))
                    }
                };
                DiagnosticInfoCollection di;
                StatusCodeCollection statusCodes;
                await Task.Run(() => _session.Write(null, values, out statusCodes, out di));
                bool ok = statusCodes.Count > 0 && StatusCode.IsGood(statusCodes[0]);
                if (ok) tag.Value = toWrite;
                return ok;
            }
            catch (Exception ex)
            {
                RaiseLog($"OPC UA 写失败[{tagName}]: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> ReadMultipleTagsAsync(params string[] tagNames)
        {
            if (tagNames == null || tagNames.Length == 0) return false;
            var ok = true;
            foreach (var n in tagNames)
                if (await ReadTagAsync(n) == null) ok = false;
            return ok;
        }

        private static object? ConvertTo(object? raw, Type? target)
        {
            if (raw == null) return null;
            if (target == null) return raw;
            try { return Convert.ChangeType(raw, target); }
            catch { return raw; }
        }

        #endregion

        #region 监控

        public void StartMonitoring(int interval = 500)
        {
            if (_session == null) return;
            StopMonitoring();
            try
            {
                _subscription = new Subscription(_session.DefaultSubscription)
                {
                    PublishingInterval = Math.Max(100, interval),
                    KeepAliveCount = 10,
                    LifetimeCount = 30
                };
                _session.AddSubscription(_subscription);
                _subscription.Create();

                foreach (var tag in _tags.Values)
                {
                    var mi = new MonitoredItem(_subscription.DefaultItem)
                    {
                        NodeId = new NodeId(tag.Address),
                        AttributeId = Attributes.Value,
                        DisplayName = tag.Name,
                        SamplingInterval = Math.Max(100, interval)
                    };
                    mi.Notification += OnMonitoredItemNotification;
                    _subscription.AddItem(mi);
                }
                _subscription.ApplyChanges();
                RaiseLog($"OPC UA 订阅已启动，监控 {_tags.Count} 个标签");
            }
            catch (Exception ex) { RaiseLog($"OPC UA 启动订阅失败: {ex.Message}"); }
        }

        public void StopMonitoring()
        {
            try
            {
                if (_subscription != null && _session != null)
                {
                    _session.RemoveSubscription(_subscription);
                    _subscription.Dispose();
                }
            }
            catch { /* 忽略 */ }
            _subscription = null;
        }

        private void OnMonitoredItemNotification(object? sender, MonitoredItemNotificationEventArgs e)
        {
            try
            {
                if (e.NotificationValue is not MonitoredItemNotification min) return;
                var name = e.MonitoredItem?.DisplayName ?? string.Empty;
                var tag = GetTag(name);
                if (tag == null) return;
                var old = tag.Value;
                var v = ConvertTo(min.Value?.Value, tag.DataType);
                tag.Value = v;
                TagValueChanged?.Invoke(this, new MyPLCManagerLibrary.TagService.TagChangedEventArgs(name, v, old));
            }
            catch { /* 通知解析异常不应影响订阅 */ }
        }

        #endregion

        #region 日志/事件

        public event EventHandler<string>? LogMessage;
        public event EventHandler<MyPLCManagerLibrary.TagService.TagChangedEventArgs>? TagValueChanged;
        public event PropertyChangedEventHandler? PropertyChanged;

        private void RaiseLog(string msg) => LogMessage?.Invoke(this, msg);
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ = DisconnectAsync();
        }
    }
}
