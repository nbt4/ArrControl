using ArrControl.Application.Authorization;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class RbacAuthorizationServiceTests
{
    [Fact]
    public void Catalog_contains_the_exact_permissions_and_system_role_matrix()
    {
        var expectedPermissions = new[]
        {
            "instances.read",
            "instances.manage",
            "library.read",
            "search.execute",
            "queue.manage",
            "tasks.execute",
            "users.manage",
            "audit.read",
            "settings.manage",
            "authorization.manage",
        };
        Assert.Equal(
            expectedPermissions.Order(StringComparer.Ordinal),
            RbacPermissions.All.Order(StringComparer.Ordinal));
        Assert.True(RbacPermissions.IsKnown(RbacPermissions.InstancesRead));
        Assert.False(RbacPermissions.IsKnown("Instances.Read"));
        Assert.False(RbacPermissions.IsKnown(" instances.read"));

        var administrator = Assert.Single(
            RbacSystemRoles.All,
            role => role.NormalizedName == RbacSystemRoles.AdministratorNormalized);
        var @operator = Assert.Single(
            RbacSystemRoles.All,
            role => role.NormalizedName == RbacSystemRoles.OperatorNormalized);
        var viewer = Assert.Single(
            RbacSystemRoles.All,
            role => role.NormalizedName == RbacSystemRoles.ViewerNormalized);

        Assert.Equal(
            RbacPermissions.All.Order(StringComparer.Ordinal),
            administrator.Permissions.Order(StringComparer.Ordinal));
        Assert.Equal(
            new[]
            {
                RbacPermissions.InstancesRead,
                RbacPermissions.LibraryRead,
                RbacPermissions.QueueManage,
                RbacPermissions.SearchExecute,
                RbacPermissions.TasksExecute,
            }.Order(StringComparer.Ordinal),
            @operator.Permissions.Order(StringComparer.Ordinal));
        Assert.Equal(
            new[]
            {
                RbacPermissions.InstancesRead,
                RbacPermissions.LibraryRead,
            }.Order(StringComparer.Ordinal),
            viewer.Permissions.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Snapshot_is_fail_closed_deduplicated_and_global_dominates_group_grants()
    {
        var firstGroup = Guid.CreateVersion7();
        var secondGroup = Guid.CreateVersion7();
        var snapshot = EffectiveAuthorization.Create(
        [
            new(RbacPermissions.InstancesRead, firstGroup),
            new(RbacPermissions.InstancesRead, null),
            new(RbacPermissions.InstancesRead, secondGroup),
            new(RbacPermissions.LibraryRead, firstGroup),
            new(RbacPermissions.LibraryRead, firstGroup),
            new(RbacPermissions.SearchExecute, Guid.Empty),
            new("Instances.Read", null),
            new("unknown.permission", null),
        ]);

        var instances = Assert.Single(
            snapshot.Grants,
            grant => grant.PermissionCode == RbacPermissions.InstancesRead);
        Assert.True(instances.IsGlobal);
        Assert.Empty(instances.InstanceGroupIds);
        var library = Assert.Single(
            snapshot.Grants,
            grant => grant.PermissionCode == RbacPermissions.LibraryRead);
        Assert.False(library.IsGlobal);
        Assert.Equal(firstGroup, Assert.Single(library.InstanceGroupIds));
        Assert.Equal(2, snapshot.Grants.Count);

        Assert.True(snapshot.HasAnyScope(RbacPermissions.InstancesRead));
        Assert.True(snapshot.HasGlobal(RbacPermissions.InstancesRead));
        Assert.True(snapshot.HasInstanceGroup(RbacPermissions.InstancesRead, secondGroup));
        Assert.True(snapshot.HasInstanceGroup(RbacPermissions.InstancesRead, null));
        Assert.True(snapshot.HasInstanceGroup(RbacPermissions.LibraryRead, firstGroup));
        Assert.False(snapshot.HasInstanceGroup(RbacPermissions.LibraryRead, secondGroup));
        Assert.False(snapshot.HasInstanceGroup(RbacPermissions.LibraryRead, null));
        Assert.False(snapshot.HasAnyScope(RbacPermissions.SearchExecute));
        Assert.False(snapshot.HasAnyScope("Instances.Read"));
        Assert.False(snapshot.HasAnyScope("unknown.permission"));
    }

    [Fact]
    public async Task Service_passes_actor_identity_and_caches_one_snapshot_per_actor()
    {
        var userId = Guid.CreateVersion7();
        var sessionId = Guid.CreateVersion7();
        var instanceGroupId = Guid.CreateVersion7();
        var store = new FakeStore(
        [
            new(RbacPermissions.UsersManage, null),
            new(RbacPermissions.InstancesRead, instanceGroupId),
        ]);
        var service = new RbacAuthorizationService(store);

        var snapshot = await service.GetSnapshotAsync(userId, sessionId, CancellationToken.None);
        Assert.True(await service.HasAnyScopeAsync(
            userId,
            sessionId,
            RbacPermissions.InstancesRead,
            CancellationToken.None));
        Assert.True(await service.HasGlobalAsync(
            userId,
            sessionId,
            RbacPermissions.UsersManage,
            CancellationToken.None));
        Assert.True(await service.HasInstanceGroupAsync(
            userId,
            sessionId,
            RbacPermissions.InstancesRead,
            instanceGroupId,
            CancellationToken.None));
        Assert.False(await service.HasInstanceGroupAsync(
            userId,
            sessionId,
            RbacPermissions.InstancesRead,
            null,
            CancellationToken.None));

        Assert.Same(snapshot, await service.GetSnapshotAsync(
            userId,
            sessionId,
            CancellationToken.None));
        Assert.Equal(1, store.CallCount);
        Assert.Equal(userId, store.LastUserId);
        Assert.Equal(sessionId, store.LastSessionId);
    }

    [Fact]
    public async Task Service_separates_actor_caches_and_empty_actor_ids_fail_closed()
    {
        var store = new FakeStore([new(RbacPermissions.InstancesRead, null)]);
        var service = new RbacAuthorizationService(store);
        var firstUser = Guid.CreateVersion7();
        var secondUser = Guid.CreateVersion7();

        Assert.False(await service.HasAnyScopeAsync(
            Guid.Empty,
            Guid.CreateVersion7(),
            RbacPermissions.InstancesRead,
            CancellationToken.None));
        Assert.False(await service.HasAnyScopeAsync(
            firstUser,
            Guid.Empty,
            RbacPermissions.InstancesRead,
            CancellationToken.None));
        Assert.Equal(0, store.CallCount);

        Assert.True(await service.HasAnyScopeAsync(
            firstUser,
            Guid.CreateVersion7(),
            RbacPermissions.InstancesRead,
            CancellationToken.None));
        Assert.True(await service.HasAnyScopeAsync(
            secondUser,
            Guid.CreateVersion7(),
            RbacPermissions.InstancesRead,
            CancellationToken.None));
        Assert.Equal(2, store.CallCount);
    }

    private sealed class FakeStore(IReadOnlyCollection<StoredPermissionGrant> grants)
        : IRbacGrantStore
    {
        public int CallCount { get; private set; }

        public Guid LastUserId { get; private set; }

        public Guid LastSessionId { get; private set; }

        public Task<IReadOnlyCollection<StoredPermissionGrant>> GetGrantsAsync(
            Guid userId,
            Guid sessionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastUserId = userId;
            LastSessionId = sessionId;
            return Task.FromResult(grants);
        }
    }
}
