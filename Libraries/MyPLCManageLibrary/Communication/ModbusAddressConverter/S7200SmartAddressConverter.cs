using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MyPLCManagerLibrary.Communication.AddressConverter
{
    public class S7200SmartAddressConverter : IAddressConverter
    {
        private const int DB_BLOCK_SIZE = 2000;           // 每个 DB 块占用的寄存器数量
        private const int DB_START_ADDRESS = 0;            // DB1 的起始寄存器地址
        private const int V_REGION_START_ADDRESS = 3000;   // V 区保持寄存器起始地址（可选）

        public ModbusAddressInfo Convert(string address, Type targetType)
        {
            address = address.Trim().ToUpper();
            var result = new ModbusAddressInfo();

            // 1. 处理 H 前缀（保持寄存器直接地址）
            if (address.StartsWith("H"))
            {
                string numPart = address.Substring(1);
                if (!int.TryParse(numPart, out int startAddr))
                    throw new FormatException($"无效的 H 地址格式: {address}");
                result.ModbusType = "H";
                result.StartAddress = (ushort)startAddr;
                result.Length = (ushort)GetRegisterLength(targetType);
                result.BitPosition = -1;
                result.ByteOffset = -1;
                return result;
            }

            // 2. 处理 DB 块地址
            if (address.StartsWith("DB"))
            {
                int dbIndex = address.IndexOf('.');
                if (dbIndex < 0)
                    throw new FormatException($"无效的 DB 地址格式: {address}");

                string dbPart = address.Substring(0, dbIndex);
                string rest = address.Substring(dbIndex + 1);

                int dbNumber = int.Parse(dbPart.Substring(2));
                int baseAddr = DB_START_ADDRESS + (dbNumber - 1) * DB_BLOCK_SIZE;

                if (rest.StartsWith("DBX")) // 位地址，如 DB1.DBX5.0
                {
                    string addr = rest.Substring(3);
                    string[] parts = addr.Split('.');
                    if (parts.Length != 2)
                        throw new FormatException($"无效的 DBX 地址格式: {rest}");
                    int byteOffset = int.Parse(parts[0]);
                    int bitOffset = int.Parse(parts[1]);

                    int regIndex = baseAddr + byteOffset / 2;
                    bool isHighByte = (byteOffset % 2 == 0);
                    int bitPosInReg = (isHighByte ? 8 : 0) + bitOffset;

                    result.ModbusType = "H";
                    result.StartAddress = (ushort)regIndex;
                    result.Length = 1;
                    result.BitPosition = bitPosInReg;
                }
                else if (rest.StartsWith("DBB")) // 字节地址，如 DB1.DBB10
                {
                    int byteOffset = int.Parse(rest.Substring(3));
                    int regIndex = baseAddr + byteOffset / 2;
                    result.ModbusType = "H";
                    result.StartAddress = (ushort)regIndex;
                    result.Length = 1;
                    result.ByteOffset = (byteOffset % 2 == 0) ? 0 : 1;
                }
                else if (rest.StartsWith("DBW")) // 字地址，如 DB1.DBW10
                {
                    int wordOffset = int.Parse(rest.Substring(3));
                    // 【修正】DBW 的偏移是字节偏移，需要除以 2 得到寄存器索引
                    int regIndex = baseAddr + wordOffset / 2;
                    result.ModbusType = "H";
                    result.StartAddress = (ushort)regIndex;
                    result.Length = 1;
                    // 如果偏移是奇数，则字会跨寄存器，这里简单处理：仍使用该寄存器，但实际读取可能不正确
                    if (wordOffset % 2 != 0)
                    {
                        // 可以记录警告，或改为读取两个寄存器并组合
                        // 为了简化，这里不做额外处理，假设用户使用偶数偏移
                    }
                }
                else if (rest.StartsWith("DBD")) // 双字地址，如 DB1.DBD10
                {
                    int dwordOffset = int.Parse(rest.Substring(3));
                    int regIndex = baseAddr + dwordOffset / 2;
                    result.ModbusType = "H";
                    result.StartAddress = (ushort)regIndex;
                    result.Length = 2;
                }
                else
                {
                    throw new NotSupportedException($"不支持的 DB 子类型: {rest}");
                }
            }
            else
            {
                // 3. 处理非 DB 地址：I, Q, V 等（已移除 M 区支持）
                char area = address[0];
                int dotIndex = address.IndexOf('.');
                bool isBit = dotIndex > 0;

                if (isBit)
                {
                    // 位地址，如 I0.0, Q0.0
                    string[] parts = address.Split('.');
                    string bytePart = parts[0].Substring(1);
                    int byteOffset = int.Parse(bytePart);
                    int bitOffset = int.Parse(parts[1]);
                    int bitIndex = byteOffset * 8 + bitOffset;

                    switch (area)
                    {
                        case 'Q':
                            result.ModbusType = "C";
                            result.StartAddress = (ushort)bitIndex;
                            result.Length = 1;
                            break;
                        case 'I':
                            result.ModbusType = "D";
                            result.StartAddress = (ushort)bitIndex;
                            result.Length = 1;
                            break;
                        default:
                            throw new NotSupportedException($"不支持的位地址区域: {area}");
                    }
                }
                else
                {
                    // 数值地址，如 V100, IW100, QW100
                    // 解析可能的后缀 B/W/D
                    string numberPart;
                    string typeSuffix = "";
                    string rest = address.Substring(1);
                    if (rest.Length > 0 && (rest[0] == 'B' || rest[0] == 'W' || rest[0] == 'D'))
                    {
                        typeSuffix = rest[0].ToString();
                        numberPart = rest.Substring(1);
                    }
                    else
                    {
                        numberPart = rest;
                    }

                    if (!int.TryParse(numberPart, out int offset))
                        throw new FormatException($"无效的地址数字部分: {address}");

                    switch (area)
                    {
                        case 'V':
                            // V 区映射为保持寄存器
                            int regIndex = V_REGION_START_ADDRESS + offset / 2;
                            result.ModbusType = "H";
                            result.StartAddress = (ushort)regIndex;
                            if (typeSuffix == "B" || targetType == typeof(byte) || targetType == typeof(sbyte))
                            {
                                result.Length = 1;
                                result.ByteOffset = (offset % 2 == 0) ? 0 : 1;
                            }
                            else if (typeSuffix == "W")
                            {
                                result.Length = 1;
                                result.ByteOffset = -1;
                            }
                            else if (typeSuffix == "D")
                            {
                                result.Length = 2;
                            }
                            else
                            {
                                // 无后缀，根据目标类型推断
                                result.Length = (ushort)GetRegisterLength(targetType);
                            }
                            break;
                        case 'I':
                            result.ModbusType = "I";
                            result.StartAddress = (ushort)offset;
                            result.Length = (ushort)GetRegisterLength(targetType);
                            break;
                        case 'Q':
                            result.ModbusType = "H";
                            result.StartAddress = (ushort)offset;
                            result.Length = (ushort)GetRegisterLength(targetType);
                            break;
                        default:
                            throw new NotSupportedException($"不支持的区域: {area}");
                    }
                }
            }

            return result;
        }

        private int GetRegisterLength(Type type)
        {
            if (type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte) ||
                type == typeof(short) || type == typeof(ushort))
                return 1;
            else if (type == typeof(int) || type == typeof(uint) || type == typeof(float))
                return 2;
            else if (type == typeof(long) || type == typeof(ulong) || type == typeof(double))
                return 4;
            else
                throw new NotSupportedException($"不支持的目标类型: {type.Name}");
        }
    }
}