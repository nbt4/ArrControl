using System.Net;

namespace ArrControl.Application.Identity;

public static class LocalIdentityConstants
{
    public const string ActiveUserState = "active";
    public const string AdministratorRoleName = "Administrator";
    public const string AdministratorRoleNormalizedName = "ADMINISTRATOR";
    public const string SessionIdClaim = "arrcontrol_session_id";
    public const string AuthenticationMethodClaim = "arrcontrol_auth_method";
    public const string LocalAuthenticationMethod = "local";
    public const string OidcAuthenticationMethod = "oidc";
}

public sealed record LocalAuthSettings(
    TimeSpan AccessTokenLifetime,
    TimeSpan RefreshTokenLifetime,
    TimeSpan LoginFailureWindow,
    int AccountFailureLimit,
    int IpFailureLimit)
{
    public static LocalAuthSettings Default { get; } = new(
        TimeSpan.FromMinutes(15),
        TimeSpan.FromDays(30),
        TimeSpan.FromMinutes(15),
        5,
        20);

    public void Validate()
    {
        if (AccessTokenLifetime <= TimeSpan.Zero || AccessTokenLifetime > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException("The access-token lifetime must be greater than zero and at most one hour.");
        }

        if (RefreshTokenLifetime <= AccessTokenLifetime || RefreshTokenLifetime > TimeSpan.FromDays(90))
        {
            throw new InvalidOperationException("The refresh-token lifetime must exceed the access-token lifetime and be at most 90 days.");
        }

        if (LoginFailureWindow <= TimeSpan.Zero || LoginFailureWindow > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException("The login-failure window must be greater than zero and at most one hour.");
        }

        if (AccountFailureLimit is < 1 or > 100)
        {
            throw new InvalidOperationException("The account failure limit must be between 1 and 100.");
        }

        if (IpFailureLimit is < 1 or > 1000)
        {
            throw new InvalidOperationException("The IP failure limit must be between 1 and 1000.");
        }
    }
}

public sealed record AuthenticationRequestContext(
    string CorrelationId,
    IPAddress? IpAddress);

public sealed record LocalUserRecord(
    Guid Id,
    string Email,
    string NormalizedEmail,
    string State,
    string? PasswordHash)
{
    public override string ToString() => "[REDACTED]";
}

public sealed record BootstrapUserRecord(
    Guid Id,
    string Email,
    string NormalizedEmail,
    string PasswordHash,
    string Locale,
    string TimeZone)
{
    public override string ToString() => "[REDACTED]";
}

public sealed record NewSessionRecord(
    Guid Id,
    Guid UserId,
    string UserEmail,
    Guid TokenFamilyId,
    byte[] AccessTokenHash,
    DateTimeOffset AccessExpiresAt,
    byte[] RefreshTokenHash,
    DateTimeOffset RefreshExpiresAt,
    string AuthenticationMethod,
    string? ReplacementPasswordHash = null)
{
    public override string ToString() => "[REDACTED]";
}

public sealed record SessionTokenMaterial(
    Guid Id,
    byte[] AccessTokenHash,
    DateTimeOffset RequestedAccessExpiresAt,
    byte[] RefreshTokenHash)
{
    public override string ToString() => "[REDACTED]";
}

public sealed record ValidatedSession(
    Guid UserId,
    Guid SessionId,
    string Email,
    string AuthenticationMethod);

public sealed record LoginFailureCounts(int AccountFailures, int IpFailures);

public enum BootstrapStoreStatus
{
    Created,
    Updated,
    AlreadyDisabled,
    ExistingUsersDisabled,
}

public enum RefreshStoreStatus
{
    Succeeded,
    Invalid,
    Replayed,
}

public sealed record RefreshStoreResult(
    RefreshStoreStatus Status,
    Guid UserId = default,
    Guid SessionId = default,
    string? Email = null,
    DateTimeOffset AccessExpiresAt = default,
    DateTimeOffset RefreshExpiresAt = default);

public interface ILocalIdentityStore
{
    Task<bool> HasUsersAsync(CancellationToken cancellationToken);

    Task<bool> IsBootstrapDisabledAsync(CancellationToken cancellationToken);

    Task<BootstrapStoreStatus> BootstrapAsync(
        BootstrapUserRecord user,
        AuthenticationRequestContext requestContext,
        CancellationToken cancellationToken);

    Task<LocalUserRecord?> FindLocalUserAsync(
        string normalizedEmail,
        CancellationToken cancellationToken);

    Task<ILoginThrottleLease> AcquireLoginThrottleAsync(
        string actorIdentifier,
        IPAddress? ipAddress,
        DateTimeOffset since,
        CancellationToken cancellationToken);

    Task RecordLoginFailureAsync(
        LocalUserRecord? user,
        string actorIdentifier,
        string outcome,
        AuthenticationRequestContext requestContext,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken);

    Task CreateSessionAsync(
        NewSessionRecord session,
        AuthenticationRequestContext requestContext,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken);

    Task<ValidatedSession?> ValidateAccessTokenAsync(
        byte[] accessTokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<RefreshStoreResult> RotateRefreshTokenAsync(
        byte[] refreshTokenHash,
        SessionTokenMaterial replacement,
        AuthenticationRequestContext requestContext,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task RevokeSessionFamilyAsync(
        Guid? sessionId,
        byte[]? refreshTokenHash,
        AuthenticationRequestContext requestContext,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public enum PasswordVerificationStatus
{
    Invalid,
    Valid,
    CapacityExceeded,
}

public sealed record PasswordVerificationResult(
    PasswordVerificationStatus Status,
    string? ReplacementHash = null)
{
    public override string ToString() => $"PasswordVerificationResult {{ Status = {Status} }}";
}

public interface IPasswordHasher
{
    Task<string> HashAsync(string password, CancellationToken cancellationToken);

    Task<PasswordVerificationResult> VerifyAsync(
        string password,
        string? passwordHash,
        CancellationToken cancellationToken);
}

public sealed class SecretToken
{
    public SecretToken(string value, byte[] hash)
    {
        Value = value;
        Hash = hash;
    }

    public string Value { get; }
    public byte[] Hash { get; }

    public override string ToString() => "[REDACTED]";
}

public interface ISessionTokenService
{
    SecretToken Issue();

    bool TryHash(string? token, out byte[] hash);
}

public enum LoginStatus
{
    Succeeded,
    InvalidCredentials,
    RateLimited,
}

public enum RefreshStatus
{
    Succeeded,
    InvalidToken,
    Replayed,
}

public sealed record IssuedSession(
    Guid UserId,
    Guid SessionId,
    string Email,
    SecretToken AccessToken,
    DateTimeOffset AccessExpiresAt,
    SecretToken RefreshToken,
    DateTimeOffset RefreshExpiresAt);

public sealed record LoginResult(
    LoginStatus Status,
    IssuedSession? Session = null,
    TimeSpan? RetryAfter = null);

public sealed record RefreshResult(RefreshStatus Status, IssuedSession? Session = null);

public enum BootstrapStatus
{
    Created,
    Updated,
    AlreadyDisabled,
}
