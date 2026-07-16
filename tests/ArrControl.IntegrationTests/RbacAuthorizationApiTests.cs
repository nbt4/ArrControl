using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text.Json;
using ArrControl.Api.Authorization;
using ArrControl.Api.Identity;
using ArrControl.Application.Authorization;
using ArrControl.Application.Audit;
using ArrControl.Application.Connections;
using ArrControl.Application.Identity;
using ArrControl.Infrastructure.Identity;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Connections;
using ArrControl.Infrastructure.Persistence.Identity;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArrControl.IntegrationTests;

public sealed class RbacAuthorizationApiTests(AuthApiDatabaseFixture databaseFixture)
    : IClassFixture<AuthApiDatabaseFixture>
{
    private const string AdminEmail = "rbac-admin@example.invalid";
    private const string AdminPassword = "correct horse battery staple 123!";

    [Fact]
    public async Task Rbac_catalog_administration_and_instance_scopes_are_enforced_immediately()
    {
        var connectionString = await databaseFixture.CreateMigratedSchemaAsync();
        using var factory = new AuthApiFactory(connectionString, AdminEmail, AdminPassword);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

        using (var anonymous = await client.GetAsync("/api/v1/instances"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal("authentication_required", await ReadProblemCodeAsync(anonymous));
        }

        using (var anonymousMissing = await client.GetAsync("/api/v1/missing"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousMissing.StatusCode);
        }

        using (var openApi = await client.GetAsync("/api/openapi/v1.json"))
        {
            openApi.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await openApi.Content.ReadAsStringAsync());
            var paths = document.RootElement.GetProperty("paths");
            foreach (var path in new[]
                     {
                         "/api/v1/auth/me",
                         "/api/v1/authorization/permissions",
                         "/api/v1/authorization/roles",
                         "/api/v1/authorization/roles/{roleId}",
                         "/api/v1/authorization/users",
                         "/api/v1/authorization/users/{userId}/role-assignments",
                         "/api/v1/authorization/instance-groups",
                         "/api/v1/instances",
                         "/api/v1/missing",
                         "/api/v1/missing/views",
                         "/api/v1/missing/views/{viewId}",
                         "/api/v1/queue",
                         "/api/v1/history",
                         "/api/v1/events",
                         "/api/v1/events/snapshot",
                         "/api/v1/audit",
                         "/api/v1/diagnostics/export",
                         "/api/v1/health/incidents",
                         "/api/v1/health/incidents/{incidentId}/acknowledgement",
                         "/api/v1/health/incidents/{incidentId}/snooze",
                         "/api/v1/operations/search",
                         "/api/v1/operations/search/preview",
                     })
            {
                Assert.True(paths.TryGetProperty(path, out _), $"Missing OpenAPI path {path}.");
            }

            Assert.True(paths.GetProperty("/api/v1/instances")
                .GetProperty("get")
                .GetProperty("responses")
                .TryGetProperty("403", out _));
        }

        var adminSession = await LoginAsync(client);
        using (var liveSnapshot = await SendAsync(
                   client, HttpMethod.Get, "/api/v1/events/snapshot", null, adminSession))
        {
            liveSnapshot.EnsureSuccessStatusCode();
            var snapshot = await liveSnapshot.Content.ReadFromJsonAsync<ArrControl.Application.Events.LiveSnapshot>();
            Assert.NotNull(snapshot);
            Assert.Equal(1, snapshot.Version);
            Assert.Contains("/api/v1/health/incidents", snapshot.Resources);
        }
        using (var snapshotRequired = await SendAsync(
                   client, HttpMethod.Get, "/api/v1/events", null, adminSession))
        {
            snapshotRequired.EnsureSuccessStatusCode();
            Assert.Equal("text/event-stream", snapshotRequired.Content.Headers.ContentType?.MediaType);
            Assert.Contains("event: snapshot-required", await snapshotRequired.Content.ReadAsStringAsync());
        }
        using (var audit = await SendAsync(
                   client, HttpMethod.Get, "/api/v1/audit?limit=5", null, adminSession))
        {
            audit.EnsureSuccessStatusCode();
            Assert.NotNull(await audit.Content.ReadFromJsonAsync<AuditPage>());
        }
        using (var diagnostics = await SendAsync(
                   client, HttpMethod.Post, "/api/v1/diagnostics/export",
                   new DiagnosticsExportRequest(1, false), adminSession))
        {
            diagnostics.EnsureSuccessStatusCode();
            Assert.Equal("application/zip", diagnostics.Content.Headers.ContentType?.MediaType);
            await using var content = await diagnostics.Content.ReadAsStreamAsync();
            using var archive = new ZipArchive(content, ZipArchiveMode.Read);
            var entry = Assert.Single(archive.Entries);
            using var reader = new StreamReader(entry.Open());
            var exported = await reader.ReadToEndAsync();
            Assert.Contains("strict-v1", exported, StringComparison.Ordinal);
            Assert.DoesNotContain(AdminEmail, exported, StringComparison.OrdinalIgnoreCase);
        }
        var customRoleId = Guid.CreateVersion7();
        using (var missingCsrf = await SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/v1/authorization/roles/{customRoleId}",
                   new UpsertAuthorizationRoleRequest(
                       "Group Readers",
                       [RbacPermissions.InstancesRead]),
                   adminSession,
                   includeCsrf: false))
        {
            Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
            Assert.Equal("csrf_validation_failed", await ReadProblemCodeAsync(missingCsrf));
        }

        using (var created = await SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/v1/authorization/roles/{customRoleId}",
                   new UpsertAuthorizationRoleRequest(
                       "Group Readers",
                       [RbacPermissions.InstancesRead]),
                   adminSession))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var role = await created.Content.ReadFromJsonAsync<AuthorizationRole>();
            Assert.NotNull(role);
            Assert.Equal(customRoleId, role.Id);
            Assert.False(role.IsSystem);
            Assert.Equal(
                $"/api/v1/authorization/roles/{customRoleId}",
                created.Headers.Location?.OriginalString);
        }

        using (var fetched = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/v1/authorization/roles/{customRoleId}",
                   null,
                   adminSession))
        {
            fetched.EnsureSuccessStatusCode();
            Assert.Equal(
                customRoleId,
                (await fetched.Content.ReadFromJsonAsync<AuthorizationRole>())?.Id);
        }

        var seeded = await SeedScopedUserAndInstancesAsync(connectionString, customRoleId);
        await AssertCatalogAsync(connectionString);

        using (var current = await SendAsync(
                   client,
                   HttpMethod.Get,
                   "/api/v1/auth/me",
                   null,
                   adminSession))
        {
            current.EnsureSuccessStatusCode();
            Assert.True(current.Headers.CacheControl?.NoStore);
            var payload = await current.Content.ReadFromJsonAsync<CurrentAuthorizationResponse>();
            Assert.NotNull(payload);
            Assert.Contains(
                payload.Permissions,
                grant => grant.Code == RbacPermissions.AuthorizationManage && grant.Global);
        }

        using (var globalInstances = await SendAsync(
                   client,
                   HttpMethod.Get,
                   "/api/v1/instances",
                   null,
                   adminSession))
        {
            globalInstances.EnsureSuccessStatusCode();
            var instances = await globalInstances.Content.ReadFromJsonAsync<VisibleInstance[]>();
            Assert.NotNull(instances);
            Assert.Equal(3, instances.Length);
        }

        using (var scopedInstances = await SendAsync(
                   client,
                   HttpMethod.Get,
                   "/api/v1/instances",
                   null,
                   seeded.Session))
        {
            scopedInstances.EnsureSuccessStatusCode();
            var instances = await scopedInstances.Content.ReadFromJsonAsync<VisibleInstance[]>();
            var instance = Assert.Single(Assert.IsType<VisibleInstance[]>(instances));
            Assert.Equal(seeded.VisibleInstanceId, instance.Id);
            Assert.Equal(seeded.GroupId, instance.InstanceGroupId);
        }

        using (var current = await SendAsync(
                   client,
                   HttpMethod.Get,
                   "/api/v1/auth/me",
                   null,
                   seeded.Session))
        {
            current.EnsureSuccessStatusCode();
            var payload = await current.Content.ReadFromJsonAsync<CurrentAuthorizationResponse>();
            Assert.NotNull(payload);
            var grant = Assert.Single(payload.Permissions);
            Assert.Equal(RbacPermissions.InstancesRead, grant.Code);
            Assert.False(grant.Global);
            Assert.Equal(seeded.GroupId, Assert.Single(grant.InstanceGroupIds));
        }

        using (var revoked = await SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/v1/authorization/users/{seeded.UserId}/role-assignments",
                   new ReplaceRoleAssignmentsRequest([]),
                   adminSession))
        {
            revoked.EnsureSuccessStatusCode();
            Assert.Empty(await revoked.Content.ReadFromJsonAsync<RoleAssignment[]>() ?? []);
        }

        using (var denied = await SendAsync(
                   client,
                   HttpMethod.Get,
                   "/api/v1/instances",
                   null,
                   seeded.Session))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal("access_denied", await ReadProblemCodeAsync(denied));
        }

        var oidcSession = await SeedOidcRoleSessionAsync(
            connectionString,
            seeded.UserId,
            customRoleId);
        using (var localStillDenied = await SendAsync(
                   client,
                   HttpMethod.Get,
                   "/api/v1/instances",
                   null,
                   seeded.Session))
        {
            Assert.Equal(HttpStatusCode.Forbidden, localStillDenied.StatusCode);
        }

        using (var oidcInstances = await SendAsync(
                   client,
                   HttpMethod.Get,
                   "/api/v1/instances",
                   null,
                   oidcSession))
        {
            oidcInstances.EnsureSuccessStatusCode();
            Assert.Equal(
                3,
                (await oidcInstances.Content.ReadFromJsonAsync<VisibleInstance[]>())?.Length);
        }

        Guid adminUserId;
        Guid administratorRoleId;
        await using (var context = CreateContext(connectionString))
        {
            adminUserId = await context.Set<UserEntity>()
                .Where(user => user.NormalizedEmail == AdminEmail.ToUpperInvariant())
                .Select(user => user.Id)
                .SingleAsync();
            administratorRoleId = await context.Set<RoleEntity>()
                .Where(role => role.NormalizedName == RbacSystemRoles.AdministratorNormalized)
                .Select(role => role.Id)
                .SingleAsync();
        }

        using (var immutable = await SendAsync(
                   client,
                   HttpMethod.Delete,
                   $"/api/v1/authorization/roles/{administratorRoleId}",
                   null,
                   adminSession))
        {
            Assert.Equal(HttpStatusCode.Conflict, immutable.StatusCode);
            Assert.Equal("system_role_immutable", await ReadProblemCodeAsync(immutable));
        }

        using (var lockout = await SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/v1/authorization/users/{adminUserId}/role-assignments",
                   new ReplaceRoleAssignmentsRequest([]),
                   adminSession))
        {
            Assert.Equal(HttpStatusCode.Conflict, lockout.StatusCode);
            Assert.Equal("authorization_lockout", await ReadProblemCodeAsync(lockout));
        }

        await using (var context = CreateContext(connectionString))
        {
            Assert.True(await context.Set<UserRoleEntity>().AnyAsync(assignment =>
                assignment.UserId == adminUserId
                && assignment.RoleId == administratorRoleId
                && assignment.InstanceGroupId == null));
            Assert.Contains(
                await context.Set<AuditEventEntity>()
                    .AsNoTracking()
                    .Where(audit => audit.Action.StartsWith("authorization."))
                    .Select(audit => audit.Action)
                    .ToListAsync(),
                action => action == "authorization.assignments_replace");
        }
    }

    private static async Task<SeedResult> SeedScopedUserAndInstancesAsync(
        string connectionString,
        Guid roleId)
    {
        var now = DateTimeOffset.UtcNow;
        var visibleGroup = new InstanceGroupEntity
        {
            Name = "Visible group",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var hiddenGroup = new InstanceGroupEntity
        {
            Name = "Hidden group",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var user = new UserEntity
        {
            Email = "scoped-viewer@example.invalid",
            NormalizedEmail = "SCOPED-VIEWER@EXAMPLE.INVALID",
            Locale = "en",
            TimeZone = "UTC",
            State = LocalIdentityConstants.ActiveUserState,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var visibleInstance = Instance("Visible", visibleGroup, now);
        var hiddenInstance = Instance("Hidden", hiddenGroup, now);
        var ungroupedInstance = Instance("Ungrouped", null, now);
        var tokenService = new SecureSessionTokenService();
        var accessToken = tokenService.Issue();
        var refreshToken = tokenService.Issue();
        var session = new UserSessionEntity
        {
            User = user,
            TokenFamilyId = Guid.CreateVersion7(),
            AccessTokenHash = accessToken.Hash,
            AccessExpiresAt = now.AddMinutes(15),
            RefreshTokenHash = refreshToken.Hash,
            CreatedAt = now,
            ExpiresAt = now.AddDays(1),
            AuthenticationMethod = LocalIdentityConstants.LocalAuthenticationMethod,
        };

        await using var context = CreateContext(connectionString);
        context.AddRange(visibleGroup, hiddenGroup, visibleInstance, hiddenInstance, ungroupedInstance, user);
        context.Add(new UserRoleEntity
        {
            User = user,
            RoleId = roleId,
            InstanceGroup = visibleGroup,
            CreatedAt = now,
        });
        context.Add(session);
        await context.SaveChangesAsync();
        return new SeedResult(
            user.Id,
            visibleGroup.Id,
            visibleInstance.Id,
            new BrowserSession(accessToken.Value, null));
    }

    private static InstanceEntity Instance(
        string name,
        InstanceGroupEntity? group,
        DateTimeOffset now) =>
        new()
        {
            Name = name,
            Kind = "sonarr",
            BaseUrl = $"https://{name.ToLowerInvariant()}.example.invalid/",
            Enabled = true,
            TlsVerificationEnabled = true,
            Group = group,
            CreatedAt = now,
            UpdatedAt = now,
        };

    private static async Task<BrowserSession> SeedOidcRoleSessionAsync(
        string connectionString,
        Guid userId,
        Guid roleId)
    {
        var now = DateTimeOffset.UtcNow;
        var tokenService = new SecureSessionTokenService();
        var accessToken = tokenService.Issue();
        var refreshToken = tokenService.Issue();
        var familyId = Guid.CreateVersion7();
        var externalIdentity = new ExternalIdentityEntity
        {
            UserId = userId,
            Issuer = "https://auth.example.invalid/application/o/arrcontrol/",
            Subject = "rbac-scoped-user",
            ClaimsVersion = 1,
            CreatedAt = now,
            LastAuthenticatedAt = now,
        };
        var session = new UserSessionEntity
        {
            UserId = userId,
            TokenFamilyId = familyId,
            AccessTokenHash = accessToken.Hash,
            AccessExpiresAt = now.AddMinutes(15),
            RefreshTokenHash = refreshToken.Hash,
            CreatedAt = now,
            ExpiresAt = now.AddDays(1),
            AuthenticationMethod = LocalIdentityConstants.OidcAuthenticationMethod,
        };

        await using var context = CreateContext(connectionString);
        context.Add(externalIdentity);
        context.Add(new ExternalIdentityRoleEntity
        {
            ExternalIdentity = externalIdentity,
            RoleId = roleId,
            CreatedAt = now,
        });
        context.Add(session);
        context.Add(new OidcSessionContextEntity
        {
            TokenFamilyId = familyId,
            ExternalIdentity = externalIdentity,
            ProtectedIdToken = "protected-test-id-token",
            CreatedAt = now,
            ExpiresAt = now.AddDays(1),
        });
        await context.SaveChangesAsync();
        return new BrowserSession(accessToken.Value, null);
    }

    private static async Task AssertCatalogAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        Assert.Equal(10, await context.Set<PermissionEntity>().CountAsync());
        var roles = await context.Set<RoleEntity>()
            .AsNoTracking()
            .Include(role => role.Permissions)
            .ThenInclude(rolePermission => rolePermission.Permission)
            .Where(role => role.IsSystem)
            .ToListAsync();
        Assert.Equal(3, roles.Count);
        Assert.Equal(
            10,
            Assert.Single(roles, role =>
                role.NormalizedName == RbacSystemRoles.AdministratorNormalized).Permissions.Count);
        Assert.Equal(
            5,
            Assert.Single(roles, role =>
                role.NormalizedName == RbacSystemRoles.OperatorNormalized).Permissions.Count);
        Assert.Equal(
            2,
            Assert.Single(roles, role =>
                role.NormalizedName == RbacSystemRoles.ViewerNormalized).Permissions.Count);
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
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest
            {
                Email = AdminEmail,
                Password = AdminPassword,
            }),
        };
        login.Headers.Add(
            "Cookie",
            $"{LocalAuthApiConstants.CsrfCookieName}={csrf.Token}");
        login.Headers.Add(LocalAuthApiConstants.CsrfHeaderName, csrf.Token);
        using var response = await client.SendAsync(login);
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
        object? body,
        BrowserSession session,
        bool includeCsrf = true)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        var cookies = $"{LocalAuthApiConstants.AccessCookieName}={session.AccessToken}";
        if (session.CsrfToken is not null)
        {
            cookies += $"; {LocalAuthApiConstants.CsrfCookieName}={session.CsrfToken}";
        }

        request.Headers.Add("Cookie", cookies);
        if (includeCsrf && session.CsrfToken is not null)
        {
            request.Headers.Add(LocalAuthApiConstants.CsrfHeaderName, session.CsrfToken);
        }

        return await client.SendAsync(request);
    }

    private static string ReadSetCookie(HttpResponseMessage response, string name)
    {
        var header = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(name + "=", StringComparison.Ordinal));
        return header.Split(';', 2)[0][(name.Length + 1)..];
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    private sealed record BrowserSession(string AccessToken, string? CsrfToken);

    private sealed record SeedResult(
        Guid UserId,
        Guid GroupId,
        Guid VisibleInstanceId,
        BrowserSession Session);
}
