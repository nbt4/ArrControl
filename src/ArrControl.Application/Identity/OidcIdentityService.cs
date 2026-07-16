namespace ArrControl.Application.Identity;

public sealed class OidcIdentityService
{
    private readonly IOidcIdentityStore store;
    private readonly ISessionTokenService tokenService;
    private readonly LocalAuthSettings localAuthSettings;
    private readonly TimeProvider timeProvider;
    private readonly OidcRoleMapping[] roleMappings;

    public OidcIdentityService(
        IOidcIdentityStore store,
        ISessionTokenService tokenService,
        LocalAuthSettings localAuthSettings,
        OidcIdentitySettings oidcIdentitySettings,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tokenService);
        ArgumentNullException.ThrowIfNull(localAuthSettings);
        ArgumentNullException.ThrowIfNull(oidcIdentitySettings);
        ArgumentNullException.ThrowIfNull(timeProvider);

        localAuthSettings.Validate();
        oidcIdentitySettings.Validate();

        this.store = store;
        this.tokenService = tokenService;
        this.localAuthSettings = localAuthSettings;
        this.timeProvider = timeProvider;
        roleMappings = oidcIdentitySettings.RoleMappings.ToArray();
    }

    public async Task<OidcLoginResult> LoginAsync(
        OidcIdentityClaims claims,
        string protectedIdToken,
        AuthenticationRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(requestContext);
        ValidateOpaqueClaim(
            claims.Issuer,
            OidcIdentityLimits.MaximumIssuerLength,
            "oidc_issuer_invalid");
        ValidateOpaqueClaim(
            claims.Subject,
            OidcIdentityLimits.MaximumSubjectLength,
            "oidc_subject_invalid");
        if (string.IsNullOrWhiteSpace(protectedIdToken)
            || protectedIdToken.Length > OidcIdentityLimits.MaximumProtectedIdTokenLength)
        {
            throw new IdentityValidationException("oidc_id_token_invalid");
        }

        var desiredRoles = MapDesiredRoles(claims.Groups);
        string? verifiedEmail = null;
        string? verifiedNormalizedEmail = null;
        if (claims.EmailVerified
            && IdentityEmailAddress.TryNormalize(
                claims.Email,
                out var canonicalEmail,
                out var normalizedEmail))
        {
            verifiedEmail = canonicalEmail;
            verifiedNormalizedEmail = normalizedEmail;
        }

        var accessToken = tokenService.Issue();
        var refreshToken = tokenService.Issue();
        var now = timeProvider.GetUtcNow();
        var sessionId = Guid.CreateVersion7();
        var storeResult = await store.CreateSessionAsync(
            new NewOidcSessionRecord(
                claims.Issuer!,
                claims.Subject!,
                verifiedEmail,
                verifiedNormalizedEmail,
                desiredRoles,
                Guid.CreateVersion7(),
                new SessionTokenMaterial(
                    sessionId,
                    accessToken.Hash,
                    now + localAuthSettings.AccessTokenLifetime,
                    refreshToken.Hash),
                now + localAuthSettings.RefreshTokenLifetime,
                LocalIdentityConstants.OidcAuthenticationMethod,
                protectedIdToken),
            requestContext,
            now,
            cancellationToken);

        if (storeResult.Status != OidcSessionStoreStatus.Succeeded)
        {
            return new OidcLoginResult(ToLoginStatus(storeResult.Status));
        }

        if (storeResult.UserId == Guid.Empty
            || storeResult.SessionId != sessionId
            || string.IsNullOrWhiteSpace(storeResult.Email)
            || storeResult.AccessExpiresAt <= now
            || storeResult.RefreshExpiresAt <= storeResult.AccessExpiresAt)
        {
            throw new InvalidOperationException("The OIDC identity store returned an invalid successful session.");
        }

        return new OidcLoginResult(
            OidcLoginStatus.Succeeded,
            new IssuedSession(
                storeResult.UserId,
                storeResult.SessionId,
                storeResult.Email,
                accessToken,
                storeResult.AccessExpiresAt,
                refreshToken,
                storeResult.RefreshExpiresAt));
    }

    public async Task<OidcLogoutContext?> GetLogoutContextAsync(
        Guid? sessionId,
        string? refreshToken,
        CancellationToken cancellationToken)
    {
        byte[]? refreshTokenHash = null;
        if (tokenService.TryHash(refreshToken, out var parsedRefreshTokenHash))
        {
            refreshTokenHash = parsedRefreshTokenHash;
        }

        if (sessionId is null && refreshTokenHash is null)
        {
            return null;
        }

        return await store.GetLogoutContextAsync(
            sessionId,
            refreshTokenHash,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public Task RecordProtocolFailureAsync(
        AuthenticationRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestContext);
        return store.RecordProtocolFailureAsync(
            requestContext,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private IReadOnlyCollection<string> MapDesiredRoles(IReadOnlyCollection<string>? claimedGroups)
    {
        if (claimedGroups is null || claimedGroups.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (claimedGroups.Count > OidcIdentityLimits.MaximumGroups)
        {
            throw new IdentityValidationException("oidc_groups_invalid");
        }

        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in claimedGroups)
        {
            if (string.IsNullOrEmpty(group)
                || group.Length > OidcIdentityLimits.MaximumGroupLength)
            {
                throw new IdentityValidationException("oidc_groups_invalid");
            }

            groups.Add(group);
        }

        var desiredRoles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in roleMappings)
        {
            if (groups.Contains(mapping.Group))
            {
                _ = OidcIdentitySettings.TryNormalizeRoleName(mapping.Role, out var normalizedRole);
                desiredRoles.Add(normalizedRole);
            }
        }

        return desiredRoles.Order(StringComparer.Ordinal).ToArray();
    }

    private static OidcLoginStatus ToLoginStatus(OidcSessionStoreStatus status) => status switch
    {
        OidcSessionStoreStatus.Succeeded => OidcLoginStatus.Succeeded,
        OidcSessionStoreStatus.Inactive => OidcLoginStatus.Inactive,
        OidcSessionStoreStatus.RoleMissing => OidcLoginStatus.RoleMissing,
        OidcSessionStoreStatus.UnverifiedIdentity => OidcLoginStatus.UnverifiedIdentity,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown OIDC store status."),
    };

    private static void ValidateOpaqueClaim(string? value, int maximumLength, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Contains('\0'))
        {
            throw new IdentityValidationException(errorCode);
        }
    }
}
