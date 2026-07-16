using System.Data;
using ArrControl.Application.Automation;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Automation;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Automation;

public sealed class EfJobSchedulerStore(
    ArrControlDbContext dbContext,
    IDbContextFactory<ArrControlDbContext> dbContextFactory,
    TimeProvider timeProvider) : IJobSchedulerStore
{
    public async Task<IReadOnlyList<SchedulePlanningState>> ListEnabledSchedulesAsync(
        CancellationToken cancellationToken) =>
        await dbContext.Set<ScheduleEntity>()
            .AsNoTracking()
            .Where(schedule => schedule.Enabled)
            .OrderBy(schedule => schedule.Id)
            .Select(schedule => new SchedulePlanningState(
                schedule.Id,
                schedule.Type,
                schedule.Cron,
                schedule.TimeZone,
                schedule.LastEnqueuedAt))
            .ToArrayAsync(cancellationToken);

    public async Task<bool> TryEnqueueAsync(
        Guid scheduleId,
        DateTimeOffset? expectedLastEnqueuedAt,
        IReadOnlyList<ScheduledOccurrence> occurrences,
        CancellationToken cancellationToken)
    {
        if (scheduleId == Guid.Empty || occurrences.Count == 0)
        {
            throw new ArgumentException("A schedule and at least one occurrence are required.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var schedules = await dbContext.Set<ScheduleEntity>()
            .FromSqlInterpolated($"SELECT * FROM schedules WHERE id = {scheduleId} FOR UPDATE")
            .ToArrayAsync(cancellationToken);
        var schedule = schedules.SingleOrDefault();
        if (schedule is null
            || !schedule.Enabled
            || schedule.LastEnqueuedAt != expectedLastEnqueuedAt)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var ordered = occurrences.OrderBy(value => value.ScheduledFor).ToArray();
        if (ordered.Any(value => value.AvailableAt < value.ScheduledFor)
            || ordered.Select(value => value.ScheduledFor).Distinct().Count() != ordered.Length
            || (schedule.LastEnqueuedAt is DateTimeOffset previous
                && ordered[0].ScheduledFor <= previous))
        {
            throw new ArgumentException("Scheduled occurrences are invalid.", nameof(occurrences));
        }

        foreach (var occurrence in ordered)
        {
            dbContext.Add(new JobRunEntity
            {
                Id = Guid.CreateVersion7(),
                ScheduleId = scheduleId,
                State = JobRunStates.Pending,
                Attempts = 0,
                ScheduledFor = occurrence.ScheduledFor,
                AvailableAt = occurrence.AvailableAt,
                CreatedAt = timeProvider.GetUtcNow(),
            });
        }

        schedule.LastEnqueuedAt = ordered[^1].ScheduledFor;
        schedule.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ClaimedJob>> ClaimAsync(
        string leaseOwner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        int maximumAttempts,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner) || leaseOwner.Length > 200)
        {
            throw new ArgumentException("A bounded lease owner is required.", nameof(leaseOwner));
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE job_runs
            SET state = 'failed', completed_at = {{now}}, error_code = 'lease_expired',
                lease_owner = NULL, lease_until = NULL, lease_token = NULL, last_heartbeat_at = NULL
            WHERE state = 'running'
              AND lease_until <= {{now}}
              AND attempts >= {{maximumAttempts}}
              AND completed_at IS NULL
            """, cancellationToken);

        var candidateLimit = checked(maximumCount * 4);
        var candidates = await dbContext.Set<JobRunEntity>()
            .FromSqlInterpolated($$"""
                SELECT job.*
                FROM job_runs AS job
                INNER JOIN schedules AS schedule ON schedule.id = job.schedule_id
                WHERE schedule.enabled
                  AND job.completed_at IS NULL
                  AND job.available_at <= {{now}}
                  AND job.attempts < {{maximumAttempts}}
                  AND (
                    job.state IN ('pending', 'retry')
                    OR (job.state = 'running' AND job.lease_until <= {{now}})
                  )
                ORDER BY job.available_at, job.scheduled_for, job.id
                FOR UPDATE OF job SKIP LOCKED
                LIMIT {{candidateLimit}}
                """)
            .ToArrayAsync(cancellationToken);
        if (candidates.Length == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return [];
        }

        var selected = candidates
            .GroupBy(value => value.ScheduleId)
            .Select(group => group.First())
            .Concat(candidates
                .GroupBy(value => value.ScheduleId)
                .SelectMany(group => group.Skip(1)))
            .Take(maximumCount)
            .ToArray();
        var scheduleIds = selected.Select(value => value.ScheduleId).Distinct().ToArray();
        var schedules = await dbContext.Set<ScheduleEntity>()
            .AsNoTracking()
            .Where(schedule => scheduleIds.Contains(schedule.Id))
            .ToDictionaryAsync(schedule => schedule.Id, cancellationToken);
        var leaseUntil = now + leaseDuration;
        var claimed = new List<ClaimedJob>(selected.Length);
        foreach (var candidate in selected)
        {
            var token = Guid.CreateVersion7();
            candidate.State = JobRunStates.Running;
            candidate.Attempts++;
            candidate.LeaseOwner = leaseOwner;
            candidate.LeaseToken = token;
            candidate.LeaseUntil = leaseUntil;
            candidate.LastHeartbeatAt = now;
            candidate.StartedAt ??= now;
            candidate.ErrorCode = null;
            var schedule = schedules[candidate.ScheduleId];
            claimed.Add(new ClaimedJob(
                candidate.Id,
                candidate.ScheduleId,
                schedule.Type,
                schedule.ScopeJson,
                candidate.Attempts,
                token,
                leaseUntil));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    public async Task<bool> RenewAsync(
        Guid jobId,
        Guid leaseToken,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await using var renewalContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await renewalContext.Set<JobRunEntity>()
            .Where(job => job.Id == jobId
                && job.State == JobRunStates.Running
                && job.LeaseToken == leaseToken
                && job.LeaseUntil > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.LeaseUntil, now + leaseDuration)
                    .SetProperty(job => job.LastHeartbeatAt, now),
                cancellationToken) == 1;
    }

    public async Task<bool> CompleteAsync(
        ClaimedJob job,
        DateTimeOffset completedAt,
        IReadOnlyList<SyncCheckpointUpdate> checkpoints,
        CancellationToken cancellationToken)
    {
        ValidateCompletion(job, checkpoints);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var entity = await FindOwnedRunningJobAsync(job, completedAt, cancellationToken);
        if (entity is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        foreach (var checkpoint in checkpoints)
        {
            var entityCheckpoint = await dbContext.Set<SyncCheckpointEntity>().FindAsync(
                [checkpoint.InstanceId, checkpoint.Stream],
                cancellationToken);
            if (entityCheckpoint is null)
            {
                dbContext.Add(new SyncCheckpointEntity
                {
                    InstanceId = checkpoint.InstanceId,
                    Stream = checkpoint.Stream,
                    Cursor = checkpoint.Cursor,
                    LastSuccessAt = completedAt,
                    UpdatedAt = completedAt,
                });
            }
            else
            {
                entityCheckpoint.Cursor = checkpoint.Cursor;
                entityCheckpoint.LastSuccessAt = completedAt;
                entityCheckpoint.UpdatedAt = completedAt;
            }
        }

        entity.State = JobRunStates.Succeeded;
        entity.CompletedAt = completedAt;
        entity.ErrorCode = null;
        ClearLease(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> FailAsync(
        ClaimedJob job,
        string errorCode,
        DateTimeOffset failedAt,
        DateTimeOffset? retryAt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(errorCode) || errorCode.Length > 128)
        {
            throw new ArgumentException("A bounded error code is required.", nameof(errorCode));
        }

        var entity = await FindOwnedRunningJobAsync(job, failedAt, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        entity.ErrorCode = errorCode;
        if (retryAt is DateTimeOffset retry)
        {
            entity.State = JobRunStates.Retry;
            entity.AvailableAt = retry;
        }
        else
        {
            entity.State = JobRunStates.Failed;
            entity.CompletedAt = failedAt;
        }

        ClearLease(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> AbandonAsync(
        ClaimedJob job,
        DateTimeOffset availableAt,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.Set<JobRunEntity>().SingleOrDefaultAsync(
            candidate => candidate.Id == job.JobId
                && candidate.State == JobRunStates.Running
                && candidate.LeaseToken == job.LeaseToken,
            cancellationToken);
        if (entity is null)
        {
            return false;
        }

        entity.State = JobRunStates.Retry;
        entity.AvailableAt = availableAt < entity.ScheduledFor ? entity.ScheduledFor : availableAt;
        entity.ErrorCode = "worker_stopping";
        ClearLease(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private Task<JobRunEntity?> FindOwnedRunningJobAsync(
        ClaimedJob job,
        DateTimeOffset at,
        CancellationToken cancellationToken) =>
        dbContext.Set<JobRunEntity>().SingleOrDefaultAsync(
            candidate => candidate.Id == job.JobId
                && candidate.State == JobRunStates.Running
                && candidate.LeaseToken == job.LeaseToken
                && candidate.LeaseUntil > at,
            cancellationToken);

    private static void ValidateCompletion(
        ClaimedJob job,
        IReadOnlyList<SyncCheckpointUpdate> checkpoints)
    {
        if (job.JobId == Guid.Empty || job.LeaseToken == Guid.Empty
            || checkpoints.Any(value => value.InstanceId == Guid.Empty
                || string.IsNullOrWhiteSpace(value.Stream)
                || value.Stream.Length > 64
                || value.Cursor?.Length > 4096)
            || checkpoints.Select(value => (value.InstanceId, value.Stream)).Distinct().Count()
                != checkpoints.Count)
        {
            throw new ScheduledJobException("checkpoint_invalid");
        }
    }

    private static void ClearLease(JobRunEntity entity)
    {
        entity.LeaseOwner = null;
        entity.LeaseUntil = null;
        entity.LeaseToken = null;
        entity.LastHeartbeatAt = null;
    }
}
