namespace ArrControl.Application.Providers;

public sealed record ProviderIndexer(
    int Id,
    string Name,
    bool Enabled,
    bool SupportsRss,
    bool SupportsSearch,
    string Protocol,
    int Priority,
    DateTimeOffset? DisabledUntil);

public interface IProviderIndexerClient
{
    string Kind { get; }

    Task<ProviderCallResult<IReadOnlyList<ProviderIndexer>>> GetIndexersAsync(
        ProviderConnection connection,
        CancellationToken cancellationToken);
}

public sealed record ProviderSubtitleActivityDay(
    DateOnly Date,
    int SeriesCount,
    int MovieCount);

public sealed record ProviderSubtitleActivitySnapshot(
    DateTimeOffset ObservedAt,
    IReadOnlyList<ProviderSubtitleActivityDay> Days);

public interface IProviderSubtitleActivityClient
{
    string Kind { get; }

    Task<ProviderCallResult<ProviderSubtitleActivitySnapshot>> GetSubtitleActivityAsync(
        ProviderConnection connection,
        CancellationToken cancellationToken);
}
