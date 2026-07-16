namespace ArrControl.Application.Authorization;

public sealed class RbacAdministrationService(IRbacAdministrationStore store)
{
    public Task<IReadOnlyList<AuthorizationRole>> ListRolesAsync(
        CancellationToken cancellationToken) =>
        store.ListRolesAsync(cancellationToken);

    public Task<AuthorizationRole?> GetRoleAsync(
        Guid roleId,
        CancellationToken cancellationToken)
    {
        if (roleId == Guid.Empty)
        {
            throw new RbacAdministrationValidationException("role_id_invalid");
        }

        return store.GetRoleAsync(roleId, cancellationToken);
    }

    public Task<IReadOnlyList<AuthorizationUser>> ListUsersAsync(
        CancellationToken cancellationToken) =>
        store.ListUsersAsync(cancellationToken);

    public Task<IReadOnlyList<AuthorizationInstanceGroup>> ListInstanceGroupsAsync(
        CancellationToken cancellationToken) =>
        store.ListInstanceGroupsAsync(cancellationToken);

    public Task<IReadOnlyList<RoleAssignment>?> GetManualRoleAssignmentsAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            throw new RbacAdministrationValidationException("user_id_invalid");
        }

        return store.GetManualRoleAssignmentsAsync(userId, cancellationToken);
    }

    public Task<UpsertAuthorizationRoleResult> UpsertRoleAsync(
        RbacActorContext actor,
        Guid roleId,
        string? name,
        IReadOnlyCollection<string>? permissionCodes,
        CancellationToken cancellationToken)
    {
        ValidateActor(actor);
        if (roleId == Guid.Empty)
        {
            throw new RbacAdministrationValidationException("role_id_invalid");
        }

        var canonicalName = ValidateRoleName(name);
        var normalizedName = canonicalName.ToUpperInvariant();
        if (normalizedName.Length > RbacAdministrationLimits.MaximumRoleNameLength)
        {
            throw new RbacAdministrationValidationException("role_name_invalid");
        }

        var permissions = ValidatePermissions(permissionCodes);
        return store.UpsertRoleAsync(
            actor,
            new ValidatedAuthorizationRole(
                roleId,
                canonicalName,
                normalizedName,
                permissions),
            cancellationToken);
    }

    public Task<DeleteAuthorizationRoleStatus> DeleteRoleAsync(
        RbacActorContext actor,
        Guid roleId,
        CancellationToken cancellationToken)
    {
        ValidateActor(actor);
        if (roleId == Guid.Empty)
        {
            throw new RbacAdministrationValidationException("role_id_invalid");
        }

        return store.DeleteRoleAsync(actor, roleId, cancellationToken);
    }

    public Task<ReplaceRoleAssignmentsResult> ReplaceManualRoleAssignmentsAsync(
        RbacActorContext actor,
        Guid userId,
        IReadOnlyCollection<RoleAssignmentInput>? assignments,
        CancellationToken cancellationToken)
    {
        ValidateActor(actor);
        if (userId == Guid.Empty)
        {
            throw new RbacAdministrationValidationException("user_id_invalid");
        }

        if (assignments is null
            || assignments.Count > RbacAdministrationLimits.MaximumAssignmentsPerUser)
        {
            throw new RbacAdministrationValidationException("role_assignments_invalid");
        }

        var validated = new List<RoleAssignmentInput>(assignments.Count);
        var keys = new HashSet<(Guid RoleId, Guid? InstanceGroupId)>();
        foreach (var assignment in assignments)
        {
            if (assignment.RoleId == Guid.Empty
                || assignment.InstanceGroupId == Guid.Empty
                || !keys.Add((assignment.RoleId, assignment.InstanceGroupId)))
            {
                throw new RbacAdministrationValidationException("role_assignments_invalid");
            }

            validated.Add(assignment);
        }

        validated.Sort(static (left, right) =>
        {
            var roleComparison = left.RoleId.CompareTo(right.RoleId);
            if (roleComparison != 0)
            {
                return roleComparison;
            }

            return Nullable.Compare(left.InstanceGroupId, right.InstanceGroupId);
        });

        return store.ReplaceManualRoleAssignmentsAsync(
            actor,
            userId,
            validated,
            cancellationToken);
    }

    private static void ValidateActor(RbacActorContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.UserId == Guid.Empty
            || actor.SessionId == Guid.Empty
            || string.IsNullOrWhiteSpace(actor.Email))
        {
            throw new RbacAdministrationValidationException("authorization_actor_invalid");
        }
    }

    private static string ValidateRoleName(string? name)
    {
        var canonicalName = name?.Trim();
        if (string.IsNullOrEmpty(canonicalName)
            || canonicalName.Length > RbacAdministrationLimits.MaximumRoleNameLength
            || canonicalName.Any(char.IsControl))
        {
            throw new RbacAdministrationValidationException("role_name_invalid");
        }

        return canonicalName;
    }

    private static IReadOnlyList<string> ValidatePermissions(
        IReadOnlyCollection<string>? permissionCodes)
    {
        if (permissionCodes is null || permissionCodes.Count > RbacPermissions.All.Count)
        {
            throw new RbacAdministrationValidationException("role_permissions_invalid");
        }

        var permissions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var permissionCode in permissionCodes)
        {
            if (!RbacPermissions.IsKnown(permissionCode)
                || !permissions.Add(permissionCode))
            {
                throw new RbacAdministrationValidationException("role_permissions_invalid");
            }
        }

        return permissions.ToArray();
    }
}
