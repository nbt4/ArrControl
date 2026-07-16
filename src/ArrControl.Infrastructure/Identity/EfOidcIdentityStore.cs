using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using ArrControl.Application.Identity;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Identity;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Identity;

public sealed class EfOidcIdentityStore(
    ArrControlDbContext dbContext,
    IAuthenticationAuditPort authenticationAuditPort) : IOidcIdentityStore
{
    private const int CurrentClaimsVersion = 1;
    private const byte IdentityLockNamespace = 4;
    private const byte EmailLockNamespace = 5;

    public async Task<OidcSessionStoreResult> CreateSessionAsync(
        NewOidcSessionRecord session,
        AuthenticationRequestContext requestContext,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(requestContext);
        ValidateSession(session, occurredAt);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var lockKeys = new List<long>
        {
            CreateLockKey(IdentityLockNamespace, session.Issuer, session.Subject),
        };
        if (session.VerifiedNormalizedEmail is not null)
        {
            lockKeys.Add(CreateLockKey(EmailLockNamespace, session.VerifiedNormalizedEmail));
        }

        foreach (var lockKey in lockKeys.Distinct().Order())
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({lockKey})",
                cancellationToken);
        }

        var externalIdentity = await dbContext.Set<ExternalIdentityEntity>()
            .Include(x => x.User)
            .Include(x => x.RoleAssignments)
            .SingleOrDefaultAsync(
                x => x.Issuer == session.Issuer && x.Subject == session.Subject,
                cancellationToken);

        UserEntity? user;
        string? provisionedEmail = null;
        string? provisionedNormalizedEmail = null;
        if (externalIdentity is not null)
        {
            user = externalIdentity.User;
            if (user.State != LocalIdentityConstants.ActiveUserState)
            {
                return await CommitFailureAsync(
                    transaction,
                    OidcSessionStoreStatus.Inactive,
                    user,
                    session,
                    requestContext,
                    occurredAt,
                    cancellationToken);
            }
        }
        else
        {
            if (!TryValidateVerifiedEmail(session, out var canonicalEmail, out var normalizedEmail))
            {
                return await CommitFailureAsync(
                    transaction,
                    OidcSessionStoreStatus.UnverifiedIdentity,
                    null,
                    session,
                    requestContext,
                    occurredAt,
                    cancellationToken);
            }

            user = await dbContext.Set<UserEntity>()
                .SingleOrDefaultAsync(
                    x => x.NormalizedEmail == normalizedEmail,
                    cancellationToken);
            if (user is not null && user.State != LocalIdentityConstants.ActiveUserState)
            {
                return await CommitFailureAsync(
                    transaction,
                    OidcSessionStoreStatus.Inactive,
                    user,
                    session,
                    requestContext,
                    occurredAt,
                    cancellationToken);
            }

            if (user is null)
            {
                provisionedEmail = canonicalEmail;
                provisionedNormalizedEmail = normalizedEmail;
            }
        }

        var desiredRoleNames = NormalizeDesiredRoleNames(session.DesiredNormalizedRoleNames);
        if (desiredRoleNames is null)
        {
            return await CommitFailureAsync(
                transaction,
                OidcSessionStoreStatus.RoleMissing,
                user,
                session,
                requestContext,
                occurredAt,
                cancellationToken);
        }

        var desiredRoles = desiredRoleNames.Count == 0
            ? []
            : await dbContext.Set<RoleEntity>()
                .FromSqlInterpolated(
                    $"SELECT * FROM roles WHERE normalized_name = ANY({desiredRoleNames.ToArray()}) FOR KEY SHARE")
                .ToListAsync(cancellationToken);
        if (desiredRoles.Count != desiredRoleNames.Count)
        {
            return await CommitFailureAsync(
                transaction,
                OidcSessionStoreStatus.RoleMissing,
                user,
                session,
                requestContext,
                occurredAt,
                cancellationToken);
        }

        user ??= new UserEntity
        {
            Email = provisionedEmail!,
            NormalizedEmail = provisionedNormalizedEmail!,
            PasswordHash = null,
            Locale = "en",
            TimeZone = "UTC",
            State = LocalIdentityConstants.ActiveUserState,
            CreatedAt = occurredAt,
            UpdatedAt = occurredAt,
        };

        if (dbContext.Entry(user).State == EntityState.Detached)
        {
            dbContext.Add(user);
        }

        if (externalIdentity is null)
        {
            externalIdentity = new ExternalIdentityEntity
            {
                User = user,
                Issuer = session.Issuer,
                Subject = session.Subject,
                ClaimsVersion = CurrentClaimsVersion,
                CreatedAt = occurredAt,
                LastAuthenticatedAt = occurredAt,
            };
            dbContext.Add(externalIdentity);
        }
        else
        {
            externalIdentity.ClaimsVersion = CurrentClaimsVersion;
            externalIdentity.LastAuthenticatedAt = occurredAt;
        }

        ReconcileRoles(externalIdentity, desiredRoles, occurredAt);

        var accessExpiresAt = session.Session.RequestedAccessExpiresAt;
        dbContext.Add(new UserSessionEntity
        {
            Id = session.Session.Id,
            User = user,
            TokenFamilyId = session.TokenFamilyId,
            AccessTokenHash = session.Session.AccessTokenHash,
            AccessExpiresAt = accessExpiresAt,
            RefreshTokenHash = session.Session.RefreshTokenHash,
            CreatedAt = occurredAt,
            ExpiresAt = session.RefreshExpiresAt,
            AuthenticationMethod = session.AuthenticationMethod,
        });
        dbContext.Add(new OidcSessionContextEntity
        {
            TokenFamilyId = session.TokenFamilyId,
            ExternalIdentity = externalIdentity,
            ProtectedIdToken = session.ProtectedIdToken,
            CreatedAt = occurredAt,
            ExpiresAt = session.RefreshExpiresAt,
        });
        StageAudit(
            user,
            "succeeded",
            session,
            requestContext,
            occurredAt);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OidcSessionStoreResult(
            OidcSessionStoreStatus.Succeeded,
            user.Id,
            session.Session.Id,
            user.Email,
            accessExpiresAt,
            session.RefreshExpiresAt);
    }

    public async Task<OidcLogoutContext?> GetLogoutContextAsync(
        Guid? sessionId,
        byte[]? refreshTokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Guid? tokenFamilyId = null;
        if (sessionId is not null)
        {
            tokenFamilyId = await dbContext.Set<UserSessionEntity>()
                .AsNoTracking()
                .Where(x => x.Id == sessionId
                    && x.AuthenticationMethod == LocalIdentityConstants.OidcAuthenticationMethod
                    && x.ExpiresAt > now)
                .Select(x => (Guid?)x.TokenFamilyId)
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (tokenFamilyId is null && refreshTokenHash is not null)
        {
            tokenFamilyId = await dbContext.Set<UserSessionEntity>()
                .AsNoTracking()
                .Where(x => x.RefreshTokenHash == refreshTokenHash
                    && x.AuthenticationMethod == LocalIdentityConstants.OidcAuthenticationMethod
                    && x.ExpiresAt > now)
                .Select(x => (Guid?)x.TokenFamilyId)
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (tokenFamilyId is null)
        {
            return null;
        }

        return await dbContext.Set<OidcSessionContextEntity>()
            .AsNoTracking()
            .Where(x => x.TokenFamilyId == tokenFamilyId && x.ExpiresAt > now)
            .Select(x => new OidcLogoutContext(x.ProtectedIdToken))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task RecordProtocolFailureAsync(
        AuthenticationRequestContext requestContext,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestContext);
        authenticationAuditPort.Stage(new AuthenticationAuditEvent(
            null,
            "anonymous",
            "oidc",
            "identity.oidc_login",
            "protocol_failed",
            LocalIdentityConstants.OidcAuthenticationMethod,
            requestContext.CorrelationId,
            requestContext.IpAddress,
            occurredAt));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static HashSet<string>? NormalizeDesiredRoleNames(
        IReadOnlyCollection<string> desiredNormalizedRoleNames)
    {
        if (desiredNormalizedRoleNames is null
            || desiredNormalizedRoleNames.Count > OidcIdentityLimits.MaximumRoleMappings)
        {
            return null;
        }

        var normalizedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var roleName in desiredNormalizedRoleNames)
        {
            if (string.IsNullOrWhiteSpace(roleName)
                || roleName.Length > OidcIdentityLimits.MaximumRoleNameLength
                || !string.Equals(roleName, roleName.Trim(), StringComparison.Ordinal)
                || !string.Equals(roleName, roleName.ToUpperInvariant(), StringComparison.Ordinal))
            {
                return null;
            }

            normalizedNames.Add(roleName);
        }

        return normalizedNames;
    }

    private static bool TryValidateVerifiedEmail(
        NewOidcSessionRecord session,
        out string canonicalEmail,
        out string normalizedEmail)
    {
        canonicalEmail = string.Empty;
        normalizedEmail = string.Empty;
        if (session.VerifiedEmail is null
            || session.VerifiedNormalizedEmail is null
            || !IdentityEmailAddress.TryNormalize(
                session.VerifiedEmail,
                out canonicalEmail,
                out normalizedEmail))
        {
            return false;
        }

        return string.Equals(
            normalizedEmail,
            session.VerifiedNormalizedEmail,
            StringComparison.Ordinal);
    }

    private void ReconcileRoles(
        ExternalIdentityEntity externalIdentity,
        IReadOnlyCollection<RoleEntity> desiredRoles,
        DateTimeOffset occurredAt)
    {
        var desiredRoleIds = desiredRoles.Select(x => x.Id).ToHashSet();
        foreach (var obsoleteAssignment in externalIdentity.RoleAssignments
                     .Where(x => !desiredRoleIds.Contains(x.RoleId))
                     .ToArray())
        {
            dbContext.Remove(obsoleteAssignment);
        }

        var currentRoleIds = externalIdentity.RoleAssignments
            .Select(x => x.RoleId)
            .ToHashSet();
        foreach (var role in desiredRoles.Where(x => !currentRoleIds.Contains(x.Id)))
        {
            dbContext.Add(new ExternalIdentityRoleEntity
            {
                ExternalIdentity = externalIdentity,
                Role = role,
                CreatedAt = occurredAt,
            });
        }
    }

    private async Task<OidcSessionStoreResult> CommitFailureAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        OidcSessionStoreStatus status,
        UserEntity? user,
        NewOidcSessionRecord session,
        AuthenticationRequestContext requestContext,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        StageAudit(
            user,
            status switch
            {
                OidcSessionStoreStatus.Inactive => "inactive",
                OidcSessionStoreStatus.RoleMissing => "role_missing",
                OidcSessionStoreStatus.UnverifiedIdentity => "unverified_identity",
                _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown OIDC failure."),
            },
            session,
            requestContext,
            occurredAt);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OidcSessionStoreResult(status);
    }

    private void StageAudit(
        UserEntity? user,
        string outcome,
        NewOidcSessionRecord session,
        AuthenticationRequestContext requestContext,
        DateTimeOffset occurredAt)
    {
        var actorIdentifier = user?.Email;
        if (actorIdentifier is null
            && session.VerifiedEmail is { Length: > 0 } verifiedEmail
            && verifiedEmail.Length <= IdentityEmailAddress.MaximumLength)
        {
            actorIdentifier = verifiedEmail;
        }

        authenticationAuditPort.Stage(new AuthenticationAuditEvent(
            user?.Id,
            user is null ? "anonymous" : "user",
            actorIdentifier ?? "oidc",
            "identity.oidc_login",
            outcome,
            session.AuthenticationMethod,
            requestContext.CorrelationId,
            requestContext.IpAddress,
            occurredAt));
    }

    private static void ValidateSession(NewOidcSessionRecord session, DateTimeOffset occurredAt)
    {
        if (string.IsNullOrWhiteSpace(session.Issuer)
            || session.Issuer.Length > OidcIdentityLimits.MaximumIssuerLength
            || string.IsNullOrWhiteSpace(session.Subject)
            || session.Subject.Length > OidcIdentityLimits.MaximumSubjectLength
            || session.TokenFamilyId == Guid.Empty
            || session.Session.Id == Guid.Empty
            || session.Session.AccessTokenHash.Length != 32
            || session.Session.RefreshTokenHash.Length != 32
            || session.Session.RequestedAccessExpiresAt <= occurredAt
            || session.RefreshExpiresAt <= session.Session.RequestedAccessExpiresAt
            || session.AuthenticationMethod != LocalIdentityConstants.OidcAuthenticationMethod
            || string.IsNullOrWhiteSpace(session.ProtectedIdToken)
            || session.ProtectedIdToken.Length > OidcIdentityLimits.MaximumProtectedIdTokenLength)
        {
            throw new ArgumentException("The OIDC session record is invalid.", nameof(session));
        }
    }

    private static long CreateLockKey(byte lockNamespace, params string[] components)
    {
        var componentBytes = components.Select(Encoding.UTF8.GetBytes).ToArray();
        var inputLength = 1 + componentBytes.Sum(x => sizeof(int) + x.Length);
        var input = GC.AllocateUninitializedArray<byte>(inputLength);
        try
        {
            input[0] = lockNamespace;
            var offset = 1;
            foreach (var component in componentBytes)
            {
                BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(offset, sizeof(int)), component.Length);
                offset += sizeof(int);
                component.CopyTo(input, offset);
                offset += component.Length;
            }

            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(input, hash);
            return BinaryPrimitives.ReadInt64BigEndian(hash);
        }
        finally
        {
            foreach (var component in componentBytes)
            {
                CryptographicOperations.ZeroMemory(component);
            }

            CryptographicOperations.ZeroMemory(input);
        }
    }
}
