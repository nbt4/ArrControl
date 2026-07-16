using System.Text.Json;
using ArrControl.Application.Authorization;
using ArrControl.Application.Events;
using ArrControl.Infrastructure.Persistence.Activity;
using ArrControl.Infrastructure.Persistence.Automation;
using ArrControl.Infrastructure.Persistence.Catalog;
using ArrControl.Infrastructure.Persistence.Connections;
using ArrControl.Infrastructure.Persistence.Health;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArrControl.Infrastructure.Events;

public sealed class TransactionalOutboxInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        AppendEvents(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        AppendEvents(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void AppendEvents(DbContext? context)
    {
        if (context is null
            || context.ChangeTracker.Entries<OutboxMessageEntity>().Any(value => value.State == EntityState.Added))
            return;
        context.ChangeTracker.DetectChanges();
        var changes = context.ChangeTracker.Entries()
            .Where(value => value.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToArray();
        var envelopes = new Dictionary<EventAudience, HashSet<LiveEventTarget>>();
        foreach (var entry in changes) AddChange(envelopes, entry);
        var now = timeProvider.GetUtcNow();
        foreach (var pair in envelopes)
        {
            var payload = new LiveEventPayload(
                1,
                pair.Key.Resource,
                pair.Key.RequiredPermission,
                pair.Value.OrderBy(value => value.InstanceId).ThenBy(value => value.InstanceGroupId).ToArray(),
                pair.Key.ActorUserId);
            context.Add(new OutboxMessageEntity
            {
                Id = Guid.CreateVersion7(),
                Type = $"{pair.Key.Resource}.changed",
                PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
                OccurredAt = now,
                NextAttemptAt = now,
            });
        }
    }

    private static void AddChange(
        Dictionary<EventAudience, HashSet<LiveEventTarget>> events,
        EntityEntry entry)
    {
        switch (entry.Entity)
        {
            case InstanceEntity value:
                foreach (var audience in InstanceAudiences())
                    Add(events, audience, new LiveEventTarget(value.Id, value.GroupId));
                if (entry.State == EntityState.Modified
                    && entry.Property(nameof(InstanceEntity.GroupId)).OriginalValue is Guid originalGroup
                    && originalGroup != value.GroupId)
                    foreach (var audience in InstanceAudiences())
                        Add(events, audience, new LiveEventTarget(value.Id, originalGroup));
                break;
            case CredentialEntity value:
                Add(events, Audience(LiveEventResources.Instances, RbacPermissions.InstancesRead),
                    new LiveEventTarget(value.InstanceId, null));
                break;
            case ProviderCapabilityEntity value:
                Add(events, Audience(LiveEventResources.Instances, RbacPermissions.InstancesRead),
                    new LiveEventTarget(value.InstanceId, null));
                break;
            case ProviderItemEntity value:
                Add(events, Audience(LiveEventResources.Missing, RbacPermissions.LibraryRead),
                    new LiveEventTarget(value.InstanceId, null));
                break;
            case MediaEntityEntity value:
                Add(events, Audience(LiveEventResources.Missing, RbacPermissions.LibraryRead),
                    new LiveEventTarget(value.InstanceId, null));
                break;
            case MissingItemEntity value:
                Add(events, Audience(LiveEventResources.Missing, RbacPermissions.LibraryRead),
                    new LiveEventTarget(value.InstanceId, null));
                break;
            case MissingSavedViewEntity value:
                Add(events, Audience(LiveEventResources.Missing, string.Empty, value.UserId), null);
                break;
            case QueueItemEntity value:
                Add(events, Audience(LiveEventResources.Activity, RbacPermissions.InstancesRead),
                    new LiveEventTarget(value.InstanceId, null));
                break;
            case HistoryItemEntity value:
                Add(events, Audience(LiveEventResources.Activity, RbacPermissions.InstancesRead),
                    new LiveEventTarget(value.InstanceId, null));
                break;
            case HealthIncidentEntity value:
                Add(events, Audience(LiveEventResources.Health, RbacPermissions.InstancesRead),
                    new LiveEventTarget(value.InstanceId, null));
                break;
            case OperationEntity value:
                Add(events, Audience(LiveEventResources.Operations, string.Empty, value.ActorUserId), null);
                break;
            case AuditEventEntity:
                Add(events, Audience(LiveEventResources.Audit, RbacPermissions.AuditRead), null);
                break;
            case SyncCheckpointEntity value when value.Stream == "catalog":
                Add(events, Audience(LiveEventResources.Missing, RbacPermissions.LibraryRead),
                    new LiveEventTarget(value.InstanceId, null));
                break;
            case SyncCheckpointEntity value when value.Stream == "activity":
                Add(events, Audience(LiveEventResources.Activity, RbacPermissions.InstancesRead),
                    new LiveEventTarget(value.InstanceId, null));
                break;
            case SyncCheckpointEntity value when value.Stream == "health":
                Add(events, Audience(LiveEventResources.Health, RbacPermissions.InstancesRead),
                    new LiveEventTarget(value.InstanceId, null));
                break;
        }
    }

    private static EventAudience[] InstanceAudiences() =>
    [
        Audience(LiveEventResources.Instances, RbacPermissions.InstancesRead),
        Audience(LiveEventResources.Missing, RbacPermissions.LibraryRead),
        Audience(LiveEventResources.Activity, RbacPermissions.InstancesRead),
        Audience(LiveEventResources.Health, RbacPermissions.InstancesRead),
    ];

    private static EventAudience Audience(string resource, string permission, Guid? actorUserId = null) =>
        new(resource, permission, actorUserId);

    private static void Add(
        Dictionary<EventAudience, HashSet<LiveEventTarget>> events,
        EventAudience audience,
        LiveEventTarget? target)
    {
        if (!events.TryGetValue(audience, out var targets))
        {
            targets = [];
            events.Add(audience, targets);
        }
        if (target is not null) targets.Add(target);
    }

    private sealed record EventAudience(
        string Resource,
        string RequiredPermission,
        Guid? ActorUserId);
}
