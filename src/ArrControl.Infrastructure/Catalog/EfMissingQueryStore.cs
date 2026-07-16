using System.Text.Json;
using ArrControl.Application.Automation;
using ArrControl.Application.Catalog;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Automation;
using ArrControl.Infrastructure.Persistence.Catalog;
using ArrControl.Infrastructure.Persistence.Connections;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Catalog;

public sealed class EfMissingQueryStore(ArrControlDbContext dbContext) : IMissingQueryStore
{
    public async Task<MissingStoreResult> QueryAsync(
        MissingQuerySpec spec,
        CancellationToken cancellationToken)
    {
        var query =
            from missing in dbContext.Set<MissingItemEntity>().AsNoTracking()
            join provider in dbContext.Set<ProviderItemEntity>().AsNoTracking()
                on new { missing.InstanceId, missing.ProviderKey }
                equals new { provider.InstanceId, provider.ProviderKey }
            join media in dbContext.Set<MediaEntityEntity>().AsNoTracking()
                on new { missing.InstanceId, missing.ProviderKey }
                equals new { media.InstanceId, media.ProviderKey }
            join instance in dbContext.Set<InstanceEntity>().AsNoTracking()
                on missing.InstanceId equals instance.Id
            select new { missing, provider, media, instance };

        if (!spec.IncludeAllInstances)
        {
            query = query.Where(value =>
                value.instance.GroupId != null
                && spec.InstanceGroupIds.Contains(value.instance.GroupId.Value));
        }

        if (spec.Filter.InstanceIds.Count > 0)
        {
            query = query.Where(value => spec.Filter.InstanceIds.Contains(value.instance.Id));
        }

        if (spec.Filter.Kinds.Count > 0)
        {
            query = query.Where(value => spec.Filter.Kinds.Contains(value.media.CanonicalKind));
        }

        query = query.Where(value => spec.Filter.Reasons.Contains(value.missing.Reason));
        if (spec.Filter.Search is not null)
        {
            var escaped = spec.Filter.Search
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
            query = query.Where(value => EF.Functions.ILike(value.media.Title, $"%{escaped}%", "\\"));
        }

        if (spec.Cursor is not null)
        {
            query = query.Where(value =>
                string.Compare(value.media.SortTitle, spec.Cursor.SortTitle) > 0
                || (value.media.SortTitle == spec.Cursor.SortTitle
                    && value.media.Id.CompareTo(spec.Cursor.MediaEntityId) > 0));
        }

        var rows = await query
            .OrderBy(value => value.media.SortTitle)
            .ThenBy(value => value.media.Id)
            .Take(spec.Limit)
            .Select(value => new Row(
                value.media.Id,
                value.instance.Id,
                value.instance.Name,
                value.provider.ProviderKind,
                value.provider.ProviderKey,
                value.media.CanonicalKind,
                value.media.Title,
                value.media.Year,
                value.media.SeasonNumber,
                value.media.EpisodeNumber,
                value.missing.Reason,
                value.media.AvailableAt,
                value.media.SourceAddedAt,
                value.media.ExternalIdsJson,
                value.missing.FirstSeenAt,
                value.missing.UpdatedAt,
                dbContext.Set<SyncCheckpointEntity>()
                    .Where(checkpoint => checkpoint.InstanceId == value.instance.Id
                        && checkpoint.Stream == CatalogJobTypes.CheckpointStream)
                    .Select(checkpoint => (DateTimeOffset?)checkpoint.LastSuccessAt)
                    .SingleOrDefault()))
            .ToListAsync(cancellationToken);

        var items = rows.Select(row => new MissingItem(
                row.Id,
                row.InstanceId,
                row.InstanceName,
                row.ProviderKind,
                row.ProviderKey,
                row.Kind,
                row.Title,
                row.Year,
                row.SeasonNumber,
                row.EpisodeNumber,
                row.Reason,
                row.AvailableAt,
                row.SourceAddedAt,
                ReadExternalIds(row.ExternalIdsJson),
                row.FirstSeenAt,
                row.UpdatedAt,
                row.ObservedAt,
                row.ObservedAt is null || spec.Now - row.ObservedAt > spec.StaleAfter))
            .ToArray();

        var instances = dbContext.Set<InstanceEntity>().AsNoTracking()
            .Where(value => value.Kind == "sonarr" || value.Kind == "radarr"
                || value.Kind == "lidarr" || value.Kind == "readarr" || value.Kind == "whisparr");
        if (!spec.IncludeAllInstances)
        {
            instances = instances.Where(value =>
                value.GroupId != null && spec.InstanceGroupIds.Contains(value.GroupId.Value));
        }

        if (spec.Filter.InstanceIds.Count > 0)
        {
            instances = instances.Where(value => spec.Filter.InstanceIds.Contains(value.Id));
        }

        var freshnessRows = await instances
            .OrderBy(value => value.Id)
            .Select(value => new
            {
                value.Id,
                ObservedAt = dbContext.Set<SyncCheckpointEntity>()
                    .Where(checkpoint => checkpoint.InstanceId == value.Id
                        && checkpoint.Stream == CatalogJobTypes.CheckpointStream)
                    .Select(checkpoint => (DateTimeOffset?)checkpoint.LastSuccessAt)
                    .SingleOrDefault(),
            })
            .ToListAsync(cancellationToken);
        var freshness = freshnessRows.Select(value => new MissingFreshness(
                value.Id,
                value.ObservedAt,
                value.ObservedAt is null || spec.Now - value.ObservedAt > spec.StaleAfter))
            .ToArray();
        return new MissingStoreResult(items, freshness);
    }

    private static IReadOnlyDictionary<string, string> ReadExternalIds(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private sealed record Row(
        Guid Id,
        Guid InstanceId,
        string InstanceName,
        string ProviderKind,
        string ProviderKey,
        string Kind,
        string Title,
        int? Year,
        int? SeasonNumber,
        int? EpisodeNumber,
        string Reason,
        DateTimeOffset? AvailableAt,
        DateTimeOffset? SourceAddedAt,
        string ExternalIdsJson,
        DateTimeOffset FirstSeenAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? ObservedAt);
}
