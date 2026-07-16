using ArrControl.Application.Automation;
using ArrControl.Application.Catalog;
using ArrControl.Application.Providers;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class CatalogSyncJobHandlerTests
{
    private static readonly Guid InstanceId = Guid.Parse("01981362-7c00-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Successful_sync_applies_snapshot_and_returns_non_content_checkpoint()
    {
        var snapshot = new ProviderCatalogSnapshot(Now, []);
        var store = new StubStore();
        var handler = new CatalogSyncJobHandler(
            new StubResolver(Target()),
            store,
            [new StubClient(ProviderCallResult<ProviderCatalogSnapshot>.Succeeded(snapshot, null, 200))]);

        var result = await handler.ExecuteAsync(Job(), CancellationToken.None);

        Assert.Same(snapshot, store.Applied);
        var checkpoint = Assert.Single(result.Checkpoints);
        Assert.Equal(InstanceId, checkpoint.InstanceId);
        Assert.Equal(CatalogJobTypes.CheckpointStream, checkpoint.Stream);
        Assert.Contains("snapshot-diff", checkpoint.Cursor, StringComparison.Ordinal);
        Assert.DoesNotContain("api-key", checkpoint.Cursor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_failure_is_stable_and_never_applies_a_partial_snapshot()
    {
        var store = new StubStore();
        var handler = new CatalogSyncJobHandler(
            new StubResolver(Target()),
            store,
            [new StubClient(ProviderCallResult<ProviderCatalogSnapshot>.Failed(ProviderErrorCodes.RateLimited))]);

        var exception = await Assert.ThrowsAsync<ScheduledJobException>(() =>
            handler.ExecuteAsync(Job(), CancellationToken.None));

        Assert.Equal("catalog_rate_limited", exception.Code);
        Assert.Null(store.Applied);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"instanceId\":\"not-a-guid\"}")]
    [InlineData("{\"instanceId\":\"01981362-7c00-7000-8000-000000000001\",\"extra\":true}")]
    public async Task Scope_is_strictly_validated(string scopeJson)
    {
        var handler = new CatalogSyncJobHandler(
            new StubResolver(Target()),
            new StubStore(),
            [new StubClient(ProviderCallResult<ProviderCatalogSnapshot>.Failed("unused"))]);

        var exception = await Assert.ThrowsAsync<ScheduledJobException>(() =>
            handler.ExecuteAsync(Job() with { ScopeJson = scopeJson }, CancellationToken.None));

        Assert.Equal("catalog_scope_invalid", exception.Code);
    }

    private static ClaimedJob Job() =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            CatalogJobTypes.Sync,
            CatalogJobScope.Create(InstanceId),
            1,
            Guid.CreateVersion7(),
            Now.AddMinutes(1));

    private static CatalogSyncTarget Target() =>
        new(
            InstanceId,
            "sonarr",
            new ProviderConnection(
                InstanceId,
                new Uri("https://sonarr.example.invalid/"),
                true,
                false,
                "fixture-api-key"));

    private sealed class StubResolver(CatalogSyncTarget? target) : ICatalogSyncTargetResolver
    {
        public Task<CatalogSyncTarget?> ResolveAsync(Guid instanceId, CancellationToken cancellationToken) =>
            Task.FromResult(target);
    }

    private sealed class StubStore : ICatalogSnapshotStore
    {
        public ProviderCatalogSnapshot? Applied { get; private set; }

        public Task<CatalogApplyResult> ApplyAsync(
            Guid instanceId,
            string providerKind,
            ProviderCatalogSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            Applied = snapshot;
            return Task.FromResult(new CatalogApplyResult(0, 0, 0, 0, new string('a', 64)));
        }
    }

    private sealed class StubClient(ProviderCallResult<ProviderCatalogSnapshot> result)
        : IProviderCatalogClient
    {
        public string Kind => "sonarr";

        public Task<ProviderCallResult<ProviderCatalogSnapshot>> GetCatalogAsync(
            ProviderConnection connection,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
