using System.Data;
using System.Text.Json;
using ArrControl.Application.Authorization;
using ArrControl.Application.Identity;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Connections;
using ArrControl.Infrastructure.Persistence.Identity;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Authorization;

public sealed class EfRbacAdministrationStore(
    ArrControlDbContext dbContext,
    TimeProvider timeProvider) : IRbacAdministrationStore
{
    private const long AuthorizationMutationLock = 4596771108351201941;

    public async Task<IReadOnlyList<AuthorizationRole>> ListRolesAsync(
        CancellationToken cancellationToken)
    {
        var roles = await dbContext.Set<RoleEntity>()
            .AsNoTracking()
            .Include(role => role.Permissions)
            .ThenInclude(rolePermission => rolePermission.Permission)
            .OrderBy(role => role.NormalizedName)
            .ThenBy(role => role.Id)
            .ToListAsync(cancellationToken);
        return roles.Select(ToRole).ToArray();
    }

    public async Task<AuthorizationRole?> GetRoleAsync(
        Guid roleId,
        CancellationToken cancellationToken)
    {
        var role = await dbContext.Set<RoleEntity>()
            .AsNoTracking()
            .Include(value => value.Permissions)
            .ThenInclude(value => value.Permission)
            .SingleOrDefaultAsync(value => value.Id == roleId, cancellationToken);
        return role is null ? null : ToRole(role);
    }

    public async Task<IReadOnlyList<AuthorizationUser>> ListUsersAsync(
        CancellationToken cancellationToken) =>
        await dbContext.Set<UserEntity>()
            .AsNoTracking()
            .OrderBy(user => user.NormalizedEmail)
            .ThenBy(user => user.Id)
            .Select(user => new AuthorizationUser(user.Id, user.Email, user.State))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AuthorizationInstanceGroup>> ListInstanceGroupsAsync(
        CancellationToken cancellationToken) =>
        await dbContext.Set<InstanceGroupEntity>()
            .AsNoTracking()
            .OrderBy(group => group.Name)
            .ThenBy(group => group.Id)
            .Select(group => new AuthorizationInstanceGroup(group.Id, group.Name))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RoleAssignment>?> GetManualRoleAssignmentsAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (!await dbContext.Set<UserEntity>()
                .AsNoTracking()
                .AnyAsync(user => user.Id == userId, cancellationToken))
        {
            return null;
        }

        return await dbContext.Set<UserRoleEntity>()
            .AsNoTracking()
            .Where(assignment => assignment.UserId == userId)
            .OrderBy(assignment => assignment.Role.NormalizedName)
            .ThenBy(assignment => assignment.InstanceGroupId)
            .ThenBy(assignment => assignment.Id)
            .Select(assignment => new RoleAssignment(
                assignment.RoleId,
                assignment.Role.Name,
                assignment.InstanceGroupId))
            .ToListAsync(cancellationToken);
    }

    public async Task<UpsertAuthorizationRoleResult> UpsertRoleAsync(
        RbacActorContext actor,
        ValidatedAuthorizationRole requestedRole,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginMutationAsync(cancellationToken);
        if (!await ActorCanManageAuthorizationAsync(actor, cancellationToken))
        {
            await SaveAuditAsync(
                actor,
                "authorization.role_upsert",
                "forbidden",
                RoleScope(requestedRole.Id),
                RoleSummary(requestedRole.Permissions.Count),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new UpsertAuthorizationRoleResult(UpsertAuthorizationRoleStatus.Forbidden);
        }

        var role = await dbContext.Set<RoleEntity>()
            .Include(value => value.Permissions)
            .ThenInclude(value => value.Permission)
            .SingleOrDefaultAsync(value => value.Id == requestedRole.Id, cancellationToken);
        if (role?.IsSystem is true)
        {
            await SaveAuditAsync(
                actor,
                "authorization.role_upsert",
                "rejected",
                RoleScope(requestedRole.Id),
                ErrorSummary("system_role_immutable"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new UpsertAuthorizationRoleResult(
                UpsertAuthorizationRoleStatus.SystemRoleImmutable);
        }

        var nameConflict = await dbContext.Set<RoleEntity>()
            .AsNoTracking()
            .AnyAsync(
                value => value.Id != requestedRole.Id
                    && value.NormalizedName == requestedRole.NormalizedName,
                cancellationToken);
        if (nameConflict)
        {
            await SaveAuditAsync(
                actor,
                "authorization.role_upsert",
                "rejected",
                RoleScope(requestedRole.Id),
                ErrorSummary("role_name_conflict"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new UpsertAuthorizationRoleResult(UpsertAuthorizationRoleStatus.NameConflict);
        }

        var permissionEntities = await dbContext.Set<PermissionEntity>()
            .Where(permission => requestedRole.Permissions.Contains(permission.Code))
            .ToDictionaryAsync(permission => permission.Code, StringComparer.Ordinal, cancellationToken);
        if (permissionEntities.Count != requestedRole.Permissions.Count)
        {
            throw new InvalidOperationException(
                "The RBAC permission catalog is incomplete; apply pending migrations before startup.");
        }

        var created = role is null;
        role ??= new RoleEntity
        {
            Id = requestedRole.Id,
            Name = requestedRole.Name,
            NormalizedName = requestedRole.NormalizedName,
            IsSystem = false,
        };
        if (created)
        {
            dbContext.Add(role);
        }

        var desiredPermissions = requestedRole.Permissions.ToHashSet(StringComparer.Ordinal);
        var currentPermissions = role.Permissions
            .Select(value => value.Permission.Code)
            .ToHashSet(StringComparer.Ordinal);
        var changed = created
            || !string.Equals(role.Name, requestedRole.Name, StringComparison.Ordinal)
            || !string.Equals(role.NormalizedName, requestedRole.NormalizedName, StringComparison.Ordinal)
            || !currentPermissions.SetEquals(desiredPermissions);

        role.Name = requestedRole.Name;
        role.NormalizedName = requestedRole.NormalizedName;
        foreach (var obsolete in role.Permissions
                     .Where(value => !desiredPermissions.Contains(value.Permission.Code))
                     .ToArray())
        {
            dbContext.Remove(obsolete);
        }

        foreach (var permissionCode in desiredPermissions.Except(currentPermissions))
        {
            role.Permissions.Add(new RolePermissionEntity
            {
                Role = role,
                Permission = permissionEntities[permissionCode],
            });
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            if (!await HasAnyActiveGlobalManagerAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
                dbContext.ChangeTracker.Clear();
                await SaveAuditAsync(
                    actor,
                    "authorization.role_upsert",
                    "rejected",
                    RoleScope(requestedRole.Id),
                    ErrorSummary("authorization_lockout"),
                    cancellationToken);
                return new UpsertAuthorizationRoleResult(
                    UpsertAuthorizationRoleStatus.AuthorizationLockout);
            }
        }

        var status = created
            ? UpsertAuthorizationRoleStatus.Created
            : changed
                ? UpsertAuthorizationRoleStatus.Updated
                : UpsertAuthorizationRoleStatus.Unchanged;
        await SaveAuditAsync(
            actor,
            "authorization.role_upsert",
            status.ToString().ToLowerInvariant(),
            RoleScope(requestedRole.Id),
            RoleSummary(requestedRole.Permissions.Count),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new UpsertAuthorizationRoleResult(status, new AuthorizationRole(
            requestedRole.Id,
            requestedRole.Name,
            false,
            requestedRole.Permissions));
    }

    public async Task<DeleteAuthorizationRoleStatus> DeleteRoleAsync(
        RbacActorContext actor,
        Guid roleId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginMutationAsync(cancellationToken);
        if (!await ActorCanManageAuthorizationAsync(actor, cancellationToken))
        {
            await SaveAuditAsync(
                actor,
                "authorization.role_delete",
                "forbidden",
                RoleScope(roleId),
                EmptySummary(),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return DeleteAuthorizationRoleStatus.Forbidden;
        }

        var role = await dbContext.Set<RoleEntity>()
            .SingleOrDefaultAsync(value => value.Id == roleId, cancellationToken);
        if (role is null)
        {
            await SaveAuditAsync(
                actor,
                "authorization.role_delete",
                "absent",
                RoleScope(roleId),
                EmptySummary(),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return DeleteAuthorizationRoleStatus.Absent;
        }

        if (role.IsSystem)
        {
            await SaveAuditAsync(
                actor,
                "authorization.role_delete",
                "rejected",
                RoleScope(roleId),
                ErrorSummary("system_role_immutable"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return DeleteAuthorizationRoleStatus.SystemRoleImmutable;
        }

        dbContext.Remove(role);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (!await HasAnyActiveGlobalManagerAsync(cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            dbContext.ChangeTracker.Clear();
            await SaveAuditAsync(
                actor,
                "authorization.role_delete",
                "rejected",
                RoleScope(roleId),
                ErrorSummary("authorization_lockout"),
                cancellationToken);
            return DeleteAuthorizationRoleStatus.AuthorizationLockout;
        }

        await SaveAuditAsync(
            actor,
            "authorization.role_delete",
            "deleted",
            RoleScope(roleId),
            EmptySummary(),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return DeleteAuthorizationRoleStatus.Deleted;
    }

    public async Task<ReplaceRoleAssignmentsResult> ReplaceManualRoleAssignmentsAsync(
        RbacActorContext actor,
        Guid userId,
        IReadOnlyList<RoleAssignmentInput> assignments,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginMutationAsync(cancellationToken);
        if (!await ActorCanManageAuthorizationAsync(actor, cancellationToken))
        {
            await SaveAuditAsync(
                actor,
                "authorization.assignments_replace",
                "forbidden",
                UserScope(userId),
                AssignmentSummary(assignments.Count),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ReplaceRoleAssignmentsResult(ReplaceRoleAssignmentsStatus.Forbidden);
        }

        if (!await dbContext.Set<UserEntity>().AnyAsync(user => user.Id == userId, cancellationToken))
        {
            await SaveAuditAsync(
                actor,
                "authorization.assignments_replace",
                "rejected",
                UserScope(userId),
                ErrorSummary("user_not_found"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ReplaceRoleAssignmentsResult(ReplaceRoleAssignmentsStatus.UserNotFound);
        }

        var roleIds = assignments.Select(value => value.RoleId).Distinct().ToArray();
        var roles = await dbContext.Set<RoleEntity>()
            .Where(role => roleIds.Contains(role.Id))
            .ToDictionaryAsync(role => role.Id, cancellationToken);
        if (roles.Count != roleIds.Length)
        {
            await SaveAuditAsync(
                actor,
                "authorization.assignments_replace",
                "rejected",
                UserScope(userId),
                ErrorSummary("role_not_found"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ReplaceRoleAssignmentsResult(ReplaceRoleAssignmentsStatus.RoleNotFound);
        }

        var groupIds = assignments
            .Where(value => value.InstanceGroupId is not null)
            .Select(value => value.InstanceGroupId!.Value)
            .Distinct()
            .ToArray();
        var existingGroupCount = await dbContext.Set<InstanceGroupEntity>()
            .CountAsync(group => groupIds.Contains(group.Id), cancellationToken);
        if (existingGroupCount != groupIds.Length)
        {
            await SaveAuditAsync(
                actor,
                "authorization.assignments_replace",
                "rejected",
                UserScope(userId),
                ErrorSummary("instance_group_not_found"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ReplaceRoleAssignmentsResult(
                ReplaceRoleAssignmentsStatus.InstanceGroupNotFound);
        }

        var existing = await dbContext.Set<UserRoleEntity>()
            .Where(value => value.UserId == userId)
            .ToListAsync(cancellationToken);
        var requestedKeys = assignments
            .Select(value => (value.RoleId, value.InstanceGroupId))
            .ToHashSet();
        var existingKeys = existing
            .Select(value => (value.RoleId, value.InstanceGroupId))
            .ToHashSet();
        var changed = !requestedKeys.SetEquals(existingKeys);
        if (changed)
        {
            dbContext.RemoveRange(existing);
            foreach (var assignment in assignments)
            {
                dbContext.Add(new UserRoleEntity
                {
                    UserId = userId,
                    RoleId = assignment.RoleId,
                    InstanceGroupId = assignment.InstanceGroupId,
                });
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            if (!await HasAnyActiveGlobalManagerAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
                dbContext.ChangeTracker.Clear();
                await SaveAuditAsync(
                    actor,
                    "authorization.assignments_replace",
                    "rejected",
                    UserScope(userId),
                    ErrorSummary("authorization_lockout"),
                    cancellationToken);
                return new ReplaceRoleAssignmentsResult(
                    ReplaceRoleAssignmentsStatus.AuthorizationLockout);
            }
        }

        var resultAssignments = assignments
            .Select(value => new RoleAssignment(
                value.RoleId,
                roles[value.RoleId].Name,
                value.InstanceGroupId))
            .OrderBy(value => roles[value.RoleId].NormalizedName, StringComparer.Ordinal)
            .ThenBy(value => value.InstanceGroupId)
            .ThenBy(value => value.RoleId)
            .ToArray();
        var status = changed
            ? ReplaceRoleAssignmentsStatus.Updated
            : ReplaceRoleAssignmentsStatus.Unchanged;
        await SaveAuditAsync(
            actor,
            "authorization.assignments_replace",
            status.ToString().ToLowerInvariant(),
            UserScope(userId),
            AssignmentSummary(assignments.Count),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ReplaceRoleAssignmentsResult(status, resultAssignments);
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginMutationAsync(
        CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({AuthorizationMutationLock})",
            cancellationToken);
        return transaction;
    }

    private Task<bool> ActorCanManageAuthorizationAsync(
        RbacActorContext actor,
        CancellationToken cancellationToken) =>
        HasGlobalPermissionAsync(
            actor.UserId,
            actor.SessionId,
            RbacPermissions.AuthorizationManage,
            cancellationToken);

    private async Task<bool> HasGlobalPermissionAsync(
        Guid userId,
        Guid sessionId,
        string permissionCode,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hasManualGrant = await dbContext.Set<UserRoleEntity>()
            .AsNoTracking()
            .AnyAsync(assignment =>
                assignment.UserId == userId
                && assignment.User.State == LocalIdentityConstants.ActiveUserState
                && assignment.InstanceGroupId == null
                && assignment.Role.Permissions.Any(rolePermission =>
                    rolePermission.Permission.Code == permissionCode),
                cancellationToken);
        if (hasManualGrant)
        {
            return true;
        }

        return await dbContext.Set<OidcSessionContextEntity>()
            .AsNoTracking()
            .AnyAsync(context =>
                context.ExternalIdentity.UserId == userId
                && context.ExternalIdentity.User.State == LocalIdentityConstants.ActiveUserState
                && context.ExternalIdentity.RoleAssignments.Any(assignment =>
                    assignment.Role.Permissions.Any(rolePermission =>
                        rolePermission.Permission.Code == permissionCode))
                && dbContext.Set<UserSessionEntity>().Any(session =>
                    session.Id == sessionId
                    && session.UserId == userId
                    && session.TokenFamilyId == context.TokenFamilyId
                    && session.AuthenticationMethod == LocalIdentityConstants.OidcAuthenticationMethod
                    && session.AccessExpiresAt > now
                    && session.RevokedAt == null
                    && session.ReplacedBySessionId == null),
                cancellationToken);
    }

    private async Task<bool> HasAnyActiveGlobalManagerAsync(
        CancellationToken cancellationToken)
    {
        var hasManualManager = await dbContext.Set<UserRoleEntity>()
            .AsNoTracking()
            .AnyAsync(assignment =>
                assignment.InstanceGroupId == null
                && assignment.User.State == LocalIdentityConstants.ActiveUserState
                && assignment.Role.Permissions.Any(rolePermission =>
                    rolePermission.Permission.Code == RbacPermissions.AuthorizationManage),
                cancellationToken);
        if (hasManualManager)
        {
            return true;
        }

        var now = timeProvider.GetUtcNow();
        return await dbContext.Set<ExternalIdentityRoleEntity>()
            .AsNoTracking()
            .AnyAsync(assignment =>
                assignment.ExternalIdentity.User.State == LocalIdentityConstants.ActiveUserState
                && assignment.Role.Permissions.Any(rolePermission =>
                    rolePermission.Permission.Code == RbacPermissions.AuthorizationManage)
                && assignment.ExternalIdentity.SessionContexts.Any(context =>
                    context.ExpiresAt > now
                    && dbContext.Set<UserSessionEntity>().Any(session =>
                        session.UserId == assignment.ExternalIdentity.UserId
                        && session.TokenFamilyId == context.TokenFamilyId
                        && session.AuthenticationMethod == LocalIdentityConstants.OidcAuthenticationMethod
                        && session.AccessExpiresAt > now
                        && session.RevokedAt == null
                        && session.ReplacedBySessionId == null)),
                cancellationToken);
    }

    private async Task SaveAuditAsync(
        RbacActorContext actor,
        string action,
        string outcome,
        string scopeJson,
        string summaryJson,
        CancellationToken cancellationToken)
    {
        dbContext.Add(new AuditEventEntity
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = timeProvider.GetUtcNow(),
            ActorUserId = actor.UserId,
            ActorType = "user",
            ActorIdentifier = actor.Email,
            Action = action,
            ScopeJson = scopeJson,
            CorrelationId = actor.RequestContext.CorrelationId,
            Outcome = outcome,
            SummaryJson = summaryJson,
            IpAddress = actor.RequestContext.IpAddress,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static AuthorizationRole ToRole(RoleEntity role) =>
        new(
            role.Id,
            role.Name,
            role.IsSystem,
            role.Permissions
                .Select(value => value.Permission.Code)
                .Order(StringComparer.Ordinal)
                .ToArray());

    private static string RoleScope(Guid roleId) =>
        JsonSerializer.Serialize(new { kind = "role", roleId });

    private static string UserScope(Guid userId) =>
        JsonSerializer.Serialize(new { kind = "user_role_assignments", userId });

    private static string RoleSummary(int permissionCount) =>
        JsonSerializer.Serialize(new { permissionCount });

    private static string AssignmentSummary(int assignmentCount) =>
        JsonSerializer.Serialize(new { assignmentCount });

    private static string ErrorSummary(string code) =>
        JsonSerializer.Serialize(new { code });

    private static string EmptySummary() => "{}";
}
