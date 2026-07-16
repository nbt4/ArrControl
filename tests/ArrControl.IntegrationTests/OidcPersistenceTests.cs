using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArrControl.Application.Identity;
using ArrControl.Infrastructure.Identity;
using ArrControl.Infrastructure.Operations;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Identity;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArrControl.IntegrationTests;

public sealed class OidcPersistenceTests(AuthDatabaseFixture database)
    : IClassFixture<AuthDatabaseFixture>
{
    private static readonly DateTimeOffset ReferenceTime =
        new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Verified_email_links_once_and_existing_identity_reconciles_roles_without_email()
    {
        var options = await CreateMigratedSchemaAsync();
        var user = await SeedUserAsync(options, "linked@example.invalid");
        var firstRole = await SeedRoleAsync(options, "First role", "FIRST ROLE");
        var secondRole = await SeedRoleAsync(options, "Second role", "SECOND ROLE");
        await SeedUserRoleAsync(options, user.Id, firstRole.Id);
        var first = OidcSession(
            "first",
            user.Email,
            user.NormalizedEmail,
            [firstRole.NormalizedName]);

        await using (var firstContext = new ArrControlDbContext(options))
        {
            var result = await CreateOidcStore(firstContext).CreateSessionAsync(
                first,
                Request("oidc-first"),
                ReferenceTime,
                CancellationToken.None);
            Assert.Equal(OidcSessionStoreStatus.Succeeded, result.Status);
            Assert.Equal(user.Id, result.UserId);
        }

        var second = OidcSession(
            "second",
            null,
            null,
            [secondRole.NormalizedName]);
        await using (var secondContext = new ArrControlDbContext(options))
        {
            var result = await CreateOidcStore(secondContext).CreateSessionAsync(
                second,
                Request("oidc-second"),
                ReferenceTime.AddMinutes(1),
                CancellationToken.None);
            Assert.Equal(OidcSessionStoreStatus.Succeeded, result.Status);
            Assert.Equal(user.Id, result.UserId);
        }

        await using var verificationContext = new ArrControlDbContext(options);
        Assert.Single(await verificationContext.Set<UserEntity>().AsNoTracking().ToListAsync());
        var identity = Assert.Single(await verificationContext.Set<ExternalIdentityEntity>()
            .AsNoTracking()
            .ToListAsync());
        Assert.Equal(user.Id, identity.UserId);
        Assert.Equal(1, identity.ClaimsVersion);
        Assert.Equal(ReferenceTime.AddMinutes(1), identity.LastAuthenticatedAt);

        var assignment = Assert.Single(await verificationContext.Set<ExternalIdentityRoleEntity>()
            .AsNoTracking()
            .ToListAsync());
        Assert.Equal(secondRole.Id, assignment.RoleId);
        Assert.NotEqual(firstRole.Id, assignment.RoleId);
        var manualAssignment = Assert.Single(await verificationContext.Set<UserRoleEntity>()
            .AsNoTracking()
            .ToListAsync());
        Assert.Equal(user.Id, manualAssignment.UserId);
        Assert.Equal(firstRole.Id, manualAssignment.RoleId);

        var sessions = await verificationContext.Set<UserSessionEntity>()
            .AsNoTracking()
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();
        Assert.Equal(2, sessions.Count);
        Assert.All(
            sessions,
            x => Assert.Equal(LocalIdentityConstants.OidcAuthenticationMethod, x.AuthenticationMethod));
        Assert.Equal(2, await verificationContext.Set<OidcSessionContextEntity>().CountAsync());

        var audits = await verificationContext.Set<AuditEventEntity>()
            .AsNoTracking()
            .Where(x => x.Action == "identity.oidc_login" && x.Outcome == "succeeded")
            .ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.All(
            audits,
            auditEvent =>
            {
                using var summary = JsonDocument.Parse(auditEvent.SummaryJson);
                Assert.Equal(
                    LocalIdentityConstants.OidcAuthenticationMethod,
                    summary.RootElement.GetProperty("method").GetString());
            });
    }

    [Fact]
    public async Task Unverified_identity_inactive_user_and_missing_role_fail_without_identity_or_session_mutations()
    {
        var unverifiedOptions = await CreateMigratedSchemaAsync();
        await using (var context = new ArrControlDbContext(unverifiedOptions))
        {
            var result = await CreateOidcStore(context).CreateSessionAsync(
                OidcSession("unverified", null, null, []),
                Request("oidc-unverified"),
                ReferenceTime,
                CancellationToken.None);
            Assert.Equal(OidcSessionStoreStatus.UnverifiedIdentity, result.Status);
        }

        await AssertNoOidcStateAsync(unverifiedOptions, expectUsers: 0);

        var inactiveOptions = await CreateMigratedSchemaAsync();
        var inactive = await SeedUserAsync(
            inactiveOptions,
            "inactive@example.invalid",
            state: "disabled");
        await using (var context = new ArrControlDbContext(inactiveOptions))
        {
            var result = await CreateOidcStore(context).CreateSessionAsync(
                OidcSession(
                    "inactive",
                    inactive.Email,
                    inactive.NormalizedEmail,
                    []),
                Request("oidc-inactive"),
                ReferenceTime,
                CancellationToken.None);
            Assert.Equal(OidcSessionStoreStatus.Inactive, result.Status);
        }

        await AssertNoOidcStateAsync(inactiveOptions, expectUsers: 1);

        var missingRoleOptions = await CreateMigratedSchemaAsync();
        await using (var context = new ArrControlDbContext(missingRoleOptions))
        {
            var result = await CreateOidcStore(context).CreateSessionAsync(
                OidcSession(
                    "missing-role",
                    "new@example.invalid",
                    "NEW@EXAMPLE.INVALID",
                    ["ROLE THAT DOES NOT EXIST"]),
                Request("oidc-missing-role"),
                ReferenceTime,
                CancellationToken.None);
            Assert.Equal(OidcSessionStoreStatus.RoleMissing, result.Status);
        }

        await AssertNoOidcStateAsync(missingRoleOptions, expectUsers: 0);
    }

    [Fact]
    public async Task Concurrent_first_login_provisions_one_identity_and_two_independent_sessions()
    {
        var options = await CreateMigratedSchemaAsync();
        var first = OidcSession(
            "race-first",
            "race@example.invalid",
            "RACE@EXAMPLE.INVALID",
            []);
        var second = OidcSession(
            "race-second",
            "race@example.invalid",
            "RACE@EXAMPLE.INVALID",
            []);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstAttempt = CreateWhenReleasedAsync(options, first, "race-first", start.Task);
        var secondAttempt = CreateWhenReleasedAsync(options, second, "race-second", start.Task);
        start.SetResult(true);

        var results = await Task.WhenAll(firstAttempt, secondAttempt);
        Assert.All(results, x => Assert.Equal(OidcSessionStoreStatus.Succeeded, x.Status));
        Assert.Equal(results[0].UserId, results[1].UserId);

        await using var verificationContext = new ArrControlDbContext(options);
        Assert.Single(await verificationContext.Set<UserEntity>().AsNoTracking().ToListAsync());
        Assert.Single(await verificationContext.Set<ExternalIdentityEntity>().AsNoTracking().ToListAsync());
        Assert.Equal(2, await verificationContext.Set<UserSessionEntity>().CountAsync());
        Assert.Equal(2, await verificationContext.Set<OidcSessionContextEntity>().CountAsync());
    }

    [Fact]
    public async Task Concurrent_subjects_with_the_same_verified_email_link_one_user_to_both_identities()
    {
        var options = await CreateMigratedSchemaAsync();
        var first = OidcSession(
            "shared-email-first",
            "shared@example.invalid",
            "SHARED@EXAMPLE.INVALID",
            [],
            subject: "first-subject");
        var second = OidcSession(
            "shared-email-second",
            "shared@example.invalid",
            "SHARED@EXAMPLE.INVALID",
            [],
            subject: "second-subject");
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstAttempt = CreateWhenReleasedAsync(options, first, "shared-first", start.Task);
        var secondAttempt = CreateWhenReleasedAsync(options, second, "shared-second", start.Task);
        start.SetResult(true);

        var results = await Task.WhenAll(firstAttempt, secondAttempt);
        Assert.All(results, x => Assert.Equal(OidcSessionStoreStatus.Succeeded, x.Status));
        Assert.Equal(results[0].UserId, results[1].UserId);

        await using var verificationContext = new ArrControlDbContext(options);
        var user = Assert.Single(await verificationContext.Set<UserEntity>()
            .AsNoTracking()
            .ToListAsync());
        var identities = await verificationContext.Set<ExternalIdentityEntity>()
            .AsNoTracking()
            .OrderBy(x => x.Subject)
            .ToListAsync();
        Assert.Equal(2, identities.Count);
        Assert.All(identities, x => Assert.Equal(user.Id, x.UserId));
        Assert.Equal(["first-subject", "second-subject"], identities.Select(x => x.Subject));
        Assert.Equal(2, await verificationContext.Set<UserSessionEntity>().CountAsync());
    }

    [Fact]
    public async Task Oidc_method_and_logout_context_survive_refresh_rotation_until_family_expiry()
    {
        var options = await CreateMigratedSchemaAsync();
        var original = OidcSession(
            "rotation-original",
            "rotate@example.invalid",
            "ROTATE@EXAMPLE.INVALID",
            []);
        OidcSessionStoreResult created;
        await using (var creationContext = new ArrControlDbContext(options))
        {
            created = await CreateOidcStore(creationContext).CreateSessionAsync(
                original,
                Request("oidc-rotation-create"),
                ReferenceTime,
                CancellationToken.None);
        }

        Assert.Equal(OidcSessionStoreStatus.Succeeded, created.Status);
        await using (var validationContext = new ArrControlDbContext(options))
        {
            var validated = await CreateLocalStore(validationContext).ValidateAccessTokenAsync(
                original.Session.AccessTokenHash,
                ReferenceTime.AddMinutes(1),
                CancellationToken.None);
            Assert.NotNull(validated);
            Assert.Equal(LocalIdentityConstants.OidcAuthenticationMethod, validated.AuthenticationMethod);
        }

        var rotationTime = ReferenceTime.AddMinutes(2);
        var replacement = new SessionTokenMaterial(
            Guid.CreateVersion7(),
            Hash("rotation-replacement-access"),
            rotationTime.AddMinutes(15),
            Hash("rotation-replacement-refresh"));
        await using (var rotationContext = new ArrControlDbContext(options))
        {
            var rotated = await CreateLocalStore(rotationContext).RotateRefreshTokenAsync(
                original.Session.RefreshTokenHash,
                replacement,
                Request("oidc-rotation-refresh"),
                rotationTime,
                CancellationToken.None);
            Assert.Equal(RefreshStoreStatus.Succeeded, rotated.Status);
        }

        await using var logoutContext = new ArrControlDbContext(options);
        var oidcStore = CreateOidcStore(logoutContext);
        var bySession = await oidcStore.GetLogoutContextAsync(
            replacement.Id,
            null,
            rotationTime,
            CancellationToken.None);
        var byRefresh = await oidcStore.GetLogoutContextAsync(
            null,
            replacement.RefreshTokenHash,
            rotationTime,
            CancellationToken.None);
        Assert.Equal(original.ProtectedIdToken, Assert.IsType<OidcLogoutContext>(bySession).ProtectedIdToken);
        Assert.Equal(original.ProtectedIdToken, Assert.IsType<OidcLogoutContext>(byRefresh).ProtectedIdToken);
        Assert.Null(await oidcStore.GetLogoutContextAsync(
            replacement.Id,
            replacement.RefreshTokenHash,
            original.RefreshExpiresAt,
            CancellationToken.None));

        var persistedReplacement = await logoutContext.Set<UserSessionEntity>()
            .AsNoTracking()
            .SingleAsync(x => x.Id == replacement.Id);
        Assert.Equal(LocalIdentityConstants.OidcAuthenticationMethod, persistedReplacement.AuthenticationMethod);
    }

    private async Task<DbContextOptions<ArrControlDbContext>> CreateMigratedSchemaAsync()
    {
        var schema = $"oidc_{Guid.NewGuid():N}";
        var baseConnectionString = database.ConnectionString;
        var adminOptions = new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(baseConnectionString)
            .Options;
        await using (var adminContext = new ArrControlDbContext(adminOptions))
        {
            var connection = adminContext.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql($"{baseConnectionString};Search Path={schema}")
            .Options;
        await using var migrationContext = new ArrControlDbContext(options);
        await migrationContext.Database.MigrateAsync();
        Assert.Empty(await migrationContext.Database.GetPendingMigrationsAsync());
        return options;
    }

    private static async Task<UserEntity> SeedUserAsync(
        DbContextOptions<ArrControlDbContext> options,
        string email,
        string state = LocalIdentityConstants.ActiveUserState)
    {
        var user = new UserEntity
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = "local-password-hash",
            Locale = "en",
            TimeZone = "UTC",
            State = state,
            CreatedAt = ReferenceTime.AddHours(-1),
            UpdatedAt = ReferenceTime.AddHours(-1),
        };
        await using var context = new ArrControlDbContext(options);
        context.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private static async Task<RoleEntity> SeedRoleAsync(
        DbContextOptions<ArrControlDbContext> options,
        string name,
        string normalizedName)
    {
        var role = new RoleEntity
        {
            Name = name,
            NormalizedName = normalizedName,
            IsSystem = false,
            CreatedAt = ReferenceTime.AddHours(-1),
        };
        await using var context = new ArrControlDbContext(options);
        context.Add(role);
        await context.SaveChangesAsync();
        return role;
    }

    private static async Task SeedUserRoleAsync(
        DbContextOptions<ArrControlDbContext> options,
        Guid userId,
        Guid roleId)
    {
        await using var context = new ArrControlDbContext(options);
        context.Add(new UserRoleEntity
        {
            UserId = userId,
            RoleId = roleId,
            CreatedAt = ReferenceTime.AddMinutes(-30),
        });
        await context.SaveChangesAsync();
    }

    private static async Task AssertNoOidcStateAsync(
        DbContextOptions<ArrControlDbContext> options,
        int expectUsers)
    {
        await using var context = new ArrControlDbContext(options);
        Assert.Equal(expectUsers, await context.Set<UserEntity>().CountAsync());
        Assert.Empty(await context.Set<ExternalIdentityEntity>().AsNoTracking().ToListAsync());
        Assert.Empty(await context.Set<ExternalIdentityRoleEntity>().AsNoTracking().ToListAsync());
        Assert.Empty(await context.Set<UserSessionEntity>().AsNoTracking().ToListAsync());
        Assert.Empty(await context.Set<OidcSessionContextEntity>().AsNoTracking().ToListAsync());
    }

    private static async Task<OidcSessionStoreResult> CreateWhenReleasedAsync(
        DbContextOptions<ArrControlDbContext> options,
        NewOidcSessionRecord session,
        string correlationId,
        Task start)
    {
        await using var context = new ArrControlDbContext(options);
        await start;
        return await CreateOidcStore(context).CreateSessionAsync(
            session,
            Request(correlationId),
            ReferenceTime,
            CancellationToken.None);
    }

    private static NewOidcSessionRecord OidcSession(
        string label,
        string? verifiedEmail,
        string? verifiedNormalizedEmail,
        IReadOnlyCollection<string> desiredRoles,
        string subject = "authentik-subject")
    {
        var now = label == "second" ? ReferenceTime.AddMinutes(1) : ReferenceTime;
        return new NewOidcSessionRecord(
            "https://authentik.example.invalid/application/o/arrcontrol/",
            subject,
            verifiedEmail,
            verifiedNormalizedEmail,
            desiredRoles,
            Guid.CreateVersion7(),
            new SessionTokenMaterial(
                Guid.CreateVersion7(),
                Hash($"{label}-access"),
                now.AddMinutes(15),
                Hash($"{label}-refresh")),
            now.AddDays(30),
            LocalIdentityConstants.OidcAuthenticationMethod,
            $"protected-id-token-{label}");
    }

    private static AuthenticationRequestContext Request(string correlationId) =>
        new(correlationId, IPAddress.Parse("192.0.2.90"));

    private static EfOidcIdentityStore CreateOidcStore(ArrControlDbContext context) =>
        new(context, new EfAuthenticationAuditPort(context));

    private static EfLocalIdentityStore CreateLocalStore(ArrControlDbContext context) =>
        new(context, new EfAuthenticationAuditPort(context));

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
