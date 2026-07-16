using System.Text.Json;
using ArrControl.Application.Audit;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Audit;

public sealed class EfAuditQueryStore(ArrControlDbContext dbContext) : IAuditQueryStore
{
    public async Task<IReadOnlyList<AuditEventDetails>> QueryAsync(
        NormalizedAuditFilter filter,
        AuditCursor? cursor,
        int fetchCount,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Set<AuditEventEntity>().AsNoTracking()
            .Where(value => value.OccurredAt >= filter.From && value.OccurredAt <= filter.To);
        if (filter.ActorUserId is Guid actorUserId)
            query = query.Where(value => value.ActorUserId == actorUserId);
        if (filter.Action is not null)
            query = query.Where(value => value.Action == filter.Action);
        if (filter.Outcome is not null)
            query = query.Where(value => value.Outcome == filter.Outcome);
        if (filter.CorrelationId is not null)
            query = query.Where(value => value.CorrelationId == filter.CorrelationId);
        if (cursor is not null)
            query = query.Where(value => value.OccurredAt < cursor.OccurredAt
                || (value.OccurredAt == cursor.OccurredAt && value.Id.CompareTo(cursor.Id) < 0));

        var rows = await query.OrderByDescending(value => value.OccurredAt)
            .ThenByDescending(value => value.Id)
            .Take(Math.Clamp(fetchCount, 1, AuditLimits.MaximumPageSize + 1))
            .Select(value => new
            {
                value.Id,
                value.OccurredAt,
                value.ActorUserId,
                value.ActorType,
                value.ActorIdentifier,
                value.Action,
                value.ScopeJson,
                value.CorrelationId,
                value.Outcome,
                value.SummaryJson,
                value.IpAddress,
            }).ToListAsync(cancellationToken);
        return rows.Select(value => new AuditEventDetails(
            value.Id,
            value.OccurredAt,
            value.ActorUserId,
            value.ActorType,
            value.ActorIdentifier,
            value.Action,
            Parse(value.ScopeJson),
            value.CorrelationId,
            value.Outcome,
            Parse(value.SummaryJson),
            value.IpAddress?.ToString())).ToArray();
    }

    private static JsonElement Parse(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
