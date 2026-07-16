using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArrControl.Application.Authorization;

namespace ArrControl.Application.Catalog;

public static class MissingReasons
{
    public const string Missing = "missing";
    public const string NotAvailable = "not_available";

    public static IReadOnlyList<string> All { get; } = [Missing, NotAvailable];
}

public sealed record MissingFilter(
    IReadOnlyList<Guid> InstanceIds,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> Reasons,
    string? Search)
{
    public static MissingFilter Empty { get; } = new([], [], [MissingReasons.Missing], null);

    public MissingFilter Normalize() => new(
        InstanceIds.Where(value => value != Guid.Empty).Distinct().Order().ToArray(),
        Kinds.Select(value => value.Trim().ToLowerInvariant()).Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        Reasons.Select(value => value.Trim().ToLowerInvariant()).Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        string.IsNullOrWhiteSpace(Search) ? null : Search.Trim());

    public string Fingerprint() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(Normalize()))));
}

public sealed record MissingCursor(string FilterFingerprint, string SortTitle, Guid MediaEntityId);

public interface IMissingCursorCodec
{
    string Encode(MissingCursor cursor);

    MissingCursor? Decode(string value);
}

public sealed record MissingQuerySpec(
    bool IncludeAllInstances,
    IReadOnlyCollection<Guid> InstanceGroupIds,
    MissingFilter Filter,
    MissingCursor? Cursor,
    int Limit,
    DateTimeOffset Now,
    TimeSpan StaleAfter);

public sealed record MissingItem(
    Guid Id,
    Guid InstanceId,
    string InstanceName,
    string ProviderKind,
    string ProviderKey,
    string Kind,
    string Title,
    int? Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    string Reason,
    DateTimeOffset? AvailableAt,
    DateTimeOffset? SourceAddedAt,
    IReadOnlyDictionary<string, string> ExternalIds,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ObservedAt,
    bool Stale);

public sealed record MissingFreshness(
    Guid InstanceId,
    DateTimeOffset? ObservedAt,
    bool Stale);

public sealed record MissingPage(
    IReadOnlyList<MissingItem> Items,
    string? NextCursor,
    IReadOnlyList<MissingFreshness> Freshness);

public sealed record MissingStoreResult(
    IReadOnlyList<MissingItem> Items,
    IReadOnlyList<MissingFreshness> Freshness);

public interface IMissingQueryStore
{
    Task<MissingStoreResult> QueryAsync(
        MissingQuerySpec query,
        CancellationToken cancellationToken);
}

public enum MissingQueryStatus
{
    Success,
    Forbidden,
    Invalid,
    SavedViewNotFound,
}

public sealed record MissingQueryResult(
    MissingQueryStatus Status,
    MissingPage? Page = null,
    string? ErrorCode = null);

