using ArrControl.Application.Authorization;

namespace ArrControl.Application.Activity;

public sealed record AggregatedQueueItem(
    Guid InstanceId,
    string InstanceName,
    string ProviderKind,
    string ProviderKey,
    string? MediaProviderKey,
    string Title,
    string Status,
    string TrackedStatus,
    string TrackedState,
    string? Protocol,
    long SizeBytes,
    long RemainingBytes,
    DateTimeOffset? AddedAt,
    DateTimeOffset? EstimatedCompletionAt,
    string? DownloadClient,
    string? Indexer,
    int CorrelatedHistoryCount,
    string? LatestHistoryEvent,
    ImportFailureClassification? ImportFailure,
    DateTimeOffset ObservedAt,
    bool Stale);

public sealed record AggregatedHistoryItem(
    Guid InstanceId,
    string InstanceName,
    string ProviderKind,
    string ProviderKey,
    string? MediaProviderKey,
    string Title,
    string EventType,
    DateTimeOffset EventAt,
    bool QueueCorrelated,
    ImportFailureClassification? ImportFailure,
    DateTimeOffset ObservedAt,
    bool Stale);

public sealed record ActivitySnapshot(
    IReadOnlyList<AggregatedQueueItem> Queue,
    IReadOnlyList<AggregatedHistoryItem> History);

public interface IActivityQueryStore
{
    Task<ActivitySnapshot> QueryAsync(
        bool includeAll,
        IReadOnlyCollection<Guid> instanceGroupIds,
        IReadOnlyCollection<Guid> instanceIds,
        int historyLimit,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public sealed class ActivityQueryService(
    RbacAuthorizationService authorization,
    IActivityQueryStore store,
    TimeProvider timeProvider)
{
    public async Task<ActivitySnapshot?> QueryAsync(
        Guid userId,
        Guid sessionId,
        IReadOnlyCollection<Guid> instanceIds,
        int historyLimit,
        CancellationToken cancellationToken)
    {
        if (instanceIds.Count > 100 || historyLimit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(historyLimit));
        }

        var snapshot = await authorization.GetSnapshotAsync(userId, sessionId, cancellationToken);
        var grant = snapshot.Grants.SingleOrDefault(value =>
            value.PermissionCode == RbacPermissions.InstancesRead);
        return grant is null
            ? null
            : await store.QueryAsync(
                grant.IsGlobal,
                grant.InstanceGroupIds,
                instanceIds,
                historyLimit,
                timeProvider.GetUtcNow(),
                cancellationToken);
    }
}
