using System;
using System.Diagnostics;


namespace MyPLCManagerLibrary.Communication.AddressConverter
{
    /// <summary>
    /// 信捷 XG 系列 PLC 专用地址转换器（根据官方地址表实现）
    /// </summary>
    public class XinjeXGAddressConverter : IAddressConverter
    {
        public ModbusAddressInfo Convert(string address, Type targetType)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空");
            // 处理 H 前缀（Modbus 保持寄存器直接地址）
            if (address.StartsWith("H"))
            {
                string numPart = address.Substring(1);
                if (!int.TryParse(numPart, out int startAddr))
                    throw new FormatException($"无效的 H 地址格式: {address}");
                return new ModbusAddressInfo
                {
                    ModbusType = "H",
                    StartAddress = (ushort)startAddr,
                    Length = (ushort)GetRegisterLength(targetType),
                    BitPosition = -1,
                    ByteOffset = -1
                };
            }
            address = address.Trim().ToUpperInvariant();

            // 分离小数点（位访问仅适用于字对象）
            int dotIndex = address.IndexOf('.');
            string mainPart = dotIndex >= 0 ? address.Substring(0, dotIndex) : address;
            string bitPart = dotIndex >= 0 ? address.Substring(dotIndex + 1) : null;

            // 提取前缀（字母部分）
            int i = 0;
            while (i < mainPart.Length && char.IsLetter(mainPart[i])) i++;
            string prefix = mainPart.Substring(0, i);
            string numberPart = mainPart.Substring(i);

            if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(numberPart))
                throw new FormatException($"无效的地址格式: {address}");

