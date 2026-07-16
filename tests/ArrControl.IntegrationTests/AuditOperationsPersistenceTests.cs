using System.Net;
using System.Text.Json;
using ArrControl.Application.Audit;
using ArrControl.Application.Authorization;
using ArrControl.Application.Identity;
using ArrControl.Infrastructure.Audit;
using ArrControl.Infrastructure.Automation;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Connections;
using ArrControl.Infrastructure.Persistence.Health;
using ArrControl.Infrastructure.Persistence.Identity;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArrControl.IntegrationTests;

public sealed class AuditOperationsPersistenceTests(AuthApiDatabaseFixture fixture)
    : IClassFixture<AuthApiDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Query_paginates_retention_is_bounded_and_diagnostics_are_strictly_redacted()
    {
        var connectionString = await fixture.CreateMigratedSchemaAsync();
        var options = new DbContextOptionsBuilder<ArrControlDbContext>().UseNpgsql(connectionString).Options;
        const string secret = "private-name-url-address-summary-path";
        var user = new UserEntity
        {
            Email = $"{secret}@example.invalid", NormalizedEmail = $"{secret.ToUpperInvariant()}@EXAMPLE.INVALID",
            Locale = "en", TimeZone = "UTC", State = "active", CreatedAt = Now, UpdatedAt = Now,
        };
        var instance = new InstanceEntity
        {
            Name = secret, Kind = "sonarr", BaseUrl = $"https://{secret}.example.invalid/", Enabled = true,
            TlsVerificationEnabled = true, CreatedAt = Now, UpdatedAt = Now,
        };
        var incident = new HealthIncidentEntity
        {
            InstanceId = instance.Id, GroupKey = new string('a', 64), ProviderKind = "sonarr",
            Severity = "warning", FirstSeenAt = Now, LastSeenAt = Now,
        };
        await using (var seed = new ArrControlDbContext(options))
        {
            seed.AddRange(user, instance, incident);
            seed.Add(Audit(Now.AddDays(-400), "old.event", "{}", secret));
            seed.Add(Audit(Now.AddMinutes(-1), "fixture.one", "{\"kind\":\"system\"}", secret));
            seed.Add(Audit(Now.AddMinutes(-1), "fixture.two", "{\"kind\":\"system\"}", secret));
            seed.Add(Audit(Now.AddMinutes(-1), "fixture.three", "{\"kind\":\"system\"}", secret));
            await seed.SaveChangesAsync();
            var provisioner = new EfAuditRetentionScheduleProvisioner(seed, new FixedTimeProvider(Now));
            Assert.Equal(1, await provisioner.ReconcileAsync(CancellationToken.None));
            Assert.Equal(0, await provisioner.ReconcileAsync(CancellationToken.None));
        }

        await using (var context = new ArrControlDbContext(options))
        {
            var store = new EfAuditQueryStore(context);
            var filter = new NormalizedAuditFilter(
                Now.AddHours(-1), Now, null, null, null, null);
            var first = await store.QueryAsync(filter, null, 2, CancellationToken.None);
            Assert.Equal(2, first.Count);
            var next = await store.QueryAsync(
                filter,
                new AuditCursor(first[^1].OccurredAt, first[^1].Id, new string('b', 64), filter),
                2,
                CancellationToken.None);
            Assert.Single(next);
            Assert.Empty(first.Select(value => value.Id).Intersect(next.Select(value => value.Id)));
            Assert.Equal("fixture.two", Assert.Single(await store.QueryAsync(
                filter with { Action = "fixture.two" }, null, 10, CancellationToken.None)).Action);
        }

        var actor = new RbacActorContext(user.Id, Guid.CreateVersion7(), user.Email,
            new AuthenticationRequestContext("diagnostics-test", IPAddress.Parse("192.0.2.25")));
        await using (var context = new ArrControlDbContext(options))
        {
            var snapshot = await new EfAuditMaintenanceStore(context, new FixedTimeProvider(Now))
                .CreateAsync(actor, new DiagnosticsExportRequest(24, true), CancellationToken.None);
            var json = JsonSerializer.Serialize(snapshot);
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(instance.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(user.Email, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("192.0.2.25", json, StringComparison.Ordinal);
            Assert.Equal("instance-001", Assert.Single(snapshot.Instances).Reference);
            Assert.Equal(3, snapshot.Audit.Count);
        }

        await using (var context = new ArrControlDbContext(options))
        {
            var deleted = await new EfAuditMaintenanceStore(context, new FixedTimeProvider(Now))
                .DeleteExpiredAsync(Now.AddDays(-365), 100, 2, CancellationToken.None);
            Assert.Equal(1, deleted);
        }
        await using (var verification = new ArrControlDbContext(options))
        {
            Assert.False(await verification.Set<AuditEventEntity>()
                .AnyAsync(value => value.Action == "old.event"));
            var retention = await verification.Set<AuditEventEntity>().AsNoTracking()
                .SingleAsync(value => value.Action == "audit.retention");
            Assert.Equal("succeeded", retention.Outcome);
            Assert.DoesNotContain(secret, retention.SummaryJson, StringComparison.Ordinal);
            Assert.Single(await verification.Set<AuditEventEntity>()
                .Where(value => value.Action == "diagnostics.export").ToListAsync());
        }
    }

    private static AuditEventEntity Audit(
        DateTimeOffset occurredAt,
        string action,
        string scope,
        string secret) => new()
    {
        OccurredAt = occurredAt,
        ActorType = "system",
        ActorIdentifier = "fixture",
        Action = action,
        ScopeJson = scope,
        CorrelationId = Guid.CreateVersion7().ToString("N"),
        Outcome = "succeeded",
        SummaryJson = JsonSerializer.Serialize(new { secret }),
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
