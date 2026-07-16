using System.Net;
using ArrControl.Infrastructure.Persistence.Identity;

namespace ArrControl.Infrastructure.Persistence.Operations;

public sealed class AuditEventEntity
{
    public DateTimeOffset OccurredAt { get; set; }
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? ActorUserId { get; set; }
    public required string ActorType { get; set; }
    public required string ActorIdentifier { get; set; }
    public required string Action { get; set; }
    public required string ScopeJson { get; set; }
    public required string CorrelationId { get; set; }
    public required string Outcome { get; set; }
    public required string SummaryJson { get; set; }
    public IPAddress? IpAddress { get; set; }
    public UserEntity? ActorUser { get; set; }
}

public sealed class OutboxMessageEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Type { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastErrorCode { get; set; }
}
