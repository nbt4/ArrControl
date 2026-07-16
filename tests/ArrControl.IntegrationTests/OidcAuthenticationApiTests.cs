using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArrControl.Api.Identity;
using ArrControl.Application.Identity;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Identity;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace ArrControl.IntegrationTests;

public sealed class OidcAuthenticationApiTests(AuthApiDatabaseFixture databaseFixture)
    : IClassFixture<AuthApiDatabaseFixture>
{
    private const string AdminEmail = "admin@example.invalid";
    private const string AdminPassword = "correct bootstrap password";
    private const string ClientId = "arrcontrol-integration-tests";
    private const string ClientSecret = "integration-test-client-secret";
    private const string AdministratorGroup = "arrcontrol-admins";
    private const string AdministratorRole = "Administrator";
    private const string FailureRedirect = "/login?authError=oidc";
    private static readonly Uri PublicOrigin = new("https://arrcontrol.test/");

    [Theory]
    [InlineData("http://authentik.test/application/o/arrcontrol/", "https://arrcontrol.test/", ClientSecret)]
    [InlineData("https://authentik.test/application/o/arrcontrol", "https://arrcontrol.test/", ClientSecret)]
    [InlineData("https://authentik.test/application/o/arrcontrol/", "http://arrcontrol.test/", ClientSecret)]
    [InlineData("https://authentik.test/application/o/arrcontrol/", "https://arrcontrol.test/", null)]
    [InlineData("https://authentik.test/application/o/arrcontrol/", "https://arrcontrol.test/", "CHANGE_ME_OIDC_SECRET")]
    public void Unsafe_enabled_oidc_settings_fail_closed_without_echoing_the_secret(
        string authority,
        string publicUrl,
        string? clientSecret)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Oidc:Enabled"] = "true",
                ["Auth:Oidc:Authority"] = authority,
                ["Auth:Oidc:ClientId"] = ClientId,
                ["Auth:Oidc:ClientSecret"] = clientSecret,
                ["Auth:Oidc:AdministratorGroup"] = AdministratorGroup,
                ["App:PublicUrl"] = publicUrl,
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            OidcProviderSettings.Read(configuration));

        if (!string.IsNullOrEmpty(clientSecret))
        {
            Assert.DoesNotContain(clientSecret, exception.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Authentik_code_pkce_login_links_verified_admin_and_preserves_oidc_through_refresh_and_logout()
    {
        await using var scenario = await CreateScenarioAsync(
            bootstrapEmail: AdminEmail,
            bootstrapPassword: AdminPassword);

        var flow = await StartAuthorizationAsync(scenario, "/oidc-complete?view=security");

        Assert.Equal(AuthentikBackchannelHandler.AuthorizationEndpoint, flow.AuthorizationUri.GetLeftPart(UriPartial.Path));
        Assert.Equal("code", flow.Query["response_type"].ToString());
        Assert.Equal("query", flow.Query["response_mode"].ToString());
        Assert.Equal(ClientId, flow.Query["client_id"].ToString());
        Assert.Equal(
            "https://arrcontrol.test/auth/oidc/callback",
            flow.Query["redirect_uri"].ToString());
        Assert.Equal("S256", flow.Query["code_challenge_method"].ToString());
        Assert.False(string.IsNullOrWhiteSpace(flow.Query["code_challenge"].ToString()));
        Assert.False(string.IsNullOrWhiteSpace(flow.State));
        Assert.False(string.IsNullOrWhiteSpace(flow.Nonce));
        Assert.Equal(
            ["email", "openid", "profile"],
            flow.Query["scope"].ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Order(StringComparer.Ordinal)
                .ToArray());

        scenario.Provider.TokenProfile = new AuthentikTokenProfile(
            "authentik-admin-subject",
            AdminEmail,
            EmailVerified: true,
            [AdministratorGroup]);
        using var callback = await CompleteAuthorizationAsync(scenario, flow);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/oidc-complete?view=security", callback.Headers.Location?.OriginalString);
        Assert.True(callback.Headers.CacheControl?.NoStore);
        Assert.Null(scenario.Provider.ProtocolViolation);
        Assert.Equal(1, scenario.Provider.TokenRequests);
        Assert.Equal(
            flow.Query["code_challenge"].ToString(),
            Base64UrlTextEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(
                Assert.IsType<string>(scenario.Provider.LastCodeVerifier)))));
        Assert.Equal(
            "https://arrcontrol.test/auth/oidc/callback",
            scenario.Provider.LastTokenRedirectUri);

        var cookies = ReadSessionCookies(callback);
        AssertOpaqueSecureSessionCookies(cookies);

        await using (var context = CreateDbContext(scenario.ConnectionString))
        {
            var user = Assert.Single(await context.Set<UserEntity>()
                .AsNoTracking()
                .Where(x => x.NormalizedEmail == AdminEmail.ToUpperInvariant())
                .ToListAsync());
            var identity = Assert.Single(await context.Set<ExternalIdentityEntity>()
                .AsNoTracking()
                .Where(x => x.UserId == user.Id)
                .ToListAsync());
            Assert.Equal(AuthentikBackchannelHandler.Issuer, identity.Issuer);
            Assert.Equal("authentik-admin-subject", identity.Subject);

            var role = Assert.Single(await context.Set<ExternalIdentityRoleEntity>()
                .AsNoTracking()
                .Where(x => x.ExternalIdentityId == identity.Id)
                .Join(
                    context.Set<RoleEntity>(),
                    assignment => assignment.RoleId,
                    candidateRole => candidateRole.Id,
                    (_, candidateRole) => candidateRole)
                .ToListAsync());
            Assert.Equal(AdministratorRole, role.Name);

            var session = Assert.Single(await context.Set<UserSessionEntity>()
                .AsNoTracking()
                .ToListAsync());
            Assert.Equal(LocalIdentityConstants.OidcAuthenticationMethod, session.AuthenticationMethod);
            Assert.False(session.AccessTokenHash.AsSpan().SequenceEqual(WebEncoders.Base64UrlDecode(cookies.Access)));
            Assert.False(session.RefreshTokenHash.AsSpan().SequenceEqual(WebEncoders.Base64UrlDecode(cookies.Refresh)));
        }

        using (var protectedApi = await SendWithCookiesAsync(
                   scenario.Client,
                   HttpMethod.Get,
                   "/api/v1/instances",
                   (LocalAuthApiConstants.AccessCookieName, cookies.Access)))
        {
            Assert.Equal(HttpStatusCode.OK, protectedApi.StatusCode);
        }

        using var refresh = await SendMutationAsync(
            scenario.Client,
            "/api/v1/auth/refresh",
            cookies.Csrf,
            (LocalAuthApiConstants.RefreshCookieName, cookies.Refresh));
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var refreshedCookies = ReadSessionCookies(refresh);
        Assert.NotEqual(cookies.Access, refreshedCookies.Access);
        Assert.NotEqual(cookies.Refresh, refreshedCookies.Refresh);
        Assert.NotEqual(cookies.Csrf, refreshedCookies.Csrf);

        Guid tokenFamilyId;
        await using (var context = CreateDbContext(scenario.ConnectionString))
        {
            var sessions = await context.Set<UserSessionEntity>()
                .AsNoTracking()
                .OrderBy(x => x.CreatedAt)
                .ToListAsync();
            Assert.Equal(2, sessions.Count);
            Assert.All(
                sessions,
                session => Assert.Equal(
                    LocalIdentityConstants.OidcAuthenticationMethod,
                    session.AuthenticationMethod));
            Assert.NotNull(sessions[0].RevokedAt);
            Assert.Equal(sessions[1].Id, sessions[0].ReplacedBySessionId);
            tokenFamilyId = sessions[1].TokenFamilyId;
        }

        using var logout = await SendMutationAsync(
            scenario.Client,
            "/api/v1/auth/oidc/logout?returnUrl=%2Foidc-signed-out",
            refreshedCookies.Csrf,
            (LocalAuthApiConstants.AccessCookieName, refreshedCookies.Access),
            (LocalAuthApiConstants.RefreshCookieName, refreshedCookies.Refresh));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        var logoutUri = Assert.IsType<Uri>(logout.Headers.Location);
        Assert.Equal(AuthentikBackchannelHandler.EndSessionEndpoint, logoutUri.GetLeftPart(UriPartial.Path));
        var logoutQuery = QueryHelpers.ParseQuery(logoutUri.Query);
        Assert.Equal(scenario.Provider.LastIssuedIdToken, logoutQuery["id_token_hint"].ToString());
        Assert.Equal(
            "https://arrcontrol.test/auth/oidc/signed-out",
            logoutQuery["post_logout_redirect_uri"].ToString());
        Assert.False(string.IsNullOrWhiteSpace(logoutQuery["state"].ToString()));
        AssertDeletedSessionCookies(logout);

        await using (var context = CreateDbContext(scenario.ConnectionString))
        {
            var family = await context.Set<UserSessionEntity>()
                .AsNoTracking()
                .Where(x => x.TokenFamilyId == tokenFamilyId)
                .ToListAsync();
            Assert.Equal(2, family.Count);
            Assert.All(family, session => Assert.NotNull(session.RevokedAt));
        }

        using var signedOut = await scenario.Client.GetAsync(
            "/auth/oidc/signed-out?state="
            + Uri.EscapeDataString(logoutQuery["state"].ToString()));
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal("/oidc-signed-out", signedOut.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Unreadable_logout_hint_still_revokes_the_local_oidc_family()
    {
        await using var scenario = await CreateScenarioAsync(
            bootstrapEmail: AdminEmail,
            bootstrapPassword: AdminPassword);
        var flow = await StartAuthorizationAsync(scenario, "/");
        scenario.Provider.TokenProfile = new AuthentikTokenProfile(
            "unreadable-logout-subject",
            AdminEmail,
            EmailVerified: true,
            [AdministratorGroup]);
        using var callback = await CompleteAuthorizationAsync(scenario, flow);
        var cookies = ReadSessionCookies(callback);

        Guid tokenFamilyId;
        await using (var context = CreateDbContext(scenario.ConnectionString))
        {
            var session = Assert.Single(await context.Set<UserSessionEntity>().ToListAsync());
            tokenFamilyId = session.TokenFamilyId;
            var logoutContext = Assert.Single(
                await context.Set<OidcSessionContextEntity>().ToListAsync());
            logoutContext.ProtectedIdToken = "not-a-data-protection-payload";
            await context.SaveChangesAsync();
        }

        using var logout = await SendMutationAsync(
            scenario.Client,
            "/api/v1/auth/oidc/logout?returnUrl=%2Flocal-logout-complete",
            cookies.Csrf,
            (LocalAuthApiConstants.AccessCookieName, cookies.Access),
            (LocalAuthApiConstants.RefreshCookieName, cookies.Refresh));

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/local-logout-complete", logout.Headers.Location?.OriginalString);
        AssertDeletedSessionCookies(logout);
        await using var verificationContext = CreateDbContext(scenario.ConnectionString);
        var family = await verificationContext.Set<UserSessionEntity>()
            .AsNoTracking()
            .Where(x => x.TokenFamilyId == tokenFamilyId)
            .ToListAsync();
        Assert.NotEmpty(family);
        Assert.All(family, session => Assert.NotNull(session.RevokedAt));
    }

    [Fact]
    public async Task Disabled_oidc_fails_closed_without_attempting_discovery()
    {
        await using var scenario = await CreateScenarioAsync(
            bootstrapEmail: null,
            bootstrapPassword: null,
            oidcEnabled: false);

        using var status = await scenario.Client.GetAsync("/api/v1/auth/oidc/status");
        status.EnsureSuccessStatusCode();
        using var statusJson = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.False(statusJson.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, statusJson.RootElement.GetProperty("loginUrl").ValueKind);

        using var login = await scenario.Client.GetAsync("/api/v1/auth/oidc/login");
        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
        Assert.Equal("oidc_not_enabled", await ReadProblemCodeAsync(login));

        using var callback = await scenario.Client.GetAsync(
            "/auth/oidc/callback?code=untrusted&state=untrusted");
        Assert.Equal(HttpStatusCode.NotFound, callback.StatusCode);
        Assert.Empty(scenario.Provider.Requests);
    }

    [Fact]
    public async Task Unavailable_discovery_cannot_start_an_authorization_flow()
    {
        await using var scenario = await CreateScenarioAsync(
            bootstrapEmail: AdminEmail,
            bootstrapPassword: AdminPassword);
        scenario.Provider.FailDiscovery = true;

        using var response = await scenario.Client.GetAsync("/api/v1/auth/oidc/login");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(scenario.Provider.DiscoveryRequests >= 1);
        Assert.DoesNotContain(
            scenario.Provider.Requests,
            uri => uri.GetLeftPart(UriPartial.Path)
                == AuthentikBackchannelHandler.TokenEndpoint);
        await AssertNoOidcPersistenceAsync(scenario.ConnectionString, expectedUsers: 1);
    }

    [Fact]
    public async Task Discovery_cannot_expand_the_exact_configured_issuer()
    {
        await using var scenario = await CreateScenarioAsync(
            bootstrapEmail: AdminEmail,
            bootstrapPassword: AdminPassword);
        scenario.Provider.AdvertisedIssuer =
            "https://different-issuer.example/application/o/arrcontrol/";
        var flow = await StartAuthorizationAsync(scenario, "/");
        scenario.Provider.TokenProfile = new AuthentikTokenProfile(
            "discovery-issuer-subject",
            AdminEmail,
            EmailVerified: true,
            [AdministratorGroup]);

        using var callback = await CompleteAuthorizationAsync(scenario, flow);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(FailureRedirect, callback.Headers.Location?.OriginalString);
        AssertNoSessionCookies(callback);
        await AssertNoOidcPersistenceAsync(scenario.ConnectionString, expectedUsers: 1);
    }

    [Fact]
    public async Task Runtime_openapi_describes_anonymous_oidc_entrypoints_and_csrf_protected_logout()
    {
        await using var scenario = await CreateScenarioAsync(
            bootstrapEmail: null,
            bootstrapPassword: null);

        using var response = await scenario.Client.GetAsync("/api/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");

        var status = paths.GetProperty(OidcAuthenticationApi.StatusPath).GetProperty("get");
        AssertAnonymousOperation(status);
        AssertResponseStatuses(status, "200");

        var login = paths.GetProperty(OidcAuthenticationApi.LoginPath).GetProperty("get");
        AssertAnonymousOperation(login);
        AssertResponseStatuses(login, "302", "400", "404", "429", "500");

        var logout = paths.GetProperty(OidcAuthenticationApi.LogoutPath).GetProperty("post");
        AssertAnonymousOperation(logout);
        AssertResponseStatuses(logout, "302", "400", "403", "404", "429", "500");
        var csrf = Assert.Single(
            logout.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString()
                == LocalAuthApiConstants.CsrfHeaderName);
        Assert.Equal("header", csrf.GetProperty("in").GetString());
        Assert.True(csrf.GetProperty("required").GetBoolean());

        var statusSchemaReference = status.GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString();
        Assert.False(string.IsNullOrWhiteSpace(statusSchemaReference));
        var statusSchemaName = statusSchemaReference!.Split('/')[^1];
        var statusSchema = document.RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(statusSchemaName);
        Assert.False(statusSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.True(statusSchema.GetProperty("properties").TryGetProperty("enabled", out _));
        Assert.True(statusSchema.GetProperty("properties").TryGetProperty("loginUrl", out _));
        Assert.Empty(scenario.Provider.Requests);
    }

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("//evil.example/steal")]
    [InlineData("/\\evil.example/steal")]
    [InlineData("/valid-looking\\evil")]
    public async Task External_or_ambiguous_return_urls_are_rejected_before_discovery(string returnUrl)
    {
        await using var scenario = await CreateScenarioAsync(
            bootstrapEmail: AdminEmail,
            bootstrapPassword: AdminPassword);

        using var response = await scenario.Client.GetAsync(
            "/api/v1/auth/oidc/login?returnUrl=" + Uri.EscapeDataString(returnUrl));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_return_url", await ReadProblemCodeAsync(response));
        Assert.Empty(scenario.Provider.Requests);
    }

    [Fact]
    public async Task Unverified_first_identity_cannot_create_a_user_link_or_session()
    {
        await using var scenario = await CreateScenarioAsync(
            bootstrapEmail: null,
            bootstrapPassword: null);
        var flow = await StartAuthorizationAsync(scenario, "/");
        scenario.Provider.TokenProfile = new AuthentikTokenProfile(
            "unverified-subject",
            "new-user@example.invalid",
            EmailVerified: false,
            []);

        using var callback = await CompleteAuthorizationAsync(scenario, flow);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(FailureRedirect, callback.Headers.Location?.OriginalString);
        AssertNoSessionCookies(callback);
        await AssertNoOidcPersistenceAsync(scenario.ConnectionString, expectedUsers: 0);
    }

    [Theory]
    [InlineData(AuthentikTokenFault.WrongAudience)]
    [InlineData(AuthentikTokenFault.WrongSignature)]
    [InlineData(AuthentikTokenFault.WrongNonce)]
    [InlineData(AuthentikTokenFault.WrongIssuer)]
    [InlineData(AuthentikTokenFault.Expired)]
    [InlineData(AuthentikTokenFault.StringEmailVerified)]
    [InlineData(AuthentikTokenFault.NonStringGroup)]
    [InlineData(AuthentikTokenFault.NumericSubject)]
    [InlineData(AuthentikTokenFault.NullCharacterSubject)]
    public async Task Invalid_id_tokens_fail_closed_without_link_or_session(AuthentikTokenFault fault)
    {
        await using var scenario = await CreateScenarioAsync(
            bootstrapEmail: AdminEmail,
            bootstrapPassword: AdminPassword);
        var flow = await StartAuthorizationAsync(scenario, "/");
        scenario.Provider.TokenProfile = new AuthentikTokenProfile(
            $"invalid-{fault}",
            AdminEmail,
            EmailVerified: true,
            [AdministratorGroup],
            fault);

        using var callback = await CompleteAuthorizationAsync(scenario, flow);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(FailureRedirect, callback.Headers.Location?.OriginalString);
        AssertNoSessionCookies(callback);
        await AssertNoOidcPersistenceAsync(scenario.ConnectionString, expectedUsers: 1);
        await using var context = CreateDbContext(scenario.ConnectionString);
        var auditEvent = Assert.Single(await context.Set<AuditEventEntity>()
            .AsNoTracking()
            .Where(x => x.Action == "identity.oidc_login"
                && x.Outcome == "protocol_failed")
            .ToListAsync());
        Assert.Equal("anonymous", auditEvent.ActorType);
    }

    [Fact]
    public async Task Tampered_state_fails_closed_before_token_exchange()
    {
        await using var scenario = await CreateScenarioAsync(
            bootstrapEmail: AdminEmail,
            bootstrapPassword: AdminPassword);
        var flow = await StartAuthorizationAsync(scenario, "/");
        var tamperedStateCharacters = flow.State.ToCharArray();
        var tamperedIndex = tamperedStateCharacters.Length / 2;
        tamperedStateCharacters[tamperedIndex] =
            tamperedStateCharacters[tamperedIndex] == 'A' ? 'B' : 'A';
        var tamperedState = new string(tamperedStateCharacters);

        using var callback = await SendCallbackAsync(
            scenario.Client,
            flow,
            tamperedState);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(FailureRedirect, callback.Headers.Location?.OriginalString);
        Assert.Equal(0, scenario.Provider.TokenRequests);
        AssertNoSessionCookies(callback);
        await AssertNoOidcPersistenceAsync(scenario.ConnectionString, expectedUsers: 1);
    }

    [Fact]
    public async Task Missing_correlation_cookie_fails_closed_before_token_exchange()
    {
        await using var scenario = await CreateScenarioAsync(
            bootstrapEmail: AdminEmail,
            bootstrapPassword: AdminPassword);
        var flow = await StartAuthorizationAsync(scenario, "/");
        var nonceCookie = flow.ProtocolCookieHeader
            .Split("; ", StringSplitOptions.RemoveEmptyEntries)
            .Single(cookie => cookie.StartsWith(
                "__Host-arrcontrol_oidc_nonce.",
                StringComparison.Ordinal));
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/auth/oidc/callback?code=" + Uri.EscapeDataString(flow.Code)
            + "&state=" + Uri.EscapeDataString(flow.State));
        request.Headers.TryAddWithoutValidation("Cookie", nonceCookie);

        using var callback = await scenario.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(FailureRedirect, callback.Headers.Location?.OriginalString);
        Assert.Equal(0, scenario.Provider.TokenRequests);
        AssertNoSessionCookies(callback);
        await AssertNoOidcPersistenceAsync(scenario.ConnectionString, expectedUsers: 1);
    }

    [Fact]
    public async Task Invalid_callbacks_are_rate_limited_before_unbounded_audit_writes()
    {
        await using var scenario = await CreateScenarioAsync(
            bootstrapEmail: null,
            bootstrapPassword: null,
            oidcProtocolRequestLimit: 10);
        Assert.Equal(
            10,
            scenario.Services.GetRequiredService<LocalAuthTransportSettings>()
                .SessionMutationRequestLimit);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var rejected = await scenario.Client.GetAsync(
                $"/auth/oidc/callback?code=invalid&state=invalid-{attempt}");
            Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
            Assert.Equal(FailureRedirect, rejected.Headers.Location?.OriginalString);
        }

        using var limited = await scenario.Client.GetAsync(
            "/auth/oidc/callback?code=invalid&state=over-limit");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("authentication_rate_limited", await ReadProblemCodeAsync(limited));

        await using var context = CreateDbContext(scenario.ConnectionString);
        Assert.Equal(
            10,
            await context.Set<AuditEventEntity>()
                .AsNoTracking()
                .CountAsync(x => x.Action == "identity.oidc_login"
                    && x.Outcome == "protocol_failed"));
    }

    [Fact]
    public async Task Replayed_state_and_protocol_cookies_cannot_create_a_second_session()
    {
        await using var scenario = await CreateScenarioAsync(
            bootstrapEmail: AdminEmail,
            bootstrapPassword: AdminPassword);
        var flow = await StartAuthorizationAsync(scenario, "/first-login");
        scenario.Provider.TokenProfile = new AuthentikTokenProfile(
            "replay-subject",
            AdminEmail,
            EmailVerified: true,
            [AdministratorGroup]);

        using var first = await CompleteAuthorizationAsync(scenario, flow);
        Assert.Equal("/first-login", first.Headers.Location?.OriginalString);
        Assert.NotEmpty(ReadSetCookieHeaders(first, LocalAuthApiConstants.AccessCookieName));

        using var replay = await CompleteAuthorizationAsync(scenario, flow);

        Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
        Assert.Equal(FailureRedirect, replay.Headers.Location?.OriginalString);
        AssertNoSessionCookies(replay);
        await using var context = CreateDbContext(scenario.ConnectionString);
        Assert.Single(await context.Set<ExternalIdentityEntity>().AsNoTracking().ToListAsync());
        Assert.Single(await context.Set<UserSessionEntity>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Authentik_jwks_rollover_refreshes_keys_for_the_next_authorization()
    {
        await using var scenario = await CreateScenarioAsync(
            bootstrapEmail: AdminEmail,
            bootstrapPassword: AdminPassword);
        var flow = await StartAuthorizationAsync(scenario, "/after-rollover");
        Assert.Equal(1, scenario.Provider.JwksRequests);

        var options = scenario.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OidcAuthenticationApi.AuthenticationScheme);
        var configurationManager = Assert.IsType<ConfigurationManager<OpenIdConnectConfiguration>>(
            options.ConfigurationManager);
        configurationManager.RefreshInterval =
            ConfigurationManager<OpenIdConnectConfiguration>.MinimumRefreshInterval;
        await Task.Delay(
            ConfigurationManager<OpenIdConnectConfiguration>.MinimumRefreshInterval
            + TimeSpan.FromMilliseconds(250));
        scenario.Provider.RotateSigningKey();
        scenario.Provider.TokenProfile = new AuthentikTokenProfile(
            "rotated-key-subject",
            AdminEmail,
            EmailVerified: true,
            [AdministratorGroup]);
        using var callback = await CompleteAuthorizationAsync(scenario, flow);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(FailureRedirect, callback.Headers.Location?.OriginalString);
        AssertNoSessionCookies(callback);

        // ASP.NET Core requests a metadata refresh after rejecting an unknown key.
        // The authorization code is already consumed, so recovery uses a new flow.
        var retryFlow = await StartAuthorizationAsync(scenario, "/after-rollover");
        Assert.True(scenario.Provider.JwksRequests >= 2);
        using var retryCallback = await CompleteAuthorizationAsync(scenario, retryFlow);

        Assert.Equal(HttpStatusCode.Redirect, retryCallback.StatusCode);
        Assert.Equal("/after-rollover", retryCallback.Headers.Location?.OriginalString);
        Assert.NotEmpty(ReadSetCookieHeaders(
            retryCallback,
            LocalAuthApiConstants.AccessCookieName));
        await using var context = CreateDbContext(scenario.ConnectionString);
        Assert.Single(await context.Set<ExternalIdentityEntity>().AsNoTracking().ToListAsync());
        Assert.Single(await context.Set<UserSessionEntity>().AsNoTracking().ToListAsync());
    }

    private async Task<OidcApiScenario> CreateScenarioAsync(
        string? bootstrapEmail,
        string? bootstrapPassword,
        bool oidcEnabled = true,
        int? oidcProtocolRequestLimit = null)
    {
        var connectionString = await databaseFixture.CreateMigratedSchemaAsync();
        var provider = new AuthentikBackchannelHandler(ClientId, ClientSecret);
        var factory = new OidcApiFactory(
            connectionString,
            bootstrapEmail,
            bootstrapPassword,
            provider,
            oidcEnabled,
            oidcProtocolRequestLimit);
        try
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = PublicOrigin,
                HandleCookies = false,
            });
            return new OidcApiScenario(factory, client, provider, connectionString);
        }
        catch
        {
            factory.Dispose();
            provider.Dispose();
            throw;
        }
    }

    private static async Task<AuthorizationFlow> StartAuthorizationAsync(
        OidcApiScenario scenario,
        string returnUrl)
    {
        using var response = await scenario.Client.GetAsync(
            "/api/v1/auth/oidc/login?returnUrl=" + Uri.EscapeDataString(returnUrl));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var authorizationUri = Assert.IsType<Uri>(response.Headers.Location);
        var query = QueryHelpers.ParseQuery(authorizationUri.Query);
        var state = Assert.Single(query["state"])!;
        var nonce = Assert.Single(query["nonce"])!;
        var code = "authentik-code-" + Guid.NewGuid().ToString("N");
        scenario.Provider.BeginAuthorization(
            code,
            nonce,
            query["code_challenge"].ToString(),
            query["redirect_uri"].ToString());

        var protocolCookies = ReadSetCookieHeaders(response)
            .Where(header => header.StartsWith(
                    "__Host-arrcontrol_oidc_correlation.",
                    StringComparison.Ordinal)
                || header.StartsWith(
                    "__Host-arrcontrol_oidc_nonce.",
                    StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, protocolCookies.Length);
        Assert.All(protocolCookies, AssertSecureProtocolCookie);

        return new AuthorizationFlow(
            authorizationUri,
            query,
            state,
            nonce,
            code,
            string.Join("; ", protocolCookies.Select(ReadCookiePair)));
    }

    private static Task<HttpResponseMessage> CompleteAuthorizationAsync(
        OidcApiScenario scenario,
        AuthorizationFlow flow) =>
        SendCallbackAsync(scenario.Client, flow, flow.State);

    private static async Task<HttpResponseMessage> SendCallbackAsync(
        HttpClient client,
        AuthorizationFlow flow,
        string state)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/auth/oidc/callback?code=" + Uri.EscapeDataString(flow.Code)
            + "&state=" + Uri.EscapeDataString(state));
        request.Headers.TryAddWithoutValidation("Cookie", flow.ProtocolCookieHeader);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendMutationAsync(
        HttpClient client,
        string path,
        string csrf,
        params (string Name, string Value)[] cookies)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation(LocalAuthApiConstants.CsrfHeaderName, csrf);
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            string.Join(
                "; ",
                cookies.Prepend((
                        Name: LocalAuthApiConstants.CsrfCookieName,
                        Value: csrf))
                    .Select(cookie => $"{cookie.Name}={cookie.Value}")));
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendWithCookiesAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        params (string Name, string Value)[] cookies)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}")));
        return await client.SendAsync(request);
    }

    private static SessionCookies ReadSessionCookies(HttpResponseMessage response)
    {
        var accessHeader = Assert.Single(ReadSetCookieHeaders(
            response,
            LocalAuthApiConstants.AccessCookieName));
        var refreshHeader = Assert.Single(ReadSetCookieHeaders(
            response,
            LocalAuthApiConstants.RefreshCookieName));
        var csrfHeader = Assert.Single(ReadSetCookieHeaders(
            response,
            LocalAuthApiConstants.CsrfCookieName));
        return new SessionCookies(
            ReadCookieValue(accessHeader, LocalAuthApiConstants.AccessCookieName),
            ReadCookieValue(refreshHeader, LocalAuthApiConstants.RefreshCookieName),
            ReadCookieValue(csrfHeader, LocalAuthApiConstants.CsrfCookieName),
            accessHeader,
            refreshHeader,
            csrfHeader);
    }

    private static void AssertOpaqueSecureSessionCookies(SessionCookies cookies)
    {
        Assert.Equal(43, cookies.Access.Length);
        Assert.Equal(43, cookies.Refresh.Length);
        Assert.Equal(43, cookies.Csrf.Length);
        Assert.DoesNotContain('.', cookies.Access);
        Assert.DoesNotContain('.', cookies.Refresh);
        Assert.Equal(32, WebEncoders.Base64UrlDecode(cookies.Access).Length);
        Assert.Equal(32, WebEncoders.Base64UrlDecode(cookies.Refresh).Length);
        AssertSessionCookie(cookies.AccessHeader, httpOnly: true);
        AssertSessionCookie(cookies.RefreshHeader, httpOnly: true);
        AssertSessionCookie(cookies.CsrfHeader, httpOnly: false);
    }

    private static void AssertSessionCookie(string header, bool httpOnly)
    {
        Assert.Contains("path=/", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", header, StringComparison.OrdinalIgnoreCase);
        if (httpOnly)
        {
            Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.DoesNotContain("httponly", header, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertSecureProtocolCookie(string header)
    {
        Assert.Contains("path=/", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=none", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", header, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertDeletedSessionCookies(HttpResponseMessage response)
    {
        foreach (var (name, httpOnly) in new[]
                 {
                     (LocalAuthApiConstants.AccessCookieName, true),
                     (LocalAuthApiConstants.RefreshCookieName, true),
                     (LocalAuthApiConstants.CsrfCookieName, false),
                 })
        {
            var header = Assert.Single(ReadSetCookieHeaders(response, name));
            Assert.Empty(ReadCookieValue(header, name));
            Assert.Contains("expires=", header, StringComparison.OrdinalIgnoreCase);
            AssertSessionCookie(header, httpOnly);
        }
    }

    private static void AssertNoSessionCookies(HttpResponseMessage response)
    {
        Assert.Empty(ReadSetCookieHeaders(response, LocalAuthApiConstants.AccessCookieName));
        Assert.Empty(ReadSetCookieHeaders(response, LocalAuthApiConstants.RefreshCookieName));
    }

    private static string[] ReadSetCookieHeaders(
        HttpResponseMessage response,
        string? cookieName = null)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return [];
        }

        var headers = values.ToArray();
        return cookieName is null
            ? headers
            : headers.Where(value => value.StartsWith(cookieName + "=", StringComparison.Ordinal))
                .ToArray();
    }

    private static string ReadCookiePair(string setCookieHeader) =>
        setCookieHeader.Split(';', 2)[0];

    private static string ReadCookieValue(string setCookieHeader, string cookieName) =>
        ReadCookiePair(setCookieHeader)[(cookieName.Length + 1)..];

    private static async Task<string> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString()!;
    }

    private static void AssertAnonymousOperation(JsonElement operation)
    {
        var requirements = operation.GetProperty("security").EnumerateArray().ToArray();
        var requirement = Assert.Single(requirements);
        Assert.Empty(requirement.EnumerateObject());
    }

    private static void AssertResponseStatuses(
        JsonElement operation,
        params string[] expectedStatuses)
    {
        var responses = operation.GetProperty("responses");
        foreach (var status in expectedStatuses)
        {
            Assert.True(
                responses.TryGetProperty(status, out _),
                $"Missing OpenAPI response {status}.");
        }
    }

    private static async Task AssertNoOidcPersistenceAsync(
        string connectionString,
        int expectedUsers)
    {
        await using var context = CreateDbContext(connectionString);
        Assert.Equal(expectedUsers, await context.Set<UserEntity>().AsNoTracking().CountAsync());
        Assert.Empty(await context.Set<ExternalIdentityEntity>().AsNoTracking().ToListAsync());
        Assert.Empty(await context.Set<ExternalIdentityRoleEntity>().AsNoTracking().ToListAsync());
        Assert.Empty(await context.Set<UserSessionEntity>().AsNoTracking().ToListAsync());
        Assert.Empty(await context.Set<OidcSessionContextEntity>().AsNoTracking().ToListAsync());
    }

    private static ArrControlDbContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(connectionString)
            .Options);

    private sealed record AuthorizationFlow(
        Uri AuthorizationUri,
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> Query,
        string State,
        string Nonce,
        string Code,
        string ProtocolCookieHeader);

    private sealed record SessionCookies(
        string Access,
        string Refresh,
        string Csrf,
        string AccessHeader,
        string RefreshHeader,
        string CsrfHeader);

    private sealed class OidcApiScenario(
        OidcApiFactory factory,
        HttpClient client,
        AuthentikBackchannelHandler provider,
        string connectionString) : IAsyncDisposable
    {
        private OidcApiFactory Factory { get; } = factory;

        public HttpClient Client { get; } = client;

        public AuthentikBackchannelHandler Provider { get; } = provider;

        public string ConnectionString { get; } = connectionString;

        public IServiceProvider Services => Factory.Services;

        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            Factory.Dispose();
            Provider.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OidcApiFactory(
        string connectionString,
        string? bootstrapEmail,
        string? bootstrapPassword,
        AuthentikBackchannelHandler provider,
        bool oidcEnabled,
        int? oidcProtocolRequestLimit) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("App:PublicUrl", PublicOrigin.AbsoluteUri);
            builder.UseSetting("Auth:Oidc:Enabled", oidcEnabled.ToString());
            builder.UseSetting("Auth:Oidc:Authority", AuthentikBackchannelHandler.Issuer);
            builder.UseSetting("Auth:Oidc:ClientId", ClientId);
            builder.UseSetting("Auth:Oidc:ClientSecret", ClientSecret);
            builder.UseSetting("Auth:Oidc:AdministratorGroup", AdministratorGroup);
            if (oidcProtocolRequestLimit is not null)
            {
                builder.UseSetting(
                    "Auth:Local:SessionMutationRequestLimit",
                    oidcProtocolRequestLimit.Value.ToString());
            }

            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Database"] = connectionString,
                    ["Bootstrap:AdminEmail"] = bootstrapEmail,
                    ["Bootstrap:AdminPassword"] = bootstrapPassword,
                    ["App:PublicUrl"] = PublicOrigin.AbsoluteUri,
                    ["Auth:Oidc:Enabled"] = oidcEnabled.ToString(),
                    ["Auth:Oidc:Authority"] = AuthentikBackchannelHandler.Issuer,
                    ["Auth:Oidc:ClientId"] = ClientId,
                    ["Auth:Oidc:ClientSecret"] = ClientSecret,
                    ["Auth:Oidc:AdministratorGroup"] = AdministratorGroup,
                    ["Auth:Local:SessionMutationRequestLimit"] =
                        oidcProtocolRequestLimit?.ToString(),
                }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter>(
                    new FixedRemoteIpStartupFilter(IPAddress.Parse("203.0.113.84")));
                if (oidcEnabled)
                {
                    services.Configure<OpenIdConnectOptions>(
                        OidcAuthenticationApi.AuthenticationScheme,
                        options => options.BackchannelHttpHandler = provider);
                }
            });
        }
    }

    private sealed class FixedRemoteIpStartupFilter(IPAddress remoteIpAddress) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            application =>
            {
                application.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = remoteIpAddress;
                    await nextMiddleware(context);
                });
                next(application);
            };
    }
}

