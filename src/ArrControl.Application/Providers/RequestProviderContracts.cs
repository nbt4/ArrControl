namespace ArrControl.Application.Providers;

public sealed record ProviderMediaRequest(
    string ProviderKey,
    string MediaType,
    string Status,
    DateTimeOffset RequestedAt,
    string? TmdbId,
    string? TvdbId,
    string? ImdbId);

public sealed record ProviderRequestSnapshot(
    DateTimeOffset ObservedAt,
    IReadOnlyList<ProviderMediaRequest> Requests);

public interface IProviderRequestClient : IArrProviderClient
{
    Task<ProviderCallResult<ProviderRequestSnapshot>> GetRequestsAsync(
        ProviderConnection connection,
        CancellationToken cancellationToken);
}
