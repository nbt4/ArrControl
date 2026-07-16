using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ArrControl.Application.Authorization;
using ArrControl.Application.Catalog;
using ArrControl.Application.Events;
using ArrControl.Application.Identity;
using ArrControl.Application.Operations;
using ArrControl.Infrastructure.Catalog;
using ArrControl.Infrastructure.Events;
using ArrControl.Infrastructure.Operations;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace ArrControl.IntegrationTests;

public sealed class PerformanceFactAttribute : FactAttribute
{
    public PerformanceFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ARRCONTROL_RUN_PERFORMANCE_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set ARRCONTROL_RUN_PERFORMANCE_TESTS=1 to run the capacity envelope.";
        }
    }
}

public sealed class PerformanceEnvelopeTests(
    AuthApiDatabaseFixture fixture,
    ITestOutputHelper output) : IClassFixture<AuthApiDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [PerformanceFact]
    public async Task Reference_capacity_envelope_meets_latency_and_bulk_targets()
    {
        var connectionString = await fixture.CreateMigratedSchemaAsync();
        await SeedAsync(connectionString);
        var options = new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(connectionString).Options;

        await using (var warmup = new ArrControlDbContext(options))
        {
            await QueryMissingAsync(warmup);
        }

        var dashboardSamples = new ConcurrentBag<double>();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, 20),
            new ParallelOptions { MaxDegreeOfParallelism = 20 },
            async (_, cancellationToken) =>
            {
                for (var request = 0; request < 10; request++)
                {
                    await using var context = new ArrControlDbContext(options);
                    var started = Stopwatch.GetTimestamp();
                    var result = await QueryMissingAsync(context, cancellationToken);
                    dashboardSamples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    Assert.Equal(51, result.Items.Count);
                    Assert.Equal(20, result.Freshness.Count);
                }
            });

        var dashboardP95 = Percentile(dashboardSamples, 0.95);
        output.WriteLine("cached missing projection: samples={0}, p95={1:F1} ms", dashboardSamples.Count, dashboardP95);
        Assert.True(dashboardP95 < 500, $"Cached projection p95 was {dashboardP95:F1} ms.");

        Guid userId;
        Guid[] instanceIds;
        await using (var context = new ArrControlDbContext(options))
        {
            userId = await context.Set<UserEntity>().Select(value => value.Id).SingleAsync();
            instanceIds = await context.Set<ArrControl.Infrastructure.Persistence.Connections.InstanceEntity>()
                .OrderBy(value => value.Id).Select(value => value.Id).ToArrayAsync();
        }

        var targets = Enumerable.Range(0, 10_000)
            .Select(index => new OperationTargetInput(instanceIds[index % instanceIds.Length], $"target:{index:D5}"))
            .ToArray();
        var actor = new RbacActorContext(userId, Guid.CreateVersion7(), "capacity@example.invalid",
            new AuthenticationRequestContext("performance-envelope", IPAddress.Loopback));
        var command = new CreateOperationCommand(
            actor, "capacity", "/capacity", "10k-targets", new string('a', 64), true, targets);
        var bulkStarted = Stopwatch.GetTimestamp();
        CreateOperationResult created;
        await using (var context = new ArrControlDbContext(options))
        {
            created = await new EfOperationStore(context, new FixedTimeProvider(Now))
                .CreateAsync(command, CancellationToken.None);
        }

        var bulkCreate = Stopwatch.GetElapsedTime(bulkStarted).TotalMilliseconds;
        output.WriteLine("10k-target operation creation: {0:F1} ms", bulkCreate);
        Assert.Equal(CreateOperationStatus.Created, created.Status);
        Assert.Equal(10_000, created.Operation?.Targets.Count);
        Assert.True(bulkCreate < 15_000, $"10k-target creation took {bulkCreate:F1} ms.");

        var reconnectSamples = new ConcurrentBag<double>();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, 250),
            new ParallelOptions { MaxDegreeOfParallelism = 50 },
            async (_, cancellationToken) =>
            {
                await using var context = new ArrControlDbContext(options);
                var store = new EfLiveEventStore(context);
                var started = Stopwatch.GetTimestamp();
                var cursor = await store.GetLatestCursorAsync(cancellationToken);
                var batch = await store.ReadAsync(cursor,
                    new LiveEventAccess(userId, []), 100, cancellationToken);
                reconnectSamples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                Assert.Empty(batch.Events);
            });
        var reconnectP95 = Percentile(reconnectSamples, 0.95);
        output.WriteLine("250 reconnect handshakes at concurrency 50: p95={0:F1} ms", reconnectP95);
        Assert.True(reconnectP95 < 500, $"Reconnect p95 was {reconnectP95:F1} ms.");
    }

    private static Task<MissingStoreResult> QueryMissingAsync(
        ArrControlDbContext context,
        CancellationToken cancellationToken = default) =>
        new EfMissingQueryStore(context).QueryAsync(new MissingQuerySpec(
            true, [], MissingFilter.Empty, null, 51, Now, TimeSpan.FromMinutes(30)), cancellationToken);

    private static double Percentile(IEnumerable<double> samples, double percentile)
    {
        var ordered = samples.Order().ToArray();
        return ordered[(int)Math.Ceiling(ordered.Length * percentile) - 1];
    }

    private static async Task SeedAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = """
            INSERT INTO users (id, email, normalized_email, locale, time_zone, state, created_at, updated_at)
            VALUES (gen_random_uuid(), 'capacity@example.invalid', 'CAPACITY@EXAMPLE.INVALID',
                    'en', 'UTC', 'active', now(), now());

            CREATE TEMP TABLE capacity_instances (ordinal integer PRIMARY KEY, id uuid NOT NULL);
            INSERT INTO capacity_instances
            SELECT value, gen_random_uuid() FROM generate_series(1, 20) AS value;
            INSERT INTO service_instances
                (id, name, kind, base_url, enabled, tls_verification_enabled,
                 allow_private_network_access, created_at, updated_at)
            SELECT id, 'Capacity ' || ordinal, CASE WHEN ordinal % 2 = 0 THEN 'radarr' ELSE 'sonarr' END,
                   'https://capacity-' || ordinal || '.example.invalid/', true, true, false, now(), now()
            FROM capacity_instances;

            CREATE TEMP TABLE capacity_items AS
            SELECT value AS ordinal,
                   ((value - 1) % 20) + 1 AS instance_ordinal,
                   'item:' || value AS provider_key,
                   gen_random_uuid() AS media_id
            FROM generate_series(1, 100000) AS value;

            INSERT INTO provider_items
                (instance_id, provider_key, provider_kind, raw_kind, provider_data_json,
                 fingerprint, first_seen_at, updated_at)
            SELECT instances.id, items.provider_key,
                   CASE WHEN items.instance_ordinal % 2 = 0 THEN 'radarr' ELSE 'sonarr' END,
                   'movie', '{}'::jsonb, repeat('a', 64), now(), now()
            FROM capacity_items items
            JOIN capacity_instances instances ON instances.ordinal = items.instance_ordinal;

            INSERT INTO media_entities
                (id, instance_id, provider_key, canonical_kind, title, year, monitored,
                 has_file, status, external_ids_json)
            SELECT items.media_id, instances.id, items.provider_key, 'movie',
                   'Capacity title ' || lpad(items.ordinal::text, 6, '0'), 2026, true,
                   false, 'released', '{}'::jsonb
            FROM capacity_items items
            JOIN capacity_instances instances ON instances.ordinal = items.instance_ordinal;

            INSERT INTO missing_items
                (instance_id, provider_key, reason, monitored, first_seen_at, updated_at)
            SELECT instances.id, items.provider_key, 'missing', true, now(), now()
            FROM capacity_items items
            JOIN capacity_instances instances ON instances.ordinal = items.instance_ordinal;

            INSERT INTO sync_checkpoints (instance_id, stream, cursor, last_success_at, updated_at)
            SELECT id, 'catalog', 'capacity', now(), now() FROM capacity_instances;

            INSERT INTO outbox_messages
                (id, type, payload_json, occurred_at, published_at, attempt_count)
            SELECT gen_random_uuid(), 'missing.changed',
                   '{"version":1,"resource":"missing","requiredPermission":"library.read","targets":[]}'::jsonb,
                   now() - (value || ' milliseconds')::interval,
                   now() - (value || ' milliseconds')::interval, 0
            FROM generate_series(1, 500) AS value;

            ANALYZE service_instances;
            ANALYZE provider_items;
            ANALYZE media_entities;
            ANALYZE missing_items;
            ANALYZE sync_checkpoints;
            ANALYZE outbox_messages;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
