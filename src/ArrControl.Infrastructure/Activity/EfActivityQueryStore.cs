using ArrControl.Application.Activity;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Activity;
using ArrControl.Infrastructure.Persistence.Connections;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Activity;

public sealed class EfActivityQueryStore(ArrControlDbContext dbContext) : IActivityQueryStore
{
    public async Task<ActivitySnapshot> QueryAsync(
        bool includeAll,
        IReadOnlyCollection<Guid> instanceGroupIds,
        IReadOnlyCollection<Guid> instanceIds,
        int historyLimit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var queue =
            from item in dbContext.Set<QueueItemEntity>().AsNoTracking()
            join instance in dbContext.Set<InstanceEntity>().AsNoTracking()
                on item.InstanceId equals instance.Id
            select new { item, instance };
        var history =
            from item in dbContext.Set<HistoryItemEntity>().AsNoTracking()
            join instance in dbContext.Set<InstanceEntity>().AsNoTracking()
                on item.InstanceId equals instance.Id
            select new { item, instance };
        if (!includeAll)
        {
            queue = queue.Where(value => value.instance.GroupId != null
                && instanceGroupIds.Contains(value.instance.GroupId.Value));
            history = history.Where(value => value.instance.GroupId != null
                && instanceGroupIds.Contains(value.instance.GroupId.Value));
        }

        if (instanceIds.Count > 0)
        {
            queue = queue.Where(value => instanceIds.Contains(value.instance.Id));
            history = history.Where(value => instanceIds.Contains(value.instance.Id));
        }

        var queueRows = await queue.OrderByDescending(value => value.item.AddedAt)
            .ThenBy(value => value.item.InstanceId).ThenBy(value => value.item.ProviderKey)
            .Select(value => new
            {
                value.item,
                value.instance.Name,
                HistoryCount = value.item.CorrelationKey == null ? 0 : dbContext.Set<HistoryItemEntity>()
                    .Count(historyItem => historyItem.InstanceId == value.item.InstanceId
                        && historyItem.CorrelationKey == value.item.CorrelationKey),
                LatestEvent = value.item.CorrelationKey == null ? null : dbContext.Set<HistoryItemEntity>()
                    .Where(historyItem => historyItem.InstanceId == value.item.InstanceId
                        && historyItem.CorrelationKey == value.item.CorrelationKey)
                    .OrderByDescending(historyItem => historyItem.EventAt)
                    .Select(historyItem => historyItem.EventType)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        var historyRows = await history.OrderByDescending(value => value.item.EventAt)
            .ThenBy(value => value.item.InstanceId).ThenBy(value => value.item.ProviderKey)
            .Take(historyLimit)
            .Select(value => new
            {
                value.item,
                value.instance.Name,
                QueueCorrelated = value.item.CorrelationKey != null && dbContext.Set<QueueItemEntity>()
                    .Any(queueItem => queueItem.InstanceId == value.item.InstanceId
                        && queueItem.CorrelationKey == value.item.CorrelationKey),
            })
            .ToListAsync(cancellationToken);
        return new ActivitySnapshot(
            queueRows.Select(value => new AggregatedQueueItem(
                value.item.InstanceId, value.Name, value.item.ProviderKind, value.item.ProviderKey,
                value.item.MediaProviderKey, value.item.Title, value.item.Status, value.item.TrackedStatus,
                value.item.TrackedState, value.item.Protocol, value.item.SizeBytes, value.item.RemainingBytes,
                value.item.AddedAt, value.item.EstimatedCompletionAt, value.item.DownloadClient,
                value.item.Indexer, value.HistoryCount, value.LatestEvent,
                ImportFailureClassifier.ClassifyQueue(
                    value.item.Status, value.item.TrackedStatus, value.item.TrackedState),
                value.item.ObservedAt,
                now - value.item.ObservedAt > TimeSpan.FromMinutes(2))).ToArray(),
            historyRows.Select(value => new AggregatedHistoryItem(
                value.item.InstanceId, value.Name, value.item.ProviderKind, value.item.ProviderKey,
                value.item.MediaProviderKey, value.item.Title, value.item.EventType, value.item.EventAt,
                value.QueueCorrelated, ImportFailureClassifier.ClassifyHistory(value.item.EventType),
                value.item.ObservedAt,
                now - value.item.ObservedAt > TimeSpan.FromMinutes(2))).ToArray());
    }
}
