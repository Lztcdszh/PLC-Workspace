using MyPLCManagerLibrary.Model;
using S7.Net;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

public class S7Communication : IPlcCommunication
{
    private Plc _plc;
    private readonly PLCConfig _config;
    private readonly SemaphoreSlim _asyncLock = new SemaphoreSlim(1, 1);
    private readonly Action<string> _logAction;
    private bool _disposed = false;

    public bool IsConnected => _plc?.IsConnected ?? false;

    public S7Communication(PLCConfig config, Action<string> logAction = null)
    {
        _config = config;
        _logAction = logAction ?? (msg => { });
    }

    private void Log(string message) => Debug.WriteLine($"[S7Comm] {message}");

    public async Task<bool> ConnectAsync()
    {
        await _asyncLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // 先清理旧连接
            if (_plc != null)
            {
                if (_plc.IsConnected)
                    await Task.Run(() => _plc.Close()).ConfigureAwait(false);
                _plc = null;
            }

            var cpuType = MapCpuType(_config.Type);
            var newPlc = new Plc(cpuType, _config.IP, _config.Rack, _config.Slot)
            {
                ReadTimeout = _config.Timeout,
                WriteTimeout = _config.Timeout
            };

            Log($"正在连接 S7 TCP: {_config.IP}");
            await Task.Run(() =>  newPlc.Open()).ConfigureAwait(false);
            _plc = newPlc;
            Log("连接成功");
            return true;
        }
        catch (Exception ex)
        {
            Log($"连接失败: {ex.Message}");
            return false;
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _asyncLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_plc != null)
            {
                if (_plc.IsConnected)
                    await Task.Run(() => _plc.Close()).ConfigureAwait(false);
                _plc = null;
            }
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    public async Task<T> ReadAsync<T>(string address)
    {
        await _asyncLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsConnected)
                throw new InvalidOperationException("PLC未连接");

            object value = await Task.Run(() => _plc.Read(address)).ConfigureAwait(false);
            if (value == null) return default;

            Type targetType = typeof(T);

            if (targetType == typeof(float))
            {
                float result;
                if (value is byte[] bytes && bytes.Length >= 4)
                    result = S7.Net.Types.Single.FromByteArray(bytes);
                else if (value is int || value is uint || value is short || value is ushort)
                {
                    uint raw = Convert.ToUInt32(value);
                    byte[] temp = BitConverter.GetBytes(raw);
                    result = BitConverter.ToSingle(temp, 0);
                }
                else return default;
                return (T)(object)result;
            }
            else if (targetType == typeof(double))
            {
                double result;
                if (value is byte[] bytes && bytes.Length >= 8)
                    result = S7.Net.Types.Double.FromByteArray(bytes);
                else if (value is long || value is ulong)
                {
                    ulong raw = Convert.ToUInt64(value);
                    byte[] temp = BitConverter.GetBytes(raw);
                    result = BitConverter.ToDouble(temp, 0);
                }
                else return default;
                return (T)(object)result;
            }
            else if (targetType == typeof(string) && value is byte[] strBytes)
            {
                string strVal = System.Text.Encoding.ASCII.GetString(strBytes).TrimEnd('\0');
                return (T)(object)strVal;
            }
            else
            {
                return (T)Convert.ChangeType(value, targetType);
            }
        }
        catch (Exception ex)
        {
            Log($"读取地址 {address} 失败: {ex.Message}");
            throw;
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    public async Task<bool> WriteAsync<T>(string address, T value)
    {
        await _asyncLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsConnected)
                throw new InvalidOperationException("PLC未连接");
            await Task.Run(() => _plc.Write(address, value)).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log($"写入地址 {address} 失败: {ex.Message}");
            throw;
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    public async Task<bool> CheckConnectionAsync()
    {
        await _asyncLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return IsConnected;
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    public async Task SendHeartbeatAsync()
    {
        if (!IsConnected) return;
        try
        {
            await ReadAsync<bool>("M0.0").ConfigureAwait(false);
        }
        catch
        {
            Log("心跳失败");
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 同步等待锁，但避免死锁（因为Dispose可能在 finalizer 或 UI 线程）
        try
        {
            _asyncLock.Wait(2000);
            try
            {
                _plc?.Close();
                _plc = null;
            }
            finally
            {
                _asyncLock.Release();
            }
        }
        catch (Exception ex)
        {
            Log($"Dispose 异常: {ex.Message}");
        }
        _asyncLock.Dispose();
    }

    private static CpuType MapCpuType(PLCType type)
    {
        return type switch
        {
            PLCType.S7200Smart => CpuType.S7200Smart,
            PLCType.S71200 => CpuType.S71200,
            PLCType.S71500 => CpuType.S71500,
            PLCType.S7300 => CpuType.S7300,
            PLCType.S7400 => CpuType.S7400,
            _ => CpuType.S7200Smart
        };
    }
}