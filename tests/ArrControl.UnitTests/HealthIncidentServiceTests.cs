using System.Net;
using ArrControl.Application.Authorization;
using ArrControl.Application.Health;
using ArrControl.Application.Identity;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class HealthIncidentServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Mutation_requires_matching_task_scope_and_enforces_snooze_bound()
    {
        var groupId = Guid.CreateVersion7();
        var store = new RecordingStore(new HealthIncidentScope(true, groupId));
        var service = new HealthIncidentService(
            new RbacAuthorizationService(new GrantStore(
                [new StoredPermissionGrant(RbacPermissions.TasksExecute, groupId)])),
            store,
            new FixedTimeProvider(Now));
        var actor = Actor();

        Assert.Equal(HealthIncidentMutationStatus.Updated,
            (await service.SetAcknowledgementAsync(actor, store.IncidentId, true, CancellationToken.None)).Status);
        Assert.Equal(1, store.MutationCount);
        Assert.Equal(HealthIncidentMutationStatus.Invalid,
            (await service.SetSnoozeAsync(actor, store.IncidentId, Now.AddDays(31), CancellationToken.None)).Status);
        Assert.Equal(1, store.MutationCount);

        var deniedStore = new RecordingStore(new HealthIncidentScope(true, Guid.CreateVersion7()));
        var denied = new HealthIncidentService(
            new RbacAuthorizationService(new GrantStore(
                [new StoredPermissionGrant(RbacPermissions.TasksExecute, groupId)])),
            deniedStore,
            new FixedTimeProvider(Now));
        Assert.Equal(HealthIncidentMutationStatus.Forbidden,
            (await denied.SetAcknowledgementAsync(actor, deniedStore.IncidentId, true, CancellationToken.None)).Status);
        Assert.Equal(0, deniedStore.MutationCount);
    }

    private static RbacActorContext Actor() => new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), "health@example.invalid",
        new AuthenticationRequestContext("health-unit", IPAddress.Loopback));

    private sealed class GrantStore(IReadOnlyCollection<StoredPermissionGrant> grants) : IRbacGrantStore
    {
        public Task<IReadOnlyCollection<StoredPermissionGrant>> GetGrantsAsync(
            Guid userId, Guid sessionId, CancellationToken cancellationToken) => Task.FromResult(grants);
    }

    private sealed class RecordingStore(HealthIncidentScope scope) : IHealthIncidentStore
    {
        public Guid IncidentId { get; } = Guid.CreateVersion7();
        public int MutationCount { get; private set; }

        public Task<IReadOnlyList<HealthIncidentDetails>> QueryAsync(
            bool includeAll, IReadOnlyCollection<Guid> instanceGroupIds,
            IReadOnlyCollection<Guid> instanceIds, bool includeResolved,
            DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HealthIncidentDetails>>([]);

        public Task<HealthIncidentScope> GetScopeAsync(Guid incidentId, CancellationToken cancellationToken) =>
            Task.FromResult(incidentId == IncidentId ? scope : new HealthIncidentScope(false, null));

        public Task<HealthIncidentDetails?> SetAcknowledgementAsync(
            RbacActorContext actor, Guid incidentId, bool acknowledged, CancellationToken cancellationToken)
        {
            MutationCount++;
            return Task.FromResult<HealthIncidentDetails?>(Details());
        }

        public Task<HealthIncidentDetails?> SetSnoozeAsync(
            RbacActorContext actor, Guid incidentId, DateTimeOffset? snoozedUntil,
            CancellationToken cancellationToken)
        {
            MutationCount++;
            return Task.FromResult<HealthIncidentDetails?>(Details());
        }

        private HealthIncidentDetails Details() => new(
            IncidentId, Guid.CreateVersion7(), "Instance", "sonarr", "warning", null,
            Now, Now, null, null, null, null, null, false, []);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
