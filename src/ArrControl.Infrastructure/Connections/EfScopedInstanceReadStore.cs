using ArrControl.Application.Connections;
using ArrControl.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Connections;

public sealed class EfScopedInstanceReadStore(ArrControlDbContext dbContext)
    : IScopedInstanceReadStore
{
    public async Task<IReadOnlyList<VisibleInstance>> ListAsync(
        bool includeAll,
        IReadOnlyCollection<Guid> instanceGroupIds,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Instances.AsNoTracking();
        if (!includeAll)
        {
            query = query.Where(instance =>
                instance.GroupId != null
                && instanceGroupIds.Contains(instance.GroupId.Value));
        }

        return await query
            .OrderBy(instance => instance.Name)
            .ThenBy(instance => instance.Id)
            .Select(instance => new VisibleInstance(
                instance.Id,
                instance.Name,
                instance.Kind,
                instance.BaseUrl,
                instance.Enabled,
                instance.GroupId,
                instance.TlsVerificationEnabled,
                instance.AllowPrivateNetworkAccess,
                instance.Credentials.Any(),
                instance.CreatedAt,
                instance.UpdatedAt))
            .ToListAsync(cancellationToken);
    }
}
