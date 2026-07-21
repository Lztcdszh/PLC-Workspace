using System;

namespace PLCTesting.Models
{
    /// <summary>
    /// PLC 标签变化日志记录。
    /// </summary>
    public class ChangeLogRecord
    {
        public long Id { get; set; }

        /// <summary>PLC 地址，例如 DB1.DBW230 / DB1.DBB172</summary>
        public string TagAddress { get; set; } = string.Empty;

        /// <summary>标签名称（与 PLC 地址一致或自定义）</summary>
        public string TagName { get; set; } = string.Empty;

        /// <summary>变化前的值字符串</summary>
        public string OldValue { get; set; } = string.Empty;

        /// <summary>变化后的值字符串</summary>
        public string NewValue { get; set; } = string.Empty;

        /// <summary>数据类型，例如 Int16 / UInt8</summary>
        public string DataType { get; set; } = string.Empty;

        /// <summary>变化发生时间（本地时间）</summary>
        public DateTime TimeStamp { get; set; }
    }
}
