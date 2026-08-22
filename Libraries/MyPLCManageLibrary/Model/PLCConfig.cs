namespace MyPLCManagerLibrary.Model
{
    public enum PLCType
    {
        S7200Smart,
        S71200,
        S71500,
        S7300,
        S7400,
        // 如需其它厂商/类型可继续扩展
        XinjeXG,
        Unknown
    }
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Error
    }
    // 新增：协议类型枚举
    public enum ProtocolType
    {
        S7,
        ModbusTcp,
        ModbusRtu,
        // OPC UA：工业事实标准，跨厂商互操作（P0 关键能力）
        OpcUa
    }

    /// <summary>OPC UA 安全模式。</summary>
    public enum OpcUaSecurityMode
    {
        /// <summary>无安全（仅测试/内网隔离环境）。</summary>
        None,
        /// <summary>签名（防篡改）。</summary>
        Sign,
        /// <summary>签名 + 加密。</summary>
        SignAndEncrypt
    }

    // 新增：Modbus 子类型（TCP 或 RTU）
    public enum ModbusType
    {
        Tcp,
        Rtu
    }

    public class PLCConfig
    {
        // 原有 S7 属性（保持不变）
        public string Name { get; set; } = "PLC";
        public string IP { get; set; } = "127.0.0.1";
        public short Rack { get; set; } = 0;
        public short Slot { get; set; } = 1;
        public int Timeout { get; set; } = 5000;
     

        // 原有 PLC 型号（S7 专用）
        public PLCType Type { get; set; } = PLCType.S7200Smart;

        // ========== 新增协议相关属性 ==========
        public ProtocolType Protocol { get; set; } = ProtocolType.S7; // 默认为 S7，兼容旧代码

        // Modbus 参数（当 Protocol 为 ModbusTcp 或 ModbusRtu 时使用）
        public ModbusType ModbusType { get; set; } = ModbusType.Tcp;   // TCP 或 RTU
        public int Port { get; set; } = 502;                            // Modbus TCP 端口
        public string SerialPortName { get; set; } = "COM1";            // Modbus RTU 串口名
        public int BaudRate { get; set; } = 9600;                       // RTU 波特率
        /// <summary>
        /// 是否在连接断开后自动重连（默认为 true）
        /// </summary>
        public bool AutoReconnect { get; set; } = true;

        /// <summary>
        /// 自动重连的最大尝试次数（0 表示无限次，默认为 0）
        /// </summary>
        public int MaxReconnectAttempts { get; set; } = 0;

        /// <summary>
        /// 重连失败后的等待时间（毫秒），默认为 1000ms
        /// </summary>
        public int ReconnectDelayMs { get; set; } = 1000;                                                                // 可根据需要添加数据位、停止位、校验等

        /// <summary>
        /// 用于生成唯一连接键的连接字符串
        /// </summary>
        public string ConnectionKey
        {
            get
            {
                return Protocol switch
                {
                    ProtocolType.S7 => $"{Protocol}_{IP}_{Rack}_{Slot}",
                    ProtocolType.ModbusTcp => $"{Protocol}_{IP}:{Port}",
                    ProtocolType.ModbusRtu => $"{Protocol}_{SerialPortName}_{BaudRate}",
                    ProtocolType.OpcUa => $"{Protocol}_{EndpointUrl}",
                    _ => throw new NotSupportedException($"不支持的协议: {Protocol}")
                };
            }
        }
        public byte SlaveId { get; set; } = 1;

        // ========== OPC UA 相关属性（当 Protocol 为 OpcUa 时使用）==========
        /// <summary>OPC UA 服务端端点 URL，例如 opc.tcp://192.168.1.10:4840</summary>
        public string EndpointUrl { get; set; } = string.Empty;

        /// <summary>OPC UA 安全模式，默认 None（内网隔离可先用，生产建议 SignAndEncrypt）。</summary>
        public OpcUaSecurityMode OpcUaMode { get; set; } = OpcUaSecurityMode.None;

        /// <summary>OPC UA 用户名（匿名时留空）。</summary>
        public string OpcUaUser { get; set; } = string.Empty;

        /// <summary>OPC UA 密码。</summary>
        public string OpcUaPassword { get; set; } = string.Empty;

        /// <summary>用于生成唯一连接键的连接字符串（OPC UA 用端点 URL）。</summary>
        public string OpcUaConnectionKey => $"{Protocol}_{EndpointUrl}";
    }
}