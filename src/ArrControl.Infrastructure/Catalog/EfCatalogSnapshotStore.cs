using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArrControl.Application.Automation;
using ArrControl.Application.Catalog;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Catalog;

public sealed class EfCatalogSnapshotStore(ArrControlDbContext dbContext) : ICatalogSnapshotStore
{
    private const int MaximumItems = 250_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CatalogApplyResult> ApplyAsync(
        Guid instanceId,
        string providerKind,
        ProviderCatalogSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        Validate(snapshot);
        var prepared = snapshot.Items.Select(Prepare).ToArray();
        var keys = prepared.Select(value => value.Item.ProviderKey).ToHashSet(StringComparer.Ordinal);
        if (keys.Count != prepared.Length
            || prepared.Any(value => value.Item.ParentProviderKey is not null
                && !keys.Contains(value.Item.ParentProviderKey)))
        {
            throw new ScheduledJobException("catalog_snapshot_invalid");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var existing = await dbContext.Set<ProviderItemEntity>()
            .Include(value => value.MediaEntity)
            .Include(value => value.MissingItem)
            .Where(value => value.InstanceId == instanceId)
            .ToDictionaryAsync(value => value.ProviderKey, StringComparer.Ordinal, cancellationToken);

        var added = 0;
        var updated = 0;
        var unchanged = 0;
        foreach (var value in prepared)
        {
            if (!existing.TryGetValue(value.Item.ProviderKey, out var providerItem))
            {
                providerItem = new ProviderItemEntity
                {
                    InstanceId = instanceId,
                    ProviderKey = value.Item.ProviderKey,
                    ProviderKind = providerKind,
                    RawKind = value.Item.Kind,
                    ParentProviderKey = value.Item.ParentProviderKey,
                    ProviderDataJson = value.ProviderDataJson,
                    Fingerprint = value.Fingerprint,
                    FirstSeenAt = snapshot.ObservedAt,
                    UpdatedAt = snapshot.ObservedAt,
                    MediaEntity = CreateMedia(instanceId, value),
                };
                providerItem.MissingItem = CreateMissing(instanceId, value, snapshot.ObservedAt);
                dbContext.Add(providerItem);
                added++;
                continue;
            }

            if (string.Equals(providerItem.Fingerprint, value.Fingerprint, StringComparison.Ordinal))
            {
                SyncMissing(providerItem, value, snapshot.ObservedAt);
                unchanged++;
                continue;
            }

            providerItem.ProviderKind = providerKind;
            providerItem.RawKind = value.Item.Kind;
            providerItem.ParentProviderKey = value.Item.ParentProviderKey;
            providerItem.ProviderDataJson = value.ProviderDataJson;
            providerItem.Fingerprint = value.Fingerprint;
            providerItem.UpdatedAt = snapshot.ObservedAt;
            UpdateMedia(providerItem.MediaEntity, value);
            SyncMissing(providerItem, value, snapshot.ObservedAt);
            updated++;
        }

        var removedItems = existing.Values.Where(value => !keys.Contains(value.ProviderKey)).ToArray();
        dbContext.RemoveRange(removedItems);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var snapshotHash = Hash(string.Join(
            '\n',
            prepared.OrderBy(value => value.Item.ProviderKey, StringComparer.Ordinal)
                .Select(value => $"{value.Item.ProviderKey}:{value.Fingerprint}")));
        return new CatalogApplyResult(added, updated, unchanged, removedItems.Length, snapshotHash);
    }

    private static PreparedItem Prepare(CatalogItemSnapshot item)
    {
        var externalIdsJson = JsonSerializer.Serialize(
            item.ExternalIds.OrderBy(value => value.Key, StringComparer.Ordinal)
                .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal),
            JsonOptions);
        var providerDataJson = JsonSerializer.Serialize(
            item.ProviderData.OrderBy(value => value.Key, StringComparer.Ordinal)
                .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal),
            JsonOptions);
        var canonical = JsonSerializer.Serialize(new
        {
            item.ProviderKey,
            item.Kind,
            item.ParentProviderKey,
            item.Title,
            item.Year,
            item.SeasonNumber,
            item.EpisodeNumber,
            item.Monitored,
            item.HasFile,
            item.Status,
            item.AvailableAt,
            item.SourceAddedAt,
            ExternalIds = externalIdsJson,
            ProviderData = providerDataJson,
        }, JsonOptions);
        return new PreparedItem(item, externalIdsJson, providerDataJson, Hash(canonical));
    }

    private static MediaEntityEntity CreateMedia(
        Guid instanceId,
        PreparedItem value)
    {
        var entity = new MediaEntityEntity
        {
            InstanceId = instanceId,
            ProviderKey = value.Item.ProviderKey,
            CanonicalKind = value.Item.Kind,
            Title = value.Item.Title,
            Status = value.Item.Status,
            ExternalIdsJson = value.ExternalIdsJson,
        };
        UpdateMedia(entity, value);
        return entity;
    }

    private static void UpdateMedia(MediaEntityEntity entity, PreparedItem value)
    {
        entity.CanonicalKind = value.Item.Kind;
        entity.Title = value.Item.Title;
        entity.Year = value.Item.Year;
        entity.SeasonNumber = value.Item.SeasonNumber;
        entity.EpisodeNumber = value.Item.EpisodeNumber;
        entity.Monitored = value.Item.Monitored;
        entity.HasFile = value.Item.HasFile;
        entity.Status = value.Item.Status;
        entity.AvailableAt = value.Item.AvailableAt;
        entity.SourceAddedAt = value.Item.SourceAddedAt;
        entity.ExternalIdsJson = value.ExternalIdsJson;
    }

    private static MissingItemEntity? CreateMissing(
        Guid instanceId,
        PreparedItem value,
        DateTimeOffset observedAt)
    {
        var reason = MissingReason(value.Item, observedAt);
        return reason is null
            ? null
            : new MissingItemEntity
            {
                InstanceId = instanceId,
                ProviderKey = value.Item.ProviderKey,
                Reason = reason,
                Monitored = true,
                FirstSeenAt = observedAt,
                UpdatedAt = observedAt,
            };
    }

    private void SyncMissing(
        ProviderItemEntity providerItem,
        PreparedItem value,
        DateTimeOffset observedAt)
    {
        var reason = MissingReason(value.Item, observedAt);
        if (reason is null)
        {
            if (providerItem.MissingItem is not null)
            {
                dbContext.Remove(providerItem.MissingItem);
                providerItem.MissingItem = null;
            }

            return;
        }

        if (providerItem.MissingItem is null)
        {
            providerItem.MissingItem = CreateMissing(providerItem.InstanceId, value, observedAt);
            return;
        }

        if (!string.Equals(providerItem.MissingItem.Reason, reason, StringComparison.Ordinal)
            || !providerItem.MissingItem.Monitored)
        {
            providerItem.MissingItem.Reason = reason;
            providerItem.MissingItem.Monitored = true;
            providerItem.MissingItem.UpdatedAt = observedAt;
        }
    }

    private static string? MissingReason(CatalogItemSnapshot item, DateTimeOffset observedAt)
    {
        if (!CatalogItemKinds.Searchable.Contains(item.Kind)
            || !item.Monitored
            || item.HasFile != false)
        {
            return null;
        }

        return item.AvailableAt is not null && item.AvailableAt > observedAt
            ? MissingReasons.NotAvailable
            : MissingReasons.Missing;
    }

    private static void Validate(ProviderCatalogSnapshot snapshot)
    {
        if (snapshot.Items.Count > MaximumItems
            || snapshot.ObservedAt == default
            || snapshot.Items.Any(item => string.IsNullOrWhiteSpace(item.ProviderKey)
                || item.ProviderKey.Length > 200
                || string.IsNullOrWhiteSpace(item.Kind)
                || item.Kind.Length > 32
                || item.ParentProviderKey?.Length > 200
                || string.IsNullOrWhiteSpace(item.Title)
                || item.Title.Length > 1000
                || item.Status.Length is 0 or > 64
                || item.Year is <= 0
                || item.SeasonNumber is < 0
                || item.EpisodeNumber is < 0))
        {
            throw new ScheduledJobException("catalog_snapshot_invalid");
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record PreparedItem(
        CatalogItemSnapshot Item,
        string ExternalIdsJson,
        string ProviderDataJson,
        string Fingerprint);
}
