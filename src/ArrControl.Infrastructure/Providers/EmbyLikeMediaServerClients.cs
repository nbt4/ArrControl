using System.Text.Json;
using ArrControl.Application.Connections;
using ArrControl.Application.Providers;

namespace ArrControl.Infrastructure.Providers;

public sealed class JellyfinClient(IProviderHttpTransport transport, TimeProvider? timeProvider = null)
    : EmbyLikeMediaServerClient(transport, timeProvider, "jellyfin", "Jellyfin", string.Empty, 10, 10, 11);

public sealed class EmbyClient(IProviderHttpTransport transport, TimeProvider? timeProvider = null)
    : EmbyLikeMediaServerClient(transport, timeProvider, "emby", "Emby", "emby/", 4, 8, 9);

public abstract class EmbyLikeMediaServerClient(
    IProviderHttpTransport transport,
    TimeProvider? timeProvider,
    string kind,
    string product,
    string pathPrefix,
    int supportedMajor,
    int minimumMinor,
    int maximumMinor) : IProviderMediaServerClient, IProviderCredentialContract
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    public string Kind => kind;
    public IReadOnlyList<string> RequiredCredentialPurposes => [CredentialPurposes.ApiKey];

    public async Task<ProviderCallResult<ProviderSystemStatus>> GetSystemStatusAsync(
        ProviderConnection connection, CancellationToken cancellationToken)
    {
        if (!TryHeaders(connection, out var headers))
            return ProviderCallResult<ProviderSystemStatus>.Failed(ProviderErrorCodes.CredentialMissing);
        using var response = await transport.SendAsync(connection,
            new ProviderHttpRequest(HttpMethod.Get, pathPrefix + "System/Info", headers: headers), cancellationToken);
        var failure = SupportingProviderReader.Failure<ProviderSystemStatus>(response);
        if (failure is not null) return failure;
        try
        {
            var status = JsonSerializer.Deserialize<EmbySystemInfo>(response.Body, JsonOptions);
            if (status is null || string.IsNullOrWhiteSpace(status.ProductName)
                || !status.ProductName.StartsWith(product, StringComparison.OrdinalIgnoreCase)
                || !Version.TryParse(status.Version, out var version))
                return SupportingProviderReader.Invalid<ProviderSystemStatus>(response);
            if (version.Major != supportedMajor || version.Minor < minimumMinor || version.Minor > maximumMinor)
                return ProviderCallResult<ProviderSystemStatus>.Failed(
                    ProviderErrorCodes.UnsupportedVersion, response.RateLimit, response.StatusCode);
            return ProviderCallResult<ProviderSystemStatus>.Succeeded(
                new ProviderSystemStatus(product, status.Version!, null, null), response.RateLimit, response.StatusCode);
        }
        catch (JsonException)
        {
            return SupportingProviderReader.Invalid<ProviderSystemStatus>(response);
        }
    }

    public async Task<ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>> GetHealthAsync(
        ProviderConnection connection, CancellationToken cancellationToken)
    {
        var result = await GetMediaServerSnapshotAsync(connection, cancellationToken);
        return result.Success
            ? ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>.Succeeded([], result.RateLimit, 200)
            : ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>.Failed(
                result.ErrorCode!, result.RateLimit, result.HttpStatusCode);
    }

    public async Task<ProviderCallResult<ProviderMediaServerSnapshot>> GetMediaServerSnapshotAsync(
        ProviderConnection connection, CancellationToken cancellationToken)
    {
        if (!TryHeaders(connection, out var headers))
            return ProviderCallResult<ProviderMediaServerSnapshot>.Failed(ProviderErrorCodes.CredentialMissing);
        using var countsResponse = await transport.SendAsync(connection,
            new ProviderHttpRequest(HttpMethod.Get, pathPrefix + "Items/Counts", headers: headers), cancellationToken);
        var failure = SupportingProviderReader.Failure<ProviderMediaServerSnapshot>(countsResponse);
        if (failure is not null) return failure;
        using var sessionsResponse = await transport.SendAsync(connection,
            new ProviderHttpRequest(HttpMethod.Get, pathPrefix + "Sessions", headers: headers), cancellationToken);
        failure = SupportingProviderReader.Failure<ProviderMediaServerSnapshot>(sessionsResponse);
        if (failure is not null) return failure;
        try
        {
            var counts = JsonSerializer.Deserialize<EmbyItemCounts>(countsResponse.Body, JsonOptions);
            var sessions = JsonSerializer.Deserialize<EmbySession[]>(sessionsResponse.Body, JsonOptions);
            if (counts is null || sessions is null || sessions.Length > 1_000 || !counts.Valid())
                return SupportingProviderReader.Invalid<ProviderMediaServerSnapshot>(sessionsResponse);
            var active = sessions.Where(value => value.NowPlayingItem is not null).ToArray();
            var paused = active.Count(value => value.PlayState?.IsPaused == true);
            var transcoding = active.Count(value => value.TranscodingInfo is not null);
            var libraries = new ProviderMediaLibrarySummary(null, null, null, null, null, null,
                new ProviderMediaItemCounts(counts.MovieCount, counts.SeriesCount, counts.EpisodeCount,
                    counts.SongCount, counts.AlbumCount, counts.BookCount, counts.ItemCount));
            var playback = new ProviderPlaybackSummary(active.Length, active.Length - paused, paused, 0,
                transcoding, 0, active.Length - transcoding, 0);
            return ProviderCallResult<ProviderMediaServerSnapshot>.Succeeded(
                new ProviderMediaServerSnapshot((timeProvider ?? TimeProvider.System).GetUtcNow(), libraries, playback),
                sessionsResponse.RateLimit ?? countsResponse.RateLimit, 200);
        }
        catch (JsonException)
        {
            return SupportingProviderReader.Invalid<ProviderMediaServerSnapshot>(sessionsResponse);
        }
    }

    private static bool TryHeaders(ProviderConnection connection, out IReadOnlyDictionary<string, string> headers)
    {
        headers = new Dictionary<string, string>();
        if (!connection.TryGetCredential(CredentialPurposes.ApiKey, out var token)) return false;
        headers = new Dictionary<string, string> { ["X-Emby-Token"] = token };
        return true;
    }

    private sealed class EmbySystemInfo { public string? ProductName { get; init; } public string? Version { get; init; } }
    private sealed class EmbyItemCounts
    {
        public int MovieCount { get; init; }
        public int SeriesCount { get; init; }
        public int EpisodeCount { get; init; }
        public int SongCount { get; init; }
        public int AlbumCount { get; init; }
        public int BookCount { get; init; }
        public int ItemCount { get; init; }
        public bool Valid() => MovieCount >= 0 && SeriesCount >= 0 && EpisodeCount >= 0
            && SongCount >= 0 && AlbumCount >= 0 && BookCount >= 0 && ItemCount >= 0;
    }
    private sealed class EmbySession
    {
        public EmbyNowPlayingItem? NowPlayingItem { get; init; }
        public EmbyPlayState? PlayState { get; init; }
        public EmbyTranscodingInfo? TranscodingInfo { get; init; }
    }
    private sealed class EmbyNowPlayingItem { }
    private sealed class EmbyPlayState { public bool IsPaused { get; init; } }
    private sealed class EmbyTranscodingInfo { }
}
