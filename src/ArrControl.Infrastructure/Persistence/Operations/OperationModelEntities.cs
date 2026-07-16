namespace ArrControl.Infrastructure.Persistence.Operations;

public sealed class OperationEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ActorUserId { get; set; }
    public required string Type { get; set; }
    public required string Route { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestHash { get; set; }
    public required string State { get; set; }
    public bool DryRun { get; set; }
    public bool CancellationRequested { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset IdempotencyExpiresAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public ICollection<OperationTargetEntity> Targets { get; } = new List<OperationTargetEntity>();
}

public sealed class OperationTargetEntity
{
    public Guid OperationId { get; set; }
    public Guid InstanceId { get; set; }
    public required string TargetKey { get; set; }
    public required string State { get; set; }
    public string? ErrorCode { get; set; }
    public string? ResultJson { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public OperationEntity Operation { get; set; } = null!;
}
