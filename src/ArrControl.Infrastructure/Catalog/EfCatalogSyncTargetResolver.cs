using ArrControl.Application.Automation;
using ArrControl.Application.Catalog;
using ArrControl.Application.Connections;
using ArrControl.Application.Providers;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Connections;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Catalog;

public sealed class EfCatalogSyncTargetResolver(
    ArrControlDbContext dbContext,
    ICredentialProtector protector) : ICatalogSyncTargetResolver
{
    public async Task<CatalogSyncTarget?> ResolveAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var instance = await dbContext.Set<InstanceEntity>()
            .AsNoTracking()
            .Where(value => value.Id == instanceId && value.Enabled)
            .Select(value => new
            {
                value.Id,
                value.Kind,
                value.BaseUrl,
                value.TlsVerificationEnabled,
                value.AllowPrivateNetworkAccess,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (instance is null)
        {
            return null;
        }

        var credentials = await dbContext.Set<CredentialEntity>()
            .AsNoTracking()
            .Where(value => value.InstanceId == instanceId)
            .Select(value => new StoredProtectedCredential(
                value.InstanceId,
                value.Purpose,
                value.Ciphertext,
                value.Nonce,
                value.Tag,
                value.KeyVersion))
            .ToListAsync(cancellationToken);
        if (credentials.Count == 0)
        {
            throw new ScheduledJobException("catalog_credential_missing");
        }

        try
        {
            var secrets = credentials.ToDictionary(
                value => value.Purpose,
                value => protector.Unprotect(value).Value,
                StringComparer.Ordinal);
            return new CatalogSyncTarget(
                instance.Id,
                instance.Kind,
                new ProviderConnection(
                    instance.Id,
                    new Uri(instance.BaseUrl, UriKind.Absolute),
                    instance.TlsVerificationEnabled,
                    instance.AllowPrivateNetworkAccess,
                    secrets));
        }
        catch (CredentialDecryptionException)
        {
            throw new ScheduledJobException("catalog_credential_invalid");
        }
    }
}
