using System;
using System.ComponentModel;

namespace MyPLCManagerLibrary.Model
{
    public class PLCTag : INotifyPropertyChanged
    {
        private object _value;
        private DateTime _lastUpdate;

        public event PropertyChangedEventHandler PropertyChanged;

        public string Name { get; set; }
        public string Address { get; set; }
        public Type DataType { get; set; }
        public string Description { get; set; }

        public object Value
        {
            get => _value;
            set
            {
                if (!Equals(_value, value))
                {
                    _value = value;
                    LastUpdate = DateTime.Now;
                    OnPropertyChanged(nameof(Value));
                }
            }
        }

        public DateTime LastUpdate
        {
            get => _lastUpdate;
            private set
            {
                _lastUpdate = value;
                OnPropertyChanged(nameof(LastUpdate));
            }
        }
        public void RefreshUI()
        {
            OnPropertyChanged(nameof(Value));
           
        }
        public string LastUpdateText => LastUpdate.ToString("HH:mm:ss.fff");

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // 布尔类型标签
        public static PLCTag CreateBool(string name, string address, string description = "")
        {
            return new PLCTag
            {
                Name = name,
                Address = address,
                DataType = typeof(bool),
                Description = description,
                Value = false
            };
        }

        // 32位整数类型标签
        public static PLCTag CreateInt(string name, string address, string description = "")
        {
            return new PLCTag
            {
                Name = name,
                Address = address,
                DataType = typeof(int),
                Description = description,
                Value = 0
            };
        }

        // 单精度浮点类型标签
        public static PLCTag CreateFloat(string name, string address, string description = "")
        {
            return new PLCTag
            {
                Name = name,
                Address = address,
                DataType = typeof(float),
                Description = description,
                Value = 0.0f
            };
        }

        // 16位无符号整数类型标签 (Word)
        public static PLCTag CreateWord(string name, string address, string description = "")
        {
            return new PLCTag
            {
                Name = name,
                Address = address,
                DataType = typeof(ushort),
                Description = description,
                Value = (ushort)0
            };
        }

        // 16位有符号整数类型标签
        public static PLCTag CreateShort(string name, string address, string description = "")
        {
            return new PLCTag
            {
                Name = name,
                Address = address,
                DataType = typeof(short),
                Description = description,
                Value = (short)0
            };
        }

        // 8位无符号整数类型标签 (Byte)
        public static PLCTag CreateByte(string name, string address, string description = "")
        {
            return new PLCTag
            {
                Name = name,
                Address = address,
                DataType = typeof(byte),
                Description = description,
                Value = (byte)0
            };
        }

        // 32位无符号整数类型标签 (DWord)
        public static PLCTag CreateDWord(string name, string address, string description = "")
        {
            return new PLCTag
            {
                Name = name,
                Address = address,
                DataType = typeof(uint),
                Description = description,
                Value = (uint)0
            };
        }

        // 双精度浮点类型标签
        public static PLCTag CreateDouble(string name, string address, string description = "")
        {
            return new PLCTag
            {
                Name = name,
                Address = address,
                DataType = typeof(double),
                Description = description,
                Value = 0.0
            };
        }

        // 字符串类型标签
        public static PLCTag CreateString(string name, string address, int length, string description = "")
        {
            return new PLCTag
            {
                Name = name,
                Address = address,
                DataType = typeof(string),
                Description = description,
                Value = string.Empty
            };
        }

        // 通用的创建方法，可以指定任意类型
        public static PLCTag Create<T>(string name, string address, T defaultValue = default, string description = "")
        {
            return new PLCTag
            {
                Name = name,
                Address = address,
                DataType = typeof(T),
                Description = description,
                Value = defaultValue
            };
        }

        // 获取值的类型安全版本
        public T GetValue<T>()
        {
            try
            {
                if (Value is T typedValue)
                {
                    return typedValue;
                }
                else if (Value != null)
                {
                    // 尝试类型转换
                    return (T)Convert.ChangeType(Value, typeof(T));
                }
                return default;
            }
            catch
            {
                return default;
            }
        }

        // 判断是否是特定类型
        public bool IsBoolType => DataType == typeof(bool);
        public bool IsIntType => DataType == typeof(int);
        public bool IsFloatType => DataType == typeof(float);
        public bool IsWordType => DataType == typeof(ushort);
        public bool IsShortType => DataType == typeof(short);
        public bool IsByteType => DataType == typeof(byte);
        public bool IsDWordType => DataType == typeof(uint);
        public bool IsDoubleType => DataType == typeof(double);
        public bool IsStringType => DataType == typeof(string);

        // 获取值的安全字符串表示
        public string ValueString => Value?.ToString() ?? "null";

        // 获取标签的完整信息
        public string FullInfo => $"名称: {Name}, 地址: {Address}, 类型: {DataType.Name}, 值: {ValueString}, 描述: {Description}";

        public bool IsMonitored { get; set; } = true;
    }
}