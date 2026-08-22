using System.Collections.Generic;

namespace MyPLCFoundationLibrary.Historian;

/// <summary>
/// 过程历史库配置。
/// </summary>
public sealed class HistorianConfig
{
    /// <summary>采样间隔（毫秒），默认 1000ms。</summary>
    public int SamplingIntervalMs { get; set; } = 1000;

    /// <summary>原始数据保留天数，超过则清理，默认 30 天。聚合数据长期保留。</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>是否启用聚合（分钟/小时级压缩），默认开启。</summary>
    public bool AggregationEnabled { get; set; } = true;

    /// <summary>聚合桶大小（毫秒），默认 60000（1 分钟）。</summary>
    public int AggregationIntervalMs { get; set; } = 60_000;

    /// <summary>需要采样的标签列表（与采样器产出的 Tag 对应）。</summary>
    public List<string> Tags { get; set; } = new();
}