public sealed class MissingQueryService(
    RbacAuthorizationService authorization,
    IMissingQueryStore store,
    IMissingSavedViewStore savedViews,
    IMissingCursorCodec cursors,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    public async Task<MissingQueryResult> QueryAsync(
        Guid userId,
        Guid sessionId,
        MissingFilter requestedFilter,
        Guid? savedViewId,
        string? cursorValue,
        int limit,
        CancellationToken cancellationToken)
    {
        var snapshot = await authorization.GetSnapshotAsync(userId, sessionId, cancellationToken);
        var grant = snapshot.Grants.SingleOrDefault(value =>
            value.PermissionCode == RbacPermissions.LibraryRead);
        if (grant is null)
        {
            return new MissingQueryResult(MissingQueryStatus.Forbidden);
        }

        var filter = requestedFilter.Normalize();
        if (savedViewId is not null)
        {
            var view = await savedViews.GetAsync(userId, savedViewId.Value, cancellationToken);
            if (view is null)
            {
                return new MissingQueryResult(MissingQueryStatus.SavedViewNotFound);
            }

            filter = view.Filter.Normalize();
        }

        if (!Valid(filter, limit))
        {
            return new MissingQueryResult(MissingQueryStatus.Invalid, ErrorCode: "missing_filter_invalid");
        }

        MissingCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(cursorValue))
        {
            cursor = cursors.Decode(cursorValue);
            if (cursor is null
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(cursor.FilterFingerprint),
                    Encoding.ASCII.GetBytes(filter.Fingerprint())))
            {
                return new MissingQueryResult(MissingQueryStatus.Invalid, ErrorCode: "missing_cursor_invalid");
            }
        }

        var stored = await store.QueryAsync(
            new MissingQuerySpec(
                grant.IsGlobal,
                grant.InstanceGroupIds,
                filter,
                cursor,
                limit + 1,
                timeProvider.GetUtcNow(),
                StaleAfter),
            cancellationToken);
        var hasMore = stored.Items.Count > limit;
        var items = stored.Items.Take(limit).ToArray();
        var nextCursor = hasMore
            ? cursors.Encode(new MissingCursor(
                filter.Fingerprint(),
                items[^1].Title.ToLowerInvariant(),
                items[^1].Id))
            : null;
        return new MissingQueryResult(
            MissingQueryStatus.Success,
            new MissingPage(items, nextCursor, stored.Freshness));
    }

    private static bool Valid(MissingFilter filter, int limit) =>
        limit is >= 1 and <= 200
        && filter.InstanceIds.Count <= 100
        && filter.Kinds.Count <= 4
        && filter.Kinds.All(CatalogItemKinds.Searchable.Contains)
        && filter.Reasons.Count is >= 1 and <= 2
        && filter.Reasons.All(value => MissingReasons.All.Contains(value, StringComparer.Ordinal))
        && (filter.Search is null || filter.Search.Length <= 200);
}

public sealed record MissingSavedView(
    Guid Id,
    string Name,
    MissingFilter Filter,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface IMissingSavedViewStore
{
    Task<IReadOnlyList<MissingSavedView>> ListAsync(Guid userId, CancellationToken cancellationToken);

    Task<MissingSavedView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    Task<MissingSavedViewWriteResult> UpsertAsync(
        RbacActorContext actor,
        Guid id,
        string name,
        MissingFilter filter,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(
        RbacActorContext actor,
        Guid id,
        CancellationToken cancellationToken);
}

public enum MissingSavedViewWriteStatus
{
    Created,
    Updated,
    Invalid,
    NameConflict,
}

public sealed record MissingSavedViewWriteResult(
    MissingSavedViewWriteStatus Status,
    MissingSavedView? View = null);

public sealed class MissingSavedViewService(IMissingSavedViewStore store)
{
    public Task<IReadOnlyList<MissingSavedView>> ListAsync(
        Guid userId,
        CancellationToken cancellationToken) => store.ListAsync(userId, cancellationToken);

    public async Task<MissingSavedViewWriteResult> UpsertAsync(
        RbacActorContext actor,
        Guid id,
        string name,
        MissingFilter filter,
        CancellationToken cancellationToken)
    {
        var normalized = filter.Normalize();
        if (id == Guid.Empty
            || string.IsNullOrWhiteSpace(name)
            || name.Trim().Length > 120
            || normalized.InstanceIds.Count > 100
            || normalized.Kinds.Any(value => !CatalogItemKinds.Searchable.Contains(value))
            || normalized.Reasons.Count is < 1 or > 2
            || normalized.Reasons.Any(value => !MissingReasons.All.Contains(value, StringComparer.Ordinal))
            || normalized.Search?.Length > 200)
        {
            return new MissingSavedViewWriteResult(MissingSavedViewWriteStatus.Invalid);
        }

        return await store.UpsertAsync(actor, id, name.Trim(), normalized, cancellationToken);
    }

    public Task<bool> DeleteAsync(
        RbacActorContext actor,
        Guid id,
        CancellationToken cancellationToken) => store.DeleteAsync(actor, id, cancellationToken);
}
