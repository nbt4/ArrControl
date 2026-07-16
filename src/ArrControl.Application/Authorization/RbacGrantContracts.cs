namespace ArrControl.Application.Authorization;

public sealed record StoredPermissionGrant(
    string PermissionCode,
    Guid? InstanceGroupId);

public interface IRbacGrantStore
{
    Task<IReadOnlyCollection<StoredPermissionGrant>> GetGrantsAsync(
        Guid userId,
        Guid sessionId,
        CancellationToken cancellationToken);
}
