using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MyPLCManagerLibrary.Communication.AddressConverter
{
    public class ModbusAddressInfo
    {
        /// <summary>Modbus 区域类型：C=线圈, D=离散输入, H=保持寄存器, I=输入寄存器</summary>
        public string ModbusType { get; set; }
        /// <summary>起始地址（线圈/寄存器索引）</summary>
        public ushort StartAddress { get; set; }
        /// <summary>需要读取/写入的寄存器或线圈数量</summary>
        public ushort Length { get; set; } = 1;
        /// <summary>对于字节类型（byte/sbyte），指定在寄存器中的位置：0=高字节，1=低字节；其他类型忽略</summary>
        public int ByteOffset { get; set; } = -1;
        /// <summary>对于位类型（bool），指定在寄存器/线圈中的位位置（0~15）；-1表示不是位</summary>
        public int BitPosition { get; set; } = -1;
    }

    /// <summary>
    /// 地址转换器接口
    /// </summary>
    public interface IAddressConverter
    {
        ModbusAddressInfo Convert(string address, Type targetType);
    }
}
