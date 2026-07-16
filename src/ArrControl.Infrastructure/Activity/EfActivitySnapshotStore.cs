using System.Security.Cryptography;
using System.Text;
using ArrControl.Application.Activity;
using ArrControl.Application.Automation;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Activity;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Activity;

public sealed class EfActivitySnapshotStore(ArrControlDbContext dbContext) : IActivitySnapshotStore
{
    public async Task ApplyAsync(
        Guid instanceId,
        string providerKind,
        ProviderActivitySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.Queue.Count > 10_000
            || snapshot.History.Count > 10_000
            || snapshot.Queue.Select(value => value.ProviderKey).Distinct(StringComparer.Ordinal).Count() != snapshot.Queue.Count
            || snapshot.History.Select(value => value.ProviderKey).Distinct(StringComparer.Ordinal).Count() != snapshot.History.Count)
        {
            throw new ScheduledJobException("activity_snapshot_invalid");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Set<QueueItemEntity>()
            .Where(value => value.InstanceId == instanceId)
            .ExecuteDeleteAsync(cancellationToken);
        dbContext.AddRange(snapshot.Queue.Select(value => Map(instanceId, providerKind, value, snapshot.ObservedAt)));

        var history = await dbContext.Set<HistoryItemEntity>()
            .Where(value => value.InstanceId == instanceId)
            .ToDictionaryAsync(value => value.ProviderKey, StringComparer.Ordinal, cancellationToken);
        foreach (var value in snapshot.History)
        {
            if (!history.TryGetValue(value.ProviderKey, out var entity))
            {
                entity = new HistoryItemEntity
                {
                    InstanceId = instanceId,
                    ProviderKey = value.ProviderKey,
                    ProviderKind = providerKind,
                    Title = value.Title,
                    EventType = value.EventType,
                };
                dbContext.Add(entity);
            }

            entity.ProviderKind = providerKind;
            entity.MediaProviderKey = value.MediaProviderKey;
            entity.DownloadId = Limit(value.DownloadId, 512);
            entity.CorrelationKey = Correlation(value.DownloadId);
            entity.Title = Limit(value.Title, 1000)!;
            entity.EventType = Limit(value.EventType, 64)!;
            entity.EventAt = value.EventAt;
            entity.ObservedAt = snapshot.ObservedAt;
        }

        var retention = snapshot.ObservedAt.AddDays(-30);
        dbContext.RemoveRange(history.Values.Where(value => value.EventAt < retention));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static QueueItemEntity Map(
        Guid instanceId,
        string providerKind,
        QueueItemSnapshot value,
        DateTimeOffset observedAt) => new()
    {
        InstanceId = instanceId,
        ProviderKey = value.ProviderKey,
        ProviderKind = providerKind,
        MediaProviderKey = value.MediaProviderKey,
        DownloadId = Limit(value.DownloadId, 512),
        CorrelationKey = Correlation(value.DownloadId),
        Title = Limit(value.Title, 1000)!,
        Status = Limit(value.Status, 64)!,
        TrackedStatus = Limit(value.TrackedStatus, 64)!,
        TrackedState = Limit(value.TrackedState, 64)!,
        Protocol = Limit(value.Protocol, 32),
        SizeBytes = ToInt64(value.SizeBytes),
        RemainingBytes = ToInt64(value.RemainingBytes),
        AddedAt = value.AddedAt,
        EstimatedCompletionAt = value.EstimatedCompletionAt,
        DownloadClient = Limit(value.DownloadClient, 200),
        Indexer = Limit(value.Indexer, 200),
        ObservedAt = observedAt,
    };

    private static string? Correlation(string? downloadId) =>
        string.IsNullOrWhiteSpace(downloadId)
            ? null
            : Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(downloadId.Trim().ToUpperInvariant())));

    private static string? Limit(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maximum)];

    private static long ToInt64(double value) =>
        !double.IsFinite(value) || value <= 0 ? 0 : (long)Math.Min(value, long.MaxValue);
}
