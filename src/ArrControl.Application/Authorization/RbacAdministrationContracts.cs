using ArrControl.Application.Identity;

namespace ArrControl.Application.Authorization;

public static class RbacAdministrationLimits
{
    public const int MaximumRoleNameLength = 120;
    public const int MaximumAssignmentsPerUser = 256;
}

public sealed record RbacActorContext(
    Guid UserId,
    Guid SessionId,
    string Email,
    AuthenticationRequestContext RequestContext);

public sealed record AuthorizationRole(
    Guid Id,
    string Name,
    bool IsSystem,
    IReadOnlyList<string> Permissions);

public sealed record AuthorizationUser(
    Guid UserId,
    string Email,
    string State);

public sealed record AuthorizationInstanceGroup(
    Guid Id,
    string Name);

public sealed record RoleAssignment(
    Guid RoleId,
    string RoleName,
    Guid? InstanceGroupId);

public sealed record RoleAssignmentInput(
    Guid RoleId,
    Guid? InstanceGroupId);

public sealed record ValidatedAuthorizationRole(
    Guid Id,
    string Name,
    string NormalizedName,
    IReadOnlyList<string> Permissions);

public enum UpsertAuthorizationRoleStatus
{
    Created,
    Updated,
    Unchanged,
    Forbidden,
    NameConflict,
    SystemRoleImmutable,
    AuthorizationLockout,
}

public sealed record UpsertAuthorizationRoleResult(
    UpsertAuthorizationRoleStatus Status,
    AuthorizationRole? Role = null);

public enum DeleteAuthorizationRoleStatus
{
    Deleted,
    Absent,
    Forbidden,
    SystemRoleImmutable,
    AuthorizationLockout,
}

public enum ReplaceRoleAssignmentsStatus
{
    Updated,
    Unchanged,
    Forbidden,
    UserNotFound,
    RoleNotFound,
    InstanceGroupNotFound,
    AuthorizationLockout,
}

public sealed record ReplaceRoleAssignmentsResult(
    ReplaceRoleAssignmentsStatus Status,
    IReadOnlyList<RoleAssignment>? Assignments = null);

public interface IRbacAdministrationStore
{
    Task<IReadOnlyList<AuthorizationRole>> ListRolesAsync(
        CancellationToken cancellationToken);

    Task<AuthorizationRole?> GetRoleAsync(
        Guid roleId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AuthorizationUser>> ListUsersAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AuthorizationInstanceGroup>> ListInstanceGroupsAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RoleAssignment>?> GetManualRoleAssignmentsAsync(
        Guid userId,
        CancellationToken cancellationToken);

    Task<UpsertAuthorizationRoleResult> UpsertRoleAsync(
        RbacActorContext actor,
        ValidatedAuthorizationRole role,
        CancellationToken cancellationToken);

    Task<DeleteAuthorizationRoleStatus> DeleteRoleAsync(
        RbacActorContext actor,
        Guid roleId,
        CancellationToken cancellationToken);

    Task<ReplaceRoleAssignmentsResult> ReplaceManualRoleAssignmentsAsync(
        RbacActorContext actor,
        Guid userId,
        IReadOnlyList<RoleAssignmentInput> assignments,
        CancellationToken cancellationToken);
}

public sealed class RbacAdministrationValidationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
