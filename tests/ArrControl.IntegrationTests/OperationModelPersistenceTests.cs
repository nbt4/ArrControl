using System.Net;
using ArrControl.Application.Authorization;
using ArrControl.Application.Identity;
using ArrControl.Application.Operations;
using ArrControl.Infrastructure.Operations;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Connections;
using ArrControl.Infrastructure.Persistence.Identity;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArrControl.IntegrationTests;

public sealed class OperationModelPersistenceTests(AuthApiDatabaseFixture fixture)
    : IClassFixture<AuthApiDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Idempotency_dry_run_targets_partial_completion_and_cancellation_are_durable()
    {
        var connectionString = await fixture.CreateMigratedSchemaAsync();
        var options = new DbContextOptionsBuilder<ArrControlDbContext>().UseNpgsql(connectionString).Options;
        var user = new UserEntity
        {
            Email = $"operation-{Guid.NewGuid():N}@example.invalid",
            NormalizedEmail = $"OPERATION-{Guid.NewGuid():N}@EXAMPLE.INVALID",
            Locale = "en", TimeZone = "UTC", State = "active", CreatedAt = Now, UpdatedAt = Now,
        };
        var first = Instance("First");
        var second = Instance("Second");
        await using (var seed = new ArrControlDbContext(options))
        {
            seed.AddRange(user, first, second);
            await seed.SaveChangesAsync();
        }
        var actor = new RbacActorContext(user.Id, Guid.CreateVersion7(), user.Email,
            new AuthenticationRequestContext("operation-test", IPAddress.Loopback));
        var command = new CreateOperationCommand(
            actor, "fixture", "/fixture", "same-key", new string('a', 64), true,
            [new(first.Id, "target-1"), new(second.Id, "target-2")]);
        Guid operationId;
        await using (var context = new ArrControlDbContext(options))
        {
            var store = new EfOperationStore(context, new FixedTimeProvider(Now));
            var created = await store.CreateAsync(command, CancellationToken.None);
            Assert.Equal(CreateOperationStatus.Created, created.Status);
            Assert.True(created.Operation!.DryRun);
            operationId = created.Operation.Id;
        }
        await using (var context = new ArrControlDbContext(options))
        {
            var store = new EfOperationStore(context, new FixedTimeProvider(Now));
            Assert.Equal(operationId,
                (await store.CreateAsync(command, CancellationToken.None)).Operation?.Id);
            Assert.Equal(CreateOperationStatus.IdempotencyConflict,
                (await store.CreateAsync(command with { RequestHash = new string('b', 64) }, CancellationToken.None)).Status);
            Assert.True(await store.TryStartAsync(operationId, CancellationToken.None));
            Assert.True(await store.CompleteTargetAsync(operationId, first.Id, "target-1", true, null,
                "{\"dryRun\":true}", CancellationToken.None));
            Assert.True(await store.CompleteTargetAsync(operationId, second.Id, "target-2", false,
                "provider_failed", null, CancellationToken.None));
            Assert.True(await store.CompleteAsync(operationId, CancellationToken.None));
            Assert.Equal(OperationStates.Partial,
                (await store.GetAsync(user.Id, operationId, CancellationToken.None))?.State);
        }

        var cancelCommand = command with { IdempotencyKey = "cancel-key", RequestHash = new string('c', 64) };
        await using (var context = new ArrControlDbContext(options))
        {
            var store = new EfOperationStore(context, new FixedTimeProvider(Now.AddMinutes(1)));
            var pending = await store.CreateAsync(cancelCommand, CancellationToken.None);
            Assert.True(await store.RequestCancellationAsync(actor, pending.Operation!.Id, CancellationToken.None));
            var cancelled = await store.GetAsync(user.Id, pending.Operation.Id, CancellationToken.None);
            Assert.Equal(OperationStates.Cancelled, cancelled?.State);
            Assert.All(cancelled!.Targets, value => Assert.Equal(OperationTargetStates.Cancelled, value.State));
        }

        await using (var context = new ArrControlDbContext(options))
        {
            var store = new EfOperationStore(context, new FixedTimeProvider(Now.AddHours(25)));
            var reused = await store.CreateAsync(command, CancellationToken.None);
            Assert.Equal(CreateOperationStatus.Created, reused.Status);
            Assert.NotEqual(operationId, reused.Operation?.Id);
            Assert.NotNull(await store.GetAsync(user.Id, operationId, CancellationToken.None));
        }

        await using var verification = new ArrControlDbContext(options);
        var audits = await verification.Set<AuditEventEntity>()
            .Where(value => value.ActorUserId == user.Id && value.Action.StartsWith("operation."))
            .ToListAsync();
        Assert.NotEmpty(audits);
        Assert.All(audits, value =>
        {
            Assert.DoesNotContain("same-key", value.SummaryJson, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('a', 64), value.SummaryJson, StringComparison.Ordinal);
        });
    }

    private static InstanceEntity Instance(string prefix) => new()
    {
        Name = $"{prefix} {Guid.NewGuid():N}", Kind = "sonarr",
        BaseUrl = "https://operation.example.invalid/", Enabled = true,
        TlsVerificationEnabled = true, CreatedAt = Now, UpdatedAt = Now,
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
