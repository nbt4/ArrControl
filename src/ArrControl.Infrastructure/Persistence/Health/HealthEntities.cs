namespace ArrControl.Infrastructure.Persistence.Health;

public sealed class HealthIncidentEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid InstanceId { get; set; }
    public required string GroupKey { get; set; }
    public required string ProviderKind { get; set; }
    public required string Severity { get; set; }
    public string? RemediationUrl { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public Guid? AcknowledgedByUserId { get; set; }
    public DateTimeOffset? SnoozedUntil { get; set; }
    public Guid? SnoozedByUserId { get; set; }
    public ICollection<HealthIncidentSourceEntity> Sources { get; } = new List<HealthIncidentSourceEntity>();
}

public sealed class HealthIncidentSourceEntity
{
    public Guid IncidentId { get; set; }
    public required string SourceKey { get; set; }
    public int ProviderIssueId { get; set; }
    public required string Source { get; set; }
    public required string Severity { get; set; }
    public string? Message { get; set; }
    public string? RemediationUrl { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public bool Active { get; set; }
    public HealthIncidentEntity Incident { get; set; } = null!;
}
