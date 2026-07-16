using ArrControl.Application.Authorization;
using ArrControl.Application.Events;
using ArrControl.Infrastructure.Events;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Activity;
using ArrControl.Infrastructure.Persistence.Catalog;
using ArrControl.Infrastructure.Persistence.Connections;
using ArrControl.Infrastructure.Persistence.Identity;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArrControl.IntegrationTests;

public sealed class TransactionalOutboxPersistenceTests(AuthApiDatabaseFixture fixture)
    : IClassFixture<AuthApiDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Changes_commit_with_redacted_outbox_and_published_events_replay_with_current_scope()
    {
        var connectionString = await fixture.CreateMigratedSchemaAsync();
        var plain = new DbContextOptionsBuilder<ArrControlDbContext>().UseNpgsql(connectionString).Options;
        var groupA = Group("A");
        var groupB = Group("B");
        var instanceA = Instance("A", groupA);
        var instanceB = Instance("B", groupB);
        var user = User();
        await using (var seed = new ArrControlDbContext(plain))
        {
            seed.AddRange(instanceA, instanceB, user);
            await seed.SaveChangesAsync();
        }

        var intercepted = new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(new TransactionalOutboxInterceptor(new FixedTimeProvider(Now)))
            .Options;
        const string diagnostic = "secret title and path must not enter the outbox";
        await using (var context = new ArrControlDbContext(intercepted))
        {
            context.AddRange(Queue(instanceA.Id, "a", diagnostic), Queue(instanceB.Id, "b", diagnostic));
            await context.SaveChangesAsync();
            context.Add(new MissingSavedViewEntity
            {
                UserId = user.Id, Name = "Live", NormalizedName = "LIVE", FilterJson = "{}",
                CreatedAt = Now, UpdatedAt = Now,
            });
            await context.SaveChangesAsync();
        }

        await using (var verification = new ArrControlDbContext(plain))
        {
            var rows = await verification.Set<OutboxMessageEntity>().AsNoTracking().ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, value =>
            {
                Assert.Null(value.PublishedAt);
                Assert.DoesNotContain(diagnostic, value.PayloadJson, StringComparison.Ordinal);
            });
        }

        await using (var context = new ArrControlDbContext(intercepted))
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            context.Add(Queue(instanceA.Id, "rolled-back", diagnostic));
            await context.SaveChangesAsync();
            Assert.Equal(3, await context.Set<OutboxMessageEntity>().CountAsync());
            await transaction.RollbackAsync();
        }
        await using (var verification = new ArrControlDbContext(plain))
            Assert.Equal(2, await verification.Set<OutboxMessageEntity>().CountAsync());

        await using (var context = new ArrControlDbContext(intercepted))
            Assert.Equal(2, await new EfOutboxPublisher(context, new FixedTimeProvider(Now.AddSeconds(1)))
                .PublishBatchAsync(100, CancellationToken.None));

        string cursor;
        await using (var context = new ArrControlDbContext(plain))
        {
            var store = new EfLiveEventStore(context);
            var access = new LiveEventAccess(user.Id,
            [
                new InstanceGroupAuthorization(
                    RbacPermissions.InstancesRead, false, new HashSet<Guid> { groupA.Id }),
            ]);
            var batch = await store.ReadAsync(LiveEventService.OriginCursor, access, 100, CancellationToken.None);
            Assert.True(batch.CursorAdvanced);
            cursor = batch.Cursor;
            var activity = Assert.Single(batch.Events, value => value.Resource == LiveEventResources.Activity);
            Assert.Equal(instanceA.Id, Assert.Single(activity.InstanceIds));
            Assert.Single(batch.Events, value => value.Resource == LiveEventResources.Missing);
            Assert.True(await store.CursorExistsAsync(cursor, CancellationToken.None));
            Assert.Empty((await store.ReadAsync(cursor, access, 100, CancellationToken.None)).Events);
        }

        await using (var context = new ArrControlDbContext(plain))
        {
            var deleted = await new EfOutboxPublisher(context, new FixedTimeProvider(Now.AddDays(8)))
                .DeleteExpiredAsync(Now.AddDays(1), 100, CancellationToken.None);
            Assert.Equal(2, deleted);
            Assert.False(await new EfLiveEventStore(context).CursorExistsAsync(cursor, CancellationToken.None));
        }
    }

    private static InstanceGroupEntity Group(string name) => new()
    {
        Name = $"Outbox {name} {Guid.NewGuid():N}", CreatedAt = Now, UpdatedAt = Now,
    };

    private static InstanceEntity Instance(string name, InstanceGroupEntity group) => new()
    {
        Name = $"Outbox {name} {Guid.NewGuid():N}", Kind = "sonarr", Group = group,
        BaseUrl = $"https://{name.ToLowerInvariant()}.example.invalid/", Enabled = true,
        TlsVerificationEnabled = true, CreatedAt = Now, UpdatedAt = Now,
    };

    private static UserEntity User() => new()
    {
        Email = $"outbox-{Guid.NewGuid():N}@example.invalid",
        NormalizedEmail = $"OUTBOX-{Guid.NewGuid():N}@EXAMPLE.INVALID",
        Locale = "en", TimeZone = "UTC", State = "active", CreatedAt = Now, UpdatedAt = Now,
    };

    private static QueueItemEntity Queue(Guid instanceId, string key, string title) => new()
    {
        InstanceId = instanceId, ProviderKey = key, ProviderKind = "sonarr", Title = title,
        Status = "downloading", TrackedStatus = "ok", TrackedState = "downloading",
        SizeBytes = 100, RemainingBytes = 50, ObservedAt = Now,
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
