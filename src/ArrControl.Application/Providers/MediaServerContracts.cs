namespace ArrControl.Application.Providers;

public sealed record ProviderMediaItemCounts(
    int Movies,
    int Series,
    int Episodes,
    int Songs,
    int Albums,
    int Books,
    int Total);

public sealed record ProviderMediaLibrarySummary(
    int? LibraryCount,
    int? MovieLibraries,
    int? SeriesLibraries,
    int? MusicLibraries,
    int? PhotoLibraries,
    int? OtherLibraries,
    ProviderMediaItemCounts? Items);

public sealed record ProviderPlaybackSummary(
    int Active,
    int Playing,
    int Paused,
    int Buffering,
    int Transcoding,
    int DirectStreaming,
    int DirectPlaying,
    int UnknownDelivery);

public sealed record ProviderMediaServerSnapshot(
    DateTimeOffset ObservedAt,
    ProviderMediaLibrarySummary Libraries,
    ProviderPlaybackSummary Playback);

public interface IProviderMediaServerClient : IArrProviderClient
{
    Task<ProviderCallResult<ProviderMediaServerSnapshot>> GetMediaServerSnapshotAsync(
        ProviderConnection connection,
        CancellationToken cancellationToken);
}
