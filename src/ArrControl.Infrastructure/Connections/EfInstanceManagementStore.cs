using System.Data;
using System.Text.Json;
using ArrControl.Application.Authorization;
using ArrControl.Application.Connections;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Connections;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Connections;

public sealed class EfInstanceManagementStore(
    ArrControlDbContext dbContext,
    TimeProvider timeProvider) : IInstanceManagementStore
{
    private const long InstanceMutationLock = 4596771108351201948;

    public async Task<InstanceScope> GetScopeAsync(
        Guid instanceId,
        CancellationToken cancellationToken) =>
        await dbContext.Instances
            .AsNoTracking()
            .Where(instance => instance.Id == instanceId)
            .Select(instance => new InstanceScope(true, instance.GroupId))
            .SingleOrDefaultAsync(cancellationToken)
        ?? new InstanceScope(false, null);

    public Task<bool> InstanceGroupExistsAsync(
        Guid instanceGroupId,
        CancellationToken cancellationToken) =>
        dbContext.Set<InstanceGroupEntity>().AnyAsync(
            group => group.Id == instanceGroupId,
            cancellationToken);

    public async Task<InstanceDetails?> GetAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var instance = await dbContext.Instances
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == instanceId, cancellationToken);
        return instance is null
            ? null
            : await ToDetailsAsync(instance, cancellationToken);
    }

    public async Task<InstanceWriteResult> CreateAsync(
        RbacActorContext actor,
        Guid instanceId,
        ValidatedInstanceInput input,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginMutationAsync(cancellationToken);
        if (await dbContext.Instances.AnyAsync(
                instance => instance.Id == instanceId,
                cancellationToken))
        {
            return await FinishWriteAsync(
                transaction,
                actor,
                instanceId,
                "connection.instance_create",
                "id_conflict",
                InstanceWriteStatus.NameConflict,
                null,
                cancellationToken);
        }

        if (input.InstanceGroupId is Guid groupId
            && !await dbContext.Set<InstanceGroupEntity>().AnyAsync(
                group => group.Id == groupId,
                cancellationToken))
        {
            return await FinishWriteAsync(
                transaction,
                actor,
                instanceId,
                "connection.instance_create",
                "group_not_found",
                InstanceWriteStatus.GroupNotFound,
                null,
                cancellationToken);
        }

        if (await NameExistsAsync(input.Name, instanceId, cancellationToken))
        {
            return await FinishWriteAsync(
                transaction,
                actor,
                instanceId,
                "connection.instance_create",
                "name_conflict",
                InstanceWriteStatus.NameConflict,
                null,
                cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        var entity = new InstanceEntity
        {
            Id = instanceId,
            Name = input.Name,
            Kind = input.Kind,
            BaseUrl = input.BaseUri.AbsoluteUri,
            Enabled = input.Enabled,
            GroupId = input.InstanceGroupId,
            TlsVerificationEnabled = input.TlsVerificationEnabled,
            AllowPrivateNetworkAccess = input.AllowPrivateNetworkAccess,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.Add(entity);
        AddAudit(
            actor,
            instanceId,
            "connection.instance_create",
            "created",
            new
            {
                kind = input.Kind,
                instanceGroupId = input.InstanceGroupId,
                input.Enabled,
                input.TlsVerificationEnabled,
                input.AllowPrivateNetworkAccess,
            });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new InstanceWriteResult(
            InstanceWriteStatus.Created,
            await ToDetailsAsync(entity, cancellationToken));
    }

    public async Task<InstanceWriteResult> UpdateAsync(
        RbacActorContext actor,
        Guid instanceId,
        ValidatedInstanceInput input,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginMutationAsync(cancellationToken);
        var entity = await dbContext.Instances.SingleOrDefaultAsync(
            instance => instance.Id == instanceId,
            cancellationToken);
        if (entity is null)
        {
            return await FinishWriteAsync(
                transaction,
                actor,
                instanceId,
                "connection.instance_update",
                "not_found",
                InstanceWriteStatus.NotFound,
                null,
                cancellationToken);
        }

        if (input.InstanceGroupId is Guid groupId
            && !await dbContext.Set<InstanceGroupEntity>().AnyAsync(
                group => group.Id == groupId,
                cancellationToken))
        {
            return await FinishWriteAsync(
                transaction,
                actor,
                instanceId,
                "connection.instance_update",
                "group_not_found",
                InstanceWriteStatus.GroupNotFound,
                null,
                cancellationToken);
        }

        if (await NameExistsAsync(input.Name, instanceId, cancellationToken))
        {
            return await FinishWriteAsync(
                transaction,
                actor,
                instanceId,
                "connection.instance_update",
                "name_conflict",
                InstanceWriteStatus.NameConflict,
                null,
                cancellationToken);
        }

        var connectionChanged = entity.Kind != input.Kind
            || entity.BaseUrl != input.BaseUri.AbsoluteUri
            || entity.TlsVerificationEnabled != input.TlsVerificationEnabled
            || entity.AllowPrivateNetworkAccess != input.AllowPrivateNetworkAccess;
        entity.Name = input.Name;
        entity.Kind = input.Kind;
        entity.BaseUrl = input.BaseUri.AbsoluteUri;
        entity.Enabled = input.Enabled;
        entity.GroupId = input.InstanceGroupId;
        entity.TlsVerificationEnabled = input.TlsVerificationEnabled;
        entity.AllowPrivateNetworkAccess = input.AllowPrivateNetworkAccess;
        entity.UpdatedAt = timeProvider.GetUtcNow();
        if (connectionChanged)
        {
            dbContext.RemoveRange(dbContext.Set<ProviderCapabilityEntity>()
                .Where(capability => capability.InstanceId == instanceId));
        }

        AddAudit(
            actor,
            instanceId,
            "connection.instance_update",
            "updated",
            new
            {
                kind = input.Kind,
                instanceGroupId = input.InstanceGroupId,
                input.Enabled,
                input.TlsVerificationEnabled,
                input.AllowPrivateNetworkAccess,
                capabilitiesInvalidated = connectionChanged,
            });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new InstanceWriteResult(
            InstanceWriteStatus.Updated,
            await ToDetailsAsync(entity, cancellationToken));
    }

    public async Task<InstanceDeleteStatus> DeleteAsync(
        RbacActorContext actor,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginMutationAsync(cancellationToken);
        var entity = await dbContext.Instances.SingleOrDefaultAsync(
            instance => instance.Id == instanceId,
            cancellationToken);
        if (entity is null)
        {
            AddAudit(actor, instanceId, "connection.instance_delete", "not_found", new { });
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return InstanceDeleteStatus.NotFound;
        }

        dbContext.Remove(entity);
        AddAudit(actor, instanceId, "connection.instance_delete", "deleted", new { });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return InstanceDeleteStatus.Deleted;
    }

    public async Task SaveProbeAsync(
        RbacActorContext actor,
        Guid instanceId,
        ConnectionProbeObservation observation,
        CancellationToken cancellationToken)
    {
        if (observation.Capabilities.Count == 0
            || observation.Capabilities.Any(value =>
                !ProviderCapabilities.IsKnown(value.Capability)))
        {
            throw new InvalidOperationException("The provider capability snapshot is invalid.");
        }

        await using var transaction = await BeginMutationAsync(cancellationToken);
        if (!await dbContext.Instances.AnyAsync(
                instance => instance.Id == instanceId,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        var existing = await dbContext.Set<ProviderCapabilityEntity>()
            .Where(capability => capability.InstanceId == instanceId)
            .ToListAsync(cancellationToken);
        var observations = observation.Capabilities
            .GroupBy(value => value.Capability, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToDictionary(value => value.Capability, StringComparer.Ordinal);
        dbContext.RemoveRange(existing.Where(value => !observations.ContainsKey(value.Capability)));
        foreach (var value in observations.Values)
        {
            var entity = existing.SingleOrDefault(item => item.Capability == value.Capability);
            if (entity is null)
            {
                entity = new ProviderCapabilityEntity
                {
                    InstanceId = instanceId,
                    Capability = value.Capability,
                };
                dbContext.Add(entity);
            }

            entity.Supported = value.Supported;
            entity.ObservedAt = value.ObservedAt;
        }

        AddAudit(
            actor,
            instanceId,
            "connection.instance_probe",
            observation.Outcome,
            new
            {
                observation.Connected,
                observation.HttpStatusCode,
                capabilities = observations.Keys.Order(StringComparer.Ordinal),
            });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InstanceGroupDetails>> ListGroupsAsync(
        CancellationToken cancellationToken) =>
        await dbContext.Set<InstanceGroupEntity>()
            .AsNoTracking()
            .OrderBy(group => group.Name)
            .ThenBy(group => group.Id)
            .Select(group => new InstanceGroupDetails(
                group.Id,
                group.Name,
                group.CreatedAt,
                group.UpdatedAt))
            .ToListAsync(cancellationToken);

    public async Task<InstanceGroupWriteResult> UpsertGroupAsync(
        RbacActorContext actor,
        Guid instanceGroupId,
        string name,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginMutationAsync(cancellationToken);
        var entity = await dbContext.Set<InstanceGroupEntity>().SingleOrDefaultAsync(
            group => group.Id == instanceGroupId,
            cancellationToken);
        if (await dbContext.Set<InstanceGroupEntity>().AnyAsync(
                group => group.Id != instanceGroupId && group.Name.ToUpper() == name.ToUpper(),
                cancellationToken))
        {
            AddGroupAudit(actor, instanceGroupId, "connection.instance_group_upsert", "name_conflict");
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new InstanceGroupWriteResult(InstanceGroupWriteStatus.NameConflict);
        }

        var created = entity is null;
        var now = timeProvider.GetUtcNow();
        entity ??= new InstanceGroupEntity
        {
            Id = instanceGroupId,
            Name = name,
            CreatedAt = now,
        };
        if (created)
        {
            dbContext.Add(entity);
        }

        entity.Name = name;
        entity.UpdatedAt = now;
        AddGroupAudit(
            actor,
            instanceGroupId,
            "connection.instance_group_upsert",
            created ? "created" : "updated");
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new InstanceGroupWriteResult(
            created ? InstanceGroupWriteStatus.Created : InstanceGroupWriteStatus.Updated,
            new InstanceGroupDetails(entity.Id, entity.Name, entity.CreatedAt, entity.UpdatedAt));
    }

    public async Task<InstanceGroupDeleteStatus> DeleteGroupAsync(
        RbacActorContext actor,
        Guid instanceGroupId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginMutationAsync(cancellationToken);
        var entity = await dbContext.Set<InstanceGroupEntity>().SingleOrDefaultAsync(
            group => group.Id == instanceGroupId,
            cancellationToken);
        if (entity is null)
        {
            AddGroupAudit(actor, instanceGroupId, "connection.instance_group_delete", "not_found");
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return InstanceGroupDeleteStatus.NotFound;
        }

        if (await dbContext.Instances.AnyAsync(
                instance => instance.GroupId == instanceGroupId,
                cancellationToken))
        {
            AddGroupAudit(actor, instanceGroupId, "connection.instance_group_delete", "in_use");
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return InstanceGroupDeleteStatus.InUse;
        }

        dbContext.Remove(entity);
        AddGroupAudit(actor, instanceGroupId, "connection.instance_group_delete", "deleted");
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return InstanceGroupDeleteStatus.Deleted;
    }

    private async Task<InstanceDetails> ToDetailsAsync(
        InstanceEntity instance,
        CancellationToken cancellationToken)
    {
        var capabilities = await dbContext.Set<ProviderCapabilityEntity>()
            .AsNoTracking()
            .Where(capability => capability.InstanceId == instance.Id)
            .OrderBy(capability => capability.Capability)
            .Select(capability => new ProviderCapabilityObservation(
                capability.Capability,
                capability.Supported,
                capability.ObservedAt))
            .ToListAsync(cancellationToken);
        return new InstanceDetails(
            instance.Id,
            instance.Name,
            instance.Kind,
            instance.BaseUrl,
            instance.Enabled,
            instance.GroupId,
            instance.TlsVerificationEnabled,
            instance.AllowPrivateNetworkAccess,
            capabilities,
            instance.CreatedAt,
            instance.UpdatedAt);
    }

    private Task<bool> NameExistsAsync(
        string name,
        Guid instanceId,
        CancellationToken cancellationToken) =>
        dbContext.Instances.AnyAsync(
            instance => instance.Id != instanceId && instance.Name.ToUpper() == name.ToUpper(),
            cancellationToken);

    private async Task<InstanceWriteResult> FinishWriteAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        RbacActorContext actor,
        Guid instanceId,
        string action,
        string outcome,
        InstanceWriteStatus status,
        InstanceDetails? instance,
        CancellationToken cancellationToken)
    {
        AddAudit(actor, instanceId, action, outcome, new { });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new InstanceWriteResult(status, instance);
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginMutationAsync(
        CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({InstanceMutationLock})",
            cancellationToken);
        return transaction;
    }

    private void AddAudit(
        RbacActorContext actor,
        Guid instanceId,
        string action,
        string outcome,
        object summary)
    {
        dbContext.Add(new AuditEventEntity
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = timeProvider.GetUtcNow(),
            ActorUserId = actor.UserId,
            ActorType = "user",
            ActorIdentifier = actor.Email,
            Action = action,
            ScopeJson = JsonSerializer.Serialize(new { kind = "instance", instanceId }),
            CorrelationId = actor.RequestContext.CorrelationId,
            Outcome = outcome,
            SummaryJson = JsonSerializer.Serialize(summary),
            IpAddress = actor.RequestContext.IpAddress,
        });
    }

    private void AddGroupAudit(
        RbacActorContext actor,
        Guid instanceGroupId,
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
                kind = "instance_group",
                instanceGroupId,
            }),
            CorrelationId = actor.RequestContext.CorrelationId,
            Outcome = outcome,
            SummaryJson = "{}",
            IpAddress = actor.RequestContext.IpAddress,
        });
    }
}
