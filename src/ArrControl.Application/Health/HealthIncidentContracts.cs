using System.Security.Cryptography;
using System.Text;
using ArrControl.Application.Authorization;
using ArrControl.Application.Automation;
using ArrControl.Application.Providers;

namespace ArrControl.Application.Health;

public static class HealthJobTypes
{
    public const string Sync = "health.sync";
    public const string CheckpointStream = "health";
}

public static class HealthIncidentLimits
{
    public const int MaximumSourceLength = 300;
    public const int MaximumMessageLength = 4_000;
    public const int MaximumSourcesPerSnapshot = 1_000;
    public static readonly TimeSpan MaximumSnooze = TimeSpan.FromDays(30);
}

public sealed record HealthIncidentSourceSnapshot(
    string SourceKey,
    int ProviderIssueId,
    string Source,
    string Severity,
    string? Message,
    string? RemediationUrl);

public sealed record HealthIncidentGroupSnapshot(
    string GroupKey,
    string Severity,
    string? RemediationUrl,
    IReadOnlyList<HealthIncidentSourceSnapshot> Sources);

public static class HealthIncidentGrouper
{
    public static IReadOnlyList<HealthIncidentGroupSnapshot> Group(
        string providerKind,
        IReadOnlyList<ProviderHealthIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(providerKind)
            || issues.Count > HealthIncidentLimits.MaximumSourcesPerSnapshot)
        {
            throw new ArgumentException("The health snapshot is invalid.", nameof(issues));
        }

