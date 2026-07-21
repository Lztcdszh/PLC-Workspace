using MyPLCManagerLibrary.Model;
using NModbus;
using NModbus.Device;
using NModbus.IO;
using NModbus.Serial;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MyPLCManagerLibrary.Communication.AddressConverter;

/// <summary>
/// Modbus 通信实现（调试信息使用 Debug.WriteLine）
/// </summary>
public class ModbusCommunication : IPlcCommunication
{
    private IModbusMaster _master;
    private TcpClient _tcpClient;
    private SerialPort _serialPort;
    private readonly PLCConfig _config;
    private readonly SemaphoreSlim _asyncLock = new SemaphoreSlim(1, 1);
    private bool _isConnected = false;
    private readonly IAddressConverter _converter;

    public bool IsConnected => _isConnected;

    // 移除了 logAction 参数，所有日志直接输出到 Debug
    public ModbusCommunication(PLCConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        // 根据 PLC 类型创建对应的转换器
        _converter = config.Type switch
        {
            PLCType.S7200Smart => new S7200SmartAddressConverter(),
            PLCType.XinjeXG => new XinjeXGAddressConverter(),
            _ => throw new NotSupportedException($"PLC类型 {config.Type} 的地址转换器未实现")
        };
    }

    // 辅助日志方法，直接使用 Debug.WriteLine
    private void Log(string msg) => Debug.WriteLine($"[ModbusComm] {msg}");

