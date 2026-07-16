using ArrControl.Application.Authorization;

namespace ArrControl.Application.Connections;

public sealed class ScopedInstanceReadService(
    RbacAuthorizationService authorizationService,
    IScopedInstanceReadStore store)
{
    public async Task<IReadOnlyList<VisibleInstance>?> ListAsync(
        Guid userId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var snapshot = await authorizationService.GetSnapshotAsync(
            userId,
            sessionId,
            cancellationToken);
        var grant = snapshot.Grants.SingleOrDefault(
            value => value.PermissionCode == RbacPermissions.InstancesRead);
        if (grant is null)
        {
            return null;
        }

        return await store.ListAsync(
            grant.IsGlobal,
            grant.InstanceGroupIds,
            cancellationToken);
    }
}
