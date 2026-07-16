using System.Net;
using ArrControl.Application.Authorization;
using ArrControl.Application.Health;
using ArrControl.Application.Identity;
using ArrControl.Application.Providers;
using ArrControl.Infrastructure.Automation;
using ArrControl.Infrastructure.Health;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Connections;
using ArrControl.Infrastructure.Persistence.Health;
using ArrControl.Infrastructure.Persistence.Identity;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArrControl.IntegrationTests;

public sealed class HealthIncidentPersistenceTests(AuthApiDatabaseFixture fixture)
    : IClassFixture<AuthApiDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Snapshot_groups_sources_resolves_reopens_and_audits_user_state_without_diagnostics()
    {
        var connectionString = await fixture.CreateMigratedSchemaAsync();
        var options = new DbContextOptionsBuilder<ArrControlDbContext>().UseNpgsql(connectionString).Options;
        var group = new InstanceGroupEntity
        {
            Name = $"Health {Guid.NewGuid():N}", CreatedAt = Now, UpdatedAt = Now,
        };
        var instance = new InstanceEntity
        {
            Name = $"Health {Guid.NewGuid():N}", Kind = "sonarr", Group = group,
            BaseUrl = "https://health.example.invalid/", Enabled = true,
            TlsVerificationEnabled = true, CreatedAt = Now, UpdatedAt = Now,
        };
        var user = new UserEntity
        {
            Email = $"health-{Guid.NewGuid():N}@example.invalid",
            NormalizedEmail = $"HEALTH-{Guid.NewGuid():N}@EXAMPLE.INVALID",
            Locale = "en", TimeZone = "UTC", State = "active", CreatedAt = Now, UpdatedAt = Now,
        };
        await using (var seed = new ArrControlDbContext(options))
        {
            seed.AddRange(instance, user);
            await seed.SaveChangesAsync();
            var provisioner = new EfHealthScheduleProvisioner(seed, new FixedTimeProvider(Now));
            Assert.Equal(1, await provisioner.ReconcileAsync(CancellationToken.None));
            Assert.Equal(0, await provisioner.ReconcileAsync(CancellationToken.None));
        }

        const string diagnostic = "private diagnostic media path must not enter audit";
        var remediation = new Uri("https://wiki.servarr.com/sonarr/system#health");
        var initial = HealthIncidentGrouper.Group("sonarr",
        [
            new ProviderHealthIssue(1, "DownloadClientCheck", "warning", diagnostic, remediation),
            new ProviderHealthIssue(2, "IndexerCheck", "error", "indexer unavailable", remediation),
        ]);
        await using (var context = new ArrControlDbContext(options))
            await new EfHealthIncidentStore(context, new FixedTimeProvider(Now)).ApplyAsync(
                instance.Id, "sonarr", Now, initial, CancellationToken.None);

        Guid incidentId;
        await using (var context = new ArrControlDbContext(options))
        {
            var store = new EfHealthIncidentStore(context, new FixedTimeProvider(Now.AddMinutes(1)));
            Assert.Empty(await store.QueryAsync(false, [Guid.CreateVersion7()], [], false,
                Now.AddMinutes(1), CancellationToken.None));
            var incident = Assert.Single(await store.QueryAsync(false, [group.Id], [], false,
                Now.AddMinutes(1), CancellationToken.None));
            incidentId = incident.Id;
            Assert.Equal("error", incident.Severity);
            Assert.Equal(2, incident.Sources.Count);
            Assert.Equal(remediation.AbsoluteUri, incident.RemediationUrl);
        }

        var actor = new RbacActorContext(user.Id, Guid.CreateVersion7(), user.Email,
            new AuthenticationRequestContext("health-test", IPAddress.Loopback));
        await using (var context = new ArrControlDbContext(options))
        {
            var store = new EfHealthIncidentStore(context, new FixedTimeProvider(Now.AddMinutes(2)));
            Assert.NotNull(await store.SetAcknowledgementAsync(actor, incidentId, true, CancellationToken.None));
            var snoozed = await store.SetSnoozeAsync(
                actor, incidentId, Now.AddDays(1), CancellationToken.None);
            Assert.Equal(Now.AddDays(1), snoozed?.SnoozedUntil);
        }

        var reduced = HealthIncidentGrouper.Group("sonarr",
            [new ProviderHealthIssue(2, "IndexerCheck", "warning", "updated", remediation)]);
        await using (var context = new ArrControlDbContext(options))
            await new EfHealthIncidentStore(context, new FixedTimeProvider(Now.AddMinutes(3))).ApplyAsync(
                instance.Id, "sonarr", Now.AddMinutes(3), reduced, CancellationToken.None);
        await using (var context = new ArrControlDbContext(options))
        {
            var incident = Assert.Single(await new EfHealthIncidentStore(context, new FixedTimeProvider(Now))
                .QueryAsync(true, [], [], false, Now.AddMinutes(3), CancellationToken.None));
            Assert.Equal("warning", incident.Severity);
            Assert.Equal(2, incident.Sources.Count);
            Assert.Single(incident.Sources, value => value.Active);
        }

        await using (var context = new ArrControlDbContext(options))
            await new EfHealthIncidentStore(context, new FixedTimeProvider(Now.AddMinutes(4))).ApplyAsync(
                instance.Id, "sonarr", Now.AddMinutes(4), [], CancellationToken.None);
        await using (var context = new ArrControlDbContext(options))
        {
            var store = new EfHealthIncidentStore(context, new FixedTimeProvider(Now.AddMinutes(4)));
            Assert.Empty(await store.QueryAsync(true, [], [], false, Now.AddMinutes(4), CancellationToken.None));
            Assert.NotNull(Assert.Single(await store.QueryAsync(true, [], [], true,
                Now.AddMinutes(4), CancellationToken.None)).ResolvedAt);
        }

        await using (var context = new ArrControlDbContext(options))
            await new EfHealthIncidentStore(context, new FixedTimeProvider(Now.AddMinutes(5))).ApplyAsync(
                instance.Id, "sonarr", Now.AddMinutes(5), reduced, CancellationToken.None);
        await using (var context = new ArrControlDbContext(options))
        {
            var reopened = Assert.Single(await new EfHealthIncidentStore(context, new FixedTimeProvider(Now))
                .QueryAsync(true, [], [], false, Now.AddMinutes(5), CancellationToken.None));
            Assert.Equal(incidentId, reopened.Id);
            Assert.Null(reopened.ResolvedAt);
            Assert.Null(reopened.AcknowledgedAt);
            Assert.Null(reopened.SnoozedUntil);
            var audits = await context.Set<AuditEventEntity>().AsNoTracking()
                .Where(value => value.ActorUserId == user.Id && value.Action.StartsWith("health."))
                .ToListAsync();
            Assert.Equal(2, audits.Count);
            Assert.All(audits, value => Assert.DoesNotContain(diagnostic, value.SummaryJson, StringComparison.Ordinal));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
