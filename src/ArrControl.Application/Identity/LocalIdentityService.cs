using System.Text;

namespace ArrControl.Application.Identity;

public sealed class LocalIdentityService(
    ILocalIdentityStore store,
    IPasswordHasher passwordHasher,
    ISessionTokenService tokenService,
    LocalAuthSettings settings,
    TimeProvider timeProvider)
{
    private const int MinimumPasswordLength = 12;
    private const int MaximumPasswordBytes = 1024;
    private static readonly TimeSpan LockContentionRetryAfter = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PasswordCapacityRetryAfter = TimeSpan.FromSeconds(2);

    public async Task<BootstrapStatus> BootstrapAsync(
        string email,
        string password,
        AuthenticationRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        if (!IdentityEmailAddress.TryNormalize(email, out var canonicalEmail, out var normalizedEmail))
        {
            throw new IdentityValidationException("bootstrap_email_invalid");
        }

        ValidateNewPassword(password);

        if (await store.IsBootstrapDisabledAsync(cancellationToken))
        {
            return BootstrapStatus.AlreadyDisabled;
        }

        var passwordHash = await passwordHasher.HashAsync(password, cancellationToken);
        var status = await store.BootstrapAsync(
            new BootstrapUserRecord(
                Guid.CreateVersion7(),
                canonicalEmail,
                normalizedEmail,
                passwordHash,
                "en",
                "UTC"),
            requestContext,
            cancellationToken);

        return status == BootstrapStoreStatus.Created
            ? BootstrapStatus.Created
            : BootstrapStatus.AlreadyDisabled;
    }

    public async Task<LoginResult> LoginAsync(
        string? email,
        string? password,
        AuthenticationRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        var emailIsValid = IdentityEmailAddress.TryNormalize(email, out _, out var normalizedEmail);
        var actorIdentifier = emailIsValid ? normalizedEmail : "unknown";
        var now = timeProvider.GetUtcNow();
        await using var throttleLease = await store.AcquireLoginThrottleAsync(
            actorIdentifier,
            requestContext.IpAddress,
            now - settings.LoginFailureWindow,
            cancellationToken);

        if (!throttleLease.Acquired)
        {
            await store.RecordLoginFailureAsync(
                null,
                actorIdentifier,
                "rate_limited",
                requestContext,
                now,
                cancellationToken);
            await throttleLease.CommitAsync(cancellationToken);
            return new LoginResult(
                LoginStatus.RateLimited,
                RetryAfter: LockContentionRetryAfter);
        }

        if (throttleLease.FailureCounts.AccountFailures >= settings.AccountFailureLimit
            || throttleLease.FailureCounts.IpFailures >= settings.IpFailureLimit)
        {
            await store.RecordLoginFailureAsync(
                null,
                actorIdentifier,
                "rate_limited",
                requestContext,
                now,
                cancellationToken);
            await throttleLease.CommitAsync(cancellationToken);
            return new LoginResult(
                LoginStatus.RateLimited,
                RetryAfter: settings.LoginFailureWindow);
        }

        var user = emailIsValid
            ? await store.FindLocalUserAsync(normalizedEmail, cancellationToken)
            : null;
        var suppliedPassword = IsAcceptableLoginPassword(password) ? password! : string.Empty;
        var verification = await passwordHasher.VerifyAsync(
            suppliedPassword,
            user?.PasswordHash,
            cancellationToken);

        if (verification.Status == PasswordVerificationStatus.CapacityExceeded)
        {
            await store.RecordLoginFailureAsync(
                user,
                actorIdentifier,
                "rate_limited",
                requestContext,
                now,
                cancellationToken);
            await throttleLease.CommitAsync(cancellationToken);
            return new LoginResult(
                LoginStatus.RateLimited,
                RetryAfter: PasswordCapacityRetryAfter);
        }

        var isValid = emailIsValid
            && IsAcceptableLoginPassword(password)
            && verification.Status == PasswordVerificationStatus.Valid
            && user is { State: LocalIdentityConstants.ActiveUserState, PasswordHash: not null };

        if (!isValid)
        {
            await store.RecordLoginFailureAsync(
                user,
                actorIdentifier,
                "failed",
                requestContext,
                now,
                cancellationToken);
            await throttleLease.CommitAsync(cancellationToken);
            return new LoginResult(LoginStatus.InvalidCredentials);
        }

        var authenticatedUser = user!;
        var accessToken = tokenService.Issue();
        var refreshToken = tokenService.Issue();
        var sessionId = Guid.CreateVersion7();
        var accessExpiresAt = now + settings.AccessTokenLifetime;
        var refreshExpiresAt = now + settings.RefreshTokenLifetime;

        await store.CreateSessionAsync(
            new NewSessionRecord(
                sessionId,
                authenticatedUser.Id,
                authenticatedUser.Email,
                Guid.CreateVersion7(),
                accessToken.Hash,
                accessExpiresAt,
                refreshToken.Hash,
                refreshExpiresAt,
                LocalIdentityConstants.LocalAuthenticationMethod,
                verification.ReplacementHash),
            requestContext,
            now,
            cancellationToken);
        await throttleLease.CommitAsync(cancellationToken);

        return new LoginResult(
            LoginStatus.Succeeded,
            new IssuedSession(
                authenticatedUser.Id,
                sessionId,
                authenticatedUser.Email,
                accessToken,
                accessExpiresAt,
                refreshToken,
                refreshExpiresAt));
    }

    public async Task<RefreshResult> RefreshAsync(
        string? refreshToken,
        AuthenticationRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        if (!tokenService.TryHash(refreshToken, out var refreshTokenHash))
        {
            return new RefreshResult(RefreshStatus.InvalidToken);
        }

        var newAccessToken = tokenService.Issue();
        var newRefreshToken = tokenService.Issue();
        var now = timeProvider.GetUtcNow();
        var newSessionId = Guid.CreateVersion7();
        var storeResult = await store.RotateRefreshTokenAsync(
            refreshTokenHash,
            new SessionTokenMaterial(
                newSessionId,
                newAccessToken.Hash,
                now + settings.AccessTokenLifetime,
                newRefreshToken.Hash),
            requestContext,
            now,
            cancellationToken);

        if (storeResult.Status != RefreshStoreStatus.Succeeded)
        {
            return new RefreshResult(
                storeResult.Status == RefreshStoreStatus.Replayed
                    ? RefreshStatus.Replayed
                    : RefreshStatus.InvalidToken);
        }

        return new RefreshResult(
            RefreshStatus.Succeeded,
            new IssuedSession(
                storeResult.UserId,
                storeResult.SessionId,
                storeResult.Email!,
                newAccessToken,
                storeResult.AccessExpiresAt,
                newRefreshToken,
                storeResult.RefreshExpiresAt));
    }

    public Task LogoutAsync(
        Guid? sessionId,
        string? refreshToken,
        AuthenticationRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        byte[]? refreshTokenHash = null;
        if (tokenService.TryHash(refreshToken, out var parsedRefreshTokenHash))
        {
            refreshTokenHash = parsedRefreshTokenHash;
        }

        return store.RevokeSessionFamilyAsync(
            sessionId,
            refreshTokenHash,
            requestContext,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public async Task<ValidatedSession?> ValidateAccessTokenAsync(
        string? accessToken,
        CancellationToken cancellationToken)
    {
        if (!tokenService.TryHash(accessToken, out var accessTokenHash))
        {
            return null;
        }

        return await store.ValidateAccessTokenAsync(
            accessTokenHash,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private static void ValidateNewPassword(string password)
    {
        if (string.IsNullOrEmpty(password)
            || password.Length < MinimumPasswordLength
            || Encoding.UTF8.GetByteCount(password) > MaximumPasswordBytes)
        {
            throw new IdentityValidationException("bootstrap_password_invalid");
        }
    }

    private static bool IsAcceptableLoginPassword(string? password) =>
        !string.IsNullOrEmpty(password)
        && Encoding.UTF8.GetByteCount(password) <= MaximumPasswordBytes;
}

public sealed class IdentityValidationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
