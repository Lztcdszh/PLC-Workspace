using MyPLCManagerLibrary.Model;
using S7.Net;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

public class PLCService : INotifyPropertyChanged, IDisposable
{
    private readonly IPlcCommunication _communication;
    private readonly PLCConfig _config;
    private CancellationTokenSource _operationCts;
    private DateTime _lastOperationTime = DateTime.MinValue;
    private readonly object _stateLock = new object();
    private bool _disposed = false;

    private static readonly HashSet<string> _connectionErrorKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "连接", "socket", "network", "timeout", "断开", "无法访问", "closed"
    };

    public event PropertyChangedEventHandler PropertyChanged;
    public event EventHandler<string> LogMessage;

    public PLCService(PLCConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _communication = CreateCommunication(config);
        _operationCts = new CancellationTokenSource();
        AddLog($"PLCService 初始化: AutoReconnect={config.AutoReconnect}, MaxReconnectAttempts={config.MaxReconnectAttempts}, ReconnectDelayMs={config.ReconnectDelayMs}");
    }

    private IPlcCommunication CreateCommunication(PLCConfig config)
    {
        return config.Protocol switch
        {
            ProtocolType.S7 => new S7Communication(config, AddLog),
            ProtocolType.ModbusTcp or ProtocolType.ModbusRtu => new ModbusCommunication(config),
            _ => throw new NotSupportedException($"不支持的协议: {config.Protocol}")
        };
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (_isConnected != value)
            {
                _isConnected = value;
                OnPropertyChanged(nameof(IsConnected));
            }
        }
    }

    private string _status = "未连接";
    public string Status
    {
        get => _status;
        private set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
            }
        }
    }

    public Task SendHeartbeatAsync() => _communication.SendHeartbeatAsync();
    public Task<bool> CheckConnectionAsync() => _communication.CheckConnectionAsync();

    public async Task<bool> ConnectAsync()
    {
        lock (_stateLock)
        {
            if (IsConnected) return true;
        }

        try
        {
            Status = "连接中...";
            var connectTask = _communication.ConnectAsync();
            var timeoutTask = Task.Delay(_config.Timeout, _operationCts.Token);
            var completedTask = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);

            if (completedTask == timeoutTask)
            {
                Status = "连接超时";
                AddLog($"连接超时，AutoReconnect={_config.AutoReconnect}");
                if (_config.AutoReconnect) _ = Task.Run(() => HandleConnectionErrorAsync());
                return false;
            }

            var result = await connectTask.ConfigureAwait(false);
            if (result)
            {
                _reconnectAttempts = 0;
                IsConnected = true;
                Status = "已连接";
                _lastOperationTime = DateTime.Now;
                AddLog("ConnectAsync 成功");
                return true;
            }
            else
            {
                Status = "连接失败";
                AddLog($"连接失败，AutoReconnect={_config.AutoReconnect}");
                if (_config.AutoReconnect) _ = Task.Run(() => HandleConnectionErrorAsync());
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            Status = "已取消";
            return false;
        }
        catch (Exception ex)
        {
            AddLog($"连接异常: {ex.Message}");
            Status = "连接错误";
            if (_config.AutoReconnect) _ = Task.Run(() => HandleConnectionErrorAsync());
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        AddLog("DisconnectAsync 被调用");
        try
        {
            _operationCts?.Cancel();
            await Task.Delay(100).ConfigureAwait(false);

            var oldCts = _operationCts;
            _operationCts = new CancellationTokenSource();
            oldCts?.Dispose();

            await _communication.DisconnectAsync().ConfigureAwait(false);
            Status = "已断开";
        }
        catch
        {
            Status = "断开错误";
        }
        finally
        {
            IsConnected = false;
        }
    }

    public async Task<T> ReadAsync<T>(string address)
    {
        try
        {
            return await ExecuteWithRetry(async () =>
            {
                if (!IsConnected) throw new InvalidOperationException("PLC未连接");
                _lastOperationTime = DateTime.Now;
                return await _communication.ReadAsync<T>(address).ConfigureAwait(false);
            }, $"读取 {address}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取失败: {ex.Message}");
            return default;
        }
    }

    public async Task<bool> WriteAsync<T>(string address, T value)
    {
        return await ExecuteWithRetry(async () =>
        {
            if (!IsConnected) throw new InvalidOperationException("PLC未连接");
            _lastOperationTime = DateTime.Now;
            var result = await _communication.WriteAsync(address, value).ConfigureAwait(false);
            return result;
        }, $"写入 {address}").ConfigureAwait(false);
    }

    private int _reconnectAttempts = 0;
    private bool _isReconnecting = false;
    private readonly object _reconnectLock = new object();

    private async Task<T> ExecuteWithRetry<T>(Func<Task<T>> operation, string operationName, int maxRetries = 2)
    {
        int retryCount = 0;
        while (retryCount <= maxRetries)
        {
            try
            {
                _operationCts.Token.ThrowIfCancellationRequested();
                return await operation().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (retryCount < maxRetries && IsConnectionError(ex))
            {
                retryCount++;
                AddLog($"{operationName} 失败，准备重试 ({retryCount}/{maxRetries}): {ex.Message}");

                if (!_isReconnecting)
                {
                    _ = Task.Run(() => HandleConnectionErrorAsync());
                }

                //await Task.Delay(500 * retryCount, _operationCts.Token).ConfigureAwait(false);
                await Task.Delay(500, _operationCts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                throw;
            }
        }
        throw new InvalidOperationException($"{operationName} 重试失败");
    }

    private bool IsConnectionError(Exception ex)
    {
        if (ex is PlcException) return true;
        var message = ex.Message;
        foreach (var keyword in _connectionErrorKeywords)
        {
            if (message.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public async Task HandleConnectionErrorAsync()
    {
        lock (_reconnectLock)
        {
            if (_isReconnecting)
            {
                AddLog("HandleConnectionErrorAsync 忽略：已在重连中");
                return;
            }
            _isReconnecting = true;
        }

        try
        {
            AddLog($"HandleConnectionErrorAsync 开始，当前 _reconnectAttempts={_reconnectAttempts}");
            lock (_stateLock)
            {
                IsConnected = false;
                Status = "连接丢失";
            }

            if (!_config.AutoReconnect)
            {
                AddLog("AutoReconnect 为 false，不进行重连");
                return;
            }

            int localAttempt = 0;
            while (!_operationCts.Token.IsCancellationRequested)
            {
                Status = "重连中...";
                if (_config.MaxReconnectAttempts > 0 && _reconnectAttempts >= _config.MaxReconnectAttempts)
                {
                    AddLog($"达到最大重连次数 ({_config.MaxReconnectAttempts})，停止重连");
                    Status = "重连失败";
                    break;
                }

                localAttempt++;
                _reconnectAttempts++;
                AddLog($"尝试第 {localAttempt} 次重连（累计 {_reconnectAttempts} 次），等待 {_config.ReconnectDelayMs}ms...");
                await Task.Delay(_config.ReconnectDelayMs, _operationCts.Token).ConfigureAwait(false);

                // 重要：不要调用 DisconnectAsync()，因为它会取消 _operationCts
                // 直接让 _communication.ConnectAsync() 内部处理旧连接的清理
                try
                {
                    // 可选：主动通知通信层断开（但不影响 _operationCts）
                    await _communication.DisconnectAsync().ConfigureAwait(false);
                    await Task.Delay(200).ConfigureAwait(false); // 短暂等待

                    var connectTask = _communication.ConnectAsync();
                    var timeoutTask = Task.Delay(_config.Timeout, _operationCts.Token);
                    var completedTask = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);

                    bool success = false;
                    if (completedTask == connectTask)
                        success = await connectTask.ConfigureAwait(false);

                    if (success)
                    {
                        _reconnectAttempts = 0;
                        IsConnected = true;
                        Status = "已连接";
                        AddLog("重连成功！");
                        return;
                    }
                    else
                    {
                        AddLog("重连失败（连接返回 false）");
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"重连尝试异常: {ex.Message}");
                }
            }
        }
        finally
        {
            lock (_reconnectLock)
            {
                _isReconnecting = false;
            }
            AddLog("重连流程结束");
        }
    }

    private void AddLog(string message)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            LogMessage?.Invoke(this, $"{timestamp} - {message}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AddLog 事件处理异常: {ex}");
        }
    }

    protected virtual void OnPropertyChanged(string propertyName)
    {
        try
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PropertyChanged 事件异常: {ex}");
        }
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _communication?.Dispose();
        _disposed = true;
    }
}