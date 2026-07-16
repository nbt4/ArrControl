using System.Buffers.Binary;
using System.Data.Common;
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
using Testcontainers.PostgreSql;
using Xunit;

namespace ArrControl.IntegrationTests;

public sealed class AuthDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer database = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("arrcontrol_auth_integration_tests")
        .WithUsername("arrcontrol_auth_integration_tests")
        .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
        .Build();

    public string ConnectionString => database.GetConnectionString();

    public Task InitializeAsync() => database.StartAsync();

    public Task DisposeAsync() => database.DisposeAsync().AsTask();
}

public sealed class AuthPersistenceTests(AuthDatabaseFixture database) : IClassFixture<AuthDatabaseFixture>
{
    private const string InitialMigration = "20260714221653_InitialFoundation";

    private static readonly DateTimeOffset ReferenceTime =
        new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Auth_migration_upgrades_legacy_sessions_and_enforces_the_latest_contract()
    {
        var options = await CreateSchemaOptionsAsync();
        var userId = Guid.CreateVersion7();
        var familyId = Guid.CreateVersion7();
        var firstSessionId = Guid.CreateVersion7();
        var secondSessionId = Guid.CreateVersion7();
        var firstRefreshHash = SHA512.HashData(Encoding.UTF8.GetBytes("legacy-first-refresh"));
        var secondRefreshHash = SHA256.HashData(Encoding.UTF8.GetBytes("legacy-second-refresh"));

        await using var migrationContext = new ArrControlDbContext(options);
        await migrationContext.Database.MigrateAsync(InitialMigration);
        await migrationContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO users
                (id, email, normalized_email, password_hash, locale, time_zone, state,
                 created_at, updated_at)
            VALUES
                ({userId}, 'legacy@example.invalid', 'LEGACY@EXAMPLE.INVALID', NULL,
                 'en', 'UTC', 'active', {ReferenceTime.AddHours(-2)}, {ReferenceTime.AddHours(-2)});

            INSERT INTO user_sessions
                (id, user_id, token_family_id, refresh_token_hash, created_at, expires_at)
            VALUES
                ({firstSessionId}, {userId}, {familyId}, {firstRefreshHash},
                 {ReferenceTime.AddMinutes(-30)}, {ReferenceTime.AddDays(1)}),
                ({secondSessionId}, {userId}, {familyId}, {secondRefreshHash},
                 {ReferenceTime.AddMinutes(-10)}, {ReferenceTime.AddDays(1)});
            """);

        await migrationContext.Database.MigrateAsync();

        var appliedMigrations = (await migrationContext.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(InitialMigration, appliedMigrations[0]);
        Assert.True(appliedMigrations.Length > 1, "An auth migration must follow InitialFoundation.");
        Assert.Empty(await migrationContext.Database.GetPendingMigrationsAsync());

        var sessions = await migrationContext.Set<UserSessionEntity>()
            .AsNoTracking()
            .Where(x => x.TokenFamilyId == familyId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();
        Assert.Equal(2, sessions.Count);
        Assert.All(
            sessions,
            session =>
            {
                Assert.Equal(32, session.AccessTokenHash.Length);
                Assert.Equal(32, session.RefreshTokenHash.Length);
                Assert.True(session.AccessExpiresAt > session.CreatedAt);
                Assert.True(session.AccessExpiresAt <= session.ExpiresAt);
                Assert.Equal(
                    LocalIdentityConstants.LocalAuthenticationMethod,
                    session.AuthenticationMethod);
            });
        Assert.Equal(
            2,
            sessions.Select(x => Convert.ToHexString(x.AccessTokenHash)).Distinct().Count());
        Assert.Equal(
            2,
            sessions.Select(x => Convert.ToHexString(x.RefreshTokenHash)).Distinct().Count());
        var activeSession = Assert.Single(sessions, x => x.RevokedAt is null);
        var revokedSession = Assert.Single(sessions, x => x.RevokedAt is not null);
        await Assert.ThrowsAnyAsync<DbException>(() =>
            migrationContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE user_sessions
                SET replaced_by_session_id = {revokedSession.Id}
                WHERE id = {activeSession.Id}
                """));

        var tables = await ReadCatalogNamesAsync(
            migrationContext,
            """
            SELECT tablename
            FROM pg_catalog.pg_tables
            WHERE schemaname = current_schema()
            """);
        Assert.Contains("identity_bootstrap_state", tables);
        Assert.Contains("external_identity_roles", tables);
        Assert.Contains("oidc_session_contexts", tables);
        var bootstrapState = Assert.Single(await migrationContext.Set<IdentityBootstrapStateEntity>()
            .AsNoTracking()
            .ToListAsync());
        Assert.Null(bootstrapState.AdminUserId);

        await migrationContext.Set<UserEntity>().ExecuteDeleteAsync();
        Assert.Equal(
            BootstrapStoreStatus.AlreadyDisabled,
            await CreateStore(migrationContext).BootstrapAsync(
                BootstrapUser("replacement@example.invalid"),
                Request("legacy-bootstrap-retry", "192.0.2.70"),
                CancellationToken.None));
        Assert.Empty(await migrationContext.Set<UserEntity>().AsNoTracking().ToListAsync());

        var sessionConstraints = await ReadCatalogNamesAsync(
            migrationContext,
            """
            SELECT constraint_record.conname
            FROM pg_catalog.pg_constraint AS constraint_record
            INNER JOIN pg_catalog.pg_class AS relation
                ON relation.oid = constraint_record.conrelid
            INNER JOIN pg_catalog.pg_namespace AS namespace
                ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = current_schema()
              AND relation.relname = 'user_sessions'
              AND constraint_record.contype = 'c'
            """);
        Assert.Contains("ck_user_sessions_access_token_hash_length", sessionConstraints);
        Assert.Contains("ck_user_sessions_refresh_token_hash_length", sessionConstraints);
        Assert.Contains("ck_user_sessions_expiration", sessionConstraints);
        Assert.Contains("ck_user_sessions_access_expiration", sessionConstraints);
        Assert.Contains("ck_user_sessions_last_used_at", sessionConstraints);
        Assert.Contains("ck_user_sessions_revoked_at", sessionConstraints);
        Assert.Contains("ck_user_sessions_replacement", sessionConstraints);
        Assert.Contains("ck_user_sessions_replacement_requires_revocation", sessionConstraints);
        Assert.Contains("ck_user_sessions_authentication_method", sessionConstraints);

        var indexes = await ReadCatalogNamesAsync(
            migrationContext,
            """
            SELECT indexname
            FROM pg_catalog.pg_indexes
            WHERE schemaname = current_schema()
            """);
        Assert.Contains("ux_user_sessions_access_token_hash", indexes);
        Assert.Contains("ux_user_sessions_active_token_family_id", indexes);
        Assert.Contains("ix_audit_events_login_account_throttle", indexes);
        Assert.Contains("ix_audit_events_login_ip_throttle", indexes);
    }

    [Fact]
    public async Task Concurrent_bootstrap_creates_exactly_one_admin_and_persists_the_sentinel()
    {
        var options = await CreateMigratedSchemaAsync();
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCandidate = BootstrapUser("first-admin@example.invalid");
        var secondCandidate = BootstrapUser("second-admin@example.invalid");

        var firstAttempt = BootstrapWhenReleasedAsync(options, firstCandidate, start.Task);
        var secondAttempt = BootstrapWhenReleasedAsync(options, secondCandidate, start.Task);
        start.SetResult(true);

        var statuses = await Task.WhenAll(firstAttempt, secondAttempt);

        Assert.Single(statuses, x => x == BootstrapStoreStatus.Created);
        Assert.Single(statuses, x => x == BootstrapStoreStatus.Updated);

        await using var verificationContext = new ArrControlDbContext(options);
        var user = Assert.Single(await verificationContext.Set<UserEntity>()
            .AsNoTracking()
            .ToListAsync());
        var sentinel = Assert.Single(await verificationContext.Set<IdentityBootstrapStateEntity>()
            .AsNoTracking()
            .ToListAsync());
        var administratorRole = Assert.Single(await verificationContext.Set<RoleEntity>()
            .AsNoTracking()
            .Where(x => x.NormalizedName == LocalIdentityConstants.AdministratorRoleNormalizedName)
            .ToListAsync());
        var assignment = Assert.Single(await verificationContext.Set<UserRoleEntity>()
            .AsNoTracking()
            .ToListAsync());

        Assert.Equal(user.Id, sentinel.AdminUserId);
        Assert.Equal(LocalIdentityConstants.AdministratorRoleNormalizedName, administratorRole.NormalizedName);
        Assert.True(administratorRole.IsSystem);
        Assert.Equal(user.Id, assignment.UserId);
        Assert.Equal(administratorRole.Id, assignment.RoleId);
        Assert.Null(assignment.InstanceGroupId);
        Assert.Single(await verificationContext.Set<AuditEventEntity>()
            .AsNoTracking()
            .Where(x => x.Action == "identity.bootstrap" && x.Outcome == "succeeded")
            .ToListAsync());

        await using var finalAttemptContext = new ArrControlDbContext(options);
        var finalAttemptStore = CreateStore(finalAttemptContext);
        Assert.True(await finalAttemptStore.IsBootstrapDisabledAsync(CancellationToken.None));
        Assert.Equal(
            BootstrapStoreStatus.Updated,
            await finalAttemptStore.BootstrapAsync(
                BootstrapUser("third-admin@example.invalid"),
                Request("bootstrap-final", "192.0.2.12"),
                CancellationToken.None));
    }

    [Fact]
    public async Task Session_creation_persists_only_hashes_and_access_validation_observes_expiry()
    {
        var options = await CreateMigratedSchemaAsync();
        var user = await SeedUserAsync(options);
        var accessTokenHash = Hash("access-token-material");
        var refreshTokenHash = Hash("refresh-token-material");
        var session = new NewSessionRecord(
            Guid.CreateVersion7(),
            user.Id,
            user.Email,
            Guid.CreateVersion7(),
            accessTokenHash,
            ReferenceTime.AddMinutes(15),
            refreshTokenHash,
            ReferenceTime.AddDays(1),
            LocalIdentityConstants.LocalAuthenticationMethod);

        await using (var creationContext = new ArrControlDbContext(options))
        {
            await CreateStore(creationContext).CreateSessionAsync(
                session,
                Request("session-create", "192.0.2.20"),
                ReferenceTime,
                CancellationToken.None);
        }

        await using var validationContext = new ArrControlDbContext(options);
        var store = CreateStore(validationContext);
        var validated = await store.ValidateAccessTokenAsync(
            accessTokenHash,
            ReferenceTime.AddMinutes(1),
            CancellationToken.None);

        Assert.NotNull(validated);
        Assert.Equal(user.Id, validated.UserId);
        Assert.Equal(session.Id, validated.SessionId);
        Assert.Equal(user.Email, validated.Email);
        Assert.Null(await store.ValidateAccessTokenAsync(
            Hash("different-access-token"),
            ReferenceTime.AddMinutes(1),
            CancellationToken.None));
        Assert.Null(await store.ValidateAccessTokenAsync(
            accessTokenHash,
            session.AccessExpiresAt,
            CancellationToken.None));

        var persisted = await validationContext.Set<UserSessionEntity>()
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(accessTokenHash, persisted.AccessTokenHash);
        Assert.Equal(refreshTokenHash, persisted.RefreshTokenHash);
    }

    [Fact]
    public async Task Concurrent_refresh_rotation_allows_one_success_and_replay_revokes_the_family()
    {
        var options = await CreateMigratedSchemaAsync();
        var user = await SeedUserAsync(options);
        var familyId = Guid.CreateVersion7();
        var original = await CreateSessionAsync(options, user, familyId, "original", ReferenceTime);
        var rotationTime = ReferenceTime.AddMinutes(5);
        var firstReplacement = Replacement("first-replacement", rotationTime);
        var secondReplacement = Replacement("second-replacement", rotationTime);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstRotation = RotateWhenReleasedAsync(
            options,
            original.RefreshTokenHash,
            firstReplacement,
            Request("refresh-first", "192.0.2.30"),
            rotationTime,
            start.Task);
        var secondRotation = RotateWhenReleasedAsync(
            options,
            original.RefreshTokenHash,
            secondReplacement,
            Request("refresh-second", "192.0.2.31"),
            rotationTime,
            start.Task);
        start.SetResult(true);

        var results = await Task.WhenAll(firstRotation, secondRotation);
        var succeeded = Assert.Single(results, x => x.Status == RefreshStoreStatus.Succeeded);
        Assert.Single(results, x => x.Status == RefreshStoreStatus.Replayed);

        await using var verificationContext = new ArrControlDbContext(options);
        var sessions = await verificationContext.Set<UserSessionEntity>()
            .AsNoTracking()
            .Where(x => x.TokenFamilyId == familyId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();

        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, session => Assert.NotNull(session.RevokedAt));
        var persistedOriginal = Assert.Single(sessions, x => x.Id == original.Id);
        var persistedReplacement = Assert.Single(sessions, x => x.Id != original.Id);
        Assert.Equal(succeeded.SessionId, persistedReplacement.Id);
        Assert.Equal(succeeded.SessionId, persistedOriginal.ReplacedBySessionId);
        Assert.Equal(rotationTime, persistedOriginal.LastUsedAt);
        Assert.Equal(
            1,
            await verificationContext.Set<AuditEventEntity>()
                .CountAsync(x => x.Action == "identity.refresh" && x.Outcome == "succeeded"));
        Assert.Equal(
            1,
            await verificationContext.Set<AuditEventEntity>()
                .CountAsync(x => x.Action == "identity.refresh_reuse" && x.Outcome == "failed"));
    }

    [Fact]
    public async Task Replay_racing_the_current_token_cannot_leave_a_replacement_active()
    {
        var options = await CreateMigratedSchemaAsync();
        var user = await SeedUserAsync(options);
        var familyId = Guid.CreateVersion7();
        var original = await CreateSessionAsync(options, user, familyId, "race-original", ReferenceTime);
        var firstRotationAt = ReferenceTime.AddMinutes(2);
        var current = Replacement("race-current", firstRotationAt);
        await using (var firstRotationContext = new ArrControlDbContext(options))
        {
            var firstRotation = await CreateStore(firstRotationContext).RotateRefreshTokenAsync(
                original.RefreshTokenHash,
                current,
                Request("race-first-rotation", "192.0.2.33"),
                firstRotationAt,
                CancellationToken.None);
            Assert.Equal(RefreshStoreStatus.Succeeded, firstRotation.Status);
        }

        await using var blockerContext = new ArrControlDbContext(options);
        await using var blockerTransaction = await blockerContext.Database.BeginTransactionAsync();
        await blockerContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({SessionFamilyLockKey(familyId)})");

        var replayAt = firstRotationAt.AddMinutes(1);
        var currentRotationAt = firstRotationAt.AddMinutes(2);
        var successor = Replacement("race-successor", currentRotationAt);
        var currentRotation = RotateWhenReleasedAsync(
            options,
            current.RefreshTokenHash,
            successor,
            Request("race-current-rotation", "192.0.2.34"),
            currentRotationAt,
            Task.CompletedTask);

        var bothWaitedForFamilyLock = false;
        Task<RefreshStoreResult>? oldTokenReplay = null;
        try
        {
            var currentWaitedFirst = await WaitForAdvisoryWaitersAsync(options, 1);
            oldTokenReplay = RotateWhenReleasedAsync(
                options,
                original.RefreshTokenHash,
                Replacement("race-replay-unused", replayAt),
                Request("race-old-replay", "192.0.2.35"),
                replayAt,
                Task.CompletedTask);
            bothWaitedForFamilyLock = currentWaitedFirst
                && await WaitForAdvisoryWaitersAsync(options, 2);
        }
        finally
        {
            await blockerTransaction.CommitAsync();
        }

        Assert.True(bothWaitedForFamilyLock);
        var currentResult = await currentRotation;
        var replayResult = await oldTokenReplay!;
        Assert.Equal(RefreshStoreStatus.Succeeded, currentResult.Status);
        Assert.Equal(RefreshStoreStatus.Replayed, replayResult.Status);

        await using var verificationContext = new ArrControlDbContext(options);
        var familySessions = await verificationContext.Set<UserSessionEntity>()
            .AsNoTracking()
            .Where(x => x.TokenFamilyId == familyId)
            .ToListAsync();
        Assert.True(familySessions.Count >= 2);
        Assert.All(familySessions, session => Assert.NotNull(session.RevokedAt));
        var persistedSuccessor = Assert.Single(familySessions, x => x.Id == successor.Id);
        Assert.Equal(currentRotationAt, persistedSuccessor.CreatedAt);
        Assert.True(persistedSuccessor.RevokedAt >= persistedSuccessor.CreatedAt);
    }

    [Fact]
    public async Task Logout_is_idempotent_and_does_not_move_the_original_revocation_time()
    {
        var options = await CreateMigratedSchemaAsync();
        var user = await SeedUserAsync(options);
        var session = await CreateSessionAsync(
            options,
            user,
            Guid.CreateVersion7(),
            "logout",
            ReferenceTime);
        var firstLogoutAt = ReferenceTime.AddMinutes(2);

        await RevokeAsync(
            options,
            session.Id,
            null,
            Request("logout-first", "192.0.2.40"),
            firstLogoutAt);
        await RevokeAsync(
            options,
            session.Id,
            session.RefreshTokenHash,
            Request("logout-repeat", "192.0.2.40"),
            firstLogoutAt.AddMinutes(1));

        await using var verificationContext = new ArrControlDbContext(options);
        var persisted = await verificationContext.Set<UserSessionEntity>()
            .AsNoTracking()
            .SingleAsync(x => x.Id == session.Id);
        Assert.Equal(firstLogoutAt, persisted.RevokedAt);

        var logoutEvents = await verificationContext.Set<AuditEventEntity>()
            .AsNoTracking()
            .Where(x => x.Action == "identity.logout")
            .OrderBy(x => x.OccurredAt)
            .ToListAsync();
        Assert.Equal(2, logoutEvents.Count);
        Assert.All(logoutEvents, auditEvent => Assert.Equal("succeeded", auditEvent.Outcome));
    }

    [Fact]
    public async Task Authentication_audits_are_redacted_and_throttle_counts_use_the_requested_window()
    {
        var options = await CreateMigratedSchemaAsync();
        var user = await SeedUserAsync(options);
        var primaryIp = IPAddress.Parse("192.0.2.50");
        var secondaryIp = IPAddress.Parse("192.0.2.51");
        const string passwordSecret = "password-secret-must-not-appear";
        var accessTokenHash = Hash("redaction-access-token");
        var refreshTokenHash = Hash("redaction-refresh-token");

        await using (var auditContext = new ArrControlDbContext(options))
        {
            var store = CreateStore(auditContext);
            await store.CreateSessionAsync(
                new NewSessionRecord(
                    Guid.CreateVersion7(),
                    user.Id,
                    user.Email,
                    Guid.CreateVersion7(),
                    accessTokenHash,
                    ReferenceTime.AddMinutes(15),
                    refreshTokenHash,
                    ReferenceTime.AddDays(1),
                    LocalIdentityConstants.LocalAuthenticationMethod,
                    passwordSecret),
                new AuthenticationRequestContext("audit-login", primaryIp),
                ReferenceTime.AddMinutes(-6),
                CancellationToken.None);
            await store.RecordLoginFailureAsync(
                user,
                user.NormalizedEmail,
                "failed",
                new AuthenticationRequestContext("audit-target-primary", primaryIp),
                ReferenceTime.AddMinutes(-5),
                CancellationToken.None);
            await store.RecordLoginFailureAsync(
                user,
                user.NormalizedEmail,
                "failed",
                new AuthenticationRequestContext("audit-target-secondary", secondaryIp),
                ReferenceTime.AddMinutes(-4),
                CancellationToken.None);
            await store.RecordLoginFailureAsync(
                null,
                "OTHER@EXAMPLE.INVALID",
                "failed",
                new AuthenticationRequestContext("audit-other-primary", primaryIp),
                ReferenceTime.AddMinutes(-3),
                CancellationToken.None);
            await store.RecordLoginFailureAsync(
                user,
                user.NormalizedEmail,
                "rate_limited",
                new AuthenticationRequestContext("audit-rate-limited", primaryIp),
                ReferenceTime.AddMinutes(-2),
                CancellationToken.None);
            await store.RecordLoginFailureAsync(
                user,
                user.NormalizedEmail,
                "failed",
                new AuthenticationRequestContext("audit-outside-window", primaryIp),
                ReferenceTime.AddHours(-1),
                CancellationToken.None);
        }

        await using var verificationContext = new ArrControlDbContext(options);
        var verificationStore = CreateStore(verificationContext);
        await using var throttleLease = await verificationStore.AcquireLoginThrottleAsync(
            user.NormalizedEmail,
            primaryIp,
            ReferenceTime.AddMinutes(-15),
            CancellationToken.None);
        var counts = throttleLease.FailureCounts;

        Assert.True(throttleLease.Acquired);
        Assert.Equal(2, counts.AccountFailures);
        Assert.Equal(2, counts.IpFailures);
        await throttleLease.CommitAsync(CancellationToken.None);

        var audits = await verificationContext.Set<AuditEventEntity>()
            .AsNoTracking()
            .Where(x => x.Action == "identity.login")
            .ToListAsync();
        Assert.Equal(6, audits.Count);
        foreach (var auditEvent in audits)
        {
            using var scope = JsonDocument.Parse(auditEvent.ScopeJson);
            using var summary = JsonDocument.Parse(auditEvent.SummaryJson);
            Assert.Equal("authentication", scope.RootElement.GetProperty("kind").GetString());
            Assert.Equal("local", summary.RootElement.GetProperty("method").GetString());
            Assert.Single(scope.RootElement.EnumerateObject());
            Assert.Single(summary.RootElement.EnumerateObject());

            var persistedAuditText = string.Join(
                '|',
                auditEvent.ActorIdentifier,
                auditEvent.Action,
                auditEvent.ScopeJson,
                auditEvent.CorrelationId,
                auditEvent.Outcome,
                auditEvent.SummaryJson);
            Assert.DoesNotContain(passwordSecret, persistedAuditText, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Convert.ToHexString(accessTokenHash),
                persistedAuditText,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                Convert.ToHexString(refreshTokenHash),
                persistedAuditText,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Login_throttle_lease_serializes_overlapping_account_and_ip_attempts()
    {
        var options = await CreateMigratedSchemaAsync();
        const string actorIdentifier = "ADMIN@EXAMPLE.INVALID";
        var ipAddress = IPAddress.Parse("192.0.2.80");

        await using var firstContext = new ArrControlDbContext(options);
        var firstStore = CreateStore(firstContext);
        await using var firstLease = await firstStore.AcquireLoginThrottleAsync(
            actorIdentifier,
            ipAddress,
            ReferenceTime.AddMinutes(-15),
            CancellationToken.None);
        Assert.True(firstLease.Acquired);

        await using var accountConflictContext = new ArrControlDbContext(options);
        await using var accountConflict = await CreateStore(accountConflictContext)
            .AcquireLoginThrottleAsync(
                actorIdentifier,
                IPAddress.Parse("192.0.2.81"),
                ReferenceTime.AddMinutes(-15),
                CancellationToken.None);
        Assert.False(accountConflict.Acquired);

        await using var ipConflictContext = new ArrControlDbContext(options);
        await using var ipConflict = await CreateStore(ipConflictContext)
            .AcquireLoginThrottleAsync(
                "OTHER@EXAMPLE.INVALID",
                ipAddress,
                ReferenceTime.AddMinutes(-15),
                CancellationToken.None);
        Assert.False(ipConflict.Acquired);

        await using var independentContext = new ArrControlDbContext(options);
        await using var independent = await CreateStore(independentContext)
            .AcquireLoginThrottleAsync(
                "INDEPENDENT@EXAMPLE.INVALID",
                IPAddress.Parse("192.0.2.82"),
                ReferenceTime.AddMinutes(-15),
                CancellationToken.None);
        Assert.True(independent.Acquired);
        await independent.CommitAsync(CancellationToken.None);

        await firstStore.RecordLoginFailureAsync(
            null,
            actorIdentifier,
            "failed",
            new AuthenticationRequestContext("serialized-login", ipAddress),
            ReferenceTime,
            CancellationToken.None);
        await firstLease.CommitAsync(CancellationToken.None);

        await using var verificationContext = new ArrControlDbContext(options);
        await using var verification = await CreateStore(verificationContext)
            .AcquireLoginThrottleAsync(
                actorIdentifier,
                ipAddress,
                ReferenceTime.AddMinutes(-15),
                CancellationToken.None);
        Assert.True(verification.Acquired);
        Assert.Equal(1, verification.FailureCounts.AccountFailures);
        Assert.Equal(1, verification.FailureCounts.IpFailures);
        await verification.CommitAsync(CancellationToken.None);
    }

    private async Task<DbContextOptions<ArrControlDbContext>> CreateMigratedSchemaAsync()
    {
        var options = await CreateSchemaOptionsAsync();
        await using var migrationContext = new ArrControlDbContext(options);
        await migrationContext.Database.MigrateAsync();
        Assert.Empty(await migrationContext.Database.GetPendingMigrationsAsync());
        return options;
    }

    private async Task<DbContextOptions<ArrControlDbContext>> CreateSchemaOptionsAsync()
    {
        var schema = $"auth_{Guid.NewGuid():N}";
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
        return options;
    }

    private static async Task<IReadOnlyList<string>> ReadCatalogNamesAsync(
        ArrControlDbContext context,
        string commandText)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            var names = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static async Task<bool> WaitForAdvisoryWaitersAsync(
        DbContextOptions<ArrControlDbContext> options,
        int expectedCount)
    {
        await using var context = new ArrControlDbContext(options);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT count(*) FROM pg_stat_activity " +
                "WHERE datname = current_database() " +
                "AND wait_event_type = 'Lock' AND wait_event = 'advisory'";
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var count = Convert.ToInt32(await command.ExecuteScalarAsync());
                if (count >= expectedCount)
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25));
            }

            return false;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static async Task<BootstrapStoreStatus> BootstrapWhenReleasedAsync(
        DbContextOptions<ArrControlDbContext> options,
        BootstrapUserRecord user,
        Task start)
    {
        await using var context = new ArrControlDbContext(options);
        var store = CreateStore(context);
        await start;
        return await store.BootstrapAsync(
            user,
            Request($"bootstrap-{user.Id:N}", "192.0.2.10"),
            CancellationToken.None);
    }

    private static async Task<RefreshStoreResult> RotateWhenReleasedAsync(
        DbContextOptions<ArrControlDbContext> options,
        byte[] refreshTokenHash,
        SessionTokenMaterial replacement,
        AuthenticationRequestContext requestContext,
        DateTimeOffset now,
        Task start)
    {
        await using var context = new ArrControlDbContext(options);
        var store = CreateStore(context);
        await start;
        return await store.RotateRefreshTokenAsync(
            refreshTokenHash,
            replacement,
            requestContext,
            now,
            CancellationToken.None);
    }

    private static async Task RevokeAsync(
        DbContextOptions<ArrControlDbContext> options,
        Guid? sessionId,
        byte[]? refreshTokenHash,
        AuthenticationRequestContext requestContext,
        DateTimeOffset now)
    {
        await using var context = new ArrControlDbContext(options);
        await CreateStore(context).RevokeSessionFamilyAsync(
            sessionId,
            refreshTokenHash,
            requestContext,
            now,
            CancellationToken.None);
    }

    private static async Task<LocalUserRecord> SeedUserAsync(
        DbContextOptions<ArrControlDbContext> options)
    {
        var entity = new UserEntity
        {
            Email = "operator@example.invalid",
            NormalizedEmail = "OPERATOR@EXAMPLE.INVALID",
            PasswordHash = "test-password-hash",
            Locale = "en",
            TimeZone = "UTC",
            State = LocalIdentityConstants.ActiveUserState,
            CreatedAt = ReferenceTime.AddHours(-1),
            UpdatedAt = ReferenceTime.AddHours(-1),
        };
        await using var context = new ArrControlDbContext(options);
        context.Add(entity);
        await context.SaveChangesAsync();
        return new LocalUserRecord(
            entity.Id,
            entity.Email,
            entity.NormalizedEmail,
            entity.State,
            entity.PasswordHash);
    }

    private static async Task<NewSessionRecord> CreateSessionAsync(
        DbContextOptions<ArrControlDbContext> options,
        LocalUserRecord user,
        Guid familyId,
        string tokenLabel,
        DateTimeOffset now)
    {
        var session = new NewSessionRecord(
            Guid.CreateVersion7(),
            user.Id,
            user.Email,
            familyId,
            Hash($"{tokenLabel}-access"),
            now.AddMinutes(15),
            Hash($"{tokenLabel}-refresh"),
            now.AddDays(1),
            LocalIdentityConstants.LocalAuthenticationMethod);
        await using var context = new ArrControlDbContext(options);
        await CreateStore(context).CreateSessionAsync(
            session,
            Request($"session-{tokenLabel}", "192.0.2.60"),
            now,
            CancellationToken.None);
        return session;
    }

    private static BootstrapUserRecord BootstrapUser(string email) =>
        new(
            Guid.CreateVersion7(),
            email,
            email.ToUpperInvariant(),
            "test-password-hash",
            "en",
            "UTC");

    private static SessionTokenMaterial Replacement(string label, DateTimeOffset now) =>
        new(
            Guid.CreateVersion7(),
            Hash($"{label}-access"),
            now.AddMinutes(15),
            Hash($"{label}-refresh"));

    private static AuthenticationRequestContext Request(string correlationId, string ipAddress) =>
        new(correlationId, IPAddress.Parse(ipAddress));

    private static EfLocalIdentityStore CreateStore(ArrControlDbContext context) =>
        new(context, new EfAuthenticationAuditPort(context));

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static long SessionFamilyLockKey(Guid tokenFamilyId)
    {
        Span<byte> input = stackalloc byte[17];
        input[0] = 3;
        tokenFamilyId.TryWriteBytes(input[1..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return BinaryPrimitives.ReadInt64BigEndian(hash);
    }
}
