using System.Text.Json;
using ArrControl.Application.Events;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Connections;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Events;

public sealed class EfLiveEventStore(ArrControlDbContext dbContext) : ILiveEventStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string> GetLatestCursorAsync(CancellationToken cancellationToken) =>
        await dbContext.Set<OutboxMessageEntity>().AsNoTracking()
            .Where(value => value.PublishedAt != null)
            .OrderByDescending(value => value.OccurredAt).ThenByDescending(value => value.Id)
            .Select(value => value.Id.ToString())
            .FirstOrDefaultAsync(cancellationToken)
        ?? LiveEventService.OriginCursor;

    public async Task<bool> CursorExistsAsync(string cursor, CancellationToken cancellationToken) =>
        Guid.TryParseExact(cursor, "D", out var id)
        && await dbContext.Set<OutboxMessageEntity>().AsNoTracking()
            .AnyAsync(value => value.Id == id && value.PublishedAt != null, cancellationToken);

    public async Task<LiveEventBatch> ReadAsync(
        string cursor,
        LiveEventAccess access,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(maximumCount, 1, 500);
        List<OutboxMessageEntity> rows;
        if (cursor == LiveEventService.OriginCursor)
        {
            rows = await dbContext.Set<OutboxMessageEntity>().AsNoTracking()
                .Where(value => value.PublishedAt != null)
                .OrderBy(value => value.OccurredAt).ThenBy(value => value.Id)
                .Take(limit + 1).ToListAsync(cancellationToken);
        }
        else
        {
            var cursorId = Guid.ParseExact(cursor, "D");
            var point = await dbContext.Set<OutboxMessageEntity>().AsNoTracking()
                .Where(value => value.Id == cursorId && value.PublishedAt != null)
                .Select(value => new { value.OccurredAt, value.Id })
                .SingleAsync(cancellationToken);
            rows = await dbContext.Set<OutboxMessageEntity>().FromSqlInterpolated($"""
                    SELECT * FROM outbox_messages
                    WHERE published_at IS NOT NULL
                      AND (occurred_at, id) > ({point.OccurredAt}, {point.Id})
                    ORDER BY occurred_at, id
                    LIMIT {limit + 1}
                    """).AsNoTracking().ToListAsync(cancellationToken);
        }

        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        if (rows.Count == 0) return new LiveEventBatch([], cursor, false, false);
        var currentGroups = await CurrentGroupsAsync(rows, cancellationToken);
        var output = new List<LiveEvent>();
        foreach (var row in rows)
        {
            var payload = Parse(row.PayloadJson);
            if (payload is null || payload.Version != 1) continue;
            var visible = VisibleTargets(payload, access, currentGroups);
            if (visible is null) continue;
            output.Add(new LiveEvent(row.Id, row.Type, payload.Resource, row.OccurredAt, visible));
        }

        return new LiveEventBatch(
            output,
            rows[^1].Id.ToString("D"),
            true,
            hasMore);
    }

    private async Task<Dictionary<Guid, Guid?>> CurrentGroupsAsync(
        IReadOnlyCollection<OutboxMessageEntity> rows,
        CancellationToken cancellationToken)
    {
        var ids = rows.Select(value => Parse(value.PayloadJson))
            .Where(value => value is not null)
            .SelectMany(value => value!.Targets)
            .Select(value => value.InstanceId).Distinct().ToArray();
        return await dbContext.Set<InstanceEntity>().AsNoTracking()
            .Where(value => ids.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, value => value.GroupId, cancellationToken);
    }

    private static Guid[]? VisibleTargets(
        LiveEventPayload payload,
        LiveEventAccess access,
        IReadOnlyDictionary<Guid, Guid?> currentGroups)
    {
        if (payload.ActorUserId is Guid actorUserId)
            return actorUserId == access.UserId ? [] : null;
        var grant = access.Grants.SingleOrDefault(value =>
            value.PermissionCode == payload.RequiredPermission);
        if (grant is null) return null;
        if (payload.Targets.Count == 0) return grant.IsGlobal ? [] : null;
        if (grant.IsGlobal)
            return payload.Targets.Select(value => value.InstanceId).Distinct().Order().ToArray();

        var visible = payload.Targets.Where(target =>
            (currentGroups.TryGetValue(target.InstanceId, out var currentGroup)
                ? currentGroup
                : target.InstanceGroupId) is Guid groupId
            && grant.InstanceGroupIds.Contains(groupId))
            .Select(value => value.InstanceId).Distinct().Order().ToArray();
        return visible.Length == 0 ? null : visible;
    }

    private static LiveEventPayload? Parse(string value)
    {
        if (value.Length > 65_536) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<LiveEventPayload>(value, JsonOptions);
            return payload is not null
                && payload.Resource.Length is > 0 and <= 64
                && payload.RequiredPermission.Length <= 64
                && payload.Targets.Count <= 1000
                    ? payload
                    : null;
        }
        catch (JsonException) { return null; }
    }
}
