using System.Text.Json;
using ArrControl.Application.Audit;
using ArrControl.Application.Authorization;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class AuditQueryServiceTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid SessionId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Query_requires_global_audit_permission()
    {
        var store = new RecordingStore([]);
        var service = Service(
            [new StoredPermissionGrant(RbacPermissions.AuditRead, Guid.CreateVersion7())], store,
            new CursorCodec());

        var result = await service.QueryAsync(
            UserId, SessionId, new AuditFilter(null, null, null, null, null, null),
            null, 50, CancellationToken.None);

        Assert.Equal(AuditQueryStatus.Forbidden, result.Status);
        Assert.Null(store.Filter);
    }

    [Fact]
    public async Task Cursor_is_filter_bound_and_page_size_is_enforced()
    {
        var codec = new CursorCodec();
        var store = new RecordingStore([Event("one"), Event("two")]);
        var service = Service(
            [new StoredPermissionGrant(RbacPermissions.AuditRead, null)], store, codec);
        var first = await service.QueryAsync(
            UserId, SessionId, new AuditFilter(null, null, null, null, null, null),
            null, 1, CancellationToken.None);
        Assert.Equal(AuditQueryStatus.Success, first.Status);
        Assert.Single(first.Page!.Items);
        Assert.Equal("cursor", first.Page.NextCursor);
        Assert.Equal(Now.AddDays(-30), store.Filter?.From);
        Assert.Equal(2, store.FetchCount);

        var changed = await service.QueryAsync(
            UserId, SessionId, new AuditFilter(null, null, null, "changed", null, null),
            "cursor", 1, CancellationToken.None);
        Assert.Equal(AuditQueryStatus.Invalid, changed.Status);
        Assert.Equal("audit_cursor_invalid", changed.ErrorCode);
    }

    private static AuditQueryService Service(
        IReadOnlyCollection<StoredPermissionGrant> grants,
        IAuditQueryStore store,
        IAuditCursorCodec codec) => new(
        new RbacAuthorizationService(new GrantStore(grants)),
        store,
        codec,
        new FixedTimeProvider(Now));

    private static AuditEventDetails Event(string action) => new(
        Guid.CreateVersion7(), Now.AddMinutes(-1), null, "system", "system", action,
        Json("{}"), "correlation", "succeeded", Json("{}"), null);

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class GrantStore(IReadOnlyCollection<StoredPermissionGrant> grants) : IRbacGrantStore
    {
        public Task<IReadOnlyCollection<StoredPermissionGrant>> GetGrantsAsync(
            Guid userId, Guid sessionId, CancellationToken cancellationToken) => Task.FromResult(grants);
    }

    private sealed class RecordingStore(IReadOnlyList<AuditEventDetails> rows) : IAuditQueryStore
    {
        public NormalizedAuditFilter? Filter { get; private set; }
        public int FetchCount { get; private set; }

        public Task<IReadOnlyList<AuditEventDetails>> QueryAsync(
            NormalizedAuditFilter filter, AuditCursor? cursor, int fetchCount,
            CancellationToken cancellationToken)
        {
            Filter = filter;
            FetchCount = fetchCount;
            return Task.FromResult(rows);
        }
    }

    private sealed class CursorCodec : IAuditCursorCodec
    {
        private AuditCursor? cursor;
        public string Encode(AuditCursor value) { cursor = value; return "cursor"; }
        public AuditCursor? Decode(string value) => value == "cursor" ? cursor : null;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
