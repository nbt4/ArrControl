using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Events;

public interface IOutboxPublisher
{
    Task<int> PublishBatchAsync(int maximumCount, CancellationToken cancellationToken);
    Task<int> DeleteExpiredAsync(DateTimeOffset cutoff, int maximumCount, CancellationToken cancellationToken);
}

public sealed class EfOutboxPublisher(
    ArrControlDbContext dbContext,
    TimeProvider timeProvider) : IOutboxPublisher
{
    public async Task<int> PublishBatchAsync(int maximumCount, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(maximumCount, 1, 500);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var rows = await dbContext.Set<OutboxMessageEntity>().FromSqlInterpolated($"""
                SELECT * FROM outbox_messages
                WHERE published_at IS NULL AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
                ORDER BY occurred_at, id
                FOR UPDATE SKIP LOCKED
                LIMIT {limit}
                """).ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            row.PublishedAt = now;
            row.AttemptCount++;
            row.NextAttemptAt = null;
            row.LastErrorCode = null;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return rows.Count;
    }

    public Task<int> DeleteExpiredAsync(
        DateTimeOffset cutoff,
        int maximumCount,
        CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM outbox_messages WHERE ctid IN (
                SELECT ctid FROM outbox_messages
                WHERE published_at IS NOT NULL AND occurred_at < {cutoff}
                ORDER BY occurred_at, id
                LIMIT {Math.Clamp(maximumCount, 1, 10_000)})
            """, cancellationToken);
}
