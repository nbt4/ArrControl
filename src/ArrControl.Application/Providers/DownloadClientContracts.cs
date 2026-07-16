using ArrControl.Application.Activity;

namespace ArrControl.Application.Providers;

public interface IProviderCredentialContract
{
    IReadOnlyList<string> RequiredCredentialPurposes { get; }
}

public interface IProviderDownloadClient : IArrProviderClient, IProviderActivityClient
{
    bool SupportsRetry { get; }

    Task<ProviderCallResult<bool>> SetPausedAsync(
        ProviderConnection connection, string providerKey, bool paused, CancellationToken cancellationToken);

    Task<ProviderCallResult<bool>> RemoveAsync(
        ProviderConnection connection, string providerKey, bool deleteData, CancellationToken cancellationToken);

    Task<ProviderCallResult<bool>> RetryAsync(
        ProviderConnection connection, string providerKey, CancellationToken cancellationToken);
}
