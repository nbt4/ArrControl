using ArrControl.Application.Automation;
using ArrControl.Application.Providers;

namespace ArrControl.Application.Catalog;

public static class CatalogJobTypes
{
    public const string Sync = "catalog.sync";
    public const string CheckpointStream = "catalog";
}

public static class CatalogItemKinds
{
    public const string Movie = "movie";
    public const string Series = "series";
    public const string Season = "season";
    public const string Episode = "episode";
    public const string Artist = "artist";
    public const string Album = "album";
    public const string Author = "author";
    public const string Book = "book";

    public static IReadOnlySet<string> Searchable { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Movie, Episode, Album, Book };
}

public sealed record CatalogItemSnapshot(
    string ProviderKey,
    string Kind,
    string? ParentProviderKey,
    string Title,
    int? Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    bool Monitored,
    bool? HasFile,
    string Status,
    DateTimeOffset? AvailableAt,
    DateTimeOffset? SourceAddedAt,
    IReadOnlyDictionary<string, string> ExternalIds,
    IReadOnlyDictionary<string, object?> ProviderData);

public sealed record ProviderCatalogSnapshot(
    DateTimeOffset ObservedAt,
    IReadOnlyList<CatalogItemSnapshot> Items);

public interface IProviderCatalogClient
{
    string Kind { get; }

    Task<ProviderCallResult<ProviderCatalogSnapshot>> GetCatalogAsync(
        ProviderConnection connection,
        CancellationToken cancellationToken);
}

public sealed record CatalogSyncTarget(
    Guid InstanceId,
    string Kind,
    ProviderConnection Connection);

public interface ICatalogSyncTargetResolver
{
    Task<CatalogSyncTarget?> ResolveAsync(
        Guid instanceId,
        CancellationToken cancellationToken);
}

public sealed record CatalogApplyResult(
    int Added,
    int Updated,
    int Unchanged,
    int Removed,
    string SnapshotHash);

public interface ICatalogSnapshotStore
{
    Task<CatalogApplyResult> ApplyAsync(
        Guid instanceId,
        string providerKind,
        ProviderCatalogSnapshot snapshot,
        CancellationToken cancellationToken);
}

public interface ICatalogScheduleProvisioner
{
    Task<int> ReconcileAsync(CancellationToken cancellationToken);
}

public sealed class CatalogSyncJobHandler(
    ICatalogSyncTargetResolver targetResolver,
    ICatalogSnapshotStore snapshotStore,
    IEnumerable<IProviderCatalogClient> clients) : IScheduledJobHandler
{
    public string Type => CatalogJobTypes.Sync;

    public async Task<JobHandlerResult> ExecuteAsync(
        ClaimedJob job,
        CancellationToken cancellationToken)
    {
        var instanceId = CatalogJobScope.ParseInstanceId(job.ScopeJson);
        var target = await targetResolver.ResolveAsync(instanceId, cancellationToken);
        if (target is null)
        {
            return JobHandlerResult.Completed;
        }

        var client = clients.SingleOrDefault(value =>
            string.Equals(value.Kind, target.Kind, StringComparison.Ordinal));
        if (client is null)
        {
            throw new ScheduledJobException("catalog_provider_unsupported");
        }

        ProviderCallResult<ProviderCatalogSnapshot> result;
        try
        {
            result = await client.GetCatalogAsync(target.Connection, cancellationToken);
        }
        catch (ProviderTransportException exception)
        {
            throw new ScheduledJobException($"catalog_{exception.Code}");
        }

        if (!result.Success || result.Value is null)
        {
            throw new ScheduledJobException($"catalog_{result.ErrorCode ?? ProviderErrorCodes.Unknown}");
        }

        var applied = await snapshotStore.ApplyAsync(
            instanceId,
            target.Kind,
            result.Value,
            cancellationToken);
        var cursor = System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 1,
            strategy = "snapshot-diff",
            observedAt = result.Value.ObservedAt,
            snapshotHash = applied.SnapshotHash,
            itemCount = result.Value.Items.Count,
        });
        return new JobHandlerResult(
        [
            new SyncCheckpointUpdate(instanceId, CatalogJobTypes.CheckpointStream, cursor),
        ]);
    }
}

public static class CatalogJobScope
{
    public static string Create(Guid instanceId) =>
        System.Text.Json.JsonSerializer.Serialize(new { instanceId });

    public static Guid ParseInstanceId(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                && root.TryGetProperty("instanceId", out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String
                && Guid.TryParse(value.GetString(), out var instanceId)
                && instanceId != Guid.Empty
                && root.EnumerateObject().Count() == 1)
            {
                return instanceId;
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        throw new ScheduledJobException("catalog_scope_invalid");
    }
}
