using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArrControl.Api.Identity;
using ArrControl.Application.Identity;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ArrControl.IntegrationTests;

public sealed class LocalAuthenticationApiTests(AuthApiDatabaseFixture databaseFixture)
    : IClassFixture<AuthApiDatabaseFixture>
{
    private const string AdminEmail = "admin@example.invalid";
    private const string AdminPassword = "correct bootstrap password";

    [Fact]
    public async Task Authentication_mutations_require_a_matching_csrf_cookie_and_header()
    {
        using var scenario = await CreateScenarioAsync();
        var firstCsrf = await GetCsrfAsync(scenario.Client);
        var secondCsrf = await GetCsrfAsync(scenario.Client);

        using var loginWithoutCsrf = await LoginAsync(
            scenario.Client,
            AdminEmail,
            AdminPassword);
        using var refreshWithoutCsrf = await SendMutationAsync(
            scenario.Client,
            "/api/v1/auth/refresh");
        using var logoutWithoutCsrf = await SendMutationAsync(
            scenario.Client,
            "/api/v1/auth/logout");
        using var loginWithMismatch = await LoginAsync(
            scenario.Client,
            AdminEmail,
            AdminPassword,
            firstCsrf.Token,
            secondCsrf.Token);

        Assert.Equal(HttpStatusCode.Forbidden, loginWithoutCsrf.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refreshWithoutCsrf.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, logoutWithoutCsrf.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, loginWithMismatch.StatusCode);
        Assert.Equal("csrf_validation_failed", await ReadProblemCodeAsync(loginWithoutCsrf));
        Assert.Equal("csrf_validation_failed", await ReadProblemCodeAsync(refreshWithoutCsrf));
        Assert.Equal("csrf_validation_failed", await ReadProblemCodeAsync(logoutWithoutCsrf));
        Assert.Equal("csrf_validation_failed", await ReadProblemCodeAsync(loginWithMismatch));
    }

    [Fact]
    public async Task Login_transport_errors_return_typed_problem_details()
    {
        using var scenario = await CreateScenarioAsync();
        var csrf = await GetCsrfAsync(scenario.Client);

        using var malformedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json"),
        };
        AddCsrf(malformedRequest, csrf.Token, csrf.Token);
        using var malformedResponse = await scenario.Client.SendAsync(
            malformedRequest,
            CancellationToken.None);

        var oversizedJson = JsonSerializer.Serialize(new
        {
            email = AdminEmail,
            password = new string('x', 5_000),
        });
        using var oversizedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent(oversizedJson, Encoding.UTF8, "application/json"),
        };
        AddCsrf(oversizedRequest, csrf.Token, csrf.Token);
        using var oversizedResponse = await scenario.Client.SendAsync(
            oversizedRequest,
            CancellationToken.None);

        await AssertTypedProblemAsync(
            malformedResponse,
            HttpStatusCode.BadRequest,
            "invalid_request");
        await AssertTypedProblemAsync(
            oversizedResponse,
            HttpStatusCode.RequestEntityTooLarge,
            "request_too_large");
    }

    [Theory]
    [InlineData(AdminEmail, null, "requires both")]
    [InlineData(null, AdminPassword, "requires both")]
    [InlineData(AdminEmail, "CHANGE_ME_TO_A_LONG_RANDOM_ADMIN_PASSWORD", "placeholder")]
    public async Task Unsafe_bootstrap_configuration_fails_startup_without_creating_a_user(
        string? email,
        string? password,
        string expectedMessage)
    {
        var connectionString = await databaseFixture.CreateMigratedSchemaAsync();
        using var factory = new AuthApiFactory(connectionString, email, password);

        var exception = Assert.ThrowsAny<Exception>(() => CreateHttpsClient(factory));

        Assert.Contains(expectedMessage, exception.ToString(), StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(email))
        {
            Assert.DoesNotContain(email, exception.ToString(), StringComparison.Ordinal);
        }

        if (!string.IsNullOrEmpty(password))
        {
            Assert.DoesNotContain(password, exception.ToString(), StringComparison.Ordinal);
        }

        var options = new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using var context = new ArrControlDbContext(options);
        Assert.Empty(await context.Set<UserEntity>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public void Unsafe_local_authentication_limits_fail_validation()
    {
        var defaults = LocalAuthSettings.Default;
        Assert.Throws<InvalidOperationException>(() =>
            (defaults with { AccountFailureLimit = 0 }).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            (defaults with { IpFailureLimit = 0 }).Validate());
        Assert.Throws<InvalidOperationException>(() => new LocalAuthTransportSettings(9, 120).Validate());
        Assert.Throws<InvalidOperationException>(() => new LocalAuthTransportSettings(60, 9).Validate());
    }

    [Fact]
    public async Task Runtime_openapi_exposes_the_local_authentication_contract()
    {
        using var scenario = await CreateScenarioAsync();
        using var response = await scenario.Client.GetAsync(
            "/api/openapi/v1.json",
            CancellationToken.None);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));
        var root = document.RootElement;

        var securitySchemes = root.GetProperty("components").GetProperty("securitySchemes");
        Assert.Equal(
            LocalAuthApiConstants.AccessCookieName,
            securitySchemes.GetProperty("cookieAuth").GetProperty("name").GetString());
        Assert.Equal(
            LocalAuthApiConstants.RefreshCookieName,
            securitySchemes.GetProperty("refreshCookie").GetProperty("name").GetString());

        var paths = root.GetProperty("paths");
        var login = paths.GetProperty("/api/v1/auth/login").GetProperty("post");
        var loginResponses = login.GetProperty("responses");
        foreach (var status in new[] { "200", "400", "401", "403", "413", "429" })
        {
            Assert.True(loginResponses.TryGetProperty(status, out _), $"Missing login response {status}.");
        }

        Assert.Contains(
            login.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString()
                == LocalAuthApiConstants.CsrfHeaderName);
        Assert.True(paths.GetProperty("/api/v1/auth/csrf")
            .GetProperty("get")
            .GetProperty("responses")
            .TryGetProperty("500", out _));
        Assert.True(paths.GetProperty("/api/v1/auth/refresh")
            .GetProperty("post")
            .GetProperty("responses")
            .TryGetProperty("429", out _));
        Assert.True(paths.GetProperty("/api/v1/auth/logout")
            .GetProperty("post")
            .GetProperty("responses")
            .TryGetProperty("429", out _));

        var problemSchemas = root.GetProperty("components")
            .GetProperty("schemas")
            .EnumerateObject()
            .Select(x => x.Value)
            .Where(schema => schema.TryGetProperty("properties", out var properties)
                && properties.TryGetProperty("code", out _)
                && properties.TryGetProperty("traceId", out _))
            .ToArray();
        var problemSchema = Assert.Single(problemSchemas);
        var required = problemSchema.GetProperty("required")
            .EnumerateArray()
            .Select(x => x.GetString())
            .ToArray();
        Assert.Contains("code", required);
        Assert.Contains("traceId", required);
    }

    [Fact]
    public async Task Untrusted_forwarded_for_values_do_not_bypass_the_login_ip_limiter()
    {
        var connectionString = await databaseFixture.CreateMigratedSchemaAsync();
        using var factory = new AuthApiFactory(
            connectionString,
            AdminEmail,
            AdminPassword);
        using var client = CreateHttpsClient(factory);
        var settings = factory.Services.GetRequiredService<LocalAuthTransportSettings>();
        Assert.Equal(60, settings.LoginRequestLimit);

        for (var attempt = 0; attempt < settings.LoginRequestLimit; attempt++)
        {
            using var allowed = await LoginWithoutCsrfFromForwardedIpAsync(
                client,
                $"198.51.100.{attempt + 1}");
            Assert.Equal(HttpStatusCode.Forbidden, allowed.StatusCode);
            Assert.Equal("csrf_validation_failed", await ReadProblemCodeAsync(allowed));
        }

        using var rejected = await LoginWithoutCsrfFromForwardedIpAsync(client, "198.51.100.250");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("authentication_rate_limited", await ReadProblemCodeAsync(rejected));
    }

    [Theory]
    [InlineData("/api/v1/auth/refresh")]
    [InlineData("/api/v1/auth/logout")]
    public async Task Session_mutations_have_a_raw_per_ip_request_limit(string path)
    {
        using var scenario = await CreateScenarioAsync();
        var settings = scenario.Factory.Services.GetRequiredService<LocalAuthTransportSettings>();
        Assert.Equal(120, settings.SessionMutationRequestLimit);

        for (var attempt = 0; attempt < settings.SessionMutationRequestLimit; attempt++)
        {
            using var allowed = await SendMutationAsync(scenario.Client, path);
            Assert.Equal(HttpStatusCode.Forbidden, allowed.StatusCode);
            Assert.Equal("csrf_validation_failed", await ReadProblemCodeAsync(allowed));
        }

        using var rejected = await SendMutationAsync(scenario.Client, path);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("authentication_rate_limited", await ReadProblemCodeAsync(rejected));
    }

    [Fact]
    public async Task Bootstrap_variables_update_the_bootstrap_admin_and_revoke_sessions()
    {
        using var scenario = await CreateScenarioAsync();
        var initialCsrf = await GetCsrfAsync(scenario.Client);
        using var initialLogin = await LoginAsync(
            scenario.Client,
            AdminEmail,
            AdminPassword,
            initialCsrf.Token,
            initialCsrf.Token);
        Assert.Equal(HttpStatusCode.OK, initialLogin.StatusCode);
        var initialCookies = await ReadSessionCookiesAsync(initialLogin);

        using var secondFactory = new AuthApiFactory(
            scenario.ConnectionString,
            "second-admin@example.invalid",
            "different bootstrap password");
        using var secondClient = CreateHttpsClient(secondFactory);
        var csrf = await GetCsrfAsync(secondClient);

        await AssertBootstrapStateAsync(
            scenario.ConnectionString,
            "second-admin@example.invalid",
            "different bootstrap password");

        using var revokedSession = await GetInstancesAsync(secondClient, initialCookies.Access);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedSession.StatusCode);

        using var wrongPassword = await LoginAsync(
            secondClient,
            "second-admin@example.invalid",
            "incorrect password",
            csrf.Token,
            csrf.Token);
        using var unknownAccount = await LoginAsync(
            secondClient,
            AdminEmail,
            "incorrect password",
            csrf.Token,
            csrf.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownAccount.StatusCode);
        Assert.Equal(
            await ReadProblemSignatureAsync(wrongPassword),
            await ReadProblemSignatureAsync(unknownAccount));

        using var anonymousInstances = await secondClient.GetAsync(
            "/api/v1/instances",
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousInstances.StatusCode);
        Assert.Equal("authentication_required", await ReadProblemCodeAsync(anonymousInstances));

        using var login = await LoginAsync(
            secondClient,
            "second-admin@example.invalid",
            "different bootstrap password",
            csrf.Token,
            csrf.Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var cookies = await ReadSessionCookiesAsync(login);
        Assert.Equal("__Host-arrcontrol_session", LocalAuthApiConstants.AccessCookieName);
        Assert.Equal("__Host-arrcontrol_refresh", LocalAuthApiConstants.RefreshCookieName);
        Assert.Equal("__Host-arrcontrol_csrf", LocalAuthApiConstants.CsrfCookieName);
        AssertCookieFlags(cookies.AccessHeader, "/", httpOnly: true);
        AssertCookieFlags(cookies.RefreshHeader, "/", httpOnly: true);
        AssertCookieFlags(cookies.CsrfHeader, "/", httpOnly: false);

        using var authenticatedInstances = await GetInstancesAsync(secondClient, cookies.Access);
        Assert.Equal(HttpStatusCode.OK, authenticatedInstances.StatusCode);
        Assert.Equal("[]", await authenticatedInstances.Content.ReadAsStringAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Refresh_rotates_access_and_refresh_tokens_and_replay_revokes_the_family()
    {
        using var scenario = await CreateScenarioAsync();
        var csrf = await GetCsrfAsync(scenario.Client);
        using var login = await LoginAsync(
            scenario.Client,
            AdminEmail,
            AdminPassword,
            csrf.Token,
            csrf.Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var original = await ReadSessionCookiesAsync(login);

        using var originalAccess = await GetInstancesAsync(scenario.Client, original.Access);
        Assert.Equal(HttpStatusCode.OK, originalAccess.StatusCode);

        using var refresh = await SendMutationAsync(
            scenario.Client,
            "/api/v1/auth/refresh",
            original.Csrf,
            original.Csrf,
            (LocalAuthApiConstants.RefreshCookieName, original.Refresh));
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var replacement = await ReadSessionCookiesAsync(refresh);

        Assert.NotEqual(original.Access, replacement.Access);
        Assert.NotEqual(original.Refresh, replacement.Refresh);
        Assert.NotEqual(original.Csrf, replacement.Csrf);

        using var retiredAccess = await GetInstancesAsync(scenario.Client, original.Access);
        using var replacementAccess = await GetInstancesAsync(scenario.Client, replacement.Access);
        Assert.Equal(HttpStatusCode.Unauthorized, retiredAccess.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replacementAccess.StatusCode);

        using var replay = await SendMutationAsync(
            scenario.Client,
            "/api/v1/auth/refresh",
            original.Csrf,
            original.Csrf,
            (LocalAuthApiConstants.RefreshCookieName, original.Refresh));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("authentication_failed", await ReadProblemCodeAsync(replay));

        using var revokedAccess = await GetInstancesAsync(scenario.Client, replacement.Access);
        using var revokedRefresh = await SendMutationAsync(
            scenario.Client,
            "/api/v1/auth/refresh",
            replacement.Csrf,
            replacement.Csrf,
            (LocalAuthApiConstants.RefreshCookieName, replacement.Refresh));
        Assert.Equal(HttpStatusCode.Unauthorized, revokedAccess.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedRefresh.StatusCode);
    }

    [Fact]
    public async Task Logout_is_idempotent_clears_cookies_and_rejects_the_old_session()
    {
        using var scenario = await CreateScenarioAsync();
        var csrf = await GetCsrfAsync(scenario.Client);
        using var login = await LoginAsync(
            scenario.Client,
            AdminEmail,
            AdminPassword,
            csrf.Token,
            csrf.Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookies = await ReadSessionCookiesAsync(login);

        using var firstLogout = await SendMutationAsync(
            scenario.Client,
            "/api/v1/auth/logout",
            cookies.Csrf,
            cookies.Csrf,
            (LocalAuthApiConstants.AccessCookieName, cookies.Access),
            (LocalAuthApiConstants.RefreshCookieName, cookies.Refresh));
        using var repeatedLogout = await SendMutationAsync(
            scenario.Client,
            "/api/v1/auth/logout",
            cookies.Csrf,
            cookies.Csrf,
            (LocalAuthApiConstants.AccessCookieName, cookies.Access),
            (LocalAuthApiConstants.RefreshCookieName, cookies.Refresh));

        Assert.Equal(HttpStatusCode.NoContent, firstLogout.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, repeatedLogout.StatusCode);
        AssertDeletedCookie(firstLogout, LocalAuthApiConstants.AccessCookieName, "/", httpOnly: true);
        AssertDeletedCookie(
            firstLogout,
            LocalAuthApiConstants.RefreshCookieName,
            "/",
            httpOnly: true);
        AssertDeletedCookie(
            firstLogout,
            LocalAuthApiConstants.CsrfCookieName,
            "/",
            httpOnly: false);

        using var oldAccess = await GetInstancesAsync(scenario.Client, cookies.Access);
        using var oldRefresh = await SendMutationAsync(
            scenario.Client,
            "/api/v1/auth/refresh",
            cookies.Csrf,
            cookies.Csrf,
            (LocalAuthApiConstants.RefreshCookieName, cookies.Refresh));
        Assert.Equal(HttpStatusCode.Unauthorized, oldAccess.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, oldRefresh.StatusCode);
    }

    private async Task<ApiScenario> CreateScenarioAsync()
    {
        var connectionString = await databaseFixture.CreateMigratedSchemaAsync();
        var factory = new AuthApiFactory(connectionString, AdminEmail, AdminPassword);
        try
        {
            return new ApiScenario(factory, CreateHttpsClient(factory), connectionString);
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    private static HttpClient CreateHttpsClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

    private static async Task<CsrfGrant> GetCsrfAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf", CancellationToken.None);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<CsrfTokenResponse>(CancellationToken.None);
        Assert.NotNull(payload);
        var header = ReadSetCookie(response, LocalAuthApiConstants.CsrfCookieName);
        var cookieValue = ReadCookieValue(header, LocalAuthApiConstants.CsrfCookieName);
        Assert.Equal(payload.Token, cookieValue);
        AssertCookieFlags(header, "/", httpOnly: false);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return new CsrfGrant(cookieValue);
    }

    private static async Task<HttpResponseMessage> LoginAsync(
        HttpClient client,
        string email,
        string password,
        string? csrfCookie = null,
        string? csrfHeader = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email, password }),
        };
        AddCsrf(request, csrfCookie, csrfHeader);
        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<HttpResponseMessage> SendMutationAsync(
        HttpClient client,
        string path,
        string? csrfCookie = null,
        string? csrfHeader = null,
        params (string Name, string Value)[] cookies)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        AddCsrf(request, csrfCookie, csrfHeader, cookies);
        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<HttpResponseMessage> LoginWithoutCsrfFromForwardedIpAsync(
        HttpClient client,
        string forwardedIpAddress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email = AdminEmail, password = AdminPassword }),
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedIpAddress);
        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<HttpResponseMessage> GetInstancesAsync(
        HttpClient client,
        string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/instances");
        AddCookies(request, (LocalAuthApiConstants.AccessCookieName, accessToken));
        return await client.SendAsync(request, CancellationToken.None);
    }

    private static void AddCsrf(
        HttpRequestMessage request,
        string? csrfCookie,
        string? csrfHeader,
        params (string Name, string Value)[] cookies)
    {
        if (csrfHeader is not null)
        {
            request.Headers.TryAddWithoutValidation(LocalAuthApiConstants.CsrfHeaderName, csrfHeader);
        }

        if (csrfCookie is not null)
        {
            AddCookies(
                request,
                cookies.Prepend((LocalAuthApiConstants.CsrfCookieName, csrfCookie)).ToArray());
        }
        else
        {
            AddCookies(request, cookies);
        }
    }

    private static void AddCookies(
        HttpRequestMessage request,
        params (string Name, string Value)[] cookies)
    {
        if (cookies.Length > 0)
        {
            request.Headers.TryAddWithoutValidation(
                "Cookie",
                string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}")));
        }
    }

    private static async Task<SessionCookies> ReadSessionCookiesAsync(HttpResponseMessage response)
    {
        var accessHeader = ReadSetCookie(response, LocalAuthApiConstants.AccessCookieName);
        var refreshHeader = ReadSetCookie(response, LocalAuthApiConstants.RefreshCookieName);
        var csrfHeader = ReadSetCookie(response, LocalAuthApiConstants.CsrfCookieName);
        var cookies = new SessionCookies(
            ReadCookieValue(accessHeader, LocalAuthApiConstants.AccessCookieName),
            ReadCookieValue(refreshHeader, LocalAuthApiConstants.RefreshCookieName),
            ReadCookieValue(csrfHeader, LocalAuthApiConstants.CsrfCookieName),
            accessHeader,
            refreshHeader,
            csrfHeader);
        var payload = await response.Content.ReadFromJsonAsync<AuthSessionResponse>(CancellationToken.None);
        Assert.NotNull(payload);
        Assert.Equal(cookies.Csrf, payload.CsrfToken);
        return cookies;
    }

    private static string ReadSetCookie(HttpResponseMessage response, string cookieName)
    {
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var values));
        return Assert.Single(
            values,
            value => value.StartsWith(cookieName + "=", StringComparison.Ordinal));
    }

    private static string ReadCookieValue(string setCookieHeader, string cookieName)
    {
        var firstSegment = setCookieHeader.Split(';', 2)[0];
        return firstSegment[(cookieName.Length + 1)..];
    }

    private static void AssertCookieFlags(string setCookieHeader, string path, bool httpOnly)
    {
        var attributes = setCookieHeader.Split(';')
            .Skip(1)
            .Select(attribute => attribute.Trim())
            .ToArray();
        Assert.Contains(attributes, attribute =>
            string.Equals(attribute, $"path={path}", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(attributes, attribute =>
            string.Equals(attribute, "secure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(attributes, attribute =>
            string.Equals(attribute, "samesite=lax", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(attributes, attribute =>
            attribute.StartsWith("domain=", StringComparison.OrdinalIgnoreCase));
        if (httpOnly)
        {
            Assert.Contains(attributes, attribute =>
                string.Equals(attribute, "httponly", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.DoesNotContain(attributes, attribute =>
                string.Equals(attribute, "httponly", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void AssertDeletedCookie(
        HttpResponseMessage response,
        string cookieName,
        string path,
        bool httpOnly)
    {
        var header = ReadSetCookie(response, cookieName);
        Assert.Empty(ReadCookieValue(header, cookieName));
        AssertCookieFlags(header, path, httpOnly);
        Assert.Contains("expires=", header, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));
        return document.RootElement.GetProperty("code").GetString()!;
    }

    private static async Task AssertTypedProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));
        var root = document.RootElement;
        Assert.Equal((int)expectedStatus, root.GetProperty("status").GetInt32());
        Assert.Equal(expectedCode, root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("type").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
    }

    private static async Task<ProblemSignature> ReadProblemSignatureAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));
        var root = document.RootElement;
        return new ProblemSignature(
            root.GetProperty("type").GetString()!,
            root.GetProperty("title").GetString()!,
            root.GetProperty("status").GetInt32(),
            root.GetProperty("code").GetString()!);
    }

    private static async Task AssertBootstrapStateAsync(
        string connectionString,
        string expectedEmail,
        string cleartextPassword)
    {
        var options = new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using var context = new ArrControlDbContext(options);
        var user = Assert.Single(await context.Set<UserEntity>().AsNoTracking().ToListAsync());
        var sentinel = Assert.Single(await context.Set<IdentityBootstrapStateEntity>()
            .AsNoTracking()
            .ToListAsync());
        Assert.Equal(expectedEmail, user.Email);
        Assert.Equal(user.Id, sentinel.AdminUserId);
        Assert.NotNull(user.PasswordHash);
        Assert.StartsWith("$argon2id$", user.PasswordHash, StringComparison.Ordinal);
        Assert.DoesNotContain(cleartextPassword, user.PasswordHash, StringComparison.Ordinal);
    }

    private sealed record CsrfGrant(string Token);

    private sealed record SessionCookies(
        string Access,
        string Refresh,
        string Csrf,
        string AccessHeader,
        string RefreshHeader,
        string CsrfHeader);

    private sealed record ProblemSignature(string Type, string Title, int Status, string Code);

    private sealed class ApiScenario(
        AuthApiFactory factory,
        HttpClient client,
        string connectionString) : IDisposable
    {
        public AuthApiFactory Factory { get; } = factory;

        public HttpClient Client { get; } = client;

        public string ConnectionString { get; } = connectionString;

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }
}

public sealed class AuthApiDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer database = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("arrcontrol_api_integration_tests")
        .WithUsername("arrcontrol_api_integration_tests")
        .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
        .WithCreateParameterModifier(parameters =>
        {
            parameters.HostConfig ??= new();
            parameters.HostConfig.ShmSize = 256L * 1024 * 1024;
        })
        .Build();

    public Task InitializeAsync() => database.StartAsync();

    public Task DisposeAsync() => database.DisposeAsync().AsTask();

    public async Task<string> CreateMigratedSchemaAsync()
    {
        var connectionString = await CreateUnmigratedSchemaAsync();
        var options = new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using var context = new ArrControlDbContext(options);
        await context.Database.MigrateAsync(CancellationToken.None);
        return connectionString;
    }

    public async Task<string> CreateUnmigratedSchemaAsync()
    {
        var schema = $"api_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(database.GetConnectionString()))
        {
            await connection.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(database.GetConnectionString())
        {
            SearchPath = schema,
            // Each scenario receives a distinct schema and connection string. Keeping a
            // pool for every disposed WebApplicationFactory eventually exhausts the
            // PostgreSQL test container's connection limit in a complete test run.
            Pooling = false,
        };
        return connectionStringBuilder.ConnectionString;
    }
}

public sealed class AuthApiFactory(
    string connectionString,
    string? bootstrapEmail,
    string? bootstrapPassword,
    string? credentialKeyPath = null,
    Action<IServiceCollection>? configureTestServices = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter>(
                new FixedRemoteIpStartupFilter(IPAddress.Parse("203.0.113.42")));
            configureTestServices?.Invoke(services);
        });
        builder.ConfigureAppConfiguration(configuration =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = connectionString,
                ["Bootstrap:AdminEmail"] = bootstrapEmail,
                ["Bootstrap:AdminPassword"] = bootstrapPassword,
            };
            if (credentialKeyPath is not null)
            {
                settings["CredentialEncryption:ActiveKeyVersion"] = "1";
                settings["CredentialEncryption:Keys:0:Version"] = "1";
                settings["CredentialEncryption:Keys:0:Path"] = credentialKeyPath;
            }

            configuration.AddInMemoryCollection(settings);
        });
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
