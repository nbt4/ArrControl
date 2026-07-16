using ArrControl.Application.Automation;
using ArrControl.Infrastructure.Automation;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Automation;
using ArrControl.Infrastructure.Persistence.Connections;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArrControl.IntegrationTests;

public sealed class JobSchedulerPersistenceTests(AuthApiDatabaseFixture databaseFixture)
    : IClassFixture<AuthApiDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Claims_are_skip_locked_fenced_retryable_and_checkpoint_completion_is_atomic()
    {
        var connectionString = await databaseFixture.CreateMigratedSchemaAsync();
        var options = new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var factory = new TestContextFactory(options);
        var seeded = await SeedAsync(options);

        await using var firstContext = new ArrControlDbContext(options);
        await using var secondContext = new ArrControlDbContext(options);
        var firstStore = Store(firstContext, factory);
        var secondStore = Store(secondContext, factory);
        var claims = await Task.WhenAll(
            firstStore.ClaimAsync("worker-1", Now, TimeSpan.FromSeconds(30), 3, 1, CancellationToken.None),
            secondStore.ClaimAsync("worker-2", Now, TimeSpan.FromSeconds(30), 3, 1, CancellationToken.None));
        var claim = Assert.Single(claims.SelectMany(value => value));
        Assert.Equal(1, claim.Attempt);

        await using (var staleContext = new ArrControlDbContext(options))
        {
            var forged = claim with { LeaseToken = Guid.CreateVersion7() };
            Assert.False(await Store(staleContext, factory).CompleteAsync(
                forged,
                Now.AddSeconds(1),
                [],
                CancellationToken.None));
        }

        await using (var completionContext = new ArrControlDbContext(options))
        {
            Assert.True(await Store(completionContext, factory).CompleteAsync(
                claim,
                Now.AddSeconds(2),
                [new SyncCheckpointUpdate(seeded.InstanceId, "catalog", "cursor-42")],
                CancellationToken.None));
        }

        await using (var verification = new ArrControlDbContext(options))
        {
            var job = await verification.Set<JobRunEntity>().SingleAsync(value => value.Id == seeded.FirstJobId);
            Assert.Equal(JobRunStates.Succeeded, job.State);
            Assert.Null(job.LeaseToken);
            var checkpoint = await verification.Set<SyncCheckpointEntity>().SingleAsync();
            Assert.Equal("cursor-42", checkpoint.Cursor);
            Assert.Equal(Now.AddSeconds(2), checkpoint.LastSuccessAt);
        }

        await SeedRetryJobAsync(options, seeded.ScheduleId);

        ClaimedJob retryClaim;
        await using (var retryClaimContext = new ArrControlDbContext(options))
        {
            retryClaim = Assert.Single(await Store(retryClaimContext, factory).ClaimAsync(
                "worker-3", Now, TimeSpan.FromSeconds(30), 3, 1, CancellationToken.None));
        }

        var retryAt = Now.AddMinutes(1);
        await using (var failureContext = new ArrControlDbContext(options))
        {
            Assert.True(await Store(failureContext, factory).FailAsync(
                retryClaim,
                "upstream_unavailable",
                Now.AddSeconds(3),
                retryAt,
                CancellationToken.None));
        }

        await using (var earlyContext = new ArrControlDbContext(options))
        {
            Assert.Empty(await Store(earlyContext, factory).ClaimAsync(
                "worker-4", retryAt.AddTicks(-1), TimeSpan.FromSeconds(30), 3, 1, CancellationToken.None));
        }

        await using (var retryContext = new ArrControlDbContext(options))
        {
            var retried = Assert.Single(await Store(retryContext, factory).ClaimAsync(
                "worker-4", retryAt, TimeSpan.FromSeconds(30), 3, 1, CancellationToken.None));
            Assert.Equal(2, retried.Attempt);
            Assert.NotEqual(retryClaim.LeaseToken, retried.LeaseToken);
        }
    }

    private static async Task<SeedResult> SeedAsync(DbContextOptions<ArrControlDbContext> options)
    {
        var instance = new InstanceEntity
        {
            Name = $"Scheduler {Guid.NewGuid():N}",
            Kind = "sonarr",
            BaseUrl = "https://scheduler.example.invalid/",
            Enabled = true,
            TlsVerificationEnabled = true,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        var schedule = new ScheduleEntity
        {
            Type = "test.poll",
            Cron = "*/5 * * * *",
            TimeZone = "UTC",
            ScopeJson = "{}",
            Enabled = true,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        var first = Job(schedule, Now.AddMinutes(-2));
        await using var context = new ArrControlDbContext(options);
        context.AddRange(instance, schedule, first);
        await context.SaveChangesAsync();
        return new SeedResult(instance.Id, schedule.Id, first.Id);
    }

    private static async Task SeedRetryJobAsync(
        DbContextOptions<ArrControlDbContext> options,
        Guid scheduleId)
    {
        await using var context = new ArrControlDbContext(options);
        context.Add(new JobRunEntity
        {
            ScheduleId = scheduleId,
            State = JobRunStates.Pending,
            ScheduledFor = Now.AddMinutes(-1),
            AvailableAt = Now.AddMinutes(-1),
            CreatedAt = Now.AddMinutes(-1),
        });
        await context.SaveChangesAsync();
    }

    private static JobRunEntity Job(ScheduleEntity schedule, DateTimeOffset scheduledFor) => new()
    {
        Schedule = schedule,
        State = JobRunStates.Pending,
        ScheduledFor = scheduledFor,
        AvailableAt = scheduledFor,
        CreatedAt = scheduledFor,
    };

    private static EfJobSchedulerStore Store(
        ArrControlDbContext context,
        IDbContextFactory<ArrControlDbContext> factory) =>
        new(context, factory, TimeProvider.System);

    private sealed class TestContextFactory(DbContextOptions<ArrControlDbContext> options)
        : IDbContextFactory<ArrControlDbContext>
    {
        public ArrControlDbContext CreateDbContext() => new(options);
    }

    private sealed record SeedResult(Guid InstanceId, Guid ScheduleId, Guid FirstJobId);
}
