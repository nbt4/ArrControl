using ArrControl.Application.Catalog;
using ArrControl.Application.Search;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Catalog;
using ArrControl.Infrastructure.Persistence.Connections;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Search;

public sealed class EfSearchTargetStore(ArrControlDbContext dbContext) : ISearchTargetStore
{
    public async Task<IReadOnlyList<SearchResolvedTarget>> ResolveAsync(
        bool includeAll,
        IReadOnlyCollection<Guid> visibleGroupIds,
        SearchScopeRequest scope,
        CancellationToken cancellationToken)
    {
        var query =
            from missing in dbContext.Set<MissingItemEntity>().AsNoTracking()
            join media in dbContext.Set<MediaEntityEntity>().AsNoTracking()
                on new { missing.InstanceId, missing.ProviderKey }
                equals new { media.InstanceId, media.ProviderKey }
            join provider in dbContext.Set<ProviderItemEntity>().AsNoTracking()
                on new { missing.InstanceId, missing.ProviderKey }
                equals new { provider.InstanceId, provider.ProviderKey }
            join instance in dbContext.Set<InstanceEntity>().AsNoTracking()
                on missing.InstanceId equals instance.Id
            where missing.Reason == MissingReasons.Missing && instance.Enabled
                && (instance.Kind == "sonarr" || instance.Kind == "radarr"
                    || instance.Kind == "lidarr" || instance.Kind == "readarr"
                    || instance.Kind == "whisparr")
            select new
            {
                MediaId = media.Id,
                InstanceId = instance.Id,
                instance.GroupId,
                provider.ProviderKind,
                provider.ProviderKey,
            };
        if (!includeAll)
            query = query.Where(value => value.GroupId != null && visibleGroupIds.Contains(value.GroupId.Value));
        query = scope.Mode switch
        {
            SearchScopeModes.Selected => query.Where(value => scope.MediaEntityIds.Contains(value.MediaId)),
            SearchScopeModes.Instance => query.Where(value => scope.InstanceIds.Contains(value.InstanceId)),
            SearchScopeModes.Group => query.Where(value => value.GroupId != null
                && scope.InstanceGroupIds.Contains(value.GroupId.Value)),
            _ => query,
        };
        return await query.OrderBy(value => value.InstanceId).ThenBy(value => value.ProviderKey)
            .Select(value => new SearchResolvedTarget(value.InstanceId, value.ProviderKind, value.ProviderKey))
            .ToListAsync(cancellationToken);
    }
}
