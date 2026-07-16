using ArrControl.Application.Catalog;
using ArrControl.Application.Authorization;
using ArrControl.Application.Identity;
using ArrControl.Application.Search;
using ArrControl.Infrastructure.Automation;
using ArrControl.Infrastructure.Catalog;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Automation;
using ArrControl.Infrastructure.Persistence.Catalog;
using ArrControl.Infrastructure.Persistence.Connections;
using ArrControl.Infrastructure.Persistence.Identity;
using ArrControl.Infrastructure.Persistence.Operations;
using ArrControl.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;
using System.Net;
using Xunit;

namespace ArrControl.IntegrationTests;

public sealed class CatalogSynchronizationPersistenceTests(AuthApiDatabaseFixture databaseFixture)
    : IClassFixture<AuthApiDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Snapshot_diff_and_catalog_schedule_reconciliation_are_idempotent()
    {
        var connectionString = await databaseFixture.CreateMigratedSchemaAsync();
        var options = new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var instance = new InstanceEntity
        {
            Name = $"Catalog {Guid.NewGuid():N}",
            Kind = "sonarr",
            BaseUrl = "https://catalog.example.invalid/",
            Enabled = true,
            TlsVerificationEnabled = true,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        await using (var seed = new ArrControlDbContext(options))
        {
            seed.Add(instance);
            await seed.SaveChangesAsync();
        }

        await using (var schedules = new ArrControlDbContext(options))
        {
            var provisioner = new EfCatalogScheduleProvisioner(schedules, new FixedTimeProvider(Now));
            Assert.Equal(1, await provisioner.ReconcileAsync(CancellationToken.None));
            Assert.Equal(0, await provisioner.ReconcileAsync(CancellationToken.None));
        }

        var first = new ProviderCatalogSnapshot(Now,
        [
            Item("series:1", CatalogItemKinds.Series, null, "Series", null),
            Item("episode:2", CatalogItemKinds.Episode, "series:1", "Pilot", false),
            Item("episode:3", CatalogItemKinds.Episode, "series:1", "Zulu", false),
        ]);
        await using (var context = new ArrControlDbContext(options))
        {
            var result = await new EfCatalogSnapshotStore(context)
                .ApplyAsync(instance.Id, "sonarr", first, CancellationToken.None);
            Assert.Equal((3, 0, 0, 0), (result.Added, result.Updated, result.Unchanged, result.Removed));
            context.Add(new SyncCheckpointEntity
            {
                InstanceId = instance.Id,
                Stream = CatalogJobTypes.CheckpointStream,
                Cursor = "test-cursor",
                LastSuccessAt = Now,
                UpdatedAt = Now,
            });
            await context.SaveChangesAsync();
        }

        await using (var context = new ArrControlDbContext(options))
        {
            var rows = await new EfMissingQueryStore(context).QueryAsync(
                new MissingQuerySpec(
                    true,
                    [],
                    new MissingFilter([], [CatalogItemKinds.Episode], [MissingReasons.Missing], "Pil"),
                    null,
                    10,
                    Now.AddMinutes(1),
                    TimeSpan.FromMinutes(30)),
                CancellationToken.None);
            var missing = Assert.Single(rows.Items);
            Assert.Equal("Pilot", missing.Title);
            Assert.Equal(instance.Id, missing.InstanceId);
            Assert.Equal(Now, missing.ObservedAt);
            Assert.False(missing.Stale);

            var firstPage = await new EfMissingQueryStore(context).QueryAsync(
                new MissingQuerySpec(
                    true,
                    [],
                    new MissingFilter([], [], [MissingReasons.Missing], null),
                    null,
                    1,
                    Now,
                    TimeSpan.FromMinutes(30)),
                CancellationToken.None);
            var firstItem = Assert.Single(firstPage.Items);
            var secondPage = await new EfMissingQueryStore(context).QueryAsync(
                new MissingQuerySpec(
                    true,
                    [],
                    new MissingFilter([], [], [MissingReasons.Missing], null),
                    new MissingCursor("unused", firstItem.Title.ToLowerInvariant(), firstItem.Id),
                    1,
                    Now,
                    TimeSpan.FromMinutes(30)),
                CancellationToken.None);
            Assert.Equal("Zulu", Assert.Single(secondPage.Items).Title);
            var freshness = Assert.Single(secondPage.Freshness);
            Assert.Equal(instance.Id, freshness.InstanceId);
            Assert.False(freshness.Stale);

            var searchTargets = await new EfSearchTargetStore(context).ResolveAsync(
                true,
                [],
                new SearchScopeRequest(SearchScopeModes.All, [], [], []),
                CancellationToken.None);
            Assert.Equal(2, searchTargets.Count);
            Assert.All(searchTargets, value => Assert.StartsWith("episode:", value.ProviderKey));
        }

        await using (var context = new ArrControlDbContext(options))
        {
            var result = await new EfCatalogSnapshotStore(context)
                .ApplyAsync(instance.Id, "sonarr", first with { ObservedAt = Now.AddMinutes(15) }, CancellationToken.None);
            Assert.Equal((0, 0, 3, 0), (result.Added, result.Updated, result.Unchanged, result.Removed));
        }

        var changed = new ProviderCatalogSnapshot(Now.AddMinutes(30),
        [
            Item("series:1", CatalogItemKinds.Series, null, "Renamed Series", null),
        ]);
        await using (var context = new ArrControlDbContext(options))
        {
            var result = await new EfCatalogSnapshotStore(context)
                .ApplyAsync(instance.Id, "sonarr", changed, CancellationToken.None);
            Assert.Equal((0, 1, 0, 2), (result.Added, result.Updated, result.Unchanged, result.Removed));
        }

        await using (var verification = new ArrControlDbContext(options))
        {
            var provider = await verification.Set<ProviderItemEntity>().SingleAsync();
            var media = await verification.Set<MediaEntityEntity>().SingleAsync();
            Assert.Equal("Renamed Series", media.Title);
            Assert.Equal(first.ObservedAt, provider.FirstSeenAt);
            Assert.Equal(changed.ObservedAt, provider.UpdatedAt);
            var schedule = await verification.Set<ScheduleEntity>().SingleAsync();
            Assert.True(schedule.Enabled);
            Assert.Equal(instance.Id.ToString("D"), schedule.ScopeKey);
            Assert.Equal(CatalogJobTypes.Sync, schedule.Type);
        }

        await using (var context = new ArrControlDbContext(options))
        {
            var stored = await context.Set<InstanceEntity>().SingleAsync(value => value.Id == instance.Id);
            stored.Enabled = false;
            await context.SaveChangesAsync();
            Assert.Equal(
                1,
                await new EfCatalogScheduleProvisioner(context, new FixedTimeProvider(Now.AddHours(1)))
                    .ReconcileAsync(CancellationToken.None));
            Assert.False((await context.Set<ScheduleEntity>().SingleAsync()).Enabled);
        }
    }

    [Fact]
    public async Task Wave_two_arr_catalogs_materialize_schedules_missing_freshness_and_search_targets()
    {
        var connectionString = await databaseFixture.CreateMigratedSchemaAsync();
        var options = new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(connectionString).Options;
        var instances = new[]
        {
            NewInstance("lidarr"), NewInstance("readarr"), NewInstance("whisparr"),
        };
        await using (var seed = new ArrControlDbContext(options))
        {
            seed.AddRange(instances);
            await seed.SaveChangesAsync();
            Assert.Equal(3, await new EfCatalogScheduleProvisioner(seed, new FixedTimeProvider(Now))
                .ReconcileAsync(CancellationToken.None));
        }

        var snapshots = new[]
        {
            new ProviderCatalogSnapshot(Now,
                [Item("artist:1", CatalogItemKinds.Artist, null, "Artist", null),
                 Item("album:2", CatalogItemKinds.Album, "artist:1", "Album", false)]),
            new ProviderCatalogSnapshot(Now,
                [Item("author:1", CatalogItemKinds.Author, null, "Author", null),
                 Item("book:2", CatalogItemKinds.Book, "author:1", "Book", false)]),
            new ProviderCatalogSnapshot(Now,
                [Item("movie:2", CatalogItemKinds.Movie, null, "Movie", false)]),
        };
        await using (var context = new ArrControlDbContext(options))
        {
            var store = new EfCatalogSnapshotStore(context);
            for (var index = 0; index < instances.Length; index++)
            {
                await store.ApplyAsync(instances[index].Id, instances[index].Kind, snapshots[index],
                    CancellationToken.None);
                context.Add(new SyncCheckpointEntity
                {
                    InstanceId = instances[index].Id,
                    Stream = CatalogJobTypes.CheckpointStream,
                    Cursor = "fixture",
                    LastSuccessAt = Now,
                    UpdatedAt = Now,
                });
                await context.SaveChangesAsync();
            }
        }

        await using (var verify = new ArrControlDbContext(options))
        {
            var result = await new EfMissingQueryStore(verify).QueryAsync(new MissingQuerySpec(
                true, [], new MissingFilter([], [], [MissingReasons.Missing], null), null,
                10, Now, TimeSpan.FromMinutes(30)), CancellationToken.None);
            Assert.Equal(3, result.Items.Count);
            Assert.Equal(3, result.Freshness.Count);
            Assert.All(result.Freshness, value => Assert.False(value.Stale));
            var targets = await new EfSearchTargetStore(verify).ResolveAsync(true, [],
                new SearchScopeRequest(SearchScopeModes.All, [], [], []), CancellationToken.None);
            Assert.Equal(3, targets.Count);
            Assert.Equal(["lidarr", "readarr", "whisparr"],
                targets.Select(value => value.ProviderKind).Order().ToArray());
        }
    }

    [Fact]
    public async Task Saved_views_are_user_owned_case_insensitively_unique_and_redacted_in_audit()
    {
        var connectionString = await databaseFixture.CreateMigratedSchemaAsync();
        var options = new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var user = new UserEntity
        {
            Email = $"catalog-{Guid.NewGuid():N}@example.invalid",
            NormalizedEmail = $"CATALOG-{Guid.NewGuid():N}@EXAMPLE.INVALID",
            Locale = "en",
            TimeZone = "UTC",
            State = "active",
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        await using (var seed = new ArrControlDbContext(options))
        {
            seed.Add(user);
            await seed.SaveChangesAsync();
        }

        var actor = new RbacActorContext(
            user.Id,
            Guid.CreateVersion7(),
            user.Email,
            new AuthenticationRequestContext("saved-view-test", IPAddress.Loopback));
        var viewId = Guid.CreateVersion7();
        var filter = new MissingFilter([], [CatalogItemKinds.Movie], [MissingReasons.Missing], "private title");
        await using (var context = new ArrControlDbContext(options))
        {
            var store = new EfMissingSavedViewStore(context, new FixedTimeProvider(Now));
            Assert.Equal(
                MissingSavedViewWriteStatus.Created,
                (await store.UpsertAsync(actor, viewId, "Morning", filter, CancellationToken.None)).Status);
        }

        await using (var context = new ArrControlDbContext(options))
        {
            var store = new EfMissingSavedViewStore(context, new FixedTimeProvider(Now.AddMinutes(1)));
            Assert.Equal(
                MissingSavedViewWriteStatus.Updated,
                (await store.UpsertAsync(actor, viewId, "Morning", filter, CancellationToken.None)).Status);
            Assert.Equal(
                MissingSavedViewWriteStatus.NameConflict,
                (await store.UpsertAsync(
                    actor,
                    Guid.CreateVersion7(),
                    " morning ",
                    filter,
                    CancellationToken.None)).Status);
        }

        await using (var context = new ArrControlDbContext(options))
        {
            var store = new EfMissingSavedViewStore(context, new FixedTimeProvider(Now.AddMinutes(2)));
            var view = Assert.Single(await store.ListAsync(user.Id, CancellationToken.None));
            Assert.Equal("private title", view.Filter.Search);
            Assert.True(await store.DeleteAsync(actor, viewId, CancellationToken.None));
            Assert.False(await store.DeleteAsync(actor, viewId, CancellationToken.None));
        }

        await using var verification = new ArrControlDbContext(options);
        var audit = await verification.Set<AuditEventEntity>()
            .Where(value => value.ActorUserId == user.Id && value.Action.StartsWith("catalog.missing_view"))
            .ToListAsync();
        Assert.Equal(4, audit.Count);
        Assert.All(audit, value => Assert.DoesNotContain("private title", value.SummaryJson, StringComparison.Ordinal));
    }

    private static CatalogItemSnapshot Item(
        string providerKey,
        string kind,
        string? parent,
        string title,
        bool? hasFile) =>
        new(
            providerKey,
            kind,
            parent,
            title,
            2024,
            kind == CatalogItemKinds.Episode ? 1 : null,
            kind == CatalogItemKinds.Episode ? 1 : null,
            true,
            hasFile,
            "unknown",
            Now,
            Now.AddYears(-1),
            new Dictionary<string, string> { ["tvdb"] = "1" },
            new Dictionary<string, object?> { ["fixture"] = true });

    private static InstanceEntity NewInstance(string kind) => new()
    {
        Name = $"{kind} {Guid.NewGuid():N}",
        Kind = kind,
        BaseUrl = $"https://{kind}.example.invalid/",
        Enabled = true,
        TlsVerificationEnabled = true,
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
