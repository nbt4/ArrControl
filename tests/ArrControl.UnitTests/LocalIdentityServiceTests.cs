using System.Net;
using ArrControl.Application.Identity;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class LocalIdentityServiceTests
{
    private static readonly DateTimeOffset CurrentTime = new(2026, 7, 15, 10, 30, 0, TimeSpan.Zero);
    private static readonly AuthenticationRequestContext RequestContext = new(
        "test-correlation-id",
        IPAddress.Parse("192.0.2.42"));
    private static readonly LocalAuthSettings Settings = new(
        TimeSpan.FromMinutes(10),
        TimeSpan.FromDays(14),
        TimeSpan.FromMinutes(20),
        2,
        3);

    [Fact]
    public async Task Login_returns_the_same_failure_for_unknown_wrong_and_disabled_accounts()
    {
        var unknown = await RunFailedLoginAsync(user: null, PasswordVerificationStatus.Invalid);
        var wrong = await RunFailedLoginAsync(CreateUser(), PasswordVerificationStatus.Invalid);
        var disabled = await RunFailedLoginAsync(
            CreateUser(state: "disabled"),
            PasswordVerificationStatus.Valid);

        AssertGenericLoginFailure(unknown);
        AssertGenericLoginFailure(wrong);
        AssertGenericLoginFailure(disabled);
        Assert.Null(unknown.Hasher.LastPasswordHash);
        Assert.Equal("stored-password-hash", wrong.Hasher.LastPasswordHash);
        Assert.Equal("stored-password-hash", disabled.Hasher.LastPasswordHash);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(0, 3)]
    public async Task Login_is_throttled_before_user_lookup_or_password_verification(
        int accountFailures,
        int ipFailures)
    {
        var store = new FakeLocalIdentityStore
        {
            FailureCounts = new LoginFailureCounts(accountFailures, ipFailures),
            User = CreateUser(),
        };
        var hasher = new FakePasswordHasher(PasswordVerificationStatus.Valid);
        var tokenService = new FakeSessionTokenService();
        var service = CreateService(store, hasher, tokenService);

        var result = await service.LoginAsync(
            "admin@example.com",
            "a-valid-password",
            RequestContext,
            CancellationToken.None);

        Assert.Equal(LoginStatus.RateLimited, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(Settings.LoginFailureWindow, result.RetryAfter);
        Assert.Equal(0, store.UserLookupCount);
        Assert.Equal(0, hasher.VerifyCallCount);
        Assert.Equal(0, tokenService.IssueCallCount);
        var failure = Assert.Single(store.RecordedFailures);
        Assert.Equal("rate_limited", failure.Outcome);
        Assert.Null(failure.User);
        Assert.Equal(RequestContext.IpAddress, store.FailureLookupIpAddress);
        Assert.Equal(CurrentTime - Settings.LoginFailureWindow, store.FailureLookupSince);
    }

    [Fact]
    public async Task Password_hasher_capacity_limit_is_reported_as_throttling()
    {
        var store = new FakeLocalIdentityStore { User = CreateUser() };
        var hasher = new FakePasswordHasher(PasswordVerificationStatus.CapacityExceeded);
        var tokenService = new FakeSessionTokenService();
        var service = CreateService(store, hasher, tokenService);

        var result = await service.LoginAsync(
            "admin@example.com",
            "a-valid-password",
            RequestContext,
            CancellationToken.None);

        Assert.Equal(LoginStatus.RateLimited, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(TimeSpan.FromSeconds(2), result.RetryAfter);
        Assert.Equal(0, tokenService.IssueCallCount);
        Assert.Equal("rate_limited", Assert.Single(store.RecordedFailures).Outcome);
    }

    [Fact]
    public async Task Overlapping_account_or_ip_attempt_is_shed_before_password_verification()
    {
        var store = new FakeLocalIdentityStore
        {
            ThrottleAcquired = false,
            User = CreateUser(),
        };
        var hasher = new FakePasswordHasher(PasswordVerificationStatus.Valid);
        var tokenService = new FakeSessionTokenService();
        var service = CreateService(store, hasher, tokenService);

        var result = await service.LoginAsync(
            "admin@example.com",
            "a-valid-password",
            RequestContext,
            CancellationToken.None);

        Assert.Equal(LoginStatus.RateLimited, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(1), result.RetryAfter);
        Assert.Equal(0, store.UserLookupCount);
        Assert.Equal(0, hasher.VerifyCallCount);
        Assert.Equal(0, tokenService.IssueCallCount);
        Assert.Equal("rate_limited", Assert.Single(store.RecordedFailures).Outcome);
    }

    [Fact]
    public async Task Successful_login_issues_and_persists_a_complete_session()
    {
        var user = CreateUser();
        var store = new FakeLocalIdentityStore { User = user };
        var hasher = new FakePasswordHasher(
            PasswordVerificationStatus.Valid,
            replacementHash: "replacement-password-hash");
        var accessToken = Token("access-token", 1);
        var refreshToken = Token("refresh-token", 2);
        var tokenService = new FakeSessionTokenService(accessToken, refreshToken);
        var service = CreateService(store, hasher, tokenService);

        var result = await service.LoginAsync(
            "Admin@example.com",
            "a-valid-password",
            RequestContext,
            CancellationToken.None);

        Assert.Equal(LoginStatus.Succeeded, result.Status);
        var issued = Assert.IsType<IssuedSession>(result.Session);
        var persisted = Assert.IsType<NewSessionRecord>(store.CreatedSession);
        Assert.Equal(user.Id, issued.UserId);
        Assert.Equal(user.Email, issued.Email);
        Assert.Equal(persisted.Id, issued.SessionId);
        Assert.Same(accessToken, issued.AccessToken);
        Assert.Same(refreshToken, issued.RefreshToken);
        Assert.Equal(CurrentTime + Settings.AccessTokenLifetime, issued.AccessExpiresAt);
        Assert.Equal(CurrentTime + Settings.RefreshTokenLifetime, issued.RefreshExpiresAt);
        Assert.Equal(user.Id, persisted.UserId);
        Assert.NotEqual(Guid.Empty, persisted.TokenFamilyId);
        Assert.Equal(accessToken.Hash, persisted.AccessTokenHash);
        Assert.Equal(refreshToken.Hash, persisted.RefreshTokenHash);
        Assert.Equal(LocalIdentityConstants.LocalAuthenticationMethod, persisted.AuthenticationMethod);
        Assert.Equal("replacement-password-hash", persisted.ReplacementPasswordHash);
        Assert.Equal("ADMIN@EXAMPLE.COM", store.LastNormalizedEmail);
        Assert.Equal(CurrentTime, store.SessionOccurredAt);
        Assert.Same(RequestContext, store.SessionRequestContext);
        Assert.Empty(store.RecordedFailures);
    }

    [Fact]
    public async Task Refresh_rotates_both_tokens_and_uses_store_authoritative_expiry()
    {
        var oldRefreshHash = Bytes(3);
        var newAccessToken = Token("new-access-token", 4);
        var newRefreshToken = Token("new-refresh-token", 5);
        var userId = Guid.NewGuid();
        var absoluteRefreshExpiry = CurrentTime.AddDays(8);
        var store = new FakeLocalIdentityStore
        {
            RotateResultFactory = replacement => new RefreshStoreResult(
                RefreshStoreStatus.Succeeded,
                userId,
                replacement.Id,
                "admin@example.com",
                replacement.RequestedAccessExpiresAt,
                absoluteRefreshExpiry),
        };
        var tokenService = new FakeSessionTokenService(newAccessToken, newRefreshToken);
        tokenService.Hashes["old-refresh-token"] = oldRefreshHash;
        var service = CreateService(store, new FakePasswordHasher(), tokenService);

        var result = await service.RefreshAsync(
            "old-refresh-token",
            RequestContext,
            CancellationToken.None);

        Assert.Equal(RefreshStatus.Succeeded, result.Status);
        var issued = Assert.IsType<IssuedSession>(result.Session);
        var replacement = Assert.IsType<SessionTokenMaterial>(store.RotationReplacement);
        Assert.Equal(oldRefreshHash, store.RotationRefreshHash);
        Assert.Equal(newAccessToken.Hash, replacement.AccessTokenHash);
        Assert.Equal(newRefreshToken.Hash, replacement.RefreshTokenHash);
        Assert.Equal(CurrentTime + Settings.AccessTokenLifetime, replacement.RequestedAccessExpiresAt);
        Assert.Equal(replacement.Id, issued.SessionId);
        Assert.Equal(userId, issued.UserId);
        Assert.Same(newAccessToken, issued.AccessToken);
        Assert.Same(newRefreshToken, issued.RefreshToken);
        Assert.Equal(absoluteRefreshExpiry, issued.RefreshExpiresAt);
        Assert.Equal(CurrentTime, store.RotationOccurredAt);
        Assert.Same(RequestContext, store.RotationRequestContext);
    }

    [Theory]
    [InlineData(RefreshStoreStatus.Invalid, RefreshStatus.InvalidToken)]
    [InlineData(RefreshStoreStatus.Replayed, RefreshStatus.Replayed)]
    public async Task Refresh_preserves_generic_invalid_and_replay_outcomes(
        RefreshStoreStatus storeStatus,
        RefreshStatus expectedStatus)
    {
        var store = new FakeLocalIdentityStore
        {
            RotateResultFactory = _ => new RefreshStoreResult(storeStatus),
        };
        var tokenService = new FakeSessionTokenService(Token("access", 6), Token("refresh", 7));
        tokenService.Hashes["presented-refresh"] = Bytes(8);
        var service = CreateService(store, new FakePasswordHasher(), tokenService);

        var result = await service.RefreshAsync(
            "presented-refresh",
            RequestContext,
            CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task Malformed_refresh_is_rejected_without_issuing_or_rotating_tokens()
    {
        var store = new FakeLocalIdentityStore();
        var tokenService = new FakeSessionTokenService();
        var service = CreateService(store, new FakePasswordHasher(), tokenService);

        var result = await service.RefreshAsync(
            "malformed",
            RequestContext,
            CancellationToken.None);

        Assert.Equal(RefreshStatus.InvalidToken, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(0, tokenService.IssueCallCount);
        Assert.Equal(0, store.RotateCallCount);
    }

    [Fact]
    public async Task Logout_is_idempotent_and_passes_only_a_parsed_refresh_hash()
    {
        var sessionId = Guid.NewGuid();
        var refreshHash = Bytes(9);
        var store = new FakeLocalIdentityStore();
        var tokenService = new FakeSessionTokenService();
        tokenService.Hashes["valid-refresh"] = refreshHash;
        var service = CreateService(store, new FakePasswordHasher(), tokenService);

        await service.LogoutAsync(
            sessionId,
            "valid-refresh",
            RequestContext,
            CancellationToken.None);
        await service.LogoutAsync(
            sessionId,
            "valid-refresh",
            RequestContext,
            CancellationToken.None);
        await service.LogoutAsync(
            sessionId,
            "malformed",
            RequestContext,
            CancellationToken.None);

        Assert.Equal(3, store.Revocations.Count);
        Assert.All(store.Revocations, revocation => Assert.Equal(sessionId, revocation.SessionId));
        Assert.Equal(refreshHash, store.Revocations[0].RefreshTokenHash);
        Assert.Equal(refreshHash, store.Revocations[1].RefreshTokenHash);
        Assert.Null(store.Revocations[2].RefreshTokenHash);
        Assert.All(store.Revocations, revocation => Assert.Equal(CurrentTime, revocation.OccurredAt));
    }

    private static LocalIdentityService CreateService(
        FakeLocalIdentityStore store,
        FakePasswordHasher passwordHasher,
        FakeSessionTokenService tokenService) =>
        new(store, passwordHasher, tokenService, Settings, new FixedTimeProvider(CurrentTime));

    private static LocalUserRecord CreateUser(string state = LocalIdentityConstants.ActiveUserState) =>
        new(
            Guid.Parse("0198ae20-e580-7700-8000-000000000001"),
            "admin@example.com",
            "ADMIN@EXAMPLE.COM",
            state,
            "stored-password-hash");

    private static async Task<FailedLoginCase> RunFailedLoginAsync(
        LocalUserRecord? user,
        PasswordVerificationStatus verificationStatus)
    {
        var store = new FakeLocalIdentityStore { User = user };
        var hasher = new FakePasswordHasher(verificationStatus);
        var tokenService = new FakeSessionTokenService();
        var service = CreateService(store, hasher, tokenService);

        var result = await service.LoginAsync(
            "admin@example.com",
            "a-valid-password",
            RequestContext,
            CancellationToken.None);

        return new FailedLoginCase(result, store, hasher, tokenService);
    }

    private static void AssertGenericLoginFailure(FailedLoginCase failedLogin)
    {
        Assert.Equal(LoginStatus.InvalidCredentials, failedLogin.Result.Status);
        Assert.Null(failedLogin.Result.Session);
        Assert.Equal(1, failedLogin.Hasher.VerifyCallCount);
        Assert.Equal(0, failedLogin.TokenService.IssueCallCount);
        Assert.Equal("failed", Assert.Single(failedLogin.Store.RecordedFailures).Outcome);
        Assert.Null(failedLogin.Store.CreatedSession);
    }

    private static SecretToken Token(string value, byte fill) => new(value, Bytes(fill));

    private static byte[] Bytes(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private sealed record FailedLoginCase(
        LoginResult Result,
        FakeLocalIdentityStore Store,
        FakePasswordHasher Hasher,
        FakeSessionTokenService TokenService);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FakePasswordHasher(
        PasswordVerificationStatus status = PasswordVerificationStatus.Invalid,
        string? replacementHash = null) : IPasswordHasher
    {
        public int VerifyCallCount { get; private set; }
        public string? LastPasswordHash { get; private set; }

        public Task<string> HashAsync(string password, CancellationToken cancellationToken) =>
            Task.FromResult("unused-password-hash");

        public Task<PasswordVerificationResult> VerifyAsync(
            string password,
            string? passwordHash,
            CancellationToken cancellationToken)
        {
            VerifyCallCount++;
            LastPasswordHash = passwordHash;
            return Task.FromResult(new PasswordVerificationResult(status, replacementHash));
        }
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

    private sealed class FakeLocalIdentityStore : ILocalIdentityStore
    {
        public LocalUserRecord? User { get; init; }
        public LoginFailureCounts FailureCounts { get; init; } = new(0, 0);
        public bool ThrottleAcquired { get; init; } = true;
        public int UserLookupCount { get; private set; }
        public string? LastNormalizedEmail { get; private set; }
        public IPAddress? FailureLookupIpAddress { get; private set; }
        public DateTimeOffset FailureLookupSince { get; private set; }
        public List<RecordedFailure> RecordedFailures { get; } = [];
        public NewSessionRecord? CreatedSession { get; private set; }
        public AuthenticationRequestContext? SessionRequestContext { get; private set; }
        public DateTimeOffset SessionOccurredAt { get; private set; }
        public Func<SessionTokenMaterial, RefreshStoreResult>? RotateResultFactory { get; init; }
        public int RotateCallCount { get; private set; }
        public byte[]? RotationRefreshHash { get; private set; }
        public SessionTokenMaterial? RotationReplacement { get; private set; }
        public AuthenticationRequestContext? RotationRequestContext { get; private set; }
        public DateTimeOffset RotationOccurredAt { get; private set; }
        public List<Revocation> Revocations { get; } = [];

        public Task<bool> HasUsersAsync(CancellationToken cancellationToken) => Task.FromResult(User is not null);

        public Task<bool> IsBootstrapDisabledAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<BootstrapStoreStatus> BootstrapAsync(
            BootstrapUserRecord user,
            AuthenticationRequestContext requestContext,
            CancellationToken cancellationToken) =>
            Task.FromResult(BootstrapStoreStatus.Created);

        public Task<LocalUserRecord?> FindLocalUserAsync(
            string normalizedEmail,
            CancellationToken cancellationToken)
        {
            UserLookupCount++;
            LastNormalizedEmail = normalizedEmail;
            return Task.FromResult(User);
        }

        public Task<ILoginThrottleLease> AcquireLoginThrottleAsync(
            string actorIdentifier,
            IPAddress? ipAddress,
            DateTimeOffset since,
            CancellationToken cancellationToken)
        {
            FailureLookupIpAddress = ipAddress;
            FailureLookupSince = since;
            return Task.FromResult<ILoginThrottleLease>(
                new FakeLoginThrottleLease(ThrottleAcquired, FailureCounts));
        }

        public Task RecordLoginFailureAsync(
            LocalUserRecord? user,
            string actorIdentifier,
            string outcome,
            AuthenticationRequestContext requestContext,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken)
        {
            RecordedFailures.Add(new RecordedFailure(user, actorIdentifier, outcome));
            return Task.CompletedTask;
        }

        public Task CreateSessionAsync(
            NewSessionRecord session,
            AuthenticationRequestContext requestContext,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken)
        {
            CreatedSession = session;
            SessionRequestContext = requestContext;
            SessionOccurredAt = occurredAt;
            return Task.CompletedTask;
        }

        public Task<ValidatedSession?> ValidateAccessTokenAsync(
            byte[] accessTokenHash,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            Task.FromResult<ValidatedSession?>(null);

        public Task<RefreshStoreResult> RotateRefreshTokenAsync(
            byte[] refreshTokenHash,
            SessionTokenMaterial replacement,
            AuthenticationRequestContext requestContext,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            RotateCallCount++;
            RotationRefreshHash = refreshTokenHash;
            RotationReplacement = replacement;
            RotationRequestContext = requestContext;
            RotationOccurredAt = now;
            return Task.FromResult(
                RotateResultFactory?.Invoke(replacement)
                ?? new RefreshStoreResult(RefreshStoreStatus.Invalid));
        }

        public Task RevokeSessionFamilyAsync(
            Guid? sessionId,
            byte[]? refreshTokenHash,
            AuthenticationRequestContext requestContext,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            Revocations.Add(new Revocation(sessionId, refreshTokenHash, requestContext, now));
            return Task.CompletedTask;
        }
    }

    private sealed record RecordedFailure(
        LocalUserRecord? User,
        string ActorIdentifier,
        string Outcome);

    private sealed record Revocation(
        Guid? SessionId,
        byte[]? RefreshTokenHash,
        AuthenticationRequestContext RequestContext,
        DateTimeOffset OccurredAt);

    private sealed class FakeLoginThrottleLease(
        bool acquired,
        LoginFailureCounts failureCounts)
        : ILoginThrottleLease
    {
        public bool Acquired { get; } = acquired;

        public LoginFailureCounts FailureCounts { get; } = failureCounts;

        public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
