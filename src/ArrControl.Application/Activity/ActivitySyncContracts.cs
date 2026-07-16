using ArrControl.Application.Automation;
using ArrControl.Application.Providers;

namespace ArrControl.Application.Activity;

public static class ActivityJobTypes
{
    public const string Sync = "activity.sync";
    public const string CheckpointStream = "activity";
}

public sealed record QueueItemSnapshot(
    string ProviderKey,
    string? MediaProviderKey,
    string? DownloadId,
    string Title,
    string Status,
    string TrackedStatus,
    string TrackedState,
    string? Protocol,
    double SizeBytes,
    double RemainingBytes,
    DateTimeOffset? AddedAt,
    DateTimeOffset? EstimatedCompletionAt,
    string? DownloadClient,
    string? Indexer);

public sealed record HistoryItemSnapshot(
    string ProviderKey,
    string? MediaProviderKey,
    string? DownloadId,
    string Title,
    string EventType,
    DateTimeOffset EventAt);

public sealed record ProviderActivitySnapshot(
    DateTimeOffset ObservedAt,
    IReadOnlyList<QueueItemSnapshot> Queue,
    IReadOnlyList<HistoryItemSnapshot> History);

public interface IProviderActivityClient
{
    string Kind { get; }

    Task<ProviderCallResult<ProviderActivitySnapshot>> GetActivityAsync(
        ProviderConnection connection,
        CancellationToken cancellationToken);
}

public interface IActivitySnapshotStore
{
    Task ApplyAsync(
        Guid instanceId,
        string providerKind,
        ProviderActivitySnapshot snapshot,
        CancellationToken cancellationToken);
}

public interface IActivityScheduleProvisioner
{
    Task<int> ReconcileAsync(CancellationToken cancellationToken);
}

public sealed class ActivitySyncJobHandler(
    ArrControl.Application.Catalog.ICatalogSyncTargetResolver targetResolver,
    IActivitySnapshotStore store,
    IEnumerable<IProviderActivityClient> clients) : IScheduledJobHandler
{
    public string Type => ActivityJobTypes.Sync;

    public async Task<JobHandlerResult> ExecuteAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        Guid instanceId;
        try
        {
            instanceId = ArrControl.Application.Catalog.CatalogJobScope.ParseInstanceId(job.ScopeJson);
        }
        catch (ScheduledJobException exception) when (exception.Code == "catalog_scope_invalid")
        {
            throw new ScheduledJobException("activity_scope_invalid");
        }
        var target = await targetResolver.ResolveAsync(instanceId, cancellationToken);
        if (target is null)
        {
            return JobHandlerResult.Completed;
        }

        var client = clients.SingleOrDefault(value => value.Kind == target.Kind);
        if (client is null)
        {
            throw new ScheduledJobException("activity_provider_unsupported");
        }

        ProviderCallResult<ProviderActivitySnapshot> result;
        try
        {
            result = await client.GetActivityAsync(target.Connection, cancellationToken);
        }
        catch (ProviderTransportException exception)
        {
            throw new ScheduledJobException($"activity_{exception.Code}");
        }

        if (!result.Success || result.Value is null)
        {
            throw new ScheduledJobException($"activity_{result.ErrorCode ?? ProviderErrorCodes.Unknown}");
        }

        await store.ApplyAsync(instanceId, target.Kind, result.Value, cancellationToken);
        return new JobHandlerResult(
        [
            new SyncCheckpointUpdate(
                instanceId,
                ActivityJobTypes.CheckpointStream,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    version = 1,
                    observedAt = result.Value.ObservedAt,
                    queueCount = result.Value.Queue.Count,
                    historyCount = result.Value.History.Count,
                })),
        ]);
    }
}
