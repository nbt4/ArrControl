using ArrControl.Application.Authorization;
using ArrControl.Application.Catalog;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class MissingQueryServiceTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid SessionId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Query_normalizes_filters_limits_page_and_binds_cursor_to_filter()
    {
        var store = new StubQueryStore([Item("Alpha"), Item("Beta")]);
        var codec = new StubCursorCodec();
        var service = Service(store, codec);

        var result = await service.QueryAsync(
            UserId,
            SessionId,
            new MissingFilter([], [" EPISODE "], ["MISSING"], "  alpha "),
            null,
            null,
            1,
            CancellationToken.None);

        Assert.Equal(MissingQueryStatus.Success, result.Status);
        Assert.Equal("Alpha", Assert.Single(result.Page!.Items).Title);
        Assert.NotNull(result.Page.NextCursor);
        Assert.Equal("alpha", store.LastSpec!.Filter.Search);
        Assert.Equal(2, store.LastSpec.Limit);

        var changedFilterResult = await service.QueryAsync(
            UserId,
            SessionId,
            new MissingFilter([], [CatalogItemKinds.Movie], [MissingReasons.Missing], null),
            null,
            result.Page.NextCursor,
            1,
            CancellationToken.None);
        Assert.Equal(MissingQueryStatus.Invalid, changedFilterResult.Status);
        Assert.Equal("missing_cursor_invalid", changedFilterResult.ErrorCode);
    }

    [Fact]
    public async Task Missing_library_grant_is_forbidden_before_querying_storage()
    {
        var store = new StubQueryStore([]);
        var service = new MissingQueryService(
            new RbacAuthorizationService(new StubGrantStore([])),
            store,
            new StubSavedViews(),
            new StubCursorCodec(),
            new FixedTimeProvider(Now));

        var result = await service.QueryAsync(
            UserId,
            SessionId,
            MissingFilter.Empty,
            null,
            null,
            50,
            CancellationToken.None);

        Assert.Equal(MissingQueryStatus.Forbidden, result.Status);
        Assert.Null(store.LastSpec);
    }

    private static MissingQueryService Service(StubQueryStore store, StubCursorCodec codec) =>
        new(
            new RbacAuthorizationService(new StubGrantStore(
                [new StoredPermissionGrant(RbacPermissions.LibraryRead, null)])),
            store,
            new StubSavedViews(),
            codec,
            new FixedTimeProvider(Now));

    private static MissingItem Item(string title) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "Fixture",
            "sonarr",
            $"episode:{title}",
            CatalogItemKinds.Episode,
            title,
            2026,
            1,
            1,
            MissingReasons.Missing,
            Now,
            Now,
            new Dictionary<string, string>(),
            Now,
            Now,
            Now,
            false);

    private sealed class StubGrantStore(IReadOnlyCollection<StoredPermissionGrant> grants)
        : IRbacGrantStore
    {
        public Task<IReadOnlyCollection<StoredPermissionGrant>> GetGrantsAsync(
            Guid userId,
            Guid sessionId,
            CancellationToken cancellationToken) => Task.FromResult(grants);
    }

    private sealed class StubQueryStore(IReadOnlyList<MissingItem> rows) : IMissingQueryStore
    {
        public MissingQuerySpec? LastSpec { get; private set; }

        public Task<MissingStoreResult> QueryAsync(
            MissingQuerySpec query,
            CancellationToken cancellationToken)
        {
            LastSpec = query;
            return Task.FromResult(new MissingStoreResult(rows, []));
        }
    }

    private sealed class StubCursorCodec : IMissingCursorCodec
    {
        private readonly Dictionary<string, MissingCursor> values = [];

        public string Encode(MissingCursor cursor)
        {
            var token = $"cursor-{values.Count + 1}";
            values[token] = cursor;
            return token;
        }

        public MissingCursor? Decode(string value) => values.GetValueOrDefault(value);
    }

    private sealed class StubSavedViews : IMissingSavedViewStore
    {
        public Task<IReadOnlyList<MissingSavedView>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MissingSavedView>>([]);

        public Task<MissingSavedView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<MissingSavedView?>(null);

        public Task<MissingSavedViewWriteResult> UpsertAsync(
            RbacActorContext actor,
            Guid id,
            string name,
            MissingFilter filter,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(RbacActorContext actor, Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