            return prefix switch
            {
                "X" => ParseX(address, mainPart, numberPart, bitPart, targetType),
                "Y" => ParseY(address, mainPart, numberPart, bitPart, targetType),
                "M" => ParseM(address, numberPart, bitPart, targetType),
                "S" => ParseS(address, numberPart, bitPart, targetType),
                "SM" => ParseSM(address, numberPart, bitPart, targetType),
                "T" => ParseT(address, numberPart, bitPart, targetType),
                "C" => ParseC(address, numberPart, bitPart, targetType),
                "ET" => ParseET(address, numberPart, bitPart, targetType),
                "SEM" => ParseSEM(address, numberPart, bitPart, targetType),
                "HM" => ParseHM(address, numberPart, bitPart, targetType),
                "HS" => ParseHS(address, numberPart, bitPart, targetType),
                "HT" => ParseHT(address, numberPart, bitPart, targetType),
                "HC" => ParseHC(address, numberPart, bitPart, targetType),
                "HSC" => ParseHSC(address, numberPart, bitPart, targetType),
                "D" => ParseD(address, numberPart, bitPart, targetType),
                "ID" => ParseID(address, numberPart, bitPart, targetType),
                "QD" => ParseQD(address, numberPart, bitPart, targetType),
                "SD" => ParseSD(address, numberPart, bitPart, targetType),
                "TD" => ParseTD(address, numberPart, bitPart, targetType),
                "CD" => ParseCD(address, numberPart, bitPart, targetType),
                "ETD" => ParseETD(address, numberPart, bitPart, targetType),
                "HD" => ParseHD(address, numberPart, bitPart, targetType),
                "HSD" => ParseHSD(address, numberPart, bitPart, targetType),
                "HTD" => ParseHTD(address, numberPart, bitPart, targetType),
                "HCD" => ParseHCD(address, numberPart, bitPart, targetType),
                "HSCD" => ParseHSCD(address, numberPart, bitPart, targetType),
                "FD" => ParseFD(address, numberPart, bitPart, targetType),
                "SFD" => ParseSFD(address, numberPart, bitPart, targetType),
                "FS" => ParseFS(address, numberPart, bitPart, targetType),
                _ => throw new NotSupportedException($"不支持的地址前缀: {prefix}")
            };
        }

        // 八进制字符串转十进制

        private int ParseOctal(string octal) => System.Convert.ToInt32(octal, 8);

        // ---------- 位对象解析 ----------
        private ModbusAddressInfo ParseX(string full, string main, string numPart, string bitPart, Type type)
        {
            if (bitPart != null) throw new FormatException($"X 地址不支持位访问: {full}");
            if (type != typeof(bool)) throw new NotSupportedException($"X 地址只能读取 bool 类型，但目标是 {type.Name}");

            int addr;
            if (numPart.Length <= 3) // 本体 X0~X77
            {
                addr = 0x5000 + ParseOctal(numPart);
            }
            else if (numPart.Length >= 5) // 扩展模块 X10000~X11777
            {
                string moduleStr = numPart.Substring(0, 3);
                string offsetStr = numPart.Substring(3);
                if (!int.TryParse(moduleStr, out int module) || !IsValidXModule(module))
                    throw new FormatException($"无效的模块号: {moduleStr}");
                int offset = ParseOctal(offsetStr);
                int moduleIndex = GetXModuleIndex(module);
                int baseAddr = 0x5100 + moduleIndex * 0x40;
                addr = baseAddr + offset;
            }
            else
            {
                throw new FormatException($"无效的 X 地址格式: {full}");
            }

            return new ModbusAddressInfo { ModbusType = "D", StartAddress = (ushort)addr, Length = 1 };
        }

        private ModbusAddressInfo ParseY(string full, string main, string numPart, string bitPart, Type type)
        {
            if (bitPart != null) throw new FormatException($"Y 地址不支持位访问: {full}");
            if (type != typeof(bool)) throw new NotSupportedException($"Y 地址只能读取 bool 类型，但目标是 {type.Name}");

            int addr;
            if (numPart.Length <= 3) // 本体 Y0~Y77
            {
                addr = 0x6000 + ParseOctal(numPart);
            }
            else if (numPart.Length >= 5) // 扩展模块 Y10000~Y11777
            {
                string moduleStr = numPart.Substring(0, 3);
                string offsetStr = numPart.Substring(3);
                if (!int.TryParse(moduleStr, out int module) || !IsValidYModule(module))
                    throw new FormatException($"无效的模块号: {moduleStr}");
                int offset = ParseOctal(offsetStr);
                int moduleIndex = GetYModuleIndex(module);
                int baseAddr = 0x6100 + moduleIndex * 0x40;
                addr = baseAddr + offset;
            }
            else
            {
                throw new FormatException($"无效的 Y 地址格式: {full}");
            }

            return new ModbusAddressInfo { ModbusType = "C", StartAddress = (ushort)addr, Length = 1 };
        }

        private ModbusAddressInfo ParseM(string full, string numPart, string bitPart, Type type)
        {
            if (bitPart != null) throw new FormatException($"M 地址不支持位访问: {full}");
            if (type != typeof(bool)) throw new NotSupportedException($"M 地址只能读取 bool 类型，但目标是 {type.Name}");
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 20479)
                throw new ArgumentOutOfRangeException($"M 编号无效: {numPart}");
            return new ModbusAddressInfo { ModbusType = "C", StartAddress = (ushort)num, Length = 1 };
        }

        private ModbusAddressInfo ParseS(string full, string numPart, string bitPart, Type type)
        {
            if (bitPart != null) throw new FormatException($"S 地址不支持位访问: {full}");
            if (type != typeof(bool)) throw new NotSupportedException($"S 地址只能读取 bool 类型，但目标是 {type.Name}");
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 7999)
                throw new ArgumentOutOfRangeException($"S 编号无效: {numPart}");
            return new ModbusAddressInfo { ModbusType = "C", StartAddress = (ushort)(0x7000 + num), Length = 1 };
        }

        private ModbusAddressInfo ParseSM(string full, string numPart, string bitPart, Type type)
        {
            if (bitPart != null) throw new FormatException($"SM 地址不支持位访问: {full}");
            if (type != typeof(bool)) throw new NotSupportedException($"SM 地址只能读取 bool 类型，但目标是 {type.Name}");
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 4095)
                throw new ArgumentOutOfRangeException($"SM 编号无效: {numPart}");
            return new ModbusAddressInfo { ModbusType = "C", StartAddress = (ushort)(0x9000 + num), Length = 1 };
        }

        private ModbusAddressInfo ParseT(string full, string numPart, string bitPart, Type type)
        {
            if (bitPart != null) throw new FormatException($"T 地址不支持位访问: {full}");
            if (type != typeof(bool)) throw new NotSupportedException($"T 地址只能读取 bool 类型，但目标是 {type.Name}");
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 4095)
                throw new ArgumentOutOfRangeException($"T 编号无效: {numPart}");
            return new ModbusAddressInfo { ModbusType = "C", StartAddress = (ushort)(0xA000 + num), Length = 1 };
        }

        private ModbusAddressInfo ParseC(string full, string numPart, string bitPart, Type type)
        {
            if (bitPart != null) throw new FormatException($"C 地址不支持位访问: {full}");
            if (type != typeof(bool)) throw new NotSupportedException($"C 地址只能读取 bool 类型，但目标是 {type.Name}");
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 4095)
                throw new ArgumentOutOfRangeException($"C 编号无效: {numPart}");
            return new ModbusAddressInfo { ModbusType = "C", StartAddress = (ushort)(0xB000 + num), Length = 1 };
        }

        private ModbusAddressInfo ParseET(string full, string numPart, string bitPart, Type type)
        {
            if (bitPart != null) throw new FormatException($"ET 地址不支持位访问: {full}");
            if (type != typeof(bool)) throw new NotSupportedException($"ET 地址只能读取 bool 类型，但目标是 {type.Name}");
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 39)
                throw new ArgumentOutOfRangeException($"ET 编号无效: {numPart}");
            return new ModbusAddressInfo { ModbusType = "C", StartAddress = (ushort)(0xC000 + num), Length = 1 };
        }

        private ModbusAddressInfo ParseSEM(string full, string numPart, string bitPart, Type type)
        {
            if (bitPart != null) throw new FormatException($"SEM 地址不支持位访问: {full}");
            if (type != typeof(bool)) throw new NotSupportedException($"SEM 地址只能读取 bool 类型，但目标是 {type.Name}");
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 127)
                throw new ArgumentOutOfRangeException($"SEM 编号无效: {numPart}");
            return new ModbusAddressInfo { ModbusType = "C", StartAddress = (ushort)(0xC080 + num), Length = 1 };
        }

        private ModbusAddressInfo ParseHM(string full, string numPart, string bitPart, Type type)
        {
            if (bitPart != null) throw new FormatException($"HM 地址不支持位访问: {full}");
            if (type != typeof(bool)) throw new NotSupportedException($"HM 地址只能读取 bool 类型，但目标是 {type.Name}");
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 6143)
                throw new ArgumentOutOfRangeException($"HM 编号无效: {numPart}");
            return new ModbusAddressInfo { ModbusType = "C", StartAddress = (ushort)(0xC100 + num), Length = 1 };
        }

        private ModbusAddressInfo ParseHS(string full, string numPart, string bitPart, Type type)
        {
            if (bitPart != null) throw new FormatException($"HS 地址不支持位访问: {full}");
            if (type != typeof(bool)) throw new NotSupportedException($"HS 地址只能读取 bool 类型，但目标是 {type.Name}");
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 999)
                throw new ArgumentOutOfRangeException($"HS 编号无效: {numPart}");
            return new ModbusAddressInfo { ModbusType = "C", StartAddress = (ushort)(0xD900 + num), Length = 1 };
        }

        private ModbusAddressInfo ParseHT(string full, string numPart, string bitPart, Type type)
        {
            if (bitPart != null) throw new FormatException($"HT 地址不支持位访问: {full}");
            if (type != typeof(bool)) throw new NotSupportedException($"HT 地址只能读取 bool 类型，但目标是 {type.Name}");
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 1023)
                throw new ArgumentOutOfRangeException($"HT 编号无效: {numPart}");
            return new ModbusAddressInfo { ModbusType = "C", StartAddress = (ushort)(0xE100 + num), Length = 1 };
        }

        private ModbusAddressInfo ParseHC(string full, string numPart, string bitPart, Type type)
        {
            if (bitPart != null) throw new FormatException($"HC 地址不支持位访问: {full}");
            if (type != typeof(bool)) throw new NotSupportedException($"HC 地址只能读取 bool 类型，但目标是 {type.Name}");
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 1023)
                throw new ArgumentOutOfRangeException($"HC 编号无效: {numPart}");
            return new ModbusAddressInfo { ModbusType = "C", StartAddress = (ushort)(0xE500 + num), Length = 1 };
        }

        private ModbusAddressInfo ParseHSC(string full, string numPart, string bitPart, Type type)
        {
            if (bitPart != null) throw new FormatException($"HSC 地址不支持位访问: {full}");
            if (type != typeof(bool)) throw new NotSupportedException($"HSC 地址只能读取 bool 类型，但目标是 {type.Name}");
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 39)
                throw new ArgumentOutOfRangeException($"HSC 编号无效: {numPart}");
            return new ModbusAddressInfo { ModbusType = "C", StartAddress = (ushort)(0xE900 + num), Length = 1 };
        }

        // ---------- 字对象解析 ----------
        private ModbusAddressInfo ParseD(string full, string numPart, string bitPart, Type type)
        {
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 20479)
                throw new FormatException($"无效的 D 编号: {numPart}");
            return CreateWordResult("H", (ushort)num, bitPart, type);
        }

        private ModbusAddressInfo ParseID(string full, string numPart, string bitPart, Type type)
        {
            int addr;
            if (numPart.Length <= 3) // 本体 ID0~ID99
            {
                if (!int.TryParse(numPart, out int num) || num < 0 || num > 99)
                    throw new FormatException($"无效的 ID 本体编号: {numPart}");
                addr = 0x5000 + num;
            }
            else if (numPart.Length >= 5) // 扩展模块 ID10000~ID11599
            {
                string moduleStr = numPart.Substring(0, 3);
                string offsetStr = numPart.Substring(3);
                if (!int.TryParse(moduleStr, out int module) || module < 100 || module > 115)
                    throw new FormatException($"无效的模块号: {moduleStr}");
                if (!int.TryParse(offsetStr, out int offset) || offset < 0 || offset > 99)
                    throw new FormatException($"无效的偏移: {offsetStr}");
                int moduleIndex = module - 100;
                int baseAddr = 0x5100 + moduleIndex * 0x40;
                addr = baseAddr + offset;
            }
            else
            {
                throw new FormatException($"无效的 ID 地址格式: {full}");
            }
            return CreateWordResult("I", (ushort)addr, bitPart, type);
        }

        private ModbusAddressInfo ParseQD(string full, string numPart, string bitPart, Type type)
        {
            int addr;
            if (numPart.Length <= 3) // 本体 QD0~QD99
            {
                if (!int.TryParse(numPart, out int num) || num < 0 || num > 99)
                    throw new FormatException($"无效的 QD 本体编号: {numPart}");
                addr = 0x6000 + num;
            }
            else if (numPart.Length >= 5) // 扩展模块 QD10000~QD11599
            {
                string moduleStr = numPart.Substring(0, 3);
                string offsetStr = numPart.Substring(3);
                if (!int.TryParse(moduleStr, out int module) || module < 100 || module > 115)
                    throw new FormatException($"无效的模块号: {moduleStr}");
                if (!int.TryParse(offsetStr, out int offset) || offset < 0 || offset > 99)
                    throw new FormatException($"无效的偏移: {offsetStr}");
                int moduleIndex = module - 100;
                int baseAddr = 0x6100 + moduleIndex * 0x40;
                addr = baseAddr + offset;
            }
            else
            {
                throw new FormatException($"无效的 QD 地址格式: {full}");
            }
            return CreateWordResult("H", (ushort)addr, bitPart, type);
        }

        private ModbusAddressInfo ParseSD(string full, string numPart, string bitPart, Type type)
        {
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 4095)
                throw new FormatException($"无效的 SD 编号: {numPart}");
            return CreateWordResult("H", (ushort)(0x7000 + num), bitPart, type);
        }

        private ModbusAddressInfo ParseTD(string full, string numPart, string bitPart, Type type)
        {
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 4095)
                throw new FormatException($"无效的 TD 编号: {numPart}");
            return CreateWordResult("H", (ushort)(0x8000 + num), bitPart, type);
        }

        private ModbusAddressInfo ParseCD(string full, string numPart, string bitPart, Type type)
        {
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 4095)
                throw new FormatException($"无效的 CD 编号: {numPart}");
            return CreateWordResult("H", (ushort)(0x9000 + num), bitPart, type);
        }

        private ModbusAddressInfo ParseETD(string full, string numPart, string bitPart, Type type)
        {
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 39)
                throw new FormatException($"无效的 ETD 编号: {numPart}");
            return CreateWordResult("H", (ushort)(0xA000 + num), bitPart, type);
        }

        private ModbusAddressInfo ParseHD(string full, string numPart, string bitPart, Type type)
        {
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 6143)
                throw new FormatException($"无效的 HD 编号: {numPart}");
            return CreateWordResult("H", (ushort)(0xA080 + num), bitPart, type);
        }

        private ModbusAddressInfo ParseHSD(string full, string numPart, string bitPart, Type type)
        {
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 1023)
                throw new FormatException($"无效的 HSD 编号: {numPart}");
            return CreateWordResult("H", (ushort)(0xB880 + num), bitPart, type);
        }

        private ModbusAddressInfo ParseHTD(string full, string numPart, string bitPart, Type type)
        {
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 1023)
                throw new FormatException($"无效的 HTD 编号: {numPart}");
            return CreateWordResult("H", (ushort)(0xBC80 + num), bitPart, type);
        }

        private ModbusAddressInfo ParseHCD(string full, string numPart, string bitPart, Type type)
        {
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 1023)
                throw new FormatException($"无效的 HCD 编号: {numPart}");
            return CreateWordResult("H", (ushort)(0xC080 + num), bitPart, type);
        }

        private ModbusAddressInfo ParseHSCD(string full, string numPart, string bitPart, Type type)
        {
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 39)
                throw new FormatException($"无效的 HSCD 编号: {numPart}");
            return CreateWordResult("H", (ushort)(0xC480 + num), bitPart, type);
        }

        private ModbusAddressInfo ParseFD(string full, string numPart, string bitPart, Type type)
        {
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 8191)
                throw new FormatException($"无效的 FD 编号: {numPart}");
            return CreateWordResult("H", (ushort)(0xC4C0 + num), bitPart, type);
        }

        private ModbusAddressInfo ParseSFD(string full, string numPart, string bitPart, Type type)
        {
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 4095)
                throw new FormatException($"无效的 SFD 编号: {numPart}");
            return CreateWordResult("H", (ushort)(0xE4C0 + num), bitPart, type);
        }

        private ModbusAddressInfo ParseFS(string full, string numPart, string bitPart, Type type)
        {
            if (!int.TryParse(numPart, out int num) || num < 0 || num > 47)
                throw new FormatException($"无效的 FS 编号: {numPart}");
            return CreateWordResult("H", (ushort)(0xF4C0 + num), bitPart, type);
        }

        // 辅助方法：创建字对象结果
        private ModbusAddressInfo CreateWordResult(string modbusType, ushort baseAddr, string bitPart, Type targetType)
        {
            var result = new ModbusAddressInfo
            {
                ModbusType = modbusType,
                StartAddress = baseAddr,
                ByteOffset = -1,
                BitPosition = -1
            };

            if (bitPart != null) // 位访问
            {
                if (targetType != typeof(bool))
                    throw new InvalidOperationException($"位访问只能用于 bool 类型，但目标是 {targetType.Name}");
                if (!int.TryParse(bitPart, out int bit) || bit < 0 || bit > 15)
                    throw new FormatException($"无效的位索引: {bitPart}");
                result.BitPosition = bit;
                result.Length = 1;
            }
            else
            {
                result.Length = (ushort)GetRegisterLength(targetType);
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

        // 模块号有效性检查
        private bool IsValidXModule(int module)
        {
            // 允许的模块号：100~107, 110~117
            return (module >= 100 && module <= 107) || (module >= 110 && module <= 117);
        }

        private int GetXModuleIndex(int module)
        {
            if (module <= 107) return module - 100;
            else return module - 110 + 8; // 110 对应索引 8
        }

        private bool IsValidYModule(int module) => IsValidXModule(module); // Y 模块规则同 X
        private int GetYModuleIndex(int module) => GetXModuleIndex(module);
    }
}