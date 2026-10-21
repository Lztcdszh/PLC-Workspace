using System;
using System.Collections.Generic;

namespace PLCTesting.Services
{
    /// <summary>
    /// 日志查询条件。所有条件都是可选的，留空即不限制。
    /// 单独抽成一个对象，避免方法签名随筛选维度增加而不断膨胀
    /// （原来 Query(address, from, to, pageSize, pageIndex) 已经加不下关键字/排序/只看变化了）。
    /// </summary>
    public class ChangeLogQuery
    {
        /// <summary>PLC 地址，精确匹配；空或 null 表示全部。</summary>
        public string Address { get; set; }

        /// <summary>关键字，模糊匹配 标签名 / 地址 / 旧值 / 新值 / 描述。</summary>
        public string Keyword { get; set; }

        /// <summary>起始时间（含）。</summary>
        public DateTime? From { get; set; }

        /// <summary>结束时间（含）。</summary>
        public DateTime? To { get; set; }

        /// <summary>只看真正发生了变化的记录（排除 旧值 == 新值 的噪音）。</summary>
        public bool ChangedOnly { get; set; }

        /// <summary>true=时间升序，false=时间降序（默认，最新的在前）。</summary>
        public bool Ascending { get; set; }

        /// <summary>每页条数。</summary>
        public int PageSize { get; set; } = 100;

        /// <summary>页码，从 0 开始。</summary>
        public int PageIndex { get; set; }

        /// <summary>复制一份，避免调用方在异步查询过程中改到正在用的条件。</summary>
        public ChangeLogQuery Clone() => (ChangeLogQuery)MemberwiseClone();
    }

    /// <summary>
    /// 地址维度统计：库里实际有哪些地址、各有多少条、对应的标签名是什么。
    /// 用于把地址下拉做成动态的 —— 原来界面上硬编码了 3 个地址，
    /// 新增数据点后下拉里根本看不到，用户以为没数据。
    /// </summary>
    public class AddressStat
    {
        public string Address { get; set; } = string.Empty;

        /// <summary>该地址最近一次记录里出现过的标签名（可能等于地址本身）。</summary>
        public string TagName { get; set; } = string.Empty;

        public int Count { get; set; }

        /// <summary>下拉框显示文本：全部地址 (N) / 地址 · 标签名 (条数)</summary>
        public string Display
        {
            get
            {
                if (string.IsNullOrEmpty(Address))
                    return $"全部地址 ({Count})";

                var name = TagName;
                var showName = !string.IsNullOrWhiteSpace(name)
                               && !string.Equals(name, Address, StringComparison.OrdinalIgnoreCase);
                return showName
                    ? $"{Address} · {name} ({Count})"
                    : $"{Address} ({Count})";
            }
        }

        /// <summary>
        /// ⚠️ 必须重写：AppTheme.xaml 里 ComboBox 用的是自定义 ControlTemplate，
        /// 其 SelectionBoxItem 的呈现不保证走 DisplayMemberPath，会退回 ToString()
        /// —— 表现就是下拉框里显示成 "PLCTesting.Services.AddressStat" 这种类名。
        /// 重写 ToString 后无论模板怎么实现都能显示正确文本。
        /// </summary>
        public override string ToString() => Display;

        /// <summary>下拉框里「全部地址」的占位项。</summary>
        public static AddressStat All(int total) =>
            new AddressStat { Address = null, TagName = string.Empty, Count = total };
    }

    /// <summary>
    /// 一次分页查询的结果：总数 + 当前页数据。
    /// 合并返回可以少一次 Count 往返，也保证总数与列表来自同一次筛选条件。
    /// </summary>
    public class ChangeLogPage
    {
        public int Total { get; set; }
        public List<Models.ChangeLogRecord> Items { get; set; } = new List<Models.ChangeLogRecord>();
    }
}
