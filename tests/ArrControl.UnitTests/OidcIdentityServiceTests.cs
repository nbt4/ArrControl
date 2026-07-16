using System.Net;
using ArrControl.Application.Identity;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class OidcIdentityServiceTests
{
    private static readonly DateTimeOffset CurrentTime = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticationRequestContext RequestContext = new(
        "oidc-test-correlation",
        IPAddress.Parse("192.0.2.80"));
    private static readonly LocalAuthSettings LocalSettings = new(
        TimeSpan.FromMinutes(10),
        TimeSpan.FromDays(14),
        TimeSpan.FromMinutes(20),
        2,
        3);

    [Fact]
    public async Task Existing_subject_can_login_without_email_and_identity_values_remain_opaque()
    {
        var accessToken = Token("access-token", 1);
        var refreshToken = Token("refresh-token", 2);
        var tokenService = new FakeSessionTokenService(accessToken, refreshToken);
        var userId = Guid.NewGuid();
        var store = new FakeOidcIdentityStore
        {
            CreateResultFactory = record => new OidcSessionStoreResult(
                OidcSessionStoreStatus.Succeeded,
                userId,
                record.Session.Id,
                "existing@example.com",
                record.Session.RequestedAccessExpiresAt,
                record.RefreshExpiresAt),
        };
        var service = CreateService(store, tokenService);

        var result = await service.LoginAsync(
            new OidcIdentityClaims(
                "https://IDP.example/application/o/Tenant/",
                "Case-Sensitive-Subject",
                null,
                false,
                null),
            "protected-id-token",
            RequestContext,
            CancellationToken.None);

        Assert.Equal(OidcLoginStatus.Succeeded, result.Status);
        var issued = Assert.IsType<IssuedSession>(result.Session);
        var persisted = Assert.IsType<NewOidcSessionRecord>(store.CreatedSession);
        Assert.Equal("https://IDP.example/application/o/Tenant/", persisted.Issuer);
        Assert.Equal("Case-Sensitive-Subject", persisted.Subject);
        Assert.Null(persisted.VerifiedEmail);
        Assert.Null(persisted.VerifiedNormalizedEmail);
        Assert.Empty(persisted.DesiredNormalizedRoleNames);
        Assert.Equal(LocalIdentityConstants.OidcAuthenticationMethod, persisted.AuthenticationMethod);
        Assert.Equal("protected-id-token", persisted.ProtectedIdToken);
        Assert.Equal(accessToken.Hash, persisted.Session.AccessTokenHash);
        Assert.Equal(refreshToken.Hash, persisted.Session.RefreshTokenHash);
        Assert.Equal(CurrentTime + LocalSettings.AccessTokenLifetime, persisted.Session.RequestedAccessExpiresAt);
        Assert.Equal(CurrentTime + LocalSettings.RefreshTokenLifetime, persisted.RefreshExpiresAt);
        Assert.NotEqual(Guid.Empty, persisted.TokenFamilyId);
        Assert.Equal(userId, issued.UserId);
        Assert.Equal(persisted.Session.Id, issued.SessionId);
        Assert.Same(accessToken, issued.AccessToken);
        Assert.Same(refreshToken, issued.RefreshToken);
        Assert.Same(RequestContext, store.RequestContext);
        Assert.Equal(CurrentTime, store.OccurredAt);
    }

    [Theory]
    [InlineData(" Alice@example.com ", true, "Alice@example.com", "ALICE@EXAMPLE.COM")]
    [InlineData("alice@example.com", false, null, null)]
    [InlineData("not-an-email", true, null, null)]
    [InlineData(null, true, null, null)]
    public async Task Only_verified_syntactically_valid_email_is_offered_for_linking(
        string? email,
        bool emailVerified,
        string? expectedEmail,
        string? expectedNormalizedEmail)
    {
        var store = new FakeOidcIdentityStore
        {
            CreateResultFactory = _ => new OidcSessionStoreResult(
                OidcSessionStoreStatus.UnverifiedIdentity),
        };
        var service = CreateService(
            store,
            new FakeSessionTokenService(Token("access", 3), Token("refresh", 4)));

        var result = await service.LoginAsync(
            Claims(email: email, emailVerified: emailVerified),
            "protected-id-token",
            RequestContext,
            CancellationToken.None);

        Assert.Equal(OidcLoginStatus.UnverifiedIdentity, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(expectedEmail, store.CreatedSession!.VerifiedEmail);
        Assert.Equal(expectedNormalizedEmail, store.CreatedSession.VerifiedNormalizedEmail);
    }

    [Fact]
    public async Task Groups_map_only_by_exact_name_and_roles_are_normalized_and_deduplicated()
    {
        var settings = new OidcIdentitySettings(
        [
            new("ops-admins", "Administrator"),
            new("ops-admins", "Media Manager"),
            new("Ops-Admins", "Auditor"),
            new("viewers", "Media Manager"),
        ]);
        var store = new FakeOidcIdentityStore
        {
            CreateResultFactory = _ => new OidcSessionStoreResult(OidcSessionStoreStatus.RoleMissing),
        };
        var service = CreateService(
            store,
            new FakeSessionTokenService(Token("access", 5), Token("refresh", 6)),
            settings);

        var result = await service.LoginAsync(
            Claims(groups: ["ops-admins", "VIEWERS", "ops-admins"]),
            "protected-id-token",
            RequestContext,
            CancellationToken.None);

        Assert.Equal(OidcLoginStatus.RoleMissing, result.Status);
        Assert.Equal(
            ["ADMINISTRATOR", "MEDIA MANAGER"],
            store.CreatedSession!.DesiredNormalizedRoleNames);
    }

    [Theory]
    [InlineData(OidcSessionStoreStatus.Inactive, OidcLoginStatus.Inactive)]
    [InlineData(OidcSessionStoreStatus.RoleMissing, OidcLoginStatus.RoleMissing)]
    [InlineData(OidcSessionStoreStatus.UnverifiedIdentity, OidcLoginStatus.UnverifiedIdentity)]
    public async Task Store_fail_closed_outcomes_do_not_return_issued_tokens(
        OidcSessionStoreStatus storeStatus,
        OidcLoginStatus expectedStatus)
    {
        var store = new FakeOidcIdentityStore
        {
            CreateResultFactory = _ => new OidcSessionStoreResult(storeStatus),
        };
        var service = CreateService(
            store,
            new FakeSessionTokenService(Token("access", 7), Token("refresh", 8)));

        var result = await service.LoginAsync(
            Claims(),
            "protected-id-token",
            RequestContext,
            CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task Oversized_group_array_is_rejected_before_tokens_or_store_are_used()
    {
        var groups = Enumerable.Range(0, OidcIdentityLimits.MaximumGroups + 1)
            .Select(index => $"group-{index}")
            .ToArray();
        var store = new FakeOidcIdentityStore();
        var tokenService = new FakeSessionTokenService();
        var service = CreateService(store, tokenService);

        var exception = await Assert.ThrowsAsync<IdentityValidationException>(() => service.LoginAsync(
            Claims(groups: groups),
            "protected-id-token",
            RequestContext,
            CancellationToken.None));

        Assert.Equal("oidc_groups_invalid", exception.Code);
        Assert.Equal(0, tokenService.IssueCallCount);
        Assert.Equal(0, store.CreateCallCount);
    }

    [Theory]
    [InlineData(null, "subject", "oidc_issuer_invalid")]
    [InlineData("", "subject", "oidc_issuer_invalid")]
    [InlineData("issuer", null, "oidc_subject_invalid")]
    [InlineData("issuer", "", "oidc_subject_invalid")]
    [InlineData("issuer", "\0", "oidc_subject_invalid")]
    public async Task Missing_identity_claims_are_rejected_before_session_creation(
        string? issuer,
        string? subject,
        string expectedCode)
    {
        var store = new FakeOidcIdentityStore();
        var tokenService = new FakeSessionTokenService();
        var service = CreateService(store, tokenService);

        var exception = await Assert.ThrowsAsync<IdentityValidationException>(() => service.LoginAsync(
            Claims(issuer, subject),
            "protected-id-token",
            RequestContext,
            CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(0, tokenService.IssueCallCount);
        Assert.Equal(0, store.CreateCallCount);
    }

    [Fact]
    public async Task Logout_context_uses_only_a_parsed_refresh_hash_and_is_redacted()
    {
        var expectedHash = Bytes(9);
        var expectedContext = new OidcLogoutContext("protected-id-token");
        var store = new FakeOidcIdentityStore { LogoutContext = expectedContext };
        var tokenService = new FakeSessionTokenService();
        tokenService.Hashes["valid-refresh-token"] = expectedHash;
        var service = CreateService(store, tokenService);

        var result = await service.GetLogoutContextAsync(
            null,
            "valid-refresh-token",
            CancellationToken.None);

        Assert.Same(expectedContext, result);
        Assert.Equal(expectedHash, store.LogoutRefreshTokenHash);
        Assert.Null(store.LogoutSessionId);
        Assert.Equal(CurrentTime, store.LogoutNow);
        Assert.Equal("[REDACTED]", result!.ToString());
    }

    [Fact]
    public async Task Malformed_logout_identity_does_not_query_the_store()
    {
        var store = new FakeOidcIdentityStore();
        var service = CreateService(store, new FakeSessionTokenService());

        var result = await service.GetLogoutContextAsync(
            null,
            "malformed-refresh-token",
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, store.LogoutCallCount);
    }

    [Fact]
    public void Invalid_role_mapping_is_rejected_when_service_is_constructed()
    {
        var settings = new OidcIdentitySettings([new OidcRoleMapping("authentik Admins", " role ")]);

        var exception = Assert.Throws<InvalidOperationException>(() => CreateService(
            new FakeOidcIdentityStore(),
            new FakeSessionTokenService(),
            settings));

        Assert.Contains("target role", exception.Message, StringComparison.Ordinal);
    }

    private static OidcIdentityService CreateService(
        FakeOidcIdentityStore store,
        FakeSessionTokenService tokenService,
        OidcIdentitySettings? settings = null) =>
        new(
            store,
            tokenService,
            LocalSettings,
            settings ?? OidcIdentitySettings.Default,
            new FixedTimeProvider(CurrentTime));

    private static OidcIdentityClaims Claims(
        string? issuer = "https://idp.example/application/o/arrcontrol/",
        string? subject = "subject-123",
        string? email = "user@example.com",
        bool emailVerified = true,
        IReadOnlyCollection<string>? groups = null) =>
        new(issuer, subject, email, emailVerified, groups);

    private static SecretToken Token(string value, byte fill) => new(value, Bytes(fill));

    private static byte[] Bytes(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FakeSessionTokenService(params SecretToken[] issuedTokens) : ISessionTokenService
    {
        private readonly Queue<SecretToken> tokens = new(issuedTokens);

        public Dictionary<string, byte[]> Hashes { get; } = new(StringComparer.Ordinal);
        public int IssueCallCount { get; private set; }

        public SecretToken Issue()
        {
            IssueCallCount++;
            return tokens.Dequeue();
        }

        public bool TryHash(string? token, out byte[] hash)
        {
            if (token is not null && Hashes.TryGetValue(token, out var configuredHash))
            {
                hash = configuredHash;
                return true;
            }

            hash = [];
            return false;
        }
    }

    private sealed class FakeOidcIdentityStore : IOidcIdentityStore
    {
        public Func<NewOidcSessionRecord, OidcSessionStoreResult>? CreateResultFactory { get; init; }
        public int CreateCallCount { get; private set; }
        public NewOidcSessionRecord? CreatedSession { get; private set; }
        public AuthenticationRequestContext? RequestContext { get; private set; }
        public DateTimeOffset OccurredAt { get; private set; }
        public OidcLogoutContext? LogoutContext { get; init; }
        public int LogoutCallCount { get; private set; }
        public Guid? LogoutSessionId { get; private set; }
        public byte[]? LogoutRefreshTokenHash { get; private set; }
        public DateTimeOffset LogoutNow { get; private set; }

        public Task<OidcSessionStoreResult> CreateSessionAsync(
            NewOidcSessionRecord session,
            AuthenticationRequestContext requestContext,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken)
        {
            CreateCallCount++;
            CreatedSession = session;
            RequestContext = requestContext;
            OccurredAt = occurredAt;
            return Task.FromResult(
                CreateResultFactory?.Invoke(session)
                ?? new OidcSessionStoreResult(OidcSessionStoreStatus.UnverifiedIdentity));
        }

        public Task<OidcLogoutContext?> GetLogoutContextAsync(
            Guid? sessionId,
            byte[]? refreshTokenHash,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            LogoutCallCount++;
            LogoutSessionId = sessionId;
            LogoutRefreshTokenHash = refreshTokenHash;
            LogoutNow = now;
            return Task.FromResult(LogoutContext);
        }

        public Task RecordProtocolFailureAsync(
            AuthenticationRequestContext requestContext,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
