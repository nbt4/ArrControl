namespace ArrControl.Application.Identity;

public static class OidcIdentityLimits
{
    public const int MaximumIssuerLength = 2048;
    public const int MaximumSubjectLength = 512;
    public const int MaximumGroupLength = 512;
    public const int MaximumGroups = 128;
    public const int MaximumRoleMappings = 256;
    public const int MaximumRoleNameLength = 120;
    public const int MaximumProtectedIdTokenLength = 65_536;
}

public sealed record OidcIdentityClaims(
    string? Issuer,
    string? Subject,
    string? Email,
    bool EmailVerified,
    IReadOnlyCollection<string>? Groups)
{
    public override string ToString() => "[REDACTED]";
}

public sealed record OidcRoleMapping(string Group, string Role);

public sealed record OidcIdentitySettings(IReadOnlyCollection<OidcRoleMapping> RoleMappings)
{
    public static OidcIdentitySettings Default { get; } = new(Array.Empty<OidcRoleMapping>());

    public void Validate()
    {
        if (RoleMappings is null)
        {
            throw new InvalidOperationException("OIDC role mappings must be configured as a collection.");
        }

        if (RoleMappings.Count > OidcIdentityLimits.MaximumRoleMappings)
        {
            throw new InvalidOperationException(
                $"At most {OidcIdentityLimits.MaximumRoleMappings} OIDC role mappings may be configured.");
        }

        foreach (var mapping in RoleMappings)
        {
            if (mapping is null
                || string.IsNullOrEmpty(mapping.Group)
                || mapping.Group.Length > OidcIdentityLimits.MaximumGroupLength)
            {
                throw new InvalidOperationException("Each OIDC role mapping must have a valid exact group name.");
            }

            if (!TryNormalizeRoleName(mapping.Role, out _))
            {
                throw new InvalidOperationException("Each OIDC role mapping must have a valid target role name.");
            }
        }
    }

    internal static bool TryNormalizeRoleName(string? role, out string normalizedRole)
    {
        normalizedRole = string.Empty;
        if (string.IsNullOrWhiteSpace(role)
            || !string.Equals(role, role.Trim(), StringComparison.Ordinal)
            || role.Length > OidcIdentityLimits.MaximumRoleNameLength)
        {
            return false;
        }

        normalizedRole = role.ToUpperInvariant();
        return normalizedRole.Length <= OidcIdentityLimits.MaximumRoleNameLength;
    }
}

public sealed record NewOidcSessionRecord(
    string Issuer,
    string Subject,
    string? VerifiedEmail,
    string? VerifiedNormalizedEmail,
    IReadOnlyCollection<string> DesiredNormalizedRoleNames,
    Guid TokenFamilyId,
    SessionTokenMaterial Session,
    DateTimeOffset RefreshExpiresAt,
    string AuthenticationMethod,
    string ProtectedIdToken)
{
    public override string ToString() => "[REDACTED]";
}

public enum OidcSessionStoreStatus
{
    Succeeded,
    Inactive,
    RoleMissing,
    UnverifiedIdentity,
}

public sealed record OidcSessionStoreResult(
    OidcSessionStoreStatus Status,
    Guid UserId = default,
    Guid SessionId = default,
    string? Email = null,
    DateTimeOffset AccessExpiresAt = default,
    DateTimeOffset RefreshExpiresAt = default);

public sealed record OidcLogoutContext(string ProtectedIdToken)
{
    public override string ToString() => "[REDACTED]";
}

public interface IOidcIdentityStore
{
    Task<OidcSessionStoreResult> CreateSessionAsync(
        NewOidcSessionRecord session,
        AuthenticationRequestContext requestContext,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken);

    Task<OidcLogoutContext?> GetLogoutContextAsync(
        Guid? sessionId,
        byte[]? refreshTokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task RecordProtocolFailureAsync(
        AuthenticationRequestContext requestContext,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken);
}

public enum OidcLoginStatus
{
    Succeeded,
    Inactive,
    RoleMissing,
    UnverifiedIdentity,
}

public sealed record OidcLoginResult(
    OidcLoginStatus Status,
    IssuedSession? Session = null);
