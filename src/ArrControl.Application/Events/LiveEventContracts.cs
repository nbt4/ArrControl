using ArrControl.Application.Authorization;

namespace ArrControl.Application.Events;

public static class LiveEventResources
{
    public const string Instances = "instances";
    public const string Missing = "missing";
    public const string Activity = "activity";
    public const string Health = "health";
    public const string Operations = "operations";
    public const string Audit = "audit";
}

public sealed record LiveEventTarget(Guid InstanceId, Guid? InstanceGroupId);

public sealed record LiveEventPayload(
    int Version,
    string Resource,
    string RequiredPermission,
    IReadOnlyList<LiveEventTarget> Targets,
    Guid? ActorUserId = null);

public sealed record LiveEvent(
    Guid Id,
    string Type,
    string Resource,
    DateTimeOffset OccurredAt,
    IReadOnlyList<Guid> InstanceIds);

public sealed record LiveEventBatch(
    IReadOnlyList<LiveEvent> Events,
    string Cursor,
    bool CursorAdvanced,
    bool HasMore);

public sealed record LiveSnapshot(
    int Version,
    string Cursor,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<string> Resources);

public sealed record LiveEventAccess(
    Guid UserId,
    IReadOnlyList<InstanceGroupAuthorization> Grants);

public interface ILiveEventStore
{
    Task<string> GetLatestCursorAsync(CancellationToken cancellationToken);

    Task<bool> CursorExistsAsync(string cursor, CancellationToken cancellationToken);

    Task<LiveEventBatch> ReadAsync(
        string cursor,
        LiveEventAccess access,
        int maximumCount,
        CancellationToken cancellationToken);
}

public sealed class LiveEventService(
    RbacAuthorizationService authorization,
    ILiveEventStore store,
    TimeProvider timeProvider)
{
    public const string OriginCursor = "origin";

    public async Task<LiveSnapshot?> GetSnapshotAsync(
        Guid userId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var snapshot = await authorization.GetSnapshotAsync(userId, sessionId, cancellationToken);
        if (snapshot.Grants.Count == 0) return null;
        return new LiveSnapshot(
            1,
            await store.GetLatestCursorAsync(cancellationToken),
            timeProvider.GetUtcNow(),
            Resources(snapshot));
    }

    public async Task<LiveEventSession?> OpenAsync(
        Guid userId,
        Guid sessionId,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 64)
            return new LiveEventSession(LiveEventSessionStatus.SnapshotRequired, null, null);
        var snapshot = await authorization.GetSnapshotAsync(userId, sessionId, cancellationToken);
        if (snapshot.Grants.Count == 0) return null;
        if (!string.Equals(cursor, OriginCursor, StringComparison.Ordinal)
            && (!Guid.TryParseExact(cursor, "D", out _) || !await store.CursorExistsAsync(cursor, cancellationToken)))
            return new LiveEventSession(LiveEventSessionStatus.SnapshotRequired, null, null);
        return new LiveEventSession(
            LiveEventSessionStatus.Ready,
            cursor,
            new LiveEventAccess(userId, snapshot.Grants));
    }

    public Task<LiveEventBatch> ReadAsync(
        LiveEventSession session,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (session.Status != LiveEventSessionStatus.Ready
            || session.Cursor is null || session.Access is null)
            throw new InvalidOperationException("The live event session is not ready.");
        return store.ReadAsync(session.Cursor, session.Access, maximumCount, cancellationToken);
    }

    public LiveEventSession Advance(LiveEventSession session, LiveEventBatch batch) =>
        session with { Cursor = batch.Cursor };

    private static string[] Resources(EffectiveAuthorization authorization)
    {
        var resources = new HashSet<string>(StringComparer.Ordinal);
        if (authorization.HasAnyScope(RbacPermissions.InstancesRead))
        {
            resources.Add("/api/v1/instances");
            resources.Add("/api/v1/queue");
            resources.Add("/api/v1/history");
            resources.Add("/api/v1/health/incidents");
        }
        if (authorization.HasAnyScope(RbacPermissions.LibraryRead))
            resources.Add("/api/v1/missing");
        if (authorization.HasGlobal(RbacPermissions.AuditRead))
            resources.Add("/api/v1/audit");
        return resources.Order(StringComparer.Ordinal).ToArray();
    }
}

public enum LiveEventSessionStatus
{
    Ready,
    SnapshotRequired,
}

public sealed record LiveEventSession(
    LiveEventSessionStatus Status,
    string? Cursor,
    LiveEventAccess? Access);
