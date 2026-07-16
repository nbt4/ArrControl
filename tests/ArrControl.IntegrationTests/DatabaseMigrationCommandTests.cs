using ArrControl.Api.Operations;
using ArrControl.Infrastructure.Operations;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Automation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace ArrControl.IntegrationTests;

public sealed class DatabaseMigrationCommandTests(AuthApiDatabaseFixture databaseFixture)
    : IClassFixture<AuthApiDatabaseFixture>
{
    [Fact]
    public void Command_parser_only_intercepts_the_exact_migration_command()
    {
        Assert.False(DatabaseMigrationCommand.IsRequested([]));
        Assert.False(DatabaseMigrationCommand.IsRequested(["--urls", "http://localhost:8080"]));
        Assert.True(DatabaseMigrationCommand.IsRequested(["database", "migrate"]));
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseMigrationCommand.IsRequested(["database", "drop"]));
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseMigrationCommand.IsRequested(["database", "migrate", "--force"]));
    }

    [Fact]
    public async Task Runner_applies_every_migration_and_is_idempotent()
    {
        var connectionString = await databaseFixture.CreateUnmigratedSchemaAsync();
        var options = new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using (var firstContext = new ArrControlDbContext(options))
        {
            var first = await new DatabaseMigrationRunner(firstContext)
                .RunAsync(CancellationToken.None);
            Assert.NotEmpty(first.AppliedMigrations);
            Assert.Empty(first.PreviouslyAppliedMigrations);
        }

        await using (var secondContext = new ArrControlDbContext(options))
        {
            var second = await new DatabaseMigrationRunner(secondContext)
                .RunAsync(CancellationToken.None);
            Assert.Empty(second.AppliedMigrations);
            Assert.NotEmpty(second.PreviouslyAppliedMigrations);
            Assert.Empty(await secondContext.Database.GetPendingMigrationsAsync());
        }
    }

    [Fact]
    public async Task Scheduler_upgrade_returns_legacy_incomplete_jobs_to_a_safe_pending_state()
    {
        var connectionString = await databaseFixture.CreateUnmigratedSchemaAsync();
        var options = new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var scheduleId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var scheduledFor = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        await using (var legacy = new ArrControlDbContext(options))
        {
            await legacy.GetService<IMigrator>().MigrateAsync("20260716090000_RbacAuthorization");
            await legacy.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO schedules
                    (id, type, cron, time_zone, scope_json, enabled, created_at, updated_at)
                VALUES
                    ({{scheduleId}}, 'test.poll', '*/5 * * * *', 'UTC', '{}', TRUE,
                     {{scheduledFor}}, {{scheduledFor}});
                INSERT INTO job_runs
                    (id, schedule_id, state, attempts, scheduled_for, lease_owner, lease_until,
                     started_at, created_at)
                VALUES
                    ({{jobId}}, {{scheduleId}}, 'legacy-running', 2, {{scheduledFor}}, 'old-worker',
                     {{scheduledFor.AddHours(1)}}, {{scheduledFor}}, {{scheduledFor}});
                """);
        }

        await using (var migration = new ArrControlDbContext(options))
        {
            var result = await new DatabaseMigrationRunner(migration).RunAsync(CancellationToken.None);
            Assert.Contains(result.AppliedMigrations, value => value.EndsWith("_DurableJobScheduler"));
        }

        await using var verification = new ArrControlDbContext(options);
        var job = await verification.Set<JobRunEntity>().SingleAsync(value => value.Id == jobId);
        var schedule = await verification.Set<ScheduleEntity>().SingleAsync(value => value.Id == scheduleId);
        Assert.Equal("pending", job.State);
        Assert.Equal(scheduledFor, job.AvailableAt);
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeaseToken);
        Assert.Equal(scheduledFor, schedule.LastEnqueuedAt);
    }
}
