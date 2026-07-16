using System.Buffers.Binary;
using System.Data;
using System.Net;
using System.Security.Cryptography;
using ArrControl.Application.Identity;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Identity;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Identity;

public sealed class EfLocalIdentityStore(
    ArrControlDbContext dbContext,
    IAuthenticationAuditPort authenticationAuditPort) : ILocalIdentityStore
{
    private static readonly TimeSpan MinimumRotatedSessionLifetime = TimeSpan.FromMilliseconds(1);

    public Task<bool> HasUsersAsync(CancellationToken cancellationToken) =>
        dbContext.Set<UserEntity>().AsNoTracking().AnyAsync(cancellationToken);

    public Task<bool> IsBootstrapDisabledAsync(CancellationToken cancellationToken) =>
        dbContext.Set<IdentityBootstrapStateEntity>().AsNoTracking().AnyAsync(cancellationToken);

    public async Task<BootstrapStoreStatus> BootstrapAsync(
        BootstrapUserRecord user,
        AuthenticationRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(4596771108351201937)",
            cancellationToken);
        var now = DateTimeOffset.UtcNow;

        var bootstrapState = await dbContext.Set<IdentityBootstrapStateEntity>()
            .SingleOrDefaultAsync(cancellationToken);
        if (bootstrapState?.AdminUserId is { } bootstrapAdminUserId)
        {
            var bootstrapAdmin = await dbContext.Set<UserEntity>()
                .SingleOrDefaultAsync(x => x.Id == bootstrapAdminUserId, cancellationToken);
            if (bootstrapAdmin is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return BootstrapStoreStatus.AlreadyDisabled;
            }

            var conflictingUserExists = await dbContext.Set<UserEntity>()
                .AnyAsync(
                    x => x.Id != bootstrapAdmin.Id && x.NormalizedEmail == user.NormalizedEmail,
                    cancellationToken);
            if (conflictingUserExists)
            {
                throw new InvalidOperationException(
                    "Bootstrap administrator email conflicts with an existing user.");
            }

            bootstrapAdmin.Email = user.Email;
            bootstrapAdmin.NormalizedEmail = user.NormalizedEmail;
            bootstrapAdmin.PasswordHash = user.PasswordHash;
            bootstrapAdmin.UpdatedAt = now;
            await dbContext.Set<UserSessionEntity>()
                .Where(x => x.UserId == bootstrapAdmin.Id && x.RevokedAt == null)
                .ExecuteUpdateAsync(
                    updates => updates.SetProperty(x => x.RevokedAt, now),
                    cancellationToken);
            AddAudit(
                bootstrapAdmin.Id,
                "user",
                bootstrapAdmin.Email,
                "identity.bootstrap",
                "synchronized",
                requestContext,
                now);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return BootstrapStoreStatus.Updated;
        }

        if (bootstrapState is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return BootstrapStoreStatus.AlreadyDisabled;
        }

        if (await dbContext.Set<UserEntity>().AnyAsync(cancellationToken))
        {
            dbContext.Add(new IdentityBootstrapStateEntity
            {
                CompletedAt = now,
            });
            AddAudit(
                null,
                "system",
                "startup-bootstrap",
                "identity.bootstrap",
                "disabled_existing_users",
                requestContext,
                now);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return BootstrapStoreStatus.ExistingUsersDisabled;
        }

        var userEntity = new UserEntity
        {
            Id = user.Id,
            Email = user.Email,
            NormalizedEmail = user.NormalizedEmail,
            PasswordHash = user.PasswordHash,
            Locale = user.Locale,
            TimeZone = user.TimeZone,
            State = LocalIdentityConstants.ActiveUserState,
        };
        var administratorRole = await dbContext.Set<RoleEntity>()
            .SingleOrDefaultAsync(
                x => x.NormalizedName == LocalIdentityConstants.AdministratorRoleNormalizedName,
                cancellationToken);
        administratorRole ??= new RoleEntity
        {
            Name = LocalIdentityConstants.AdministratorRoleName,
            NormalizedName = LocalIdentityConstants.AdministratorRoleNormalizedName,
            IsSystem = true,
        };
        administratorRole.Name = LocalIdentityConstants.AdministratorRoleName;
        administratorRole.IsSystem = true;

        dbContext.Add(userEntity);
        if (dbContext.Entry(administratorRole).State == EntityState.Detached)
        {
            dbContext.Add(administratorRole);
        }

        dbContext.Add(new UserRoleEntity
        {
            User = userEntity,
            Role = administratorRole,
        });
        dbContext.Add(new IdentityBootstrapStateEntity
        {
            AdminUser = userEntity,
            CompletedAt = now,
        });
        AddAudit(
            userEntity.Id,
            "user",
            userEntity.Email,
            "identity.bootstrap",
            "succeeded",
            requestContext,
            now);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return BootstrapStoreStatus.Created;
    }

    public Task<LocalUserRecord?> FindLocalUserAsync(
        string normalizedEmail,
        CancellationToken cancellationToken) =>
        dbContext.Set<UserEntity>()
            .AsNoTracking()
            .Where(x => x.NormalizedEmail == normalizedEmail)
            .Select(x => new LocalUserRecord(
                x.Id,
                x.Email,
                x.NormalizedEmail,
                x.State,
                x.PasswordHash))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<ILoginThrottleLease> AcquireLoginThrottleAsync(
        string actorIdentifier,
        IPAddress? ipAddress,
        DateTimeOffset since,
        CancellationToken cancellationToken) =>
        authenticationAuditPort.AcquireLoginThrottleAsync(
            actorIdentifier,
            ipAddress,
            since,
            cancellationToken);

    public async Task RecordLoginFailureAsync(
        LocalUserRecord? user,
        string actorIdentifier,
        string outcome,
        AuthenticationRequestContext requestContext,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        AddAudit(
            user?.Id,
            user is null ? "anonymous" : "user",
            actorIdentifier,
            "identity.login",
            outcome,
            requestContext,
            occurredAt);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task CreateSessionAsync(
        NewSessionRecord session,
        AuthenticationRequestContext requestContext,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        if (session.ReplacementPasswordHash is not null)
        {
            await dbContext.Set<UserEntity>()
                .Where(x => x.Id == session.UserId)
                .ExecuteUpdateAsync(
                    updates => updates
                        .SetProperty(x => x.PasswordHash, session.ReplacementPasswordHash)
                        .SetProperty(x => x.UpdatedAt, occurredAt),
                    cancellationToken);
        }

        dbContext.Add(ToEntity(session, occurredAt));
        AddAudit(
            session.UserId,
            "user",
            session.UserEmail,
            "identity.login",
            "succeeded",
            requestContext,
            occurredAt,
            session.AuthenticationMethod);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    public Task<ValidatedSession?> ValidateAccessTokenAsync(
        byte[] accessTokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        dbContext.Set<UserSessionEntity>()
            .AsNoTracking()
            .Where(x => x.AccessTokenHash == accessTokenHash
                && x.AccessExpiresAt > now
                && x.RevokedAt == null
                && x.ReplacedBySessionId == null
                && x.User.State == LocalIdentityConstants.ActiveUserState)
            .Select(x => new ValidatedSession(
                x.UserId,
                x.Id,
                x.User.Email,
                x.AuthenticationMethod))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<RefreshStoreResult> RotateRefreshTokenAsync(
        byte[] refreshTokenHash,
        SessionTokenMaterial replacement,
        AuthenticationRequestContext requestContext,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var tokenFamilyId = await dbContext.Set<UserSessionEntity>()
            .AsNoTracking()
            .Where(x => x.RefreshTokenHash == refreshTokenHash)
            .Select(x => (Guid?)x.TokenFamilyId)
            .SingleOrDefaultAsync(cancellationToken);
        if (tokenFamilyId is null)
        {
            AddAudit(
                null,
                "anonymous",
                "unknown",
                "identity.refresh",
                "failed",
                requestContext,
                now);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new RefreshStoreResult(RefreshStoreStatus.Invalid);
        }

        await AcquireSessionFamilyLockAsync(tokenFamilyId.Value, cancellationToken);
        var currentSession = await dbContext.Set<UserSessionEntity>()
            .FromSqlRaw(
                "SELECT * FROM user_sessions WHERE refresh_token_hash = {0} FOR UPDATE",
                refreshTokenHash)
            .Include(x => x.User)
            .SingleOrDefaultAsync(cancellationToken);

        if (currentSession is null || currentSession.TokenFamilyId != tokenFamilyId.Value)
        {
            AddAudit(
                null,
                "anonymous",
                "unknown",
                "identity.refresh",
                "failed",
                requestContext,
                now);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new RefreshStoreResult(RefreshStoreStatus.Invalid);
        }

        if (currentSession.RevokedAt is not null || currentSession.ReplacedBySessionId is not null)
        {
            await RevokeFamilyAsync(currentSession.TokenFamilyId, now, cancellationToken);
            AddAudit(
                currentSession.UserId,
                "user",
                currentSession.User.Email,
                "identity.refresh_reuse",
                "failed",
                requestContext,
                now,
                currentSession.AuthenticationMethod);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new RefreshStoreResult(RefreshStoreStatus.Replayed);
        }

        var effectiveUsedAt = currentSession.CreatedAt > now
            ? currentSession.CreatedAt
            : now;
        if (currentSession.ExpiresAt <= effectiveUsedAt + MinimumRotatedSessionLifetime
            || currentSession.User.State != LocalIdentityConstants.ActiveUserState)
        {
            currentSession.RevokedAt = effectiveUsedAt;
            currentSession.LastUsedAt = effectiveUsedAt;
            AddAudit(
                currentSession.UserId,
                "user",
                currentSession.User.Email,
                "identity.refresh",
                "failed",
                requestContext,
                now,
                currentSession.AuthenticationMethod);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new RefreshStoreResult(RefreshStoreStatus.Invalid);
        }

        currentSession.RevokedAt = effectiveUsedAt;
        currentSession.LastUsedAt = effectiveUsedAt;
        await dbContext.SaveChangesAsync(cancellationToken);

        var minimumAccessExpiresAt = effectiveUsedAt + MinimumRotatedSessionLifetime;
        var requestedAccessExpiresAt = replacement.RequestedAccessExpiresAt > minimumAccessExpiresAt
            ? replacement.RequestedAccessExpiresAt
            : minimumAccessExpiresAt;
        var accessExpiresAt = requestedAccessExpiresAt < currentSession.ExpiresAt
            ? requestedAccessExpiresAt
            : currentSession.ExpiresAt;
        var replacementSession = new UserSessionEntity
        {
            Id = replacement.Id,
            UserId = currentSession.UserId,
            TokenFamilyId = currentSession.TokenFamilyId,
            AccessTokenHash = replacement.AccessTokenHash,
            AccessExpiresAt = accessExpiresAt,
            RefreshTokenHash = replacement.RefreshTokenHash,
            CreatedAt = effectiveUsedAt,
            ExpiresAt = currentSession.ExpiresAt,
            AuthenticationMethod = currentSession.AuthenticationMethod,
        };
        dbContext.Add(replacementSession);
        await dbContext.SaveChangesAsync(cancellationToken);

        currentSession.ReplacedBySessionId = replacementSession.Id;
        AddAudit(
            currentSession.UserId,
            "user",
            currentSession.User.Email,
            "identity.refresh",
            "succeeded",
            requestContext,
            now,
            currentSession.AuthenticationMethod);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new RefreshStoreResult(
            RefreshStoreStatus.Succeeded,
            currentSession.UserId,
            replacementSession.Id,
            currentSession.User.Email,
            accessExpiresAt,
            currentSession.ExpiresAt);
    }

    public async Task RevokeSessionFamilyAsync(
        Guid? sessionId,
        byte[]? refreshTokenHash,
        AuthenticationRequestContext requestContext,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        Guid? tokenFamilyId = null;
        var useSessionId = false;
        if (sessionId is not null)
        {
            tokenFamilyId = await dbContext.Set<UserSessionEntity>()
                .AsNoTracking()
                .Where(x => x.Id == sessionId)
                .Select(x => (Guid?)x.TokenFamilyId)
                .SingleOrDefaultAsync(cancellationToken);
            useSessionId = tokenFamilyId is not null;
        }

        if (tokenFamilyId is null && refreshTokenHash is not null)
        {
            tokenFamilyId = await dbContext.Set<UserSessionEntity>()
                .AsNoTracking()
                .Where(x => x.RefreshTokenHash == refreshTokenHash)
                .Select(x => (Guid?)x.TokenFamilyId)
                .SingleOrDefaultAsync(cancellationToken);
        }

        UserSessionEntity? session = null;
        if (tokenFamilyId is not null)
        {
            await AcquireSessionFamilyLockAsync(tokenFamilyId.Value, cancellationToken);
        }

        if (useSessionId)
        {
            session = await dbContext.Set<UserSessionEntity>()
                .FromSqlRaw("SELECT * FROM user_sessions WHERE id = {0} FOR UPDATE", sessionId)
                .Include(x => x.User)
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (!useSessionId && tokenFamilyId is not null && refreshTokenHash is not null)
        {
            session = await dbContext.Set<UserSessionEntity>()
                .FromSqlRaw(
                    "SELECT * FROM user_sessions WHERE refresh_token_hash = {0} FOR UPDATE",
                    refreshTokenHash)
                .Include(x => x.User)
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (session is not null && session.TokenFamilyId != tokenFamilyId)
        {
            session = null;
        }

        if (session is not null)
        {
            await RevokeFamilyAsync(session.TokenFamilyId, now, cancellationToken);
            AddAudit(
                session.UserId,
                "user",
                session.User.Email,
                "identity.logout",
                "succeeded",
                requestContext,
                now,
                session.AuthenticationMethod);
        }
        else
        {
            AddAudit(
                null,
                "anonymous",
                "unknown",
                "identity.logout",
                "not_found",
                requestContext,
                now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private Task<int> RevokeFamilyAsync(
        Guid tokenFamilyId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        dbContext.Set<UserSessionEntity>()
            .Where(x => x.TokenFamilyId == tokenFamilyId && x.RevokedAt == null)
            .ExecuteUpdateAsync(
                updates => updates.SetProperty(
                    x => x.RevokedAt,
                    x => x.CreatedAt > now ? x.CreatedAt : now),
                cancellationToken);

    private Task AcquireSessionFamilyLockAsync(
        Guid tokenFamilyId,
        CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({CreateSessionFamilyLockKey(tokenFamilyId)})",
            cancellationToken);

    private static long CreateSessionFamilyLockKey(Guid tokenFamilyId)
    {
        Span<byte> input = stackalloc byte[17];
        input[0] = 3;
        tokenFamilyId.TryWriteBytes(input[1..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return BinaryPrimitives.ReadInt64BigEndian(hash);
    }

    private static UserSessionEntity ToEntity(NewSessionRecord session, DateTimeOffset createdAt) =>
        new()
        {
            Id = session.Id,
            UserId = session.UserId,
            TokenFamilyId = session.TokenFamilyId,
            AccessTokenHash = session.AccessTokenHash,
            AccessExpiresAt = session.AccessExpiresAt,
            RefreshTokenHash = session.RefreshTokenHash,
            CreatedAt = createdAt,
            ExpiresAt = session.RefreshExpiresAt,
            AuthenticationMethod = session.AuthenticationMethod,
        };

    private void AddAudit(
        Guid? actorUserId,
        string actorType,
        string actorIdentifier,
        string action,
        string outcome,
        AuthenticationRequestContext requestContext,
        DateTimeOffset occurredAt,
        string authenticationMethod = LocalIdentityConstants.LocalAuthenticationMethod)
    {
        authenticationAuditPort.Stage(new AuthenticationAuditEvent(
            actorUserId,
            actorType,
            actorIdentifier,
            action,
            outcome,
            authenticationMethod,
            requestContext.CorrelationId,
            requestContext.IpAddress,
            occurredAt));
    }
}
