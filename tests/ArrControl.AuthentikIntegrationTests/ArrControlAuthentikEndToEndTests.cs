using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using ArrControl.Api.Identity;
using ArrControl.Application.Identity;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Identity;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Playwright;
using Testcontainers.PostgreSql;
using Xunit;

namespace ArrControl.AuthentikIntegrationTests;

[Collection(AuthentikContainerCollection.Name)]
public sealed class ArrControlAuthentikEndToEndTests(AuthentikContainerFixture authentik)
{
    private static readonly Uri LogicalAuthority = new(
        "https://authentik.test/application/o/arrcontrol-e2e/");

    [AuthentikBrowserFact]
    public async Task ArrControl_handler_completes_real_authentik_pkce_session_and_rp_logout()
    {
        await using var database = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("arrcontrol_authentik_e2e")
            .WithUsername("arrcontrol_authentik_e2e")
            .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
            .Build();
        await database.StartAsync();
        await ApplyMigrationsAsync(database.GetConnectionString());

        using var factory = new RealAuthentikApiFactory(
            authentik,
            database.GetConnectionString());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = AuthentikContainerFixture.ArrControlPublicOrigin,
            HandleCookies = false,
        });

        using var login = await client.GetAsync(
            "/api/v1/auth/oidc/login?returnUrl=%2Freal-authentik");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var authorizationUri = Assert.IsType<Uri>(login.Headers.Location);
        var authorizationQuery = QueryHelpers.ParseQuery(authorizationUri.Query);
        Assert.Equal("code", authorizationQuery["response_type"].ToString());
        Assert.Equal("query", authorizationQuery["response_mode"].ToString());
        Assert.Equal("S256", authorizationQuery["code_challenge_method"].ToString());
        Assert.False(string.IsNullOrWhiteSpace(authorizationQuery["code_challenge"]));
        Assert.False(string.IsNullOrWhiteSpace(authorizationQuery["state"]));
        Assert.False(string.IsNullOrWhiteSpace(authorizationQuery["nonce"]));
        Assert.Equal(
            AuthentikContainerFixture.ArrControlCallbackUri.AbsoluteUri,
            authorizationQuery["redirect_uri"].ToString());
        Assert.Contains(
            "openid",
            authorizationQuery["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var protocolCookies = ReadProtocolCookieHeader(login);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        // Playwright cannot reliably observe requests owned by Authentik's service worker.
        var browserContext = await browser.NewContextAsync(
            new BrowserNewContextOptions
            {
                ServiceWorkers = ServiceWorkerPolicy.Block,
            });
        var logicalRedirects = Channel.CreateUnbounded<Uri>();
        var browserDiagnostics = new ConcurrentQueue<string>();
        var page = await browserContext.NewPageAsync();
        AttachLogicalRedirectCapture(page, logicalRedirects.Writer, browserDiagnostics);
        AttachBrowserDiagnostics(page, browserDiagnostics);

        await SignInThroughAuthentikAsync(page, authorizationUri);
        var providerCallback = await ReadLogicalRedirectAsync(
            logicalRedirects.Reader,
            AuthentikContainerFixture.ArrControlCallbackUri.AbsolutePath,
            page,
            browserDiagnostics);

        using var callbackRequest = new HttpRequestMessage(
            HttpMethod.Get,
            providerCallback.PathAndQuery);
        callbackRequest.Headers.TryAddWithoutValidation("Cookie", protocolCookies);
        using var callback = await client.SendAsync(callbackRequest);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/real-authentik", callback.Headers.Location?.OriginalString);
        var sessionCookies = ReadSessionCookies(callback);

        await AssertPersistedOidcSessionAsync(database.GetConnectionString());
        using (var protectedRequest = CreateCookieRequest(
                   HttpMethod.Get,
                   "/api/v1/instances",
                   sessionCookies.AccessPair))
        using (var protectedResponse = await client.SendAsync(protectedRequest))
        {
            Assert.Equal(HttpStatusCode.OK, protectedResponse.StatusCode);
        }

        using var logoutRequest = CreateCookieRequest(
            HttpMethod.Post,
            "/api/v1/auth/oidc/logout?returnUrl=%2Freal-logout",
            sessionCookies.AccessPair,
            sessionCookies.RefreshPair,
            sessionCookies.CsrfPair);
        logoutRequest.Headers.TryAddWithoutValidation(
            LocalAuthApiConstants.CsrfHeaderName,
            sessionCookies.CsrfValue);
        using var logout = await client.SendAsync(logoutRequest);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        var endSessionUri = Assert.IsType<Uri>(logout.Headers.Location);
        Assert.Equal(
            $"/application/o/{AuthentikContainerFixture.ApplicationSlug}/end-session/",
            endSessionUri.AbsolutePath);
        var logoutQuery = QueryHelpers.ParseQuery(endSessionUri.Query);
        Assert.Equal(
            AuthentikContainerFixture.ArrControlPostLogoutUri.AbsoluteUri,
            logoutQuery["post_logout_redirect_uri"].ToString());
        Assert.Equal(3, logoutQuery["id_token_hint"].ToString().Split('.').Length);
        Assert.False(string.IsNullOrWhiteSpace(logoutQuery["state"]));

        await AssertLocallyRevokedAsync(database.GetConnectionString());
        using (var revokedRequest = CreateCookieRequest(
                   HttpMethod.Get,
                   "/api/v1/instances",
                   sessionCookies.AccessPair))
        using (var revokedResponse = await client.SendAsync(revokedRequest))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, revokedResponse.StatusCode);
        }

        var signedOutCallbackTask = ReadLogicalRedirectAsync(
            logicalRedirects.Reader,
            AuthentikContainerFixture.ArrControlPostLogoutUri.AbsolutePath,
            page,
            browserDiagnostics);
        try
        {
            await page.GotoAsync(
                endSessionUri.AbsoluteUri,
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 120_000,
                });
        }
        catch (PlaywrightException)
        {
            // The provider's exact HTTPS redirect is captured from its 302 response;
            // the logical ArrControl test origin intentionally has no network listener.
        }

        var signedOutCallback = await signedOutCallbackTask;
        using var signedOut = await client.GetAsync(signedOutCallback.PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal("/real-logout", signedOut.Headers.Location?.OriginalString);
    }

    private static async Task ApplyMigrationsAsync(string connectionString)
    {
        await using var context = new ArrControlDbContext(
            new DbContextOptionsBuilder<ArrControlDbContext>()
                .UseNpgsql(connectionString)
                .Options);
        await context.Database.MigrateAsync();
        context.Add(new RoleEntity
        {
            Name = LocalIdentityConstants.AdministratorRoleName,
            NormalizedName = LocalIdentityConstants.AdministratorRoleNormalizedName,
            IsSystem = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private async Task AssertPersistedOidcSessionAsync(string connectionString)
    {
        await using var context = CreateDbContext(connectionString);
        var identity = Assert.Single(
            await context.Set<ExternalIdentityEntity>().AsNoTracking().ToListAsync());
        Assert.Equal(authentik.Authority.AbsoluteUri, identity.Issuer);
        Assert.False(string.IsNullOrWhiteSpace(identity.Subject));

        var user = Assert.Single(await context.Set<UserEntity>().AsNoTracking().ToListAsync());
        Assert.Equal(authentik.UserEmail, user.Email);
        Assert.Null(user.PasswordHash);

        var assignedRole = Assert.Single(
            await context.Set<ExternalIdentityRoleEntity>()
                .AsNoTracking()
                .Where(assignment => assignment.ExternalIdentityId == identity.Id)
                .Join(
                    context.Set<RoleEntity>(),
                    assignment => assignment.RoleId,
                    role => role.Id,
                    (_, role) => role.Name)
                .ToListAsync());
        Assert.Equal(LocalIdentityConstants.AdministratorRoleName, assignedRole);

        var session = Assert.Single(
            await context.Set<UserSessionEntity>().AsNoTracking().ToListAsync());
        Assert.Equal(LocalIdentityConstants.OidcAuthenticationMethod, session.AuthenticationMethod);
        Assert.Null(session.RevokedAt);

        var logoutContext = Assert.Single(
            await context.Set<OidcSessionContextEntity>().AsNoTracking().ToListAsync());
        Assert.False(string.IsNullOrWhiteSpace(logoutContext.ProtectedIdToken));
        Assert.DoesNotContain('.', logoutContext.ProtectedIdToken);
    }

    private static async Task AssertLocallyRevokedAsync(string connectionString)
    {
        await using var context = CreateDbContext(connectionString);
        var session = Assert.Single(
            await context.Set<UserSessionEntity>().AsNoTracking().ToListAsync());
        Assert.NotNull(session.RevokedAt);
    }

    private static ArrControlDbContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(connectionString)
            .Options);

    private async Task SignInThroughAuthentikAsync(IPage page, Uri authorizationUri)
    {
        await page.GotoAsync(
            authorizationUri.AbsoluteUri,
            new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 120_000,
            });
        var identificationStage = page.Locator("ak-stage-identification");
        var username = identificationStage.Locator("input[name=uidField]");
        await username.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 60_000,
        });
        await username.FillAsync(authentik.UserName);

        var inlinePassword = identificationStage.Locator("input[name=password]");
        if (await inlinePassword.IsVisibleAsync())
        {
            await inlinePassword.FillAsync(authentik.UserPassword);
            await identificationStage.Locator("button[type=submit]").ClickAsync();
        }
        else
        {
            await identificationStage.Locator("button[type=submit]").ClickAsync();
            var passwordStage = page.Locator("ak-stage-password");
            var password = passwordStage.Locator("input[name=password]");
            await password.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 60_000,
            });
            await password.FillAsync(authentik.UserPassword);
            await passwordStage.Locator("button[type=submit]").ClickAsync();
        }
    }

    private static async Task<Uri> ReadLogicalRedirectAsync(
        ChannelReader<Uri> reader,
        string expectedPath,
        IPage page,
        ConcurrentQueue<string> browserDiagnostics)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            while (true)
            {
                var candidate = await reader.ReadAsync(timeout.Token);
                if (string.Equals(candidate.AbsolutePath, expectedPath, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            var executor = page.Locator("ak-flow-executor");
            var executorState = await executor.CountAsync() == 0
                ? "absent"
                : await executor.EvaluateAsync<string>(
                    "value => JSON.stringify({"
                    + "loading: value.loading, "
                    + "component: value.challenge?.component, "
                    + "responseErrorKeys: Object.keys(value.challenge?.responseErrors ?? {})"
                    + "})");
            var cookieMetadata = (await page.Context.CookiesAsync())
                .Select(cookie =>
                    $"{cookie.Name}[domain={cookie.Domain};secure={cookie.Secure};sameSite={cookie.SameSite}]");
            var current = new Uri(page.Url);
            throw new TimeoutException(
                $"The real Authentik flow did not reach {expectedPath}. "
                + $"Current location: {current.Host}{current.AbsolutePath}. "
                + $"Executor state: {executorState}. "
                + $"Cookies: {string.Join(", ", cookieMetadata)}. "
                + $"Browser diagnostics: {string.Join(" | ", browserDiagnostics)}",
                exception);
        }
    }

    private static void AttachBrowserDiagnostics(
        IPage page,
        ConcurrentQueue<string> diagnostics)
    {
        page.Request += (_, request) =>
        {
            if (TryGetDiagnosticTarget(request, out var target))
            {
                diagnostics.Enqueue($"request {request.Method} {target}");
            }
        };
        page.Response += (_, response) =>
        {
            if (TryGetDiagnosticTarget(response.Request, out var target))
            {
                diagnostics.Enqueue(
                    $"response {response.Request.Method} {target} {response.Status}");
            }
        };
        page.RequestFailed += (_, request) =>
        {
            if (TryGetDiagnosticTarget(request, out var target))
            {
                diagnostics.Enqueue($"failed {request.Method} {target}: {request.Failure}");
            }
        };
        page.PageError += (_, error) => diagnostics.Enqueue($"page error: {error}");
    }

    private static void AttachLogicalRedirectCapture(
        IPage page,
        ChannelWriter<Uri> redirects,
        ConcurrentQueue<string> diagnostics)
    {
        page.Request += (_, request) =>
        {
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var candidate)
                || !string.Equals(
                    candidate.GetLeftPart(UriPartial.Authority),
                    AuthentikContainerFixture.ArrControlPublicOrigin.GetLeftPart(
                        UriPartial.Authority),
                    StringComparison.Ordinal))
            {
                return;
            }

            diagnostics.Enqueue(
                $"captured navigation {candidate.Host}{candidate.AbsolutePath}");
            redirects.TryWrite(candidate);
        };
    }

    private static bool TryGetDiagnosticTarget(IRequest request, out string target)
    {
        target = string.Empty;
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        if (!request.IsNavigationRequest
            && !path.StartsWith("/api/v3/flows/executor/", StringComparison.Ordinal)
            && !path.StartsWith("/application/o/", StringComparison.Ordinal))
        {
            return false;
        }

        target = uri.Host + path;
        return true;
    }

    private static string ReadProtocolCookieHeader(HttpResponseMessage response)
    {
        var cookiePairs = ReadSetCookieHeaders(response)
            .Where(value => value.StartsWith(
                    "__Host-arrcontrol_oidc_correlation.",
                    StringComparison.Ordinal)
                || value.StartsWith(
                    "__Host-arrcontrol_oidc_nonce.",
                    StringComparison.Ordinal))
            .Select(ReadCookiePair)
            .ToArray();
        Assert.Equal(2, cookiePairs.Length);
        return string.Join("; ", cookiePairs);
    }

    private static SessionCookies ReadSessionCookies(HttpResponseMessage response)
    {
        var accessPair = ReadNamedCookiePair(response, LocalAuthApiConstants.AccessCookieName);
        var refreshPair = ReadNamedCookiePair(response, LocalAuthApiConstants.RefreshCookieName);
        var csrfPair = ReadNamedCookiePair(response, LocalAuthApiConstants.CsrfCookieName);
        return new SessionCookies(
            accessPair,
            refreshPair,
            csrfPair,
            csrfPair[(LocalAuthApiConstants.CsrfCookieName.Length + 1)..]);
    }

    private static HttpRequestMessage CreateCookieRequest(
        HttpMethod method,
        string path,
        params string[] cookiePairs)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookiePairs));
        return request;
    }

    private static string ReadNamedCookiePair(HttpResponseMessage response, string name) =>
        ReadCookiePair(Assert.Single(
            ReadSetCookieHeaders(response),
            value => value.StartsWith(name + "=", StringComparison.Ordinal)));

    private static string[] ReadSetCookieHeaders(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];

    private static string ReadCookiePair(string setCookieHeader) =>
        setCookieHeader.Split(';', 2)[0];

    private sealed record SessionCookies(
        string AccessPair,
        string RefreshPair,
        string CsrfPair,
        string CsrfValue);

    private sealed class RealAuthentikApiFactory(
        AuthentikContainerFixture authentik,
        string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting(
                "App:PublicUrl",
                AuthentikContainerFixture.ArrControlPublicOrigin.AbsoluteUri);
            builder.UseSetting("DataProtection:KeysPath", string.Empty);
            builder.UseSetting("Auth:Oidc:Enabled", "true");
            builder.UseSetting("Auth:Oidc:Authority", LogicalAuthority.AbsoluteUri);
            builder.UseSetting("Auth:Oidc:ClientId", authentik.ClientId);
            builder.UseSetting("Auth:Oidc:ClientSecret", authentik.ClientSecret);
            builder.UseSetting(
                "Auth:Oidc:AdministratorGroup",
                AuthentikContainerFixture.AdministratorGroup);
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Database"] = connectionString,
                    ["App:PublicUrl"] =
                        AuthentikContainerFixture.ArrControlPublicOrigin.AbsoluteUri,
                    ["DataProtection:KeysPath"] = string.Empty,
                    ["Auth:Oidc:Enabled"] = "true",
                    ["Auth:Oidc:Authority"] = LogicalAuthority.AbsoluteUri,
                    ["Auth:Oidc:ClientId"] = authentik.ClientId,
                    ["Auth:Oidc:ClientSecret"] = authentik.ClientSecret,
                    ["Auth:Oidc:AdministratorGroup"] =
                        AuthentikContainerFixture.AdministratorGroup,
                }));
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<OpenIdConnectOptions>(
                    OidcAuthenticationApi.AuthenticationScheme,
                    options =>
                    {
                        options.Authority = authentik.Authority.AbsoluteUri;
                        options.MetadataAddress = new Uri(
                            authentik.Authority,
                            ".well-known/openid-configuration").AbsoluteUri;
                        options.RequireHttpsMetadata = false;
                        options.TokenValidationParameters.ValidIssuer =
                            authentik.Authority.AbsoluteUri;
                        options.TokenValidationParameters.IssuerValidator =
                            (issuer, _, _) => string.Equals(
                                issuer,
                                authentik.Authority.AbsoluteUri,
                                StringComparison.Ordinal)
                                ? issuer
                                : throw new SecurityTokenInvalidIssuerException(
                                    "The test token issuer is invalid.");
                        options.ConfigurationManager =
                            new Microsoft.IdentityModel.Protocols.ConfigurationManager<
                                OpenIdConnectConfiguration>(
                                options.MetadataAddress,
                                new OpenIdConnectConfigurationRetriever(),
                                new HttpDocumentRetriever(options.Backchannel)
                                {
                                    RequireHttps = false,
                                });
                    });
            });
        }
    }
}
