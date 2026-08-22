namespace MyPLCFoundationLibrary.Historian;

/// <summary>
/// 一次采样输入：标签名 + 数值 + 质量。由采样器（从实时标签读取）产出。
/// </summary>
public sealed class SampleInput
{
    /// <summary>标签名（与配置一致，例如 "DB1.DBW240" 或 "温度1"）。</summary>
    public string Tag { get; init; } = string.Empty;

    /// <summary>采样数值。</summary>
    public double Value { get; init; }

    /// <summary>质量是否良好（坏点不进入聚合）。</summary>
    public bool Good { get; init; } = true;
}

/// <summary>原始采样点（查询返回）。</summary>
public sealed class HistorianRawPoint
{
    public long Ts { get; init; }
    public double Value { get; init; }
    public bool Good { get; init; }
}

/// <summary>聚合采样点（查询返回）。</summary>
public sealed class HistorianAggPoint
{
    public long BucketStart { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Avg { get; init; }
    public double Last { get; init; }
    public int Count { get; init; }
}
