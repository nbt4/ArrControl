using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArrControl.Application.Authorization;
using ArrControl.Application.Operations;
using ArrControl.Application.Providers;

namespace ArrControl.Application.Search;

public static class SearchScopeModes
{
    public const string Selected = "selected";
    public const string Instance = "instance";
    public const string Group = "group";
    public const string All = "all";
}

public sealed record SearchScopeRequest(
    string Mode,
    IReadOnlyList<Guid> MediaEntityIds,
    IReadOnlyList<Guid> InstanceIds,
    IReadOnlyList<Guid> InstanceGroupIds);

public sealed record SearchResolvedTarget(Guid InstanceId, string ProviderKind, string ProviderKey);
public sealed record SearchPreviewInstance(Guid InstanceId, string ProviderKind, int TargetCount);
public sealed record SearchScopePreview(
    string Mode,
    int TargetCount,
    int ExcludedCount,
    IReadOnlyList<SearchPreviewInstance> Instances,
    IReadOnlyList<SearchResolvedTarget> Targets);

public interface ISearchTargetStore
{
    Task<IReadOnlyList<SearchResolvedTarget>> ResolveAsync(
        bool includeAll,
        IReadOnlyCollection<Guid> visibleGroupIds,
        SearchScopeRequest scope,
        CancellationToken cancellationToken);
}

public sealed record ProviderSearchResult(string CommandId);

public interface IProviderSearchClient
{
    string Kind { get; }
    Task<ProviderCallResult<ProviderSearchResult>> SearchAsync(
        ProviderConnection connection,
        IReadOnlyList<string> providerKeys,
        CancellationToken cancellationToken);
}

public sealed class SearchService(
    RbacAuthorizationService authorization,
    ISearchTargetStore targets,
    OperationService operations)
{
    public async Task<SearchScopePreview?> PreviewAsync(
        Guid userId,
        Guid sessionId,
        SearchScopeRequest requested,
        CancellationToken cancellationToken)
    {
        var scope = Normalize(requested);
        Validate(scope);
        var snapshot = await authorization.GetSnapshotAsync(userId, sessionId, cancellationToken);
        var grant = snapshot.Grants.SingleOrDefault(value =>
            value.PermissionCode == RbacPermissions.SearchExecute);
        if (grant is null) return null;
        var resolved = await targets.ResolveAsync(grant.IsGlobal, grant.InstanceGroupIds, scope, cancellationToken);
        var requestedCount = scope.Mode switch
        {
            SearchScopeModes.Selected => scope.MediaEntityIds.Count,
            SearchScopeModes.Instance => scope.InstanceIds.Count,
            SearchScopeModes.Group => scope.InstanceGroupIds.Count,
            _ => resolved.Count,
        };
        var matchedScopeCount = scope.Mode == SearchScopeModes.Selected
            ? resolved.Count
            : scope.Mode == SearchScopeModes.All ? resolved.Count
            : resolved.Select(value => value.InstanceId).Distinct().Count();
        return new SearchScopePreview(
            scope.Mode,
            resolved.Count,
            Math.Max(0, requestedCount - matchedScopeCount),
            resolved.GroupBy(value => new { value.InstanceId, value.ProviderKind })
                .Select(group => new SearchPreviewInstance(group.Key.InstanceId, group.Key.ProviderKind, group.Count()))
                .OrderBy(value => value.InstanceId).ToArray(),
            resolved);
    }

    public async Task<CreateOperationResult?> StartAsync(
        RbacActorContext actor,
        SearchScopeRequest scope,
        bool dryRun,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var preview = await PreviewAsync(actor.UserId, actor.SessionId, scope, cancellationToken);
        if (preview is null) return null;
        if (preview.TargetCount == 0)
            return new CreateOperationResult(CreateOperationStatus.Invalid);
        var canonical = JsonSerializer.Serialize(new
        {
            preview.Mode,
            dryRun,
            Targets = preview.Targets.OrderBy(value => value.InstanceId).ThenBy(value => value.ProviderKey),
        });
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return await operations.CreateAsync(new CreateOperationCommand(
            actor, "search", "/api/v1/operations/search", idempotencyKey, hash, dryRun,
            preview.Targets.Select(value => new OperationTargetInput(value.InstanceId, value.ProviderKey)).ToArray()),
            cancellationToken);
    }

    private static SearchScopeRequest Normalize(SearchScopeRequest value) => new(
        value.Mode?.Trim().ToLowerInvariant() ?? string.Empty,
        value.MediaEntityIds.Where(id => id != Guid.Empty).Distinct().Order().ToArray(),
        value.InstanceIds.Where(id => id != Guid.Empty).Distinct().Order().ToArray(),
        value.InstanceGroupIds.Where(id => id != Guid.Empty).Distinct().Order().ToArray());

    private static void Validate(SearchScopeRequest value)
    {
        var valid = value.Mode switch
        {
            SearchScopeModes.Selected => value.MediaEntityIds.Count is >= 1 and <= 10_000
                && value.InstanceIds.Count == 0 && value.InstanceGroupIds.Count == 0,
            SearchScopeModes.Instance => value.InstanceIds.Count is >= 1 and <= 100
                && value.MediaEntityIds.Count == 0 && value.InstanceGroupIds.Count == 0,
            SearchScopeModes.Group => value.InstanceGroupIds.Count is >= 1 and <= 100
                && value.MediaEntityIds.Count == 0 && value.InstanceIds.Count == 0,
            SearchScopeModes.All => value.MediaEntityIds.Count == 0 && value.InstanceIds.Count == 0
                && value.InstanceGroupIds.Count == 0,
            _ => false,
        };
        if (!valid) throw new ArgumentException("Search scope is invalid.", nameof(value));
    }
}
