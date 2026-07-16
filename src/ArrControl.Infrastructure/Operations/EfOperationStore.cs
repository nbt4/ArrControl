using System.Text.Json;
using ArrControl.Application.Authorization;
using ArrControl.Application.Events;
using ArrControl.Application.Operations;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Operations;

public sealed class EfOperationStore(
    ArrControlDbContext dbContext,
    TimeProvider timeProvider) : IOperationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CreateOperationResult> CreateAsync(
        CreateOperationCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var lockKey = JsonSerializer.Serialize(new[]
        {
            command.Actor.UserId.ToString("D"),
            command.Route,
            command.IdempotencyKey,
        });
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        var existing = await dbContext.Set<OperationEntity>()
            .Include(value => value.Targets)
            .SingleOrDefaultAsync(value => value.ActorUserId == command.Actor.UserId
                && value.Route == command.Route
                && value.IdempotencyKey == command.IdempotencyKey,
                cancellationToken);
        if (existing is not null && existing.IdempotencyExpiresAt > now)
        {
            await transaction.CommitAsync(cancellationToken);
            return string.Equals(existing.RequestHash, command.RequestHash, StringComparison.OrdinalIgnoreCase)
                ? new CreateOperationResult(CreateOperationStatus.Replayed, Map(existing))
                : new CreateOperationResult(CreateOperationStatus.IdempotencyConflict);
        }

        if (existing is not null)
        {
            existing.IdempotencyKey = $"expired:{existing.Id:N}";
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var entity = new OperationEntity
        {
            ActorUserId = command.Actor.UserId,
            Type = command.Type,
            Route = command.Route,
            IdempotencyKey = command.IdempotencyKey,
            RequestHash = command.RequestHash.ToLowerInvariant(),
            State = OperationStates.Pending,
            DryRun = command.DryRun,
            CreatedAt = now,
            IdempotencyExpiresAt = now.AddHours(24),
        };
        foreach (var target in command.Targets)
        {
            entity.Targets.Add(new OperationTargetEntity
            {
                InstanceId = target.InstanceId,
                TargetKey = target.TargetKey,
                State = OperationTargetStates.Pending,
            });
        }

        dbContext.Add(entity);
        AddAudit(command.Actor, entity.Id, "operation.create", "created", command.Targets.Count);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreateOperationResult(CreateOperationStatus.Created, Map(entity));
    }

    public async Task<OperationDetails?> GetAsync(
        Guid actorUserId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.Set<OperationEntity>().AsNoTracking()
            .Include(value => value.Targets)
            .SingleOrDefaultAsync(value => value.Id == operationId && value.ActorUserId == actorUserId,
                cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<bool> RequestCancellationAsync(
        RbacActorContext actor,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var entity = await dbContext.Set<OperationEntity>()
            .SingleOrDefaultAsync(value => value.Id == operationId && value.ActorUserId == actor.UserId,
                cancellationToken);
        if (entity is null)
        {
            return false;
        }

        if (entity.State is OperationStates.Pending or OperationStates.Running)
        {
            entity.CancellationRequested = true;
            if (entity.State == OperationStates.Pending)
            {
                entity.State = OperationStates.Cancelled;
                entity.CompletedAt = timeProvider.GetUtcNow();
                await dbContext.Set<OperationTargetEntity>()
                    .Where(value => value.OperationId == operationId
                        && value.State == OperationTargetStates.Pending)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(value => value.State, OperationTargetStates.Cancelled)
                        .SetProperty(value => value.CompletedAt, timeProvider.GetUtcNow()), cancellationToken);
            }
        }

        AddAudit(actor, operationId, "operation.cancel", "accepted", null);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> TryStartAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var actorUserId = await dbContext.Set<OperationEntity>().AsNoTracking()
            .Where(value => value.Id == operationId && value.State == OperationStates.Pending
                && !value.CancellationRequested)
            .Select(value => (Guid?)value.ActorUserId)
            .SingleOrDefaultAsync(cancellationToken);
        if (actorUserId is null) return false;
        var now = timeProvider.GetUtcNow();
        var updated = await dbContext.Set<OperationEntity>()
            .Where(value => value.Id == operationId && value.State == OperationStates.Pending
                && !value.CancellationRequested)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.State, OperationStates.Running)
                .SetProperty(value => value.StartedAt, now), cancellationToken) == 1;
        if (!updated) return false;
        AddOperationOutbox(actorUserId.Value, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CompleteTargetAsync(
        Guid operationId,
        Guid instanceId,
        string targetKey,
        bool succeeded,
        string? errorCode,
        string? resultJson,
        CancellationToken cancellationToken)
    {
        if (errorCode?.Length > 128 || resultJson?.Length > 65_536 || !ValidJson(resultJson)) return false;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var operation = await dbContext.Set<OperationEntity>().AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == operationId, cancellationToken);
        if (operation is null || operation.State != OperationStates.Running) return false;
        var now = timeProvider.GetUtcNow();
        var updated = await dbContext.Set<OperationTargetEntity>()
            .Where(value => value.OperationId == operationId && value.InstanceId == instanceId
                && value.TargetKey == targetKey
                && (value.State == OperationTargetStates.Pending || value.State == OperationTargetStates.Running))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.State, operation.CancellationRequested
                    ? OperationTargetStates.Cancelled
                    : succeeded ? OperationTargetStates.Succeeded : OperationTargetStates.Failed)
                .SetProperty(value => value.ErrorCode, operation.CancellationRequested ? null : errorCode)
                .SetProperty(value => value.ResultJson, operation.CancellationRequested ? null : resultJson)
                .SetProperty(value => value.StartedAt, value => value.StartedAt ?? now)
                .SetProperty(value => value.CompletedAt, now), cancellationToken) == 1;
        if (!updated) return false;
        AddOperationOutbox(operation.ActorUserId, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CompleteAsync(Guid operationId, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var entity = await dbContext.Set<OperationEntity>().Include(value => value.Targets)
            .SingleOrDefaultAsync(value => value.Id == operationId && value.State == OperationStates.Running,
                cancellationToken);
        if (entity is null) return false;
        var now = timeProvider.GetUtcNow();
        if (entity.CancellationRequested)
        {
            foreach (var target in entity.Targets.Where(value => value.State == OperationTargetStates.Pending))
            {
                target.State = OperationTargetStates.Cancelled;
                target.CompletedAt = now;
            }
            entity.State = OperationStates.Cancelled;
        }
        else
        {
            var succeeded = entity.Targets.Count(value => value.State is OperationTargetStates.Succeeded or OperationTargetStates.Skipped);
            var failed = entity.Targets.Count(value => value.State == OperationTargetStates.Failed);
            if (succeeded + failed != entity.Targets.Count) return false;
            entity.State = failed == 0 ? OperationStates.Succeeded
                : succeeded == 0 ? OperationStates.Failed : OperationStates.Partial;
        }
        entity.CompletedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<Guid>> ListPendingAsync(
        string type,
        int maximumCount,
        CancellationToken cancellationToken) =>
        await dbContext.Set<OperationEntity>().AsNoTracking()
            .Where(value => value.Type == type && value.State == OperationStates.Pending
                && !value.CancellationRequested)
            .OrderBy(value => value.CreatedAt).ThenBy(value => value.Id)
            .Take(Math.Clamp(maximumCount, 1, 100))
            .Select(value => value.Id)
            .ToListAsync(cancellationToken);

    public async Task<OperationDetails?> GetForExecutionAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.Set<OperationEntity>().AsNoTracking().Include(value => value.Targets)
            .SingleOrDefaultAsync(value => value.Id == operationId, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    private static bool ValidJson(string? value)
    {
        if (value is null) return true;
        try { using var _ = JsonDocument.Parse(value); return true; }
        catch (JsonException) { return false; }
    }

    private static OperationDetails Map(OperationEntity value) => new(
        value.Id, value.Type, value.State, value.DryRun, value.CancellationRequested,
        value.CreatedAt, value.StartedAt, value.CompletedAt,
        value.Targets.OrderBy(target => target.InstanceId).ThenBy(target => target.TargetKey)
            .Select(target => new OperationTarget(target.InstanceId, target.TargetKey, target.State,
                target.ErrorCode, target.ResultJson, target.StartedAt, target.CompletedAt)).ToArray());

    private void AddAudit(
        RbacActorContext actor, Guid operationId, string action, string outcome, int? targetCount)
    {
        dbContext.Add(new AuditEventEntity
        {
            Id = Guid.CreateVersion7(), OccurredAt = timeProvider.GetUtcNow(), ActorUserId = actor.UserId,
            ActorType = "user", ActorIdentifier = actor.Email, Action = action,
            ScopeJson = JsonSerializer.Serialize(new { kind = "operation", operationId }),
            CorrelationId = actor.RequestContext.CorrelationId, Outcome = outcome,
            SummaryJson = JsonSerializer.Serialize(new { operationId, targetCount }),
            IpAddress = actor.RequestContext.IpAddress,
        });
    }

    private void AddOperationOutbox(Guid actorUserId, DateTimeOffset occurredAt)
    {
        var payload = new LiveEventPayload(
            1, LiveEventResources.Operations, string.Empty, [], actorUserId);
        dbContext.Add(new OutboxMessageEntity
        {
            Id = Guid.CreateVersion7(),
            Type = $"{LiveEventResources.Operations}.changed",
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            OccurredAt = occurredAt,
            NextAttemptAt = occurredAt,
        });
    }
}