public enum AuthentikTokenFault
{
    None,
    WrongAudience,
    WrongSignature,
    WrongNonce,
    WrongIssuer,
    Expired,
    StringEmailVerified,
    NonStringGroup,
    NumericSubject,
    NullCharacterSubject,
}

public sealed record AuthentikTokenProfile(
    string Subject,
    string Email,
    bool EmailVerified,
    IReadOnlyCollection<string> Groups,
    AuthentikTokenFault Fault = AuthentikTokenFault.None);

public sealed class AuthentikBackchannelHandler : HttpMessageHandler
{
    public const string Issuer = "https://authentik.test/application/o/arrcontrol/";
    public const string DiscoveryEndpoint =
        "https://authentik.test/application/o/arrcontrol/.well-known/openid-configuration";
    public const string AuthorizationEndpoint = "https://authentik.test/application/o/authorize/";
    public const string TokenEndpoint = "https://authentik.test/application/o/token/";
    public const string JwksEndpoint = "https://authentik.test/application/o/arrcontrol/jwks/";
    public const string EndSessionEndpoint =
        "https://authentik.test/application/o/arrcontrol/end-session/";

    private readonly string clientId;
    private readonly string clientSecret;
    private readonly ConcurrentDictionary<string, byte> consumedAuthorizationCodes =
        new(StringComparer.Ordinal);
    private readonly List<RSA> ownedKeys = [];
    private RSA signingRsa;
    private RsaSecurityKey signingKey;
    private string? expectedCode;
    private string? expectedNonce;
    private string? expectedCodeChallenge;
    private string? expectedRedirectUri;

