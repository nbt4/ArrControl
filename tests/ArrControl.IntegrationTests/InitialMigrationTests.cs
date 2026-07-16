using System.Data.Common;
using System.Security.Cryptography;
using ArrControl.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace ArrControl.IntegrationTests;

public sealed class InitialMigrationTests : IAsyncLifetime
{
    private const string InitialMigration = "20260714221653_InitialFoundation";

    private static readonly string[] ExpectedTopLevelTables =
    [
        "__EFMigrationsHistory",
        "audit_events",
        "credentials",
        "external_identities",
        "instance_groups",
        "job_runs",
        "outbox_messages",
        "permissions",
        "provider_capabilities",
        "role_permissions",
        "roles",
        "schedules",
        "service_instances",
        "user_roles",
        "user_sessions",
        "users",
    ];

    private readonly PostgreSqlContainer database = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("arrcontrol_integration_tests")
        .WithUsername("arrcontrol_integration_tests")
        .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
        .Build();

    public Task InitializeAsync() => database.StartAsync();

    public Task DisposeAsync() => database.DisposeAsync().AsTask();

    [Fact]
    public async Task Initial_migration_creates_the_complete_schema_and_is_idempotent()
    {
        var options = new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(database.GetConnectionString())
            .Options;

        await using var context = new ArrControlDbContext(options);
        var connection = context.Database.GetDbConnection();

        await connection.OpenAsync();
        try
        {
            Assert.Empty(await ReadTopLevelTableNamesAsync(connection));
        }
        finally
        {
            await connection.CloseAsync();
        }

        Assert.Empty(await context.Database.GetAppliedMigrationsAsync());
        Assert.Contains(InitialMigration, await context.Database.GetPendingMigrationsAsync());

        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(InitialMigration);

        var appliedMigrations = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(new[] { InitialMigration }, appliedMigrations);
        Assert.DoesNotContain(InitialMigration, await context.Database.GetPendingMigrationsAsync());

        await migrator.MigrateAsync(InitialMigration);

        Assert.Equal(
            appliedMigrations,
            (await context.Database.GetAppliedMigrationsAsync()).ToArray());

        await connection.OpenAsync();
        try
        {
            var tables = (await ReadTopLevelTableNamesAsync(connection))
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                ExpectedTopLevelTables.Order(StringComparer.Ordinal).ToArray(),
                tables);

            await AssertIdentityProtectionAsync(connection);
            await AssertCredentialProtectionAsync(connection);
            await AssertScopedRoleIndexesAsync(connection);
            await AssertAuditProtectionAsync(connection);
            await AssertOutboxIndexAsync(connection);
            await AssertJobConstraintsAsync(connection);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static Task<IReadOnlyList<string>> ReadTopLevelTableNamesAsync(DbConnection connection) =>
        ReadStringsAsync(
            connection,
            """
            SELECT relation.relname
            FROM pg_catalog.pg_class AS relation
            INNER JOIN pg_catalog.pg_namespace AS namespace
                ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = 'public'
              AND relation.relkind IN ('r', 'p')
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM pg_catalog.pg_inherits AS inheritance
                  WHERE inheritance.inhrelid = relation.oid
              )
            """);

    private static async Task AssertIdentityProtectionAsync(DbConnection connection)
    {
        var userId = Guid.CreateVersion7();
        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO users (id, email, normalized_email, locale, time_zone, state)
            VALUES (@id, 'operator@example.invalid', 'OPERATOR@EXAMPLE.INVALID', 'en', 'UTC', 'active')
            """,
            ("id", userId));

        await Assert.ThrowsAnyAsync<DbException>(() =>
            ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO users (id, email, normalized_email, locale, time_zone, state)
                VALUES (@id, 'other@example.invalid', 'OPERATOR@EXAMPLE.INVALID', 'en', 'UTC', 'active')
                """,
                ("id", Guid.CreateVersion7())));

        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO external_identities (id, user_id, issuer, subject)
            VALUES (@id, @user_id, 'https://identity.example.invalid', 'migration-test-subject')
            """,
            ("id", Guid.CreateVersion7()),
            ("user_id", userId));

        await Assert.ThrowsAnyAsync<DbException>(() =>
            ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO external_identities (id, user_id, issuer, subject)
                VALUES (@id, @user_id, 'https://identity.example.invalid', 'migration-test-subject')
                """,
                ("id", Guid.CreateVersion7()),
                ("user_id", userId)));

        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO user_sessions
                (id, user_id, token_family_id, refresh_token_hash, expires_at)
            VALUES
                (@id, @user_id, @family_id, decode(repeat('ab', 32), 'hex'), now() + interval '1 day')
            """,
            ("id", Guid.CreateVersion7()),
            ("user_id", userId),
            ("family_id", Guid.CreateVersion7()));

        await Assert.ThrowsAnyAsync<DbException>(() =>
            ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO user_sessions
                    (id, user_id, token_family_id, refresh_token_hash, expires_at)
                VALUES
                    (@id, @user_id, @family_id, decode(repeat('ab', 32), 'hex'), now() + interval '1 day')
                """,
                ("id", Guid.CreateVersion7()),
                ("user_id", userId),
                ("family_id", Guid.CreateVersion7())));
    }

    private static async Task AssertCredentialProtectionAsync(DbConnection connection)
    {
        var checkConstraints = await ReadCheckConstraintNamesAsync(connection, "credentials");
        Assert.Equal(
            new[]
            {
                "ck_credentials_ciphertext_not_empty",
                "ck_credentials_key_version",
                "ck_credentials_nonce_length",
                "ck_credentials_tag_length",
            },
            checkConstraints.Order(StringComparer.Ordinal).ToArray());

        var indexDefinition = await ReadIndexDefinitionAsync(
            connection,
            "ux_credentials_instance_id_purpose");
        Assert.Contains("CREATE UNIQUE INDEX", indexDefinition);
        Assert.Contains("(instance_id, purpose)", indexDefinition);

        var instanceId = Guid.CreateVersion7();
        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO service_instances (id, name, kind, base_url)
            VALUES (@id, 'Migration Test', 'Sonarr', 'https://sonarr.example.invalid')
            """,
            ("id", instanceId));

        await Assert.ThrowsAnyAsync<DbException>(() =>
            ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO credentials
                    (id, instance_id, purpose, ciphertext, nonce, tag, key_version)
                VALUES
                    (@id, @instance_id, 'api_key', decode('01', 'hex'),
                     decode(repeat('01', 11), 'hex'), decode(repeat('01', 16), 'hex'), 1)
                """,
                ("id", Guid.CreateVersion7()),
                ("instance_id", instanceId)));
    }

    private static async Task AssertScopedRoleIndexesAsync(DbConnection connection)
    {
        var globalIndex = await ReadIndexDefinitionAsync(
            connection,
            "ux_user_roles_user_id_role_id_global");
        Assert.Contains("CREATE UNIQUE INDEX", globalIndex);
        Assert.Contains("instance_group_id IS NULL", globalIndex);

        var groupIndex = await ReadIndexDefinitionAsync(
            connection,
            "ux_user_roles_user_id_role_id_instance_group_id");
        Assert.Contains("CREATE UNIQUE INDEX", groupIndex);
        Assert.Contains("instance_group_id IS NOT NULL", groupIndex);
    }

    private static async Task AssertAuditProtectionAsync(DbConnection connection)
    {
        var partitionDefinitions = await ReadStringsAsync(
            connection,
            """
            SELECT partitioned.partstrat::text || ':' || pg_get_partkeydef(parent.oid)
            FROM pg_catalog.pg_partitioned_table AS partitioned
            INNER JOIN pg_catalog.pg_class AS parent
                ON parent.oid = partitioned.partrelid
            INNER JOIN pg_catalog.pg_namespace AS namespace
                ON namespace.oid = parent.relnamespace
            WHERE namespace.nspname = 'public'
              AND parent.relname = 'audit_events'
            """);
        var partitionDefinition = Assert.Single(partitionDefinitions);
        Assert.Contains("r:RANGE (occurred_at)", partitionDefinition);

        var defaultPartitions = await ReadStringsAsync(
            connection,
            """
            SELECT child.relname
            FROM pg_catalog.pg_inherits AS inheritance
            INNER JOIN pg_catalog.pg_class AS parent
                ON parent.oid = inheritance.inhparent
            INNER JOIN pg_catalog.pg_namespace AS namespace
                ON namespace.oid = parent.relnamespace
            INNER JOIN pg_catalog.pg_class AS child
                ON child.oid = inheritance.inhrelid
            WHERE namespace.nspname = 'public'
              AND parent.relname = 'audit_events'
              AND pg_get_expr(child.relpartbound, child.oid, true) = 'DEFAULT'
            """);
        Assert.Single(defaultPartitions);

        var updateTriggers = await ReadStringsAsync(
            connection,
            """
            SELECT pg_get_triggerdef(trigger_record.oid) || E'\n' ||
                   pg_get_functiondef(trigger_record.tgfoid)
            FROM pg_catalog.pg_trigger AS trigger_record
            INNER JOIN pg_catalog.pg_class AS relation
                ON relation.oid = trigger_record.tgrelid
            INNER JOIN pg_catalog.pg_namespace AS namespace
                ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = 'public'
              AND relation.relname = 'audit_events'
              AND NOT trigger_record.tgisinternal
              AND trigger_record.tgenabled = 'O'
              AND (trigger_record.tgtype & 16) = 16
            """);
        var updateTrigger = Assert.Single(updateTriggers).ToUpperInvariant();
        Assert.Contains("BEFORE UPDATE", updateTrigger);
        Assert.Contains("RAISE EXCEPTION", updateTrigger);

        var auditEventId = Guid.CreateVersion7();
        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO audit_events
                (id, actor_type, actor_identifier, action, scope_json, correlation_id, outcome, summary_json)
            VALUES
                (@id, 'system', 'migration-test', 'migration.test', '{}', 'migration-test', 'succeeded', '{}')
            """,
            ("id", auditEventId));

        await Assert.ThrowsAnyAsync<DbException>(() =>
            ExecuteNonQueryAsync(
                connection,
                "UPDATE audit_events SET action = 'migration.changed' WHERE id = @id",
                ("id", auditEventId)));

        var persistedActions = await ReadStringsAsync(
            connection,
            "SELECT action FROM audit_events WHERE id = @id",
            ("id", auditEventId));
        Assert.Equal("migration.test", Assert.Single(persistedActions));
    }

    private static async Task AssertOutboxIndexAsync(DbConnection connection)
    {
        var indexDefinition = await ReadIndexDefinitionAsync(
            connection,
            "ix_outbox_messages_unpublished");
        Assert.Contains("(next_attempt_at, occurred_at)", indexDefinition);
        Assert.Contains("published_at IS NULL", indexDefinition);
    }

    private static async Task AssertJobConstraintsAsync(DbConnection connection)
    {
        var checkConstraints = await ReadCheckConstraintNamesAsync(connection, "job_runs");
        Assert.Equal(
            new[]
            {
                "ck_job_runs_attempts_nonnegative",
                "ck_job_runs_completion_order",
                "ck_job_runs_lease_pair",
            },
            checkConstraints.Order(StringComparer.Ordinal).ToArray());

        var scheduleId = Guid.CreateVersion7();
        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO schedules (id, type, cron, time_zone, scope_json)
            VALUES (@id, 'migration-test', '0 * * * *', 'UTC', '{}')
            """,
            ("id", scheduleId));

        await Assert.ThrowsAnyAsync<DbException>(() =>
            ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO job_runs (id, schedule_id, state, attempts, scheduled_for)
                VALUES (@id, @schedule_id, 'pending', -1, now())
                """,
                ("id", Guid.CreateVersion7()),
                ("schedule_id", scheduleId)));
    }

    private static Task<IReadOnlyList<string>> ReadCheckConstraintNamesAsync(
        DbConnection connection,
        string tableName) =>
        ReadStringsAsync(
            connection,
            """
            SELECT constraint_record.conname
            FROM pg_catalog.pg_constraint AS constraint_record
            INNER JOIN pg_catalog.pg_class AS relation
                ON relation.oid = constraint_record.conrelid
            INNER JOIN pg_catalog.pg_namespace AS namespace
                ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = 'public'
              AND relation.relname = @table_name
              AND constraint_record.contype = 'c'
            """,
            ("table_name", tableName));

    private static async Task<string> ReadIndexDefinitionAsync(
        DbConnection connection,
        string indexName)
    {
        var definitions = await ReadStringsAsync(
            connection,
            """
            SELECT index_record.indexdef
            FROM pg_catalog.pg_indexes AS index_record
            WHERE index_record.schemaname = 'public'
              AND index_record.indexname = @index_name
            """,
            ("index_name", indexName));

        return Assert.Single(definitions);
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(
        DbConnection connection,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<int> ExecuteNonQueryAsync(
        DbConnection connection,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return await command.ExecuteNonQueryAsync();
    }
}
