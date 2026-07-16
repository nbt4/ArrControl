using ArrControl.Application.Authorization;
using ArrControl.Application.Identity;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class RbacAdministrationServiceTests
{
    [Fact]
    public async Task Role_upsert_canonicalizes_input_and_rejects_unknown_or_duplicate_permissions()
    {
        var store = new FakeAdministrationStore();
        var service = new RbacAdministrationService(store);
        var roleId = Guid.CreateVersion7();

        await service.UpsertRoleAsync(
            Actor(),
            roleId,
            "  Night Operators  ",
            [RbacPermissions.LibraryRead, RbacPermissions.InstancesRead],
            CancellationToken.None);

        Assert.NotNull(store.LastRole);
        Assert.Equal(roleId, store.LastRole.Id);
        Assert.Equal("Night Operators", store.LastRole.Name);
        Assert.Equal("NIGHT OPERATORS", store.LastRole.NormalizedName);
        Assert.Equal(
            [RbacPermissions.InstancesRead, RbacPermissions.LibraryRead],
            store.LastRole.Permissions);
        await Assert.ThrowsAsync<RbacAdministrationValidationException>(() =>
            service.UpsertRoleAsync(
                Actor(),
                Guid.CreateVersion7(),
                "Invalid",
                ["unknown.permission"],
                CancellationToken.None));
        await Assert.ThrowsAsync<RbacAdministrationValidationException>(() =>
            service.UpsertRoleAsync(
                Actor(),
                Guid.CreateVersion7(),
                "Duplicate",
                [RbacPermissions.InstancesRead, RbacPermissions.InstancesRead],
                CancellationToken.None));
    }

    [Fact]
    public async Task Assignment_replacement_is_sorted_and_rejects_empty_ids_or_duplicates()
    {
        var store = new FakeAdministrationStore();
        var service = new RbacAdministrationService(store);
        var firstRole = Guid.CreateVersion7();
        var secondRole = Guid.CreateVersion7();
        var group = Guid.CreateVersion7();

        await service.ReplaceManualRoleAssignmentsAsync(
            Actor(),
            Guid.CreateVersion7(),
            [new(secondRole, group), new(firstRole, null)],
            CancellationToken.None);

        Assert.NotNull(store.LastAssignments);
        Assert.Equal(
            new[] { firstRole, secondRole }.Order().ToArray(),
            store.LastAssignments.Select(assignment => assignment.RoleId).ToArray());
        await Assert.ThrowsAsync<RbacAdministrationValidationException>(() =>
            service.ReplaceManualRoleAssignmentsAsync(
                Actor(),
                Guid.CreateVersion7(),
                [new(Guid.Empty, null)],
                CancellationToken.None));
        await Assert.ThrowsAsync<RbacAdministrationValidationException>(() =>
            service.ReplaceManualRoleAssignmentsAsync(
                Actor(),
                Guid.CreateVersion7(),
                [new(firstRole, group), new(firstRole, group)],
                CancellationToken.None));
    }

    private static RbacActorContext Actor() => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        "admin@example.invalid",
        new AuthenticationRequestContext("unit-rbac", null));

    private sealed class FakeAdministrationStore : IRbacAdministrationStore
    {
        public ValidatedAuthorizationRole? LastRole { get; private set; }

        public IReadOnlyList<RoleAssignmentInput>? LastAssignments { get; private set; }

        public Task<IReadOnlyList<AuthorizationRole>> ListRolesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AuthorizationRole>>([]);

        public Task<AuthorizationRole?> GetRoleAsync(
            Guid roleId,
            CancellationToken cancellationToken) =>
            Task.FromResult<AuthorizationRole?>(null);

        public Task<IReadOnlyList<AuthorizationUser>> ListUsersAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AuthorizationUser>>([]);

        public Task<IReadOnlyList<AuthorizationInstanceGroup>> ListInstanceGroupsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AuthorizationInstanceGroup>>([]);

        public Task<IReadOnlyList<RoleAssignment>?> GetManualRoleAssignmentsAsync(
            Guid userId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RoleAssignment>?>([]);

        public Task<UpsertAuthorizationRoleResult> UpsertRoleAsync(
            RbacActorContext actor,
            ValidatedAuthorizationRole role,
            CancellationToken cancellationToken)
        {
            LastRole = role;
            return Task.FromResult(new UpsertAuthorizationRoleResult(
                UpsertAuthorizationRoleStatus.Created,
                new AuthorizationRole(role.Id, role.Name, false, role.Permissions)));
        }

        public Task<DeleteAuthorizationRoleStatus> DeleteRoleAsync(
            RbacActorContext actor,
            Guid roleId,
            CancellationToken cancellationToken) =>
            Task.FromResult(DeleteAuthorizationRoleStatus.Deleted);

        public Task<ReplaceRoleAssignmentsResult> ReplaceManualRoleAssignmentsAsync(
            RbacActorContext actor,
            Guid userId,
            IReadOnlyList<RoleAssignmentInput> assignments,
            CancellationToken cancellationToken)
        {
            LastAssignments = assignments;
            return Task.FromResult(new ReplaceRoleAssignmentsResult(
                ReplaceRoleAssignmentsStatus.Updated,
                []));
        }
    }
}