    public AuthentikBackchannelHandler(string clientId, string clientSecret)
    {
        this.clientId = clientId;
        this.clientSecret = clientSecret;
        (signingRsa, signingKey) = CreateKey();
    }

    public ConcurrentQueue<Uri> Requests { get; } = new();

    public AuthentikTokenProfile TokenProfile { get; set; } = new(
        "default-subject",
        "default@example.invalid",
        EmailVerified: true,
        []);

    public bool FailDiscovery { get; set; }

    public string AdvertisedIssuer { get; set; } = Issuer;

    public int DiscoveryRequests { get; private set; }

    public int JwksRequests { get; private set; }

    public int TokenRequests { get; private set; }

    public string? LastCodeVerifier { get; private set; }

    public string? LastTokenRedirectUri { get; private set; }

    public string? LastIssuedIdToken { get; private set; }

    public Exception? ProtocolViolation { get; private set; }

    public void BeginAuthorization(
        string code,
        string nonce,
        string codeChallenge,
        string redirectUri)
    {
        expectedCode = code;
        expectedNonce = nonce;
        expectedCodeChallenge = codeChallenge;
        expectedRedirectUri = redirectUri;
    }

    public void RotateSigningKey()
    {
        (signingRsa, signingKey) = CreateKey();
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var requestUri = request.RequestUri
            ?? throw new InvalidOperationException("The OIDC backchannel request URI is missing.");
        Requests.Enqueue(requestUri);
        var endpoint = requestUri.GetLeftPart(UriPartial.Path);
        if (endpoint == DiscoveryEndpoint)
        {
            DiscoveryRequests++;
            if (FailDiscovery)
            {
                return JsonResponse(
                    HttpStatusCode.ServiceUnavailable,
                    new { error = "temporarily_unavailable" });
            }

            return JsonResponse(HttpStatusCode.OK, new
            {
                issuer = AdvertisedIssuer,
                authorization_endpoint = AuthorizationEndpoint,
                token_endpoint = TokenEndpoint,
                jwks_uri = JwksEndpoint,
                end_session_endpoint = EndSessionEndpoint,
                response_types_supported = new[] { "code" },
                response_modes_supported = new[] { "query" },
                subject_types_supported = new[] { "public" },
                id_token_signing_alg_values_supported = new[] { "RS256" },
                scopes_supported = new[] { "openid", "profile", "email" },
                code_challenge_methods_supported = new[] { "S256" },
                token_endpoint_auth_methods_supported = new[] { "client_secret_post" },
            });
        }

        if (endpoint == JwksEndpoint)
        {
            JwksRequests++;
            var parameters = signingRsa.ExportParameters(includePrivateParameters: false);
            return JsonResponse(HttpStatusCode.OK, new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "RSA",
                        use = "sig",
                        alg = SecurityAlgorithms.RsaSha256,
                        kid = signingKey.KeyId,
                        n = Base64UrlTextEncoder.Encode(parameters.Modulus!),
                        e = Base64UrlTextEncoder.Encode(parameters.Exponent!),
                    },
                },
            });
        }

        if (endpoint == TokenEndpoint)
        {
            TokenRequests++;
            try
            {
                return await CreateTokenResponseAsync(request, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ProtocolViolation = exception;
                return JsonResponse(
                    HttpStatusCode.BadRequest,
                    new { error = "invalid_request" });
            }
        }

        return JsonResponse(HttpStatusCode.NotFound, new { error = "not_found" });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var key in ownedKeys)
            {
                key.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private async Task<HttpResponseMessage> CreateTokenResponseAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || request.Content is null)
        {
            throw new InvalidOperationException("Authentik token exchange must use POST form data.");
        }

        var form = QueryHelpers.ParseQuery(
            await request.Content.ReadAsStringAsync(cancellationToken));
        RequireEqual("authorization_code", form["grant_type"].ToString(), "grant_type");
        RequireEqual(expectedCode, form["code"].ToString(), "code");
        RequireEqual(expectedRedirectUri, form["redirect_uri"].ToString(), "redirect_uri");
        RequireEqual(clientId, form["client_id"].ToString(), "client_id");
        RequireEqual(clientSecret, form["client_secret"].ToString(), "client_secret");
        LastCodeVerifier = form["code_verifier"].ToString();
        LastTokenRedirectUri = form["redirect_uri"].ToString();
        if (string.IsNullOrWhiteSpace(LastCodeVerifier))
        {
            throw new InvalidOperationException("The PKCE code_verifier is missing.");
        }

        var actualChallenge = Base64UrlTextEncoder.Encode(
            SHA256.HashData(Encoding.ASCII.GetBytes(LastCodeVerifier)));
        RequireEqual(expectedCodeChallenge, actualChallenge, "code_challenge");

        if (!consumedAuthorizationCodes.TryAdd(form["code"].ToString(), 0))
        {
            return JsonResponse(
                HttpStatusCode.BadRequest,
                new { error = "invalid_grant" });
        }

        LastIssuedIdToken = CreateIdToken();
        return JsonResponse(HttpStatusCode.OK, new
        {
            access_token = "authentik-access-token",
            token_type = "Bearer",
            expires_in = 300,
            id_token = LastIssuedIdToken,
        });
    }

    private string CreateIdToken()
    {
        var now = DateTimeOffset.UtcNow;
        var issuer = TokenProfile.Fault == AuthentikTokenFault.WrongIssuer
            ? "https://different-issuer.example/application/o/arrcontrol/"
            : AdvertisedIssuer;
        var audience = TokenProfile.Fault == AuthentikTokenFault.WrongAudience
            ? "different-client"
            : clientId;
        var nonce = TokenProfile.Fault == AuthentikTokenFault.WrongNonce
            ? "different-nonce"
            : expectedNonce;
        var notBefore = TokenProfile.Fault == AuthentikTokenFault.Expired
            ? now.AddMinutes(-10)
            : now.AddSeconds(-5);
        var expires = TokenProfile.Fault == AuthentikTokenFault.Expired
            ? now.AddMinutes(-5)
            : now.AddMinutes(5);
        object emailVerified = TokenProfile.Fault == AuthentikTokenFault.StringEmailVerified
            ? "true"
            : TokenProfile.EmailVerified;
        object groups = TokenProfile.Fault == AuthentikTokenFault.NonStringGroup
            ? true
            : TokenProfile.Groups.ToArray();
        object subject = TokenProfile.Fault switch
        {
            AuthentikTokenFault.NumericSubject => 42,
            AuthentikTokenFault.NullCharacterSubject => "\0",
            _ => TokenProfile.Subject,
        };

        RSA? attackerRsa = null;
        RsaSecurityKey tokenKey;
        if (TokenProfile.Fault == AuthentikTokenFault.WrongSignature)
        {
            attackerRsa = RSA.Create(2048);
            ownedKeys.Add(attackerRsa);
            tokenKey = new RsaSecurityKey(attackerRsa) { KeyId = signingKey.KeyId };
        }
        else
        {
            tokenKey = signingKey;
        }

        var payload = new JwtPayload
        {
            { JwtRegisteredClaimNames.Iss, issuer },
            { JwtRegisteredClaimNames.Aud, audience },
            { JwtRegisteredClaimNames.Sub, subject },
            { JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds() },
            { JwtRegisteredClaimNames.Nbf, notBefore.ToUnixTimeSeconds() },
            { JwtRegisteredClaimNames.Exp, expires.ToUnixTimeSeconds() },
            { "nonce", nonce },
            { "email", TokenProfile.Email },
            { "email_verified", emailVerified },
            { "groups", groups },
        };
        var token = new JwtSecurityToken(
            new JwtHeader(new SigningCredentials(tokenKey, SecurityAlgorithms.RsaSha256)),
            payload);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private (RSA Rsa, RsaSecurityKey Key) CreateKey()
    {
        var rsa = RSA.Create(2048);
        ownedKeys.Add(rsa);
        var key = new RsaSecurityKey(rsa)
        {
            KeyId = "authentik-key-" + Guid.NewGuid().ToString("N"),
        };
        return (rsa, key);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, object payload) =>
        new(statusCode)
        {
            Content = JsonContent.Create(payload),
        };

    private static void RequireEqual(string? expected, string? actual, string field)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected OIDC {field} value.");
        }
    }
}
