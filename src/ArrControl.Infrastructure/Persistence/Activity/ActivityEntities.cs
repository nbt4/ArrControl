namespace ArrControl.Infrastructure.Persistence.Activity;

public sealed class QueueItemEntity
{
    public Guid InstanceId { get; set; }
    public required string ProviderKey { get; set; }
    public required string ProviderKind { get; set; }
    public string? MediaProviderKey { get; set; }
    public string? DownloadId { get; set; }
    public string? CorrelationKey { get; set; }
    public required string Title { get; set; }
    public required string Status { get; set; }
    public required string TrackedStatus { get; set; }
    public required string TrackedState { get; set; }
    public string? Protocol { get; set; }
    public long SizeBytes { get; set; }
    public long RemainingBytes { get; set; }
    public DateTimeOffset? AddedAt { get; set; }
    public DateTimeOffset? EstimatedCompletionAt { get; set; }
    public string? DownloadClient { get; set; }
    public string? Indexer { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
}

public sealed class HistoryItemEntity
{
    public Guid InstanceId { get; set; }
    public required string ProviderKey { get; set; }
    public required string ProviderKind { get; set; }
    public string? MediaProviderKey { get; set; }
    public string? DownloadId { get; set; }
    public string? CorrelationKey { get; set; }
    public required string Title { get; set; }
    public required string EventType { get; set; }
    public DateTimeOffset EventAt { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
}
