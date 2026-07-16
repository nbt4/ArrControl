namespace ArrControl.Application.Authorization;

public sealed class RbacAuthorizationService(IRbacGrantStore store)
{
    private readonly Lock cacheLock = new();
    private readonly Dictionary<AuthorizationActor, Task<EffectiveAuthorization>> snapshots = [];

    public async Task<EffectiveAuthorization> GetSnapshotAsync(
        Guid userId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || sessionId == Guid.Empty)
        {
            return EffectiveAuthorization.Empty;
        }

        var actor = new AuthorizationActor(userId, sessionId);
        Task<EffectiveAuthorization> snapshotTask;
        lock (cacheLock)
        {
            if (!snapshots.TryGetValue(actor, out snapshotTask!))
            {
                snapshotTask = LoadSnapshotAsync(actor, cancellationToken);
                snapshots.Add(actor, snapshotTask);
            }
        }

        return await snapshotTask.WaitAsync(cancellationToken);
    }

    public async Task<bool> HasAnyScopeAsync(
        Guid userId,
        Guid sessionId,
        string? permissionCode,
        CancellationToken cancellationToken) =>
        (await GetSnapshotAsync(userId, sessionId, cancellationToken))
            .HasAnyScope(permissionCode);

    public async Task<bool> HasGlobalAsync(
        Guid userId,
        Guid sessionId,
        string? permissionCode,
        CancellationToken cancellationToken) =>
        (await GetSnapshotAsync(userId, sessionId, cancellationToken))
            .HasGlobal(permissionCode);

    public async Task<bool> HasInstanceGroupAsync(
        Guid userId,
        Guid sessionId,
        string? permissionCode,
        Guid? instanceGroupId,
        CancellationToken cancellationToken) =>
        (await GetSnapshotAsync(userId, sessionId, cancellationToken))
            .HasInstanceGroup(permissionCode, instanceGroupId);

    private async Task<EffectiveAuthorization> LoadSnapshotAsync(
        AuthorizationActor actor,
        CancellationToken cancellationToken)
    {
        var storedGrants = await store.GetGrantsAsync(
            actor.UserId,
            actor.SessionId,
            cancellationToken);
        return EffectiveAuthorization.Create(storedGrants);
    }

    private sealed record AuthorizationActor(Guid UserId, Guid SessionId);
}
