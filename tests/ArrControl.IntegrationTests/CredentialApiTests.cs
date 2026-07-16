using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArrControl.Api.Connections;
using ArrControl.Api.Identity;
using ArrControl.Application.Connections;
using ArrControl.Application.Providers;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Connections;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ArrControl.IntegrationTests;

public sealed class CredentialApiTests(AuthApiDatabaseFixture databaseFixture)
    : IClassFixture<AuthApiDatabaseFixture>
{
    private const string AdminEmail = "credential-admin@example.invalid";
    private const string AdminPassword = "correct horse battery staple 456!";

    [Fact]
    public async Task Api_credentials_are_encrypted_write_only_scoped_and_audited()
    {
        var connectionString = await databaseFixture.CreateMigratedSchemaAsync();
        using var keyFile = TemporaryKeyFile.Create();
        var providerTransport = new StubProviderTransport();
        using var factory = new AuthApiFactory(
            connectionString,
            AdminEmail,
            AdminPassword,
            keyFile.Path,
            services =>
            {
                services.RemoveAll<IProviderApiTransport>();
                services.AddSingleton<IProviderApiTransport>(providerTransport);
            });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });
        var session = await LoginAsync(client);
        var instanceId = await SeedInstanceAsync(connectionString);
        var firstSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

        using (var created = await SendMutationAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/v1/instances/{instanceId}/credentials/{CredentialPurposes.ApiKey}",
                   new WriteCredentialRequest { Secret = firstSecret },
                   session))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var body = await created.Content.ReadAsStringAsync();
            Assert.DoesNotContain(firstSecret, body, StringComparison.Ordinal);
            Assert.DoesNotContain("ciphertext", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("nonce", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tag", body, StringComparison.OrdinalIgnoreCase);
            var metadata = await created.Content.ReadFromJsonAsync<CredentialMetadata>();
            Assert.NotNull(metadata);
            Assert.True(metadata.Configured);
            Assert.Equal(CredentialPurposes.ApiKey, metadata.Purpose);
        }

        byte[] firstNonce;
        await using (var context = CreateContext(connectionString))
        {
            var stored = await context.Set<CredentialEntity>().AsNoTracking().SingleAsync();
            firstNonce = stored.Nonce;
            Assert.Equal(12, stored.Nonce.Length);
            Assert.Equal(16, stored.Tag.Length);
            Assert.Equal(1, stored.KeyVersion);
            Assert.NotEqual(Encoding.UTF8.GetBytes(firstSecret), stored.Ciphertext);
            Assert.DoesNotContain(
                firstSecret,
                Convert.ToBase64String(stored.Ciphertext),
                StringComparison.Ordinal);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<CredentialService>();
            Assert.Equal(
                firstSecret,
                (await service.ReadForProviderAsync(
                    instanceId,
                    CredentialPurposes.ApiKey,
                    CancellationToken.None))?.Value);
        }

        using (var probeResponse = await SendMutationAsync<object>(
                   client,
                   HttpMethod.Post,
                   $"/api/v1/instances/{instanceId}/probe",
                   null,
                   session))
        {
            probeResponse.EnsureSuccessStatusCode();
            Assert.DoesNotContain(
                firstSecret,
                await probeResponse.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
            var probe = await probeResponse.Content.ReadFromJsonAsync<ConnectionProbeObservation>();
            Assert.NotNull(probe);
            Assert.True(probe.Connected);
            Assert.Equal("4.0.19.2979", probe.ProviderVersion);
            Assert.Single(probe.HealthIssues ?? []);
            Assert.Contains(probe.Capabilities, value =>
                value.Capability == ProviderCapabilities.Health && value.Supported);
        }

        Assert.Equal(firstSecret, providerTransport.ApiKey);
        await using (var context = CreateContext(connectionString))
        {
            Assert.Equal(
                [ProviderCapabilities.Health, ProviderCapabilities.History, ProviderCapabilities.Library, ProviderCapabilities.Missing, ProviderCapabilities.Probe, ProviderCapabilities.Queue, ProviderCapabilities.Search],
                await context.Set<ProviderCapabilityEntity>()
                    .AsNoTracking()
                    .OrderBy(capability => capability.Capability)
                    .Select(capability => capability.Capability)
                    .ToArrayAsync());
        }

        var secondSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        using (var updated = await SendMutationAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/v1/instances/{instanceId}/credentials/{CredentialPurposes.ApiKey}",
                   new WriteCredentialRequest { Secret = secondSecret },
                   session))
        {
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            Assert.DoesNotContain(
                secondSecret,
                await updated.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }

        await using (var context = CreateContext(connectionString))
        {
            var stored = await context.Set<CredentialEntity>().SingleAsync();
            Assert.NotEqual(firstNonce, stored.Nonce);
            var tamperedTag = stored.Tag.ToArray();
            tamperedTag[0] ^= 0x80;
            stored.Tag = tamperedTag;
            await context.SaveChangesAsync();
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<CredentialService>();
            await Assert.ThrowsAsync<CredentialDecryptionException>(() =>
                service.ReadForProviderAsync(
                    instanceId,
                    CredentialPurposes.ApiKey,
                    CancellationToken.None));
        }

        using (var metadataResponse = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/v1/instances/{instanceId}/credentials",
                   session))
        {
            metadataResponse.EnsureSuccessStatusCode();
            var body = await metadataResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain(firstSecret, body, StringComparison.Ordinal);
            Assert.DoesNotContain(secondSecret, body, StringComparison.Ordinal);
            var metadata = await metadataResponse.Content.ReadFromJsonAsync<CredentialMetadata[]>();
            Assert.Single(Assert.IsType<CredentialMetadata[]>(metadata));
        }

        await using (var context = CreateContext(connectionString))
        {
            var auditText = string.Join(
                '\n',
                await context.Set<AuditEventEntity>()
                    .AsNoTracking()
                    .Where(audit => audit.Action.StartsWith("connection."))
                    .Select(audit => audit.ScopeJson + audit.SummaryJson)
                    .ToListAsync());
            Assert.DoesNotContain(firstSecret, auditText, StringComparison.Ordinal);
            Assert.DoesNotContain(secondSecret, auditText, StringComparison.Ordinal);
            Assert.DoesNotContain("ciphertext", auditText, StringComparison.OrdinalIgnoreCase);
        }

        using (var deleted = await SendMutationAsync<object>(
                   client,
                   HttpMethod.Delete,
                   $"/api/v1/instances/{instanceId}/credentials/{CredentialPurposes.ApiKey}",
                   null,
                   session))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        await using (var context = CreateContext(connectionString))
        {
            var deleteSummary = await context.Set<AuditEventEntity>()
                .AsNoTracking()
                .Where(audit => audit.Action == "connection.credential_delete")
                .Select(audit => audit.SummaryJson)
                .SingleAsync();
            using var document = JsonDocument.Parse(deleteSummary);
            Assert.False(document.RootElement.GetProperty("configured").GetBoolean());
        }

        using (var metadataResponse = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/v1/instances/{instanceId}/credentials",
                   session))
        {
            metadataResponse.EnsureSuccessStatusCode();
            Assert.Empty(await metadataResponse.Content.ReadFromJsonAsync<CredentialMetadata[]>() ?? []);
        }
    }

    private static async Task<Guid> SeedInstanceAsync(string connectionString)
    {
        var now = DateTimeOffset.UtcNow;
        var instance = new InstanceEntity
        {
            Name = "Credential target",
            Kind = "sonarr",
            BaseUrl = "https://credential-target.example.invalid/",
            Enabled = true,
            TlsVerificationEnabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var context = CreateContext(connectionString);
        context.Add(instance);
        await context.SaveChangesAsync();
        return instance.Id;
    }

    private static ArrControlDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(connectionString)
            .Options);

    private static async Task<BrowserSession> LoginAsync(HttpClient client)
    {
        using var csrfResponse = await client.GetAsync("/api/v1/auth/csrf");
        csrfResponse.EnsureSuccessStatusCode();
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfTokenResponse>();
        Assert.NotNull(csrf);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest
            {
                Email = AdminEmail,
                Password = AdminPassword,
            }),
        };
        request.Headers.Add(
            "Cookie",
            $"{LocalAuthApiConstants.CsrfCookieName}={csrf.Token}");
        request.Headers.Add(LocalAuthApiConstants.CsrfHeaderName, csrf.Token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<AuthSessionResponse>();
        Assert.NotNull(payload);
        return new BrowserSession(
            ReadSetCookie(response, LocalAuthApiConstants.AccessCookieName),
            payload.CsrfToken);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        BrowserSession session)
    {
        var request = new HttpRequestMessage(method, path);
        AddSession(request, session, includeCsrf: false);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendMutationAsync<T>(
        HttpClient client,
        HttpMethod method,
        string path,
        T? body,
        BrowserSession session)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        AddSession(request, session, includeCsrf: true);
        return await client.SendAsync(request);
    }

    private static void AddSession(
        HttpRequestMessage request,
        BrowserSession session,
        bool includeCsrf)
    {
        request.Headers.Add(
            "Cookie",
            $"{LocalAuthApiConstants.AccessCookieName}={session.AccessToken}; "
            + $"{LocalAuthApiConstants.CsrfCookieName}={session.CsrfToken}");
        if (includeCsrf)
        {
            request.Headers.Add(LocalAuthApiConstants.CsrfHeaderName, session.CsrfToken);
        }
    }

    private static string ReadSetCookie(HttpResponseMessage response, string name)
    {
        var header = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(name + "=", StringComparison.Ordinal));
        return header.Split(';', 2)[0][(name.Length + 1)..];
    }

    private sealed record BrowserSession(string AccessToken, string CsrfToken);

    private sealed class TemporaryKeyFile : IDisposable
    {
        private TemporaryKeyFile(string path) => Path = path;

        public string Path { get; }

        public static TemporaryKeyFile Create()
        {
            var path = System.IO.Path.GetTempFileName();
            File.WriteAllText(
                path,
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + Environment.NewLine);
            return new TemporaryKeyFile(path);
        }

        public void Dispose() => File.Delete(Path);
    }

    private sealed class StubProviderTransport : IProviderApiTransport
    {
        public string? ApiKey { get; private set; }

        public Task<ProviderTransportResponse> GetAsync(
            ProviderConnection connection,
            string relativePath,
            CancellationToken cancellationToken)
        {
            ApiKey = connection.ApiKey;
            var body = relativePath switch
            {
                "api/v3/system/status" =>
                    """{"appName":"Sonarr","instanceName":"Television","version":"4.0.19.2979","branch":"main","unknownField":true}""",
                "api/v3/health" =>
                    """[{"id":1,"source":"DownloadClientCheck","type":"warning","message":"Unavailable","wikiUrl":"https://wiki.servarr.com/sonarr/system#health"}]""",
                _ => throw new InvalidOperationException("Unexpected provider path."),
            };
            return Task.FromResult(new ProviderTransportResponse(
                200,
                Encoding.UTF8.GetBytes(body),
                null));
        }
    }
}
