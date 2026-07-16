using System.Net;
using System.Net.Http.Json;
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

public sealed class InstanceManagementApiTests(AuthApiDatabaseFixture databaseFixture)
    : IClassFixture<AuthApiDatabaseFixture>
{
    private const string AdminEmail = "instance-admin@example.invalid";
    private const string AdminPassword = "correct horse battery staple 789!";

    [Fact]
    public async Task Crud_ssrf_probe_capabilities_groups_and_audit_form_one_scoped_slice()
    {
        var connectionString = await databaseFixture.CreateMigratedSchemaAsync();
        var observedAt = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        var probe = new StubProbeTransport(new ConnectionProbeObservation(
            true,
            "connected",
            401,
            observedAt,
            [new ProviderCapabilityObservation(ProviderCapabilities.Probe, true, observedAt)]));
        using var factory = new AuthApiFactory(
            connectionString,
            AdminEmail,
            AdminPassword,
            configureTestServices: services =>
            {
                services.RemoveAll<IConnectionProbeTransport>();
                services.RemoveAll<IProviderConnectionAdapter>();
                services.AddSingleton<IConnectionProbeTransport>(probe);
            });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });
        var session = await LoginAsync(client);

        using (var openApi = await client.GetAsync("/api/openapi/v1.json"))
        {
            openApi.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await openApi.Content.ReadAsStringAsync());
            var paths = document.RootElement.GetProperty("paths");
            Assert.True(paths.TryGetProperty("/api/v1/instances/{instanceId}", out _));
            Assert.True(paths.TryGetProperty("/api/v1/instances/{instanceId}/probe", out _));
            Assert.True(paths.TryGetProperty("/api/v1/instance-groups/{instanceGroupId}", out _));
        }

        using (var blocked = await SendMutationAsync(
                   client,
                   HttpMethod.Post,
                   "/api/v1/instances",
                   new WriteInstanceRequest
                   {
                       Name = "Metadata target",
                       Kind = "sonarr",
                       BaseUrl = "http://169.254.169.254/",
                       AllowPrivateNetworkAccess = true,
                   },
                   session))
        {
            Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
            Assert.Equal("outbound_address_blocked", await ReadProblemCodeAsync(blocked));
        }

        var groupId = Guid.CreateVersion7();
        using (var createdGroup = await SendMutationAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/v1/instance-groups/{groupId}",
                   new WriteInstanceGroupRequest { Name = "Living room" },
                   session))
        {
            Assert.Equal(HttpStatusCode.Created, createdGroup.StatusCode);
        }

        InstanceDetails instance;
        using (var created = await SendMutationAsync(
                   client,
                   HttpMethod.Post,
                   "/api/v1/instances",
                   new WriteInstanceRequest
                   {
                       Name = "Sonarr Home",
                       Kind = "SONARR",
                       BaseUrl = "http://10.0.0.5/sonarr",
                       InstanceGroupId = groupId,
                       AllowPrivateNetworkAccess = true,
                   },
                   session))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            instance = Assert.IsType<InstanceDetails>(
                await created.Content.ReadFromJsonAsync<InstanceDetails>());
            Assert.Equal("sonarr", instance.Kind);
            Assert.Equal("http://10.0.0.5/sonarr/", instance.BaseUrl);
            Assert.True(instance.TlsVerificationEnabled);
            Assert.Empty(instance.Capabilities);
        }

        using (var conflict = await SendMutationAsync(
                   client,
                   HttpMethod.Post,
                   "/api/v1/instances",
                   new WriteInstanceRequest
                   {
                       Name = "Sonarr Home",
                       Kind = "radarr",
                       BaseUrl = "https://8.8.8.8/",
                   },
                   session))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("instance_conflict", await ReadProblemCodeAsync(conflict));
        }

        using (var updated = await SendMutationAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/v1/instances/{instance.Id}",
                   new WriteInstanceRequest
                   {
                       Name = "Sonarr Main",
                       Kind = "sonarr",
                       BaseUrl = "http://10.0.0.6/sonarr/",
                       Enabled = false,
                       InstanceGroupId = groupId,
                       TlsVerificationEnabled = true,
                       AllowPrivateNetworkAccess = true,
                   },
                   session))
        {
            updated.EnsureSuccessStatusCode();
            Assert.False((await updated.Content.ReadFromJsonAsync<InstanceDetails>())!.Enabled);
        }

        using (var probed = await SendMutationAsync<object>(
                   client,
                   HttpMethod.Post,
                   $"/api/v1/instances/{instance.Id}/probe",
                   null,
                   session))
        {
            probed.EnsureSuccessStatusCode();
            var result = await probed.Content.ReadFromJsonAsync<ConnectionProbeObservation>();
            Assert.NotNull(result);
            Assert.True(result.Connected);
            Assert.Equal(401, result.HttpStatusCode);
        }

        Assert.Equal("http://10.0.0.6/sonarr/", probe.Target?.Uri.AbsoluteUri);
        Assert.Equal(IPAddress.Parse("10.0.0.6"), Assert.Single(probe.Target!.Addresses));
        await using (var context = CreateContext(connectionString))
        {
            var capability = await context.Set<ProviderCapabilityEntity>()
                .AsNoTracking()
                .SingleAsync();
            Assert.Equal(ProviderCapabilities.Probe, capability.Capability);
            Assert.True(capability.Supported);
            Assert.Equal(observedAt, capability.ObservedAt);
            var auditText = string.Join(
                '\n',
                await context.Set<AuditEventEntity>()
                    .AsNoTracking()
                    .Where(audit => audit.Action.StartsWith("connection.instance"))
                    .Select(audit => audit.ScopeJson + audit.SummaryJson)
                    .ToListAsync());
            Assert.DoesNotContain("10.0.0.6", auditText, StringComparison.Ordinal);
        }

        using (var connectionChanged = await SendMutationAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/v1/instances/{instance.Id}",
                   new WriteInstanceRequest
                   {
                       Name = "Sonarr Main",
                       Kind = "sonarr",
                       BaseUrl = "http://10.0.0.7/sonarr/",
                       Enabled = false,
                       InstanceGroupId = groupId,
                       TlsVerificationEnabled = true,
                       AllowPrivateNetworkAccess = true,
                   },
                   session))
        {
            connectionChanged.EnsureSuccessStatusCode();
            Assert.Empty((await connectionChanged.Content.ReadFromJsonAsync<InstanceDetails>())!
                .Capabilities);
        }

        await using (var context = CreateContext(connectionString))
        {
            Assert.False(await context.Set<ProviderCapabilityEntity>().AnyAsync());
        }

        using (var groupInUse = await SendMutationAsync<object>(
                   client,
                   HttpMethod.Delete,
                   $"/api/v1/instance-groups/{groupId}",
                   null,
                   session))
        {
            Assert.Equal(HttpStatusCode.Conflict, groupInUse.StatusCode);
            Assert.Equal("instance_group_in_use", await ReadProblemCodeAsync(groupInUse));
        }

        using (var deleted = await SendMutationAsync<object>(
                   client,
                   HttpMethod.Delete,
                   $"/api/v1/instances/{instance.Id}",
                   null,
                   session))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var deletedGroup = await SendMutationAsync<object>(
                   client,
                   HttpMethod.Delete,
                   $"/api/v1/instance-groups/{groupId}",
                   null,
                   session))
        {
            Assert.Equal(HttpStatusCode.NoContent, deletedGroup.StatusCode);
        }

        await using (var context = CreateContext(connectionString))
        {
            Assert.False(await context.Instances.AnyAsync());
            Assert.False(await context.Set<ProviderCapabilityEntity>().AnyAsync());
        }
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
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest
            {
                Email = AdminEmail,
                Password = AdminPassword,
            }),
        };
        request.Headers.Add("Cookie", $"{LocalAuthApiConstants.CsrfCookieName}={csrf!.Token}");
        request.Headers.Add(LocalAuthApiConstants.CsrfHeaderName, csrf.Token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<AuthSessionResponse>();
        return new BrowserSession(
            ReadSetCookie(response, LocalAuthApiConstants.AccessCookieName),
            payload!.CsrfToken);
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

        request.Headers.Add(
            "Cookie",
            $"{LocalAuthApiConstants.AccessCookieName}={session.AccessToken}; "
            + $"{LocalAuthApiConstants.CsrfCookieName}={session.CsrfToken}");
        request.Headers.Add(LocalAuthApiConstants.CsrfHeaderName, session.CsrfToken);
        return await client.SendAsync(request);
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    private static string ReadSetCookie(HttpResponseMessage response, string name)
    {
        var header = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(name + "=", StringComparison.Ordinal));
        return header.Split(';', 2)[0][(name.Length + 1)..];
    }

    private sealed record BrowserSession(string AccessToken, string CsrfToken);

    private sealed class StubProbeTransport(ConnectionProbeObservation observation)
        : IConnectionProbeTransport
    {
        public ResolvedOutboundTarget? Target { get; private set; }

        public Task<ConnectionProbeObservation> ProbeAsync(
            ResolvedOutboundTarget target,
            bool tlsVerificationEnabled,
            CancellationToken cancellationToken)
        {
            Target = target;
            return Task.FromResult(observation);
        }
    }
}