        return issues.Select(issue => Normalize(providerKind, issue))
            .GroupBy(value => value.GroupKey, StringComparer.Ordinal)
            .Select(group => new HealthIncidentGroupSnapshot(
                group.Key,
                group.OrderByDescending(value => SeverityRank(value.Source.Severity))
                    .Select(value => value.Source.Severity).First(),
                group.Select(value => value.Source.RemediationUrl)
                    .FirstOrDefault(value => value is not null),
                group.Select(value => value.Source)
                    .DistinctBy(value => value.SourceKey, StringComparer.Ordinal)
                    .OrderBy(value => value.Source, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(value => value.ProviderIssueId)
                    .ToArray()))
            .OrderByDescending(value => SeverityRank(value.Severity))
            .ThenBy(value => value.GroupKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static (string GroupKey, HealthIncidentSourceSnapshot Source) Normalize(
        string providerKind,
        ProviderHealthIssue issue)
    {
        var source = RequireBounded(issue.Source, HealthIncidentLimits.MaximumSourceLength);
        var message = Truncate(issue.Message, HealthIncidentLimits.MaximumMessageLength);
        var remediation = SafeRemediationUrl(issue.WikiUrl);
        var identity = remediation is null
            ? $"source:{NormalizeText(source)}"
            : $"remediation:{remediation.ToLowerInvariant().TrimEnd('/')}";
        var groupKey = Hash($"{providerKind.ToLowerInvariant()}|{identity}");
        var sourceKey = Hash($"{issue.Id}|{NormalizeText(source)}");
        return (groupKey, new HealthIncidentSourceSnapshot(
            sourceKey,
            issue.Id,
            source,
            NormalizeSeverity(issue.Severity),
            message,
            remediation));
    }

    private static string RequireBounded(string value, int maximumLength)
    {
        var trimmed = value.Trim();
        if (trimmed.Length is 0 || trimmed.Length > maximumLength)
            throw new ArgumentException("The health source is invalid.");
        return trimmed;
    }

    private static string? Truncate(string? value, int maximumLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? null
            : trimmed[..Math.Min(trimmed.Length, maximumLength)];
    }

    private static string? SafeRemediationUrl(Uri? uri) =>
        uri is { IsAbsoluteUri: true } && uri.Scheme is "http" or "https"
            ? uri.AbsoluteUri
            : null;

    private static string NormalizeText(string value) =>
        string.Join(' ', value.Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeSeverity(string? severity) => severity?.ToLowerInvariant() switch
    {
        "error" => "error",
        "warning" => "warning",
        "notice" => "notice",
        "ok" => "ok",
        _ => "unknown",
    };

    private static int SeverityRank(string severity) => severity switch
    {
        "error" => 4,
        "warning" => 3,
        "notice" => 2,
        "unknown" => 1,
        _ => 0,
    };

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public interface IHealthIncidentSnapshotStore
{
    Task ApplyAsync(
        Guid instanceId,
        string providerKind,
        DateTimeOffset observedAt,
        IReadOnlyList<HealthIncidentGroupSnapshot> groups,
        CancellationToken cancellationToken);
}

public interface IHealthScheduleProvisioner
{
    Task<int> ReconcileAsync(CancellationToken cancellationToken);
}

public sealed class HealthSyncJobHandler(
    ArrControl.Application.Catalog.ICatalogSyncTargetResolver targetResolver,
    IHealthIncidentSnapshotStore store,
    IEnumerable<IArrProviderClient> clients,
    TimeProvider timeProvider) : IScheduledJobHandler
{
    public string Type => HealthJobTypes.Sync;

    public async Task<JobHandlerResult> ExecuteAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        Guid instanceId;
        try
        {
            instanceId = ArrControl.Application.Catalog.CatalogJobScope.ParseInstanceId(job.ScopeJson);
        }
        catch (ScheduledJobException exception) when (exception.Code == "catalog_scope_invalid")
        {
            throw new ScheduledJobException("health_scope_invalid");
        }

        var target = await targetResolver.ResolveAsync(instanceId, cancellationToken);
        if (target is null) return JobHandlerResult.Completed;
        var client = clients.SingleOrDefault(value => value.Kind == target.Kind);
        if (client is null) throw new ScheduledJobException("health_provider_unsupported");

        ProviderCallResult<IReadOnlyList<ProviderHealthIssue>> result;
        try
        {
            result = await client.GetHealthAsync(target.Connection, cancellationToken);
        }
        catch (ProviderTransportException exception)
        {
            throw new ScheduledJobException($"health_{exception.Code}");
        }

        if (!result.Success || result.Value is null)
            throw new ScheduledJobException($"health_{result.ErrorCode ?? ProviderErrorCodes.Unknown}");

        var observedAt = timeProvider.GetUtcNow();
        var groups = HealthIncidentGrouper.Group(target.Kind, result.Value);
        await store.ApplyAsync(instanceId, target.Kind, observedAt, groups, cancellationToken);
        return new JobHandlerResult(
        [
            new SyncCheckpointUpdate(
                instanceId,
                HealthJobTypes.CheckpointStream,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    version = 1,
                    observedAt,
                    incidentCount = groups.Count,
                    sourceCount = groups.Sum(value => value.Sources.Count),
                })),
        ]);
    }
}

public sealed record HealthIncidentSourceDetails(
    int ProviderIssueId,
    string Source,
    string Severity,
    string? Message,
    string? RemediationUrl,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    bool Active);

public sealed record HealthIncidentDetails(
    Guid Id,
    Guid InstanceId,
    string InstanceName,
    string ProviderKind,
    string Severity,
    string? RemediationUrl,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? AcknowledgedAt,
    Guid? AcknowledgedByUserId,
    DateTimeOffset? SnoozedUntil,
    Guid? SnoozedByUserId,
    bool Stale,
    IReadOnlyList<HealthIncidentSourceDetails> Sources);

public sealed record HealthIncidentScope(bool Exists, Guid? InstanceGroupId);

public interface IHealthIncidentStore
{
    Task<IReadOnlyList<HealthIncidentDetails>> QueryAsync(
        bool includeAll,
        IReadOnlyCollection<Guid> instanceGroupIds,
        IReadOnlyCollection<Guid> instanceIds,
        bool includeResolved,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<HealthIncidentScope> GetScopeAsync(Guid incidentId, CancellationToken cancellationToken);

    Task<HealthIncidentDetails?> SetAcknowledgementAsync(
        RbacActorContext actor,
        Guid incidentId,
        bool acknowledged,
        CancellationToken cancellationToken);

    Task<HealthIncidentDetails?> SetSnoozeAsync(
        RbacActorContext actor,
        Guid incidentId,
        DateTimeOffset? snoozedUntil,
        CancellationToken cancellationToken);
}

public sealed class HealthIncidentService(
    RbacAuthorizationService authorization,
    IHealthIncidentStore store,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<HealthIncidentDetails>?> QueryAsync(
        Guid userId,
        Guid sessionId,
        IReadOnlyCollection<Guid> instanceIds,
        bool includeResolved,
        CancellationToken cancellationToken)
    {
        if (instanceIds.Count > 100) throw new ArgumentOutOfRangeException(nameof(instanceIds));
        var grant = (await authorization.GetSnapshotAsync(userId, sessionId, cancellationToken)).Grants
            .SingleOrDefault(value => value.PermissionCode == RbacPermissions.InstancesRead);
        return grant is null ? null : await store.QueryAsync(
            grant.IsGlobal, grant.InstanceGroupIds, instanceIds, includeResolved,
            timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<HealthIncidentMutationResult> SetAcknowledgementAsync(
        RbacActorContext actor,
        Guid incidentId,
        bool acknowledged,
        CancellationToken cancellationToken) =>
        MutateAsync(actor, incidentId, () => store.SetAcknowledgementAsync(
            actor, incidentId, acknowledged, cancellationToken), cancellationToken);

    public async Task<HealthIncidentMutationResult> SetSnoozeAsync(
        RbacActorContext actor,
        Guid incidentId,
        DateTimeOffset? snoozedUntil,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (snoozedUntil is not null
            && (snoozedUntil <= now || snoozedUntil > now + HealthIncidentLimits.MaximumSnooze))
            return new HealthIncidentMutationResult(HealthIncidentMutationStatus.Invalid);
        return await MutateAsync(actor, incidentId, () => store.SetSnoozeAsync(
            actor, incidentId, snoozedUntil, cancellationToken), cancellationToken);
    }

    private async Task<HealthIncidentMutationResult> MutateAsync(
        RbacActorContext actor,
        Guid incidentId,
        Func<Task<HealthIncidentDetails?>> mutation,
        CancellationToken cancellationToken)
    {
        if (incidentId == Guid.Empty) return new(HealthIncidentMutationStatus.Invalid);
        var scope = await store.GetScopeAsync(incidentId, cancellationToken);
        if (!scope.Exists) return new(HealthIncidentMutationStatus.NotFound);
        if (!await authorization.HasInstanceGroupAsync(
                actor.UserId, actor.SessionId, RbacPermissions.TasksExecute,
                scope.InstanceGroupId, cancellationToken))
            return new(HealthIncidentMutationStatus.Forbidden);
        var incident = await mutation();
        return incident is null
            ? new(HealthIncidentMutationStatus.NotFound)
            : new(HealthIncidentMutationStatus.Updated, incident);
    }
}

public enum HealthIncidentMutationStatus
{
    Updated,
    NotFound,
    Forbidden,
    Invalid,
}

public sealed record HealthIncidentMutationResult(
    HealthIncidentMutationStatus Status,
    HealthIncidentDetails? Incident = null);