    public async Task<bool> ConnectAsync()
    {
        try
        {
            if (_config.Protocol == MyPLCManagerLibrary.Model.ProtocolType.ModbusTcp)
            {
                Log($"正在连接 Modbus TCP: {_config.IP}:{_config.Port}");
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(_config.IP, _config.Port).ConfigureAwait(false);
                var adapter = new TcpClientAdapter(_tcpClient);
                var factory = new ModbusFactory();
                _master = factory.CreateIpMaster(adapter);
                _isConnected = true;
                Log("Modbus TCP 连接成功");
                return true;
            }
            else if (_config.Protocol == MyPLCManagerLibrary.Model.ProtocolType.ModbusRtu)
            {
                Log($"正在连接 Modbus RTU: {_config.SerialPortName} @ {_config.BaudRate} baud");
                _serialPort = new SerialPort(_config.SerialPortName, _config.BaudRate, Parity.None, 8, StopBits.One);
                _serialPort.Open();
                var factory = new ModbusFactory();
                _master = factory.CreateRtuMaster(_serialPort);
                _isConnected = true;
                Log("Modbus RTU 连接成功");
                return true;
            }
            else
            {
                Log($"不支持的协议类型: {_config.Protocol}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Log($"Modbus 连接失败: {_config.Name}{ex.Message}");
            _isConnected = false;
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        await _asyncLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _tcpClient?.Close();
            _serialPort?.Close();
            _master = null;
            _isConnected = false;
            Log("Modbus 连接已断开");
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    private ModbusAddressInfo ConvertAddress(string address, Type targetType)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("地址不能为空");
        return _converter.Convert(address.Trim(), targetType);
    }

    public async Task<T> ReadAsync<T>(string address)
    {
        var addrInfo = ConvertAddress(address, typeof(T));
        byte slaveId = GetSlaveId();

        await _asyncLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsConnected || _master == null)
            {
                Log($"PLC 未连接，读取 {address} 失败");
                return default;
            }

            if (addrInfo.ModbusType == "C") // 线圈
            {
                bool[] coils = await _master.ReadCoilsAsync(slaveId, addrInfo.StartAddress, addrInfo.Length)
                                             .ConfigureAwait(false);
                return (T)(object)coils[0];
            }
            else if (addrInfo.ModbusType == "D") // 离散输入
            {
                bool[] inputs = await _master.ReadInputsAsync(slaveId, addrInfo.StartAddress, addrInfo.Length)
                                              .ConfigureAwait(false);
                return (T)(object)inputs[0];
            }
            else if (addrInfo.ModbusType == "H") // 保持寄存器
            {
                ushort[] registers = await _master.ReadHoldingRegistersAsync(slaveId, addrInfo.StartAddress, addrInfo.Length)
                                                   .ConfigureAwait(false);
                if (addrInfo.BitPosition >= 0)
                {
                    bool bitValue = (registers[0] & (1 << addrInfo.BitPosition)) != 0;
                    return (T)(object)bitValue;
                }
                else
                {
                    return ConvertRegistersToValue<T>(registers, addrInfo.ByteOffset);
                }
            }
            else if (addrInfo.ModbusType == "I") // 输入寄存器
            {
                ushort[] registers = await _master.ReadInputRegistersAsync(slaveId, addrInfo.StartAddress, addrInfo.Length)
                                                   .ConfigureAwait(false);
                if (addrInfo.BitPosition >= 0)
                {
                    bool bitValue = (registers[0] & (1 << addrInfo.BitPosition)) != 0;
                    return (T)(object)bitValue;
                }
                else
                {
                    return ConvertRegistersToValue<T>(registers, addrInfo.ByteOffset);
                }
            }
            else
            {
                Log($"不支持的 Modbus 类型: {addrInfo.ModbusType}，地址: {address}");
                return default;
            }
        }
        catch (Exception ex)
        {
            Log($"读取 {address} 失败: {ex.Message}");
            return default;
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    public async Task<bool> WriteAsync<T>(string address, T value)
    {
        var addrInfo = ConvertAddress(address, typeof(T));
        byte slaveId = GetSlaveId();

        await _asyncLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsConnected || _master == null)
            {
                Log($"PLC 未连接，写入 {address} 失败");
                return false;
            }

            if (addrInfo.ModbusType == "C") // 线圈
            {
                bool val = Convert.ToBoolean(value);
                await _master.WriteSingleCoilAsync(slaveId, addrInfo.StartAddress, val)
                             .ConfigureAwait(false);
            }
            else if (addrInfo.ModbusType == "H") // 保持寄存器
            {
                if (addrInfo.BitPosition >= 0) // 位写入
                {
                    ushort[] original = await _master.ReadHoldingRegistersAsync(slaveId, addrInfo.StartAddress, 1)
                                                      .ConfigureAwait(false);
                    ushort newReg = original[0];
                    bool bitVal = Convert.ToBoolean(value);
                    if (bitVal)
                        newReg |= (ushort)(1 << addrInfo.BitPosition);
                    else
                        newReg &= (ushort)~(1 << addrInfo.BitPosition);
                    await _master.WriteSingleRegisterAsync(slaveId, addrInfo.StartAddress, newReg)
                                 .ConfigureAwait(false);
                }
                else if (addrInfo.ByteOffset >= 0) // 字节写入
                {
                    ushort[] original = await _master.ReadHoldingRegistersAsync(slaveId, addrInfo.StartAddress, 1)
                                                      .ConfigureAwait(false);
                    ushort newReg = original[0];
                    byte byteVal = Convert.ToByte(value);
                    if (addrInfo.ByteOffset == 0) // 高字节
                        newReg = (ushort)((byteVal << 8) | (original[0] & 0xFF));
                    else // 低字节
                        newReg = (ushort)((original[0] & 0xFF00) | byteVal);
                    await _master.WriteSingleRegisterAsync(slaveId, addrInfo.StartAddress, newReg)
                                 .ConfigureAwait(false);
                }
                else if (addrInfo.Length == 1) // 单寄存器写入
                {
                    ushort val = Convert.ToUInt16(value);
                    await _master.WriteSingleRegisterAsync(slaveId, addrInfo.StartAddress, val)
                                 .ConfigureAwait(false);
                }
                else // 多寄存器写入
                {
                    ushort[] registers = ConvertValueToRegisters(value, addrInfo.Length);
                    await _master.WriteMultipleRegistersAsync(slaveId, addrInfo.StartAddress, registers)
                                 .ConfigureAwait(false);
                }
            }
            else
            {
                Log($"不支持的写入类型: {addrInfo.ModbusType}，地址: {address}");
                return false;
            }

            Debug.WriteLine($"写入 {address} (转换后 {addrInfo.ModbusType}{addrInfo.StartAddress}) = {value}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"写入 {address} 失败: {ex.Message}");
            return false;
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    private T ConvertRegistersToValue<T>(ushort[] registers, int byteOffset = -1)
    {
        if (registers == null || registers.Length == 0)
            throw new InvalidOperationException("无寄存器数据");

        Type type = typeof(T);
        if (type == typeof(bool))
            return (T)(object)(registers[0] != 0);
        else if (type == typeof(byte))
        {
            ushort reg = registers[0];
            byte val = (byte)(byteOffset == 0 ? (reg >> 8) : (reg & 0xFF));
            return (T)(object)val;
        }
        else if (type == typeof(sbyte))
        {
            ushort reg = registers[0];
            sbyte val = (sbyte)(byteOffset == 0 ? (reg >> 8) : (reg & 0xFF));
            return (T)(object)val;
        }
        else if (type == typeof(short))
            return (T)(object)(short)registers[0];
        else if (type == typeof(ushort))
            return (T)(object)registers[0];
        else if (type == typeof(int) && registers.Length >= 2)
        {
            int val = (registers[0] << 16) | registers[1];
            return (T)(object)val;
        }
        else if (type == typeof(uint) && registers.Length >= 2)
        {
            uint val = (uint)((registers[0] << 16) | registers[1]);
            return (T)(object)val;
        }
        else if (type == typeof(float) && registers.Length >= 2)
        {
            uint val = (uint)((registers[0] << 16) | registers[1]);
            return (T)(object)BitConverter.ToSingle(BitConverter.GetBytes(val), 0);
        }
        else if (type == typeof(long) && registers.Length >= 4)
        {
            long val = ((long)registers[0] << 48) | ((long)registers[1] << 32) | ((long)registers[2] << 16) | registers[3];
            return (T)(object)val;
        }
        else if (type == typeof(ulong) && registers.Length >= 4)
        {
            ulong val = ((ulong)registers[0] << 48) | ((ulong)registers[1] << 32) | ((ulong)registers[2] << 16) | registers[3];
            return (T)(object)val;
        }
        else if (type == typeof(double) && registers.Length >= 4)
        {
            ulong val = ((ulong)registers[0] << 48) | ((ulong)registers[1] << 32) | ((ulong)registers[2] << 16) | registers[3];
            return (T)(object)BitConverter.ToDouble(BitConverter.GetBytes(val), 0);
        }
        else
        {
            throw new NotSupportedException($"无法将寄存器转换为 {type.Name}，寄存器数量: {registers.Length}");
        }
    }

    private ushort[] ConvertValueToRegisters(object value, ushort expectedLength)
    {
        Type type = value.GetType();
        ushort[] registers = new ushort[expectedLength];

        if (type == typeof(bool))
        {
            registers[0] = (ushort)((bool)value ? 1 : 0);
        }
        else if (type == typeof(byte) || type == typeof(sbyte))
        {
            registers[0] = Convert.ToUInt16(value);
        }
        else if (type == typeof(short) || type == typeof(ushort))
        {
            registers[0] = Convert.ToUInt16(value);
        }
        else if (type == typeof(int) || type == typeof(uint))
        {
            uint val = Convert.ToUInt32(value);
            registers[0] = (ushort)(val >> 16);
            registers[1] = (ushort)(val & 0xFFFF);
        }
        else if (type == typeof(float))
        {
            uint val = BitConverter.ToUInt32(BitConverter.GetBytes((float)value), 0);
            registers[0] = (ushort)(val >> 16);
            registers[1] = (ushort)(val & 0xFFFF);
        }
        else if (type == typeof(long) || type == typeof(ulong))
        {
            ulong val = Convert.ToUInt64(value);
            registers[0] = (ushort)(val >> 48);
            registers[1] = (ushort)((val >> 32) & 0xFFFF);
            registers[2] = (ushort)((val >> 16) & 0xFFFF);
            registers[3] = (ushort)(val & 0xFFFF);
        }
        else if (type == typeof(double))
        {
            ulong val = BitConverter.ToUInt64(BitConverter.GetBytes((double)value), 0);
            registers[0] = (ushort)(val >> 48);
            registers[1] = (ushort)((val >> 32) & 0xFFFF);
            registers[2] = (ushort)((val >> 16) & 0xFFFF);
            registers[3] = (ushort)(val & 0xFFFF);
        }
        else
        {
            throw new NotSupportedException($"不支持的类型: {type.Name}");
        }

        return registers;
    }

    public async Task<bool> CheckConnectionAsync()
    {
        return await Task.FromResult(IsConnected);
    }

    public async Task SendHeartbeatAsync()
    {
        if (IsConnected)
        {
            try
            {
                await ReadAsync<ushort>("H0").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"心跳失败: {ex.Message}");
                throw; // 让上层捕获，触发重连
            }
        }
    }

    public void Dispose()
    {
        _asyncLock.Wait();
        try
        {
            _tcpClient?.Close();
            _serialPort?.Close();
            _master = null;
            _isConnected = false;
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    private byte GetSlaveId()
    {
        return (byte)(_config.SlaveId != 0 ? _config.SlaveId : 1);
    }
}