using ArrControl.Application.Authorization;
using ArrControl.Application.Automation;

namespace ArrControl.Application.Audit;

public sealed record AuditRetentionSettings(int RetentionDays, int BatchSize, int MaximumBatches)
{
    public static AuditRetentionSettings Default { get; } = new(365, 1_000, 10);

    public void Validate()
    {
        if (RetentionDays is < 30 or > 3_650
            || BatchSize is < 100 or > 10_000
            || MaximumBatches is < 1 or > 100)
            throw new InvalidOperationException("Audit retention settings are outside safe bounds.");
    }
}

public static class AuditJobTypes
{
    public const string Retention = "audit.retention";
}

public interface IAuditRetentionStore
{
    Task<int> DeleteExpiredAsync(
        DateTimeOffset cutoff,
        int batchSize,
        int maximumBatches,
        CancellationToken cancellationToken);
}

public interface IAuditRetentionScheduleProvisioner
{
    Task<int> ReconcileAsync(CancellationToken cancellationToken);
}

public sealed class AuditRetentionJobHandler(
    IAuditRetentionStore store,
    AuditRetentionSettings settings,
    TimeProvider timeProvider) : IScheduledJobHandler
{
    public string Type => AuditJobTypes.Retention;

    public async Task<JobHandlerResult> ExecuteAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        await store.DeleteExpiredAsync(
            timeProvider.GetUtcNow().AddDays(-settings.RetentionDays),
            settings.BatchSize,
            settings.MaximumBatches,
            cancellationToken);
        return JobHandlerResult.Completed;
    }
}

public sealed record DiagnosticsExportRequest(int LookbackHours = 24, bool IncludeAudit = true);

public sealed record DiagnosticsInstance(
    string Reference,
    string Kind,
    bool Enabled,
    string? InstanceGroupReference,
    bool TlsVerificationEnabled,
    bool AllowPrivateNetworkAccess,
    IReadOnlyList<string> Capabilities);

public sealed record DiagnosticsJobSummary(string State, string? ErrorCode, int Count);

public sealed record DiagnosticsCheckpoint(
    string InstanceReference,
    string Stream,
    DateTimeOffset LastSuccessAt);

public sealed record DiagnosticsHealthSummary(string Severity, bool Resolved, int Count);

public sealed record RedactedAuditEvent(
    DateTimeOffset OccurredAt,
    string ActorType,
    string Action,
    string Outcome,
    string ScopeKind);

public sealed record DiagnosticsSnapshot(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    string RedactionProfile,
    IReadOnlyList<DiagnosticsInstance> Instances,
    IReadOnlyList<DiagnosticsJobSummary> Jobs,
    IReadOnlyList<DiagnosticsCheckpoint> Checkpoints,
    IReadOnlyList<DiagnosticsHealthSummary> Health,
    IReadOnlyList<RedactedAuditEvent> Audit);

public interface IDiagnosticsExportStore
{
    Task<DiagnosticsSnapshot> CreateAsync(
        RbacActorContext actor,
        DiagnosticsExportRequest request,
        CancellationToken cancellationToken);
}

public sealed class DiagnosticsExportService(
    RbacAuthorizationService authorization,
    IDiagnosticsExportStore store)
{
    public async Task<DiagnosticsExportResult> CreateAsync(
        RbacActorContext actor,
        DiagnosticsExportRequest request,
        CancellationToken cancellationToken)
    {
        if (!await authorization.HasGlobalAsync(
                actor.UserId, actor.SessionId, RbacPermissions.AuditRead, cancellationToken))
            return new DiagnosticsExportResult(DiagnosticsExportStatus.Forbidden);
        if (request.LookbackHours is < 1 or > 168)
            return new DiagnosticsExportResult(DiagnosticsExportStatus.Invalid);
        return new DiagnosticsExportResult(
            DiagnosticsExportStatus.Success,
            await store.CreateAsync(actor, request, cancellationToken));
    }
}

public enum DiagnosticsExportStatus { Success, Forbidden, Invalid }

public sealed record DiagnosticsExportResult(
    DiagnosticsExportStatus Status,
    DiagnosticsSnapshot? Snapshot = null);
