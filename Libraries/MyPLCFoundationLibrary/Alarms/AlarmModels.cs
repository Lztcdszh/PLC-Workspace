namespace MyPLCFoundationLibrary.Alarms;

/// <summary>报警严重级别（值用于排序/存储）。</summary>
public enum AlarmSeverity : int
{
    Info = 0,
    Warning = 1,
    Minor = 2,
    Major = 3,
    Critical = 4
}

/// <summary>报警状态机：激活 → 已确认 → 已清除。</summary>
public enum AlarmState : int
{
    Active = 0,
    Acknowledged = 1,
    Cleared = 2
}

/// <summary>一条报警记录（持久化）。</summary>
public sealed class AlarmRecord
{
    public long Id { get; set; }
    public string Tag { get; set; } = string.Empty;
    public AlarmSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime ActiveTs { get; set; }
    public DateTime? AckTs { get; set; }
    public string AckUser { get; set; } = string.Empty;
    public DateTime? ClearTs { get; set; }
    public AlarmState State { get; set; }

    /// <summary>是否已激活（未清除）。</summary>
    public bool IsActive => State != AlarmState.Cleared;
    /// <summary>是否已确认。</summary>
    public bool IsAcknowledged => State == AlarmState.Acknowledged || State == AlarmState.Cleared;
}
