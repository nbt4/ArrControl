using System.Data;
using System.Text.Json;
using ArrControl.Application.Authorization;
using ArrControl.Application.Health;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Connections;
using ArrControl.Infrastructure.Persistence.Health;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Health;

public sealed class EfHealthIncidentStore(
    ArrControlDbContext dbContext,
    TimeProvider timeProvider) : IHealthIncidentSnapshotStore, IHealthIncidentStore
{
    public async Task ApplyAsync(
        Guid instanceId,
        string providerKind,
        DateTimeOffset observedAt,
        IReadOnlyList<HealthIncidentGroupSnapshot> groups,
        CancellationToken cancellationToken)
    {
        ValidateSnapshot(instanceId, providerKind, groups);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({instanceId.ToString("D")}, 0))",
            cancellationToken);

        var incidents = await dbContext.Set<HealthIncidentEntity>()
            .Include(value => value.Sources)
            .Where(value => value.InstanceId == instanceId)
            .ToListAsync(cancellationToken);
        foreach (var incident in incidents)
            foreach (var source in incident.Sources) source.Active = false;

        foreach (var group in groups)
        {
            var incident = incidents.SingleOrDefault(value => value.GroupKey == group.GroupKey);
            if (incident is null)
            {
                incident = new HealthIncidentEntity
                {
                    Id = Guid.CreateVersion7(),
                    InstanceId = instanceId,
                    GroupKey = group.GroupKey,
                    ProviderKind = providerKind,
                    Severity = group.Severity,
                    RemediationUrl = group.RemediationUrl,
                    FirstSeenAt = observedAt,
                    LastSeenAt = observedAt,
                };
                incidents.Add(incident);
                dbContext.Add(incident);
            }
            else
            {
                var reopened = incident.ResolvedAt is not null;
                incident.ProviderKind = providerKind;
                incident.Severity = group.Severity;
                incident.RemediationUrl = group.RemediationUrl;
                incident.LastSeenAt = observedAt;
                incident.ResolvedAt = null;
                if (reopened)
                {
                    incident.AcknowledgedAt = null;
                    incident.AcknowledgedByUserId = null;
                    incident.SnoozedUntil = null;
                    incident.SnoozedByUserId = null;
                }
            }

            foreach (var snapshot in group.Sources)
            {
                var source = incident.Sources.SingleOrDefault(value => value.SourceKey == snapshot.SourceKey);
                if (source is null)
                {
                    source = new HealthIncidentSourceEntity
                    {
                        IncidentId = incident.Id,
                        SourceKey = snapshot.SourceKey,
                        FirstSeenAt = observedAt,
                        LastSeenAt = observedAt,
                        Source = snapshot.Source,
                        Severity = snapshot.Severity,
                    };
                    incident.Sources.Add(source);
                }

                source.ProviderIssueId = snapshot.ProviderIssueId;
                source.Source = snapshot.Source;
                source.Severity = snapshot.Severity;
                source.Message = snapshot.Message;
                source.RemediationUrl = snapshot.RemediationUrl;
                source.LastSeenAt = observedAt;
                source.Active = true;
            }
        }

        var activeKeys = groups.Select(value => value.GroupKey).ToHashSet(StringComparer.Ordinal);
        foreach (var incident in incidents.Where(value => value.ResolvedAt is null
                     && !activeKeys.Contains(value.GroupKey)))
            incident.ResolvedAt = observedAt;

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HealthIncidentDetails>> QueryAsync(
        bool includeAll,
        IReadOnlyCollection<Guid> instanceGroupIds,
        IReadOnlyCollection<Guid> instanceIds,
        bool includeResolved,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var query =
            from incident in dbContext.Set<HealthIncidentEntity>().AsNoTracking()
                .Include(value => value.Sources)
            join instance in dbContext.Set<InstanceEntity>().AsNoTracking()
                on incident.InstanceId equals instance.Id
            select new { incident, instance };
        if (!includeAll)
            query = query.Where(value => value.instance.GroupId != null
                && instanceGroupIds.Contains(value.instance.GroupId.Value));
        if (instanceIds.Count > 0)
            query = query.Where(value => instanceIds.Contains(value.incident.InstanceId));
        if (!includeResolved)
            query = query.Where(value => value.incident.ResolvedAt == null);

        var rows = await query.OrderBy(value => value.incident.ResolvedAt != null)
            .ThenByDescending(value => value.incident.Severity == "error")
            .ThenByDescending(value => value.incident.Severity == "warning")
            .ThenByDescending(value => value.incident.LastSeenAt)
            .ToListAsync(cancellationToken);
        return rows.Select(value => ToDetails(value.incident, value.instance.Name, now)).ToArray();
    }

    public async Task<HealthIncidentScope> GetScopeAsync(
        Guid incidentId,
        CancellationToken cancellationToken) =>
        await (from incident in dbContext.Set<HealthIncidentEntity>().AsNoTracking()
               join instance in dbContext.Set<InstanceEntity>().AsNoTracking()
                   on incident.InstanceId equals instance.Id
               where incident.Id == incidentId
               select new HealthIncidentScope(true, instance.GroupId))
            .SingleOrDefaultAsync(cancellationToken)
        ?? new HealthIncidentScope(false, null);

    public Task<HealthIncidentDetails?> SetAcknowledgementAsync(
        RbacActorContext actor,
        Guid incidentId,
        bool acknowledged,
        CancellationToken cancellationToken) =>
        MutateAsync(actor, incidentId, "health.incident_acknowledgement",
            incident =>
            {
                incident.AcknowledgedAt = acknowledged ? timeProvider.GetUtcNow() : null;
                incident.AcknowledgedByUserId = acknowledged ? actor.UserId : null;
            }, new { acknowledged }, cancellationToken);

    public Task<HealthIncidentDetails?> SetSnoozeAsync(
        RbacActorContext actor,
        Guid incidentId,
        DateTimeOffset? snoozedUntil,
        CancellationToken cancellationToken) =>
        MutateAsync(actor, incidentId, "health.incident_snooze",
            incident =>
            {
                incident.SnoozedUntil = snoozedUntil;
                incident.SnoozedByUserId = snoozedUntil is null ? null : actor.UserId;
            }, new { snoozed = snoozedUntil is not null, snoozedUntil }, cancellationToken);

    private async Task<HealthIncidentDetails?> MutateAsync(
        RbacActorContext actor,
        Guid incidentId,
        string action,
        Action<HealthIncidentEntity> mutation,
        object summary,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        var row = await (from incident in dbContext.Set<HealthIncidentEntity>()
                         join instance in dbContext.Set<InstanceEntity>()
                             on incident.InstanceId equals instance.Id
                         where incident.Id == incidentId
                         select new { incident, instance.Name })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await dbContext.Entry(row.incident).Collection(value => value.Sources).LoadAsync(cancellationToken);
        mutation(row.incident);
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
                kind = "health_incident",
                incidentId,
                instanceId = row.incident.InstanceId,
            }),
            CorrelationId = actor.RequestContext.CorrelationId,
            Outcome = "updated",
            SummaryJson = JsonSerializer.Serialize(summary),
            IpAddress = actor.RequestContext.IpAddress,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToDetails(row.incident, row.Name, timeProvider.GetUtcNow());
    }

    private static HealthIncidentDetails ToDetails(
        HealthIncidentEntity incident,
        string instanceName,
        DateTimeOffset now) => new(
        incident.Id,
        incident.InstanceId,
        instanceName,
        incident.ProviderKind,
        incident.Severity,
        incident.RemediationUrl,
        incident.FirstSeenAt,
        incident.LastSeenAt,
        incident.ResolvedAt,
        incident.AcknowledgedAt,
        incident.AcknowledgedByUserId,
        incident.SnoozedUntil,
        incident.SnoozedByUserId,
        incident.ResolvedAt is null && now - incident.LastSeenAt > TimeSpan.FromMinutes(10),
        incident.Sources.OrderByDescending(value => value.Active)
            .ThenBy(value => value.Source, StringComparer.OrdinalIgnoreCase)
            .Select(value => new HealthIncidentSourceDetails(
                value.ProviderIssueId,
                value.Source,
                value.Severity,
                value.Message,
                value.RemediationUrl,
                value.FirstSeenAt,
                value.LastSeenAt,
                value.Active))
            .ToArray());

    private static void ValidateSnapshot(
        Guid instanceId,
        string providerKind,
        IReadOnlyList<HealthIncidentGroupSnapshot> groups)
    {
        if (instanceId == Guid.Empty || string.IsNullOrWhiteSpace(providerKind)
            || providerKind.Length > 64 || groups.Count > HealthIncidentLimits.MaximumSourcesPerSnapshot
            || groups.SelectMany(value => value.Sources).Count() > HealthIncidentLimits.MaximumSourcesPerSnapshot
            || groups.Any(value => value.GroupKey.Length != 64 || value.Sources.Count == 0
                || value.Sources.Any(source => source.SourceKey.Length != 64)))
            throw new InvalidOperationException("The health snapshot is invalid.");
    }
}
