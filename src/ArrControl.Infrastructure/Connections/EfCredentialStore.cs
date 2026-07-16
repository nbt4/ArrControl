using System.Data;
using System.Text.Json;
using ArrControl.Application.Authorization;
using ArrControl.Application.Connections;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Connections;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Connections;

public sealed class EfCredentialStore(
    ArrControlDbContext dbContext,
    TimeProvider timeProvider) : ICredentialStore
{
    private const long CredentialMutationLock = 4596771108351201947;

    public async Task<InstanceCredentialScope> GetInstanceScopeAsync(
        Guid instanceId,
        CancellationToken cancellationToken) =>
        await dbContext.Set<InstanceEntity>()
            .AsNoTracking()
            .Where(instance => instance.Id == instanceId)
            .Select(instance => new InstanceCredentialScope(true, instance.GroupId))
            .SingleOrDefaultAsync(cancellationToken)
        ?? new InstanceCredentialScope(false, null);

    public async Task<CredentialUpsertStoreResult> UpsertAsync(
        RbacActorContext actor,
        Guid instanceId,
        string purpose,
        ProtectedCredential credential,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginMutationAsync(cancellationToken);
        if (!await dbContext.Set<InstanceEntity>()
                .AnyAsync(instance => instance.Id == instanceId, cancellationToken))
        {
            AddAudit(actor, instanceId, purpose, "connection.credential_upsert", "not_found");
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CredentialUpsertStoreResult(false, false);
        }

        var now = timeProvider.GetUtcNow();
        var entity = await dbContext.Set<CredentialEntity>()
            .SingleOrDefaultAsync(
                value => value.InstanceId == instanceId && value.Purpose == purpose,
                cancellationToken);
        var created = entity is null;
        entity ??= new CredentialEntity
        {
            InstanceId = instanceId,
            Purpose = purpose,
            Ciphertext = [],
            Nonce = [],
            Tag = [],
            CreatedAt = now,
        };
        if (created)
        {
            dbContext.Add(entity);
        }

        entity.Ciphertext = credential.Ciphertext.ToArray();
        entity.Nonce = credential.Nonce.ToArray();
        entity.Tag = credential.Tag.ToArray();
        entity.KeyVersion = credential.KeyVersion;
        entity.UpdatedAt = now;
        AddAudit(
            actor,
            instanceId,
            purpose,
            "connection.credential_upsert",
            created ? "created" : "updated");
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CredentialUpsertStoreResult(
            true,
            created,
            new CredentialMetadata(purpose, true, now));
    }

    public async Task<IReadOnlyList<CredentialMetadata>> ListMetadataAsync(
        Guid instanceId,
        CancellationToken cancellationToken) =>
        await dbContext.Set<CredentialEntity>()
            .AsNoTracking()
            .Where(credential => credential.InstanceId == instanceId)
            .OrderBy(credential => credential.Purpose)
            .Select(credential => new CredentialMetadata(
                credential.Purpose,
                true,
                credential.UpdatedAt))
            .ToListAsync(cancellationToken);

    public async Task<CredentialDeleteStoreResult> DeleteAsync(
        RbacActorContext actor,
        Guid instanceId,
        string purpose,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginMutationAsync(cancellationToken);
        if (!await dbContext.Set<InstanceEntity>()
                .AnyAsync(instance => instance.Id == instanceId, cancellationToken))
        {
            AddAudit(actor, instanceId, purpose, "connection.credential_delete", "not_found");
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CredentialDeleteStoreResult(false, false);
        }

        var entity = await dbContext.Set<CredentialEntity>()
            .SingleOrDefaultAsync(
                value => value.InstanceId == instanceId && value.Purpose == purpose,
                cancellationToken);
        if (entity is not null)
        {
            dbContext.Remove(entity);
        }

        AddAudit(
            actor,
            instanceId,
            purpose,
            "connection.credential_delete",
            entity is null ? "absent" : "deleted");
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CredentialDeleteStoreResult(true, entity is not null);
    }

    public Task<StoredProtectedCredential?> FindAsync(
        Guid instanceId,
        string purpose,
        CancellationToken cancellationToken) =>
        dbContext.Set<CredentialEntity>()
            .AsNoTracking()
            .Where(value => value.InstanceId == instanceId && value.Purpose == purpose)
            .Select(value => new StoredProtectedCredential(
                value.InstanceId,
                value.Purpose,
                value.Ciphertext,
                value.Nonce,
                value.Tag,
                value.KeyVersion))
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginMutationAsync(
        CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({CredentialMutationLock})",
            cancellationToken);
        return transaction;
    }

    private void AddAudit(
        RbacActorContext actor,
        Guid instanceId,
        string purpose,
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
            ScopeJson = JsonSerializer.Serialize(new
            {
                kind = "instance_credential",
                instanceId,
                purpose,
            }),
            CorrelationId = actor.RequestContext.CorrelationId,
            Outcome = outcome,
            SummaryJson = JsonSerializer.Serialize(new
            {
                configured = action == "connection.credential_upsert"
                    && outcome is "created" or "updated",
            }),
            IpAddress = actor.RequestContext.IpAddress,
        });
    }
}
