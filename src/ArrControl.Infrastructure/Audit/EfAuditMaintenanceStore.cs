using System.Data;
using System.Text.Json;
using ArrControl.Application.Audit;
using ArrControl.Application.Authorization;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Automation;
using ArrControl.Infrastructure.Persistence.Connections;
using ArrControl.Infrastructure.Persistence.Health;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Audit;

public sealed class EfAuditMaintenanceStore(
    ArrControlDbContext dbContext,
    TimeProvider timeProvider) : IAuditRetentionStore, IDiagnosticsExportStore
{
    public async Task<int> DeleteExpiredAsync(
        DateTimeOffset cutoff,
        int batchSize,
        int maximumBatches,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        var deleted = 0;
        var limitReached = false;
        for (var batch = 0; batch < maximumBatches; batch++)
        {
            var count = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM audit_events WHERE ctid IN (
                    SELECT ctid FROM audit_events
                    WHERE occurred_at < {cutoff}
                    ORDER BY occurred_at, id
                    LIMIT {batchSize})
                """, cancellationToken);
            deleted += count;
            if (count < batchSize) break;
            limitReached = batch == maximumBatches - 1;
        }

        var now = timeProvider.GetUtcNow();
        dbContext.Add(new AuditEventEntity
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = now,
            ActorType = "system",
            ActorIdentifier = "audit-retention",
            Action = "audit.retention",
            ScopeJson = "{\"kind\":\"system\"}",
            CorrelationId = $"retention-{Guid.CreateVersion7():N}",
            Outcome = limitReached ? "partial" : "succeeded",
            SummaryJson = JsonSerializer.Serialize(new { cutoff, deletedCount = deleted, limitReached }),
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<DiagnosticsSnapshot> CreateAsync(
        RbacActorContext actor,
        DiagnosticsExportRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var cutoff = now.AddHours(-request.LookbackHours);
        var instances = await dbContext.Set<InstanceEntity>().AsNoTracking()
            .Include(value => value.Capabilities)
            .OrderBy(value => value.Id).Take(10_000).ToListAsync(cancellationToken);
        var instanceReferences = instances.Select((value, index) => new { value.Id, Reference = $"instance-{index + 1:D3}" })
            .ToDictionary(value => value.Id, value => value.Reference);
        var groupReferences = instances.Where(value => value.GroupId is not null)
            .Select(value => value.GroupId!.Value).Distinct().Order()
            .Select((value, index) => new { Id = value, Reference = $"group-{index + 1:D3}" })
            .ToDictionary(value => value.Id, value => value.Reference);

        var jobRows = await dbContext.Set<JobRunEntity>().AsNoTracking()
            .Where(value => value.CreatedAt >= cutoff)
            .GroupBy(value => new { value.State, value.ErrorCode })
            .Select(value => new { value.Key.State, value.Key.ErrorCode, Count = value.Count() })
            .OrderBy(value => value.State).ThenBy(value => value.ErrorCode)
            .ToListAsync(cancellationToken);
        var jobs = jobRows.Select(value => new DiagnosticsJobSummary(
            value.State, value.ErrorCode, value.Count)).ToArray();
        var checkpoints = await dbContext.Set<SyncCheckpointEntity>().AsNoTracking()
            .Where(value => instanceReferences.Keys.Contains(value.InstanceId))
            .OrderBy(value => value.InstanceId).ThenBy(value => value.Stream)
            .Select(value => new { value.InstanceId, value.Stream, value.LastSuccessAt })
            .Take(10_000).ToListAsync(cancellationToken);
        var healthRows = await dbContext.Set<HealthIncidentEntity>().AsNoTracking()
            .GroupBy(value => new { value.Severity, Resolved = value.ResolvedAt != null })
            .Select(value => new { value.Key.Severity, value.Key.Resolved, Count = value.Count() })
            .OrderBy(value => value.Severity).ThenBy(value => value.Resolved)
            .ToListAsync(cancellationToken);
        var health = healthRows.Select(value => new DiagnosticsHealthSummary(
            value.Severity, value.Resolved, value.Count)).ToArray();
        var audit = request.IncludeAudit
            ? await ReadRedactedAuditAsync(cutoff, cancellationToken)
            : [];

        var snapshot = new DiagnosticsSnapshot(
            1,
            now,
            "strict-v1:no-secrets-no-names-no-urls-no-paths-no-addresses-no-payloads",
            instances.Select(value => new DiagnosticsInstance(
                instanceReferences[value.Id],
                value.Kind,
                value.Enabled,
                value.GroupId is Guid groupId ? groupReferences[groupId] : null,
                value.TlsVerificationEnabled,
                value.AllowPrivateNetworkAccess,
                value.Capabilities.Where(capability => capability.Supported)
                    .Select(capability => capability.Capability).Order(StringComparer.Ordinal).ToArray()))
                .ToArray(),
            jobs,
            checkpoints.Select(value => new DiagnosticsCheckpoint(
                instanceReferences[value.InstanceId], value.Stream, value.LastSuccessAt)).ToArray(),
            health,
            audit);

        dbContext.Add(new AuditEventEntity
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = now,
            ActorUserId = actor.UserId,
            ActorType = "user",
            ActorIdentifier = actor.Email,
            Action = "diagnostics.export",
            ScopeJson = "{\"kind\":\"system\"}",
            CorrelationId = actor.RequestContext.CorrelationId,
            Outcome = "succeeded",
            SummaryJson = JsonSerializer.Serialize(new { request.LookbackHours, request.IncludeAudit }),
            IpAddress = actor.RequestContext.IpAddress,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return snapshot;
    }

    private async Task<IReadOnlyList<RedactedAuditEvent>> ReadRedactedAuditAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Set<AuditEventEntity>().AsNoTracking()
            .Where(value => value.OccurredAt >= cutoff)
            .OrderByDescending(value => value.OccurredAt).ThenByDescending(value => value.Id)
            .Take(AuditLimits.MaximumExportAuditRows)
            .Select(value => new
            {
                value.OccurredAt,
                value.ActorType,
                value.Action,
                value.Outcome,
                value.ScopeJson,
            }).ToListAsync(cancellationToken);
        return rows.Select(value => new RedactedAuditEvent(
            value.OccurredAt,
            value.ActorType,
            value.Action,
            value.Outcome,
            ScopeKind(value.ScopeJson))).ToArray();
    }

    private static string ScopeKind(string scopeJson)
    {
        try
        {
            using var document = JsonDocument.Parse(scopeJson);
            return document.RootElement.TryGetProperty("kind", out var kind)
                && kind.ValueKind == JsonValueKind.String
                && kind.GetString() is { Length: > 0 and <= 64 } value
                    ? value
                    : "unknown";
        }
        catch (JsonException) { return "unknown"; }
    }
}
