using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ArrControl.Api.Authorization;
using ArrControl.Api.Identity;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Identity;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArrControl.IntegrationTests;

public sealed class UserPreferencesApiTests(AuthApiDatabaseFixture databaseFixture)
    : IClassFixture<AuthApiDatabaseFixture>
{
    private const string AdminEmail = "preferences-admin@example.invalid";
    private const string AdminPassword = "correct horse battery staple 123!";

    [Fact]
    public async Task Preferences_require_csrf_validate_persist_and_are_audited()
    {
        var connectionString = await databaseFixture.CreateMigratedSchemaAsync();
        using var factory = new AuthApiFactory(connectionString, AdminEmail, AdminPassword);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });
        var session = await LoginAsync(client);

        using (var initial = await SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", null, session))
        {
            initial.EnsureSuccessStatusCode();
            var current = await initial.Content.ReadFromJsonAsync<CurrentAuthorizationResponse>();
            Assert.NotNull(current);
            Assert.Equal("en", current.Locale);
            Assert.Equal("UTC", current.TimeZone);
        }

        using (var noCsrf = await SendAsync(
                   client,
                   HttpMethod.Put,
                   "/api/v1/auth/preferences",
                   new UpdateUserPreferencesRequest("de", "Europe/Berlin"),
                   session,
                   includeCsrf: false))
        {
            Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
            Assert.Equal("csrf_validation_failed", await ReadProblemCodeAsync(noCsrf));
        }

        using (var invalid = await SendAsync(
                   client,
                   HttpMethod.Put,
                   "/api/v1/auth/preferences",
                   new UpdateUserPreferencesRequest("fr", "Europe/Berlin"),
                   session))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("locale_not_supported", await ReadProblemCodeAsync(invalid));
        }

        using (var updated = await SendAsync(
                   client,
                   HttpMethod.Put,
                   "/api/v1/auth/preferences",
                   new UpdateUserPreferencesRequest("de", "Europe/Berlin"),
                   session))
        {
            updated.EnsureSuccessStatusCode();
            Assert.True(updated.Headers.CacheControl?.NoStore);
            var preferences = await updated.Content.ReadFromJsonAsync<UserPreferencesResponse>();
            Assert.Equal("de", preferences?.Locale);
            Assert.Equal("Europe/Berlin", preferences?.TimeZone);
        }

        using (var currentResponse = await SendAsync(
                   client,
                   HttpMethod.Get,
                   "/api/v1/auth/me",
                   null,
                   session))
        {
            currentResponse.EnsureSuccessStatusCode();
            var current = await currentResponse.Content.ReadFromJsonAsync<CurrentAuthorizationResponse>();
            Assert.Equal("de", current?.Locale);
            Assert.Equal("Europe/Berlin", current?.TimeZone);
        }

        await using var context = CreateContext(connectionString);
        var user = await context.Set<UserEntity>().SingleAsync(
            value => value.NormalizedEmail == AdminEmail.ToUpperInvariant());
        Assert.Equal("de", user.Locale);
        Assert.Equal("Europe/Berlin", user.TimeZone);
        var audit = await context.Set<AuditEventEntity>().SingleAsync(
            value => value.Action == "identity.preferences_update");
        Assert.Equal(user.Id, audit.ActorUserId);
        Assert.Equal("updated", audit.Outcome);
        Assert.DoesNotContain(AdminPassword, audit.SummaryJson, StringComparison.Ordinal);
    }

    private static async Task<BrowserSession> LoginAsync(HttpClient client)
    {
        using var csrfResponse = await client.GetAsync("/api/v1/auth/csrf");
        csrfResponse.EnsureSuccessStatusCode();
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfTokenResponse>();
        Assert.NotNull(csrf);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest { Email = AdminEmail, Password = AdminPassword }),
        };
        request.Headers.Add("Cookie", $"{LocalAuthApiConstants.CsrfCookieName}={csrf.Token}");
        request.Headers.Add(LocalAuthApiConstants.CsrfHeaderName, csrf.Token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<AuthSessionResponse>();
        Assert.NotNull(payload);
        return new BrowserSession(
            ReadSetCookie(response, LocalAuthApiConstants.AccessCookieName),
            payload.CsrfToken);
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object? body,
        BrowserSession session,
        bool includeCsrf = true)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        request.Headers.Add("Cookie",
            $"{LocalAuthApiConstants.AccessCookieName}={session.AccessToken}; " +
            $"{LocalAuthApiConstants.CsrfCookieName}={session.CsrfToken}");
        if (includeCsrf) request.Headers.Add(LocalAuthApiConstants.CsrfHeaderName, session.CsrfToken);
        return client.SendAsync(request);
    }

    private static string ReadSetCookie(HttpResponseMessage response, string name)
    {
        var header = Assert.Single(response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(name + "=", StringComparison.Ordinal));
        return header.Split(';', 2)[0][(name.Length + 1)..];
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    private static ArrControlDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ArrControlDbContext>().UseNpgsql(connectionString).Options);

    private sealed record BrowserSession(string AccessToken, string CsrfToken);
}
