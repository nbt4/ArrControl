namespace ArrControl.Infrastructure.Persistence.Automation;

public sealed class ScheduleEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Type { get; set; }
    public required string Cron { get; set; }
    public required string TimeZone { get; set; }
    public required string ScopeJson { get; set; }
    public string? ScopeKey { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastEnqueuedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<JobRunEntity> JobRuns { get; } = new List<JobRunEntity>();
}

public sealed class JobRunEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ScheduleId { get; set; }
    public required string State { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset ScheduledFor { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ScheduleEntity Schedule { get; set; } = null!;
}

public sealed class SyncCheckpointEntity
{
    public Guid InstanceId { get; set; }
    public required string Stream { get; set; }
    public string? Cursor { get; set; }
    public DateTimeOffset LastSuccessAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
