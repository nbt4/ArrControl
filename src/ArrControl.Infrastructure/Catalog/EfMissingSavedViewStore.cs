using System.Text.Json;
using ArrControl.Application.Authorization;
using ArrControl.Application.Catalog;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Catalog;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArrControl.Infrastructure.Catalog;

public sealed class EfMissingSavedViewStore(
    ArrControlDbContext dbContext,
    TimeProvider timeProvider) : IMissingSavedViewStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<MissingSavedView>> ListAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Set<MissingSavedViewEntity>()
            .AsNoTracking()
            .Where(value => value.UserId == userId)
            .OrderBy(value => value.NormalizedName)
            .ThenBy(value => value.Id)
            .ToListAsync(cancellationToken);
        return rows.Select(Map).ToArray();
    }

    public async Task<MissingSavedView?> GetAsync(
        Guid userId,
        Guid id,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.Set<MissingSavedViewEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.UserId == userId && value.Id == id, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<MissingSavedViewWriteResult> UpsertAsync(
        RbacActorContext actor,
        Guid id,
        string name,
        MissingFilter filter,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var entity = await dbContext.Set<MissingSavedViewEntity>()
            .SingleOrDefaultAsync(value => value.UserId == actor.UserId && value.Id == id, cancellationToken);
        var created = entity is null;
        entity ??= new MissingSavedViewEntity
        {
            Id = id,
            UserId = actor.UserId,
            Name = name,
            NormalizedName = NormalizeName(name),
            FilterJson = "{}",
            CreatedAt = now,
        };
        if (created)
        {
            dbContext.Add(entity);
        }

        entity.Name = name;
        entity.NormalizedName = NormalizeName(name);
        entity.FilterJson = JsonSerializer.Serialize(filter, JsonOptions);
        entity.UpdatedAt = now;
        AddAudit(actor, id, "catalog.missing_view_upsert", created ? "created" : "updated");
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new MissingSavedViewWriteResult(
                created ? MissingSavedViewWriteStatus.Created : MissingSavedViewWriteStatus.Updated,
                Map(entity));
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            return new MissingSavedViewWriteResult(MissingSavedViewWriteStatus.NameConflict);
        }
    }

    public async Task<bool> DeleteAsync(
        RbacActorContext actor,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var entity = await dbContext.Set<MissingSavedViewEntity>()
            .SingleOrDefaultAsync(value => value.UserId == actor.UserId && value.Id == id, cancellationToken);
        if (entity is not null)
        {
            dbContext.Remove(entity);
        }

        AddAudit(actor, id, "catalog.missing_view_delete", entity is null ? "absent" : "deleted");
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return entity is not null;
    }

    private static MissingSavedView Map(MissingSavedViewEntity value) =>
        new(
            value.Id,
            value.Name,
            JsonSerializer.Deserialize<MissingFilter>(value.FilterJson, JsonOptions)
                ?? MissingFilter.Empty,
            value.CreatedAt,
            value.UpdatedAt);

    private static string NormalizeName(string value) => value.Trim().ToUpperInvariant();

    private void AddAudit(
        RbacActorContext actor,
        Guid viewId,
        string action,
        string outcome)
    {
        dbContext.Add(new AuditEventEntity
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = timeProvider.GetUtcNow(),
            ActorUserId = actor.UserId,
            ActorType = "user",
            ActorIdentifier = actor.Email,
            Action = action,
            ScopeJson = JsonSerializer.Serialize(new { kind = "missing_saved_view", viewId }),
            CorrelationId = actor.RequestContext.CorrelationId,
            Outcome = outcome,
            SummaryJson = JsonSerializer.Serialize(new { viewId }),
            IpAddress = actor.RequestContext.IpAddress,
        });
    }
}
