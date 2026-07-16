using ArrControl.Application.Authorization;
using ArrControl.Application.Identity;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Identity;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Authorization;

public sealed class EfRbacGrantStore(
    ArrControlDbContext dbContext,
    TimeProvider timeProvider) : IRbacGrantStore
{
    public async Task<IReadOnlyCollection<StoredPermissionGrant>> GetGrantsAsync(
        Guid userId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hasValidSession = await dbContext.Set<UserSessionEntity>()
            .AsNoTracking()
            .AnyAsync(session =>
                session.Id == sessionId
                && session.UserId == userId
                && session.User.State == LocalIdentityConstants.ActiveUserState
                && session.AccessExpiresAt > now
                && session.RevokedAt == null
                && session.ReplacedBySessionId == null,
                cancellationToken);
        if (!hasValidSession)
        {
            return [];
        }

        var manualGrants = await dbContext.Set<UserRoleEntity>()
            .AsNoTracking()
            .Where(assignment => assignment.UserId == userId)
            .SelectMany(
                assignment => assignment.Role.Permissions,
                (assignment, rolePermission) => new StoredPermissionGrant(
                    rolePermission.Permission.Code,
                    assignment.InstanceGroupId))
            .ToListAsync(cancellationToken);

        var oidcGrants = await dbContext.Set<OidcSessionContextEntity>()
            .AsNoTracking()
            .Where(context => context.ExternalIdentity.UserId == userId
                && context.ExpiresAt > now
                && dbContext.Set<UserSessionEntity>().Any(session =>
                    session.Id == sessionId
                    && session.UserId == userId
                    && session.TokenFamilyId == context.TokenFamilyId
                    && session.AuthenticationMethod == LocalIdentityConstants.OidcAuthenticationMethod
                    && session.AccessExpiresAt > now
                    && session.RevokedAt == null
                    && session.ReplacedBySessionId == null))
            .SelectMany(context => context.ExternalIdentity.RoleAssignments)
            .SelectMany(
                assignment => assignment.Role.Permissions,
                (_, rolePermission) => new StoredPermissionGrant(
                    rolePermission.Permission.Code,
                    null))
            .ToListAsync(cancellationToken);

        manualGrants.AddRange(oidcGrants);
        return manualGrants;
    }
}
