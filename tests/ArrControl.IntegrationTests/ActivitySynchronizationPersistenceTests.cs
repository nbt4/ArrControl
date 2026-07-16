using ArrControl.Application.Activity;
using ArrControl.Infrastructure.Activity;
using ArrControl.Infrastructure.Automation;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Connections;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArrControl.IntegrationTests;

public sealed class ActivitySynchronizationPersistenceTests(AuthApiDatabaseFixture fixture)
    : IClassFixture<AuthApiDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Queue_snapshot_history_upsert_and_case_insensitive_download_correlation_are_atomic()
    {
        var connectionString = await fixture.CreateMigratedSchemaAsync();
        var options = new DbContextOptionsBuilder<ArrControlDbContext>().UseNpgsql(connectionString).Options;
        var instance = new InstanceEntity
        {
            Name = $"Activity {Guid.NewGuid():N}", Kind = "sonarr",
            BaseUrl = "https://activity.example.invalid/", Enabled = true,
            TlsVerificationEnabled = true, CreatedAt = Now, UpdatedAt = Now,
        };
        await using (var context = new ArrControlDbContext(options))
        {
            context.Add(instance);
            await context.SaveChangesAsync();
            Assert.Equal(1, await new EfActivityScheduleProvisioner(context, new FixedTimeProvider(Now))
                .ReconcileAsync(CancellationToken.None));
            Assert.Equal(0, await new EfActivityScheduleProvisioner(context, new FixedTimeProvider(Now))
                .ReconcileAsync(CancellationToken.None));
        }

        var snapshot = new ProviderActivitySnapshot(
            Now,
            [new QueueItemSnapshot("queue:1", "episode:2", "ABC-123", "Download", "downloading",
                "ok", "downloading", "usenet", 1000, 250, Now.AddMinutes(-5), Now.AddMinutes(5),
                "client", "indexer")],
            [new HistoryItemSnapshot("history:1", "episode:2", "abc-123", "Download", "grabbed", Now.AddMinutes(-10))]);
        await using (var context = new ArrControlDbContext(options))
        {
            await new EfActivitySnapshotStore(context).ApplyAsync(instance.Id, "sonarr", snapshot, CancellationToken.None);
        }

        await using (var context = new ArrControlDbContext(options))
        {
            var result = await new EfActivityQueryStore(context).QueryAsync(
                true, [], [], 100, Now.AddMinutes(1), CancellationToken.None);
            var queue = Assert.Single(result.Queue);
            Assert.Equal(1, queue.CorrelatedHistoryCount);
            Assert.Equal("grabbed", queue.LatestHistoryEvent);
            Assert.True(Assert.Single(result.History).QueueCorrelated);
        }

        await using (var context = new ArrControlDbContext(options))
        {
            await new EfActivitySnapshotStore(context).ApplyAsync(
                instance.Id, "sonarr", snapshot with { ObservedAt = Now.AddMinutes(1), Queue = [] },
                CancellationToken.None);
            var result = await new EfActivityQueryStore(context).QueryAsync(
                true, [], [], 100, Now.AddMinutes(1), CancellationToken.None);
            Assert.Empty(result.Queue);
            Assert.False(Assert.Single(result.History).QueueCorrelated);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
