using System.Globalization;
using System.Text.Json;
using ArrControl.Application.Catalog;
using ArrControl.Application.Providers;

namespace ArrControl.Infrastructure.Providers;

internal static class ArrCatalogReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
    };

    public static async Task<ProviderCallResult<ProviderCatalogSnapshot>> ReadSonarrAsync(
        IProviderApiTransport transport,
        ProviderConnection connection,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using var seriesResponse = await transport.GetAsync(
            connection,
            "api/v3/series",
            cancellationToken);
        var failure = Failure<ProviderCatalogSnapshot>(seriesResponse);
        if (failure is not null)
        {
            return failure;
        }

        try
        {
            var series = JsonSerializer.Deserialize<SonarrSeries[]>(seriesResponse.Body, JsonOptions);
            if (series is null || series.Any(value => !Valid(value.Id, value.Title)))
            {
                return Invalid<ProviderCatalogSnapshot>(seriesResponse);
            }

            var items = new List<CatalogItemSnapshot>();
            foreach (var value in series)
            {
                var seriesKey = $"series:{value.Id}";
                items.Add(new CatalogItemSnapshot(
                    seriesKey,
                    CatalogItemKinds.Series,
                    null,
                    value.Title!,
                    PositiveOrNull(value.Year),
                    null,
                    null,
                    value.Monitored,
                    Completion(value.Statistics),
                    NormalizeStatus(value.Status, "continuing", "ended", "upcoming", "deleted"),
                    value.Statistics?.NextAiring,
                    value.Added,
                    ExternalIds(("tvdb", value.TvdbId)),
                    Data(
                        ("titleSlug", value.TitleSlug),
                        ("overview", value.Overview),
                        ("upstreamStatus", value.Status))));

                foreach (var season in value.Seasons ?? [])
                {
                    if (season.SeasonNumber < 0)
                    {
                        return Invalid<ProviderCatalogSnapshot>(seriesResponse);
                    }

                    items.Add(new CatalogItemSnapshot(
                        $"season:{value.Id}:{season.SeasonNumber}",
                        CatalogItemKinds.Season,
                        seriesKey,
                        $"{value.Title} / {season.SeasonNumber.ToString(CultureInfo.InvariantCulture)}",
                        PositiveOrNull(value.Year),
                        season.SeasonNumber,
                        null,
                        season.Monitored,
                        Completion(season.Statistics),
                        "unknown",
                        season.Statistics?.NextAiring,
                        value.Added,
                        ExternalIds(("tvdb", value.TvdbId)),
                        Data(
                            ("episodeCount", season.Statistics?.EpisodeCount),
                            ("episodeFileCount", season.Statistics?.EpisodeFileCount))));
                }

                using var episodeResponse = await transport.GetAsync(
                    connection,
                    "api/v3/episode",
                    new Dictionary<string, string>
                    {
                        ["seriesId"] = value.Id.ToString(CultureInfo.InvariantCulture),
                    },
                    cancellationToken);
                failure = Failure<ProviderCatalogSnapshot>(episodeResponse);
                if (failure is not null)
                {
                    return failure;
                }

                var episodes = JsonSerializer.Deserialize<SonarrEpisode[]>(episodeResponse.Body, JsonOptions);
                if (episodes is null
                    || episodes.Any(episode => episode.Id <= 0
                        || episode.SeriesId != value.Id
                        || episode.SeasonNumber < 0
                        || episode.EpisodeNumber < 0
                        || string.IsNullOrWhiteSpace(episode.Title)))
                {
                    return Invalid<ProviderCatalogSnapshot>(episodeResponse);
                }

                foreach (var episode in episodes)
                {
                    items.Add(new CatalogItemSnapshot(
                        $"episode:{episode.Id}",
                        CatalogItemKinds.Episode,
                        $"season:{value.Id}:{episode.SeasonNumber}",
                        episode.Title!,
                        PositiveOrNull(value.Year),
                        episode.SeasonNumber,
                        episode.EpisodeNumber,
                        episode.Monitored,
                        episode.HasFile,
                        "unknown",
                        episode.AirDateUtc,
                        value.Added,
                        ExternalIds(("tvdb", episode.TvdbId)),
                        Data(
                            ("overview", episode.Overview),
                            ("absoluteEpisodeNumber", episode.AbsoluteEpisodeNumber))));
                }
            }

            return ProviderCallResult<ProviderCatalogSnapshot>.Succeeded(
                new ProviderCatalogSnapshot(timeProvider.GetUtcNow(), items),
                seriesResponse.RateLimit,
                seriesResponse.StatusCode);
        }
        catch (JsonException)
        {
            return Invalid<ProviderCatalogSnapshot>(seriesResponse);
        }
    }

    public static async Task<ProviderCallResult<ProviderCatalogSnapshot>> ReadRadarrAsync(
        IProviderApiTransport transport,
        ProviderConnection connection,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using var response = await transport.GetAsync(connection, "api/v3/movie", cancellationToken);
        var failure = Failure<ProviderCatalogSnapshot>(response);
        if (failure is not null)
        {
            return failure;
        }

        try
        {
            var movies = JsonSerializer.Deserialize<RadarrMovie[]>(response.Body, JsonOptions);
            if (movies is null || movies.Any(value => !Valid(value.Id, value.Title)))
            {
                return Invalid<ProviderCatalogSnapshot>(response);
            }

            var items = movies.Select(value => new CatalogItemSnapshot(
                    $"movie:{value.Id}",
                    CatalogItemKinds.Movie,
                    null,
                    value.Title!,
                    PositiveOrNull(value.Year),
                    null,
                    null,
                    value.Monitored,
                    value.HasFile,
                    NormalizeStatus(value.Status, "tba", "announced", "incinemas", "released", "deleted"),
                    value.DigitalRelease ?? value.PhysicalRelease ?? value.InCinemas,
                    value.Added,
                    ExternalIds(("tmdb", value.TmdbId), ("imdb", value.ImdbId)),
                    Data(
                        ("titleSlug", value.TitleSlug),
                        ("overview", value.Overview),
                        ("upstreamStatus", value.Status),
                        ("inCinemas", value.InCinemas),
                        ("digitalRelease", value.DigitalRelease),
                        ("physicalRelease", value.PhysicalRelease))))
                .ToArray();
            return ProviderCallResult<ProviderCatalogSnapshot>.Succeeded(
                new ProviderCatalogSnapshot(timeProvider.GetUtcNow(), items),
                response.RateLimit,
                response.StatusCode);
        }
        catch (JsonException)
        {
            return Invalid<ProviderCatalogSnapshot>(response);
        }
    }

    public static async Task<ProviderCallResult<ProviderCatalogSnapshot>> ReadLidarrAsync(
        IProviderApiTransport transport,
        ProviderConnection connection,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using var artistsResponse = await transport.GetAsync(connection, "api/v1/artist", cancellationToken);
        var failure = Failure<ProviderCatalogSnapshot>(artistsResponse);
        if (failure is not null) return failure;
        using var albumsResponse = await transport.GetAsync(connection, "api/v1/album", cancellationToken);
        failure = Failure<ProviderCatalogSnapshot>(albumsResponse);
        if (failure is not null) return failure;

        try
        {
            var artists = JsonSerializer.Deserialize<LidarrArtist[]>(artistsResponse.Body, JsonOptions);
            var albums = JsonSerializer.Deserialize<LidarrAlbum[]>(albumsResponse.Body, JsonOptions);
            if (artists is null || albums is null
                || artists.Any(value => !Valid(value.Id, value.ArtistName))
                || albums.Any(value => !Valid(value.Id, value.Title) || value.ArtistId <= 0)
                || albums.Any(value => artists.All(artist => artist.Id != value.ArtistId)))
                return Invalid<ProviderCatalogSnapshot>(albumsResponse);

            var items = artists.Select(value => new CatalogItemSnapshot(
                    $"artist:{value.Id}", CatalogItemKinds.Artist, null, value.ArtistName!, null,
                    null, null, value.Monitored, Completion(value.Statistics),
                    NormalizeStatus(value.Status, "continuing", "ended"), null, value.Added,
                    ExternalIds(("musicbrainz", value.ForeignArtistId)),
                    Data(("overview", value.Overview), ("upstreamStatus", value.Status))))
                .Concat(albums.Select(value => new CatalogItemSnapshot(
                    $"album:{value.Id}", CatalogItemKinds.Album, $"artist:{value.ArtistId}", value.Title!,
                    value.ReleaseDate?.Year, null, null, value.Monitored, Completion(value.Statistics),
                    "unknown", value.ReleaseDate, null,
                    ExternalIds(("musicbrainz", value.ForeignAlbumId)),
                    Data(("overview", value.Overview), ("albumType", value.AlbumType)))))
                .ToArray();
            return ProviderCallResult<ProviderCatalogSnapshot>.Succeeded(
                new ProviderCatalogSnapshot(timeProvider.GetUtcNow(), items),
                albumsResponse.RateLimit ?? artistsResponse.RateLimit, 200);
        }
        catch (JsonException)
        {
            return Invalid<ProviderCatalogSnapshot>(albumsResponse);
        }
    }

    public static async Task<ProviderCallResult<ProviderCatalogSnapshot>> ReadReadarrAsync(
        IProviderApiTransport transport,
        ProviderConnection connection,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using var authorsResponse = await transport.GetAsync(connection, "api/v1/author", cancellationToken);
        var failure = Failure<ProviderCatalogSnapshot>(authorsResponse);
        if (failure is not null) return failure;
        using var booksResponse = await transport.GetAsync(connection, "api/v1/book", cancellationToken);
        failure = Failure<ProviderCatalogSnapshot>(booksResponse);
        if (failure is not null) return failure;

        try
        {
            var authors = JsonSerializer.Deserialize<ReadarrAuthor[]>(authorsResponse.Body, JsonOptions);
            var books = JsonSerializer.Deserialize<ReadarrBook[]>(booksResponse.Body, JsonOptions);
            if (authors is null || books is null
                || authors.Any(value => !Valid(value.Id, value.AuthorName))
                || books.Any(value => !Valid(value.Id, value.Title) || value.AuthorId <= 0)
                || books.Any(value => authors.All(author => author.Id != value.AuthorId)))
                return Invalid<ProviderCatalogSnapshot>(booksResponse);

            var items = authors.Select(value => new CatalogItemSnapshot(
                    $"author:{value.Id}", CatalogItemKinds.Author, null, value.AuthorName!, null,
                    null, null, value.Monitored, Completion(value.Statistics),
                    NormalizeStatus(value.Status, "continuing", "ended"), null, value.Added,
                    ExternalIds(("goodreads", value.ForeignAuthorId)),
                    Data(("overview", value.Overview), ("upstreamStatus", value.Status))))
                .Concat(books.Select(value => new CatalogItemSnapshot(
                    $"book:{value.Id}", CatalogItemKinds.Book, $"author:{value.AuthorId}", value.Title!,
                    value.ReleaseDate?.Year, null, null, value.Monitored, Completion(value.Statistics),
                    "unknown", value.ReleaseDate, value.Added,
                    ExternalIds(("goodreads", value.ForeignBookId)),
                    Data(("overview", value.Overview), ("pageCount", PositiveOrNull(value.PageCount))))))
                .ToArray();
            return ProviderCallResult<ProviderCatalogSnapshot>.Succeeded(
                new ProviderCatalogSnapshot(timeProvider.GetUtcNow(), items),
                booksResponse.RateLimit ?? authorsResponse.RateLimit, 200);
        }
        catch (JsonException)
        {
            return Invalid<ProviderCatalogSnapshot>(booksResponse);
        }
    }

    public static async Task<ProviderCallResult<ProviderCatalogSnapshot>> ReadWhisparrAsync(
        IProviderApiTransport transport,
        ProviderConnection connection,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using var movieResponse = await transport.GetAsync(connection, "api/v3/movie", cancellationToken);
        if (movieResponse.StatusCode == 200)
            return ReadWhisparrMovies(movieResponse, timeProvider);
        if (movieResponse.StatusCode != 404)
            return Failure<ProviderCatalogSnapshot>(movieResponse)!;

        using var seriesResponse = await transport.GetAsync(connection, "api/v3/series", cancellationToken);
        var failure = Failure<ProviderCatalogSnapshot>(seriesResponse);
        if (failure is not null) return failure;
        try
        {
            var series = JsonSerializer.Deserialize<WhisparrSeries[]>(seriesResponse.Body, JsonOptions);
            if (series is null || series.Any(value => !Valid(value.Id, value.Title)))
                return Invalid<ProviderCatalogSnapshot>(seriesResponse);
            var items = new List<CatalogItemSnapshot>();
            foreach (var value in series)
            {
                items.Add(new CatalogItemSnapshot(
                    $"series:{value.Id}", CatalogItemKinds.Series, null, value.Title!, PositiveOrNull(value.Year),
                    null, null, value.Monitored, Completion(value.Statistics),
                    NormalizeStatus(value.Status, "continuing", "ended", "upcoming", "deleted"),
                    value.NextAiring, value.Added, ExternalIds(("tvdb", value.TvdbId)),
                    Data(("overview", value.Overview), ("upstreamStatus", value.Status))));
                using var episodeResponse = await transport.GetAsync(connection, "api/v3/episode",
                    new Dictionary<string, string> { ["seriesId"] = value.Id.ToString(CultureInfo.InvariantCulture) },
                    cancellationToken);
                failure = Failure<ProviderCatalogSnapshot>(episodeResponse);
                if (failure is not null) return failure;
                var episodes = JsonSerializer.Deserialize<WhisparrEpisode[]>(episodeResponse.Body, JsonOptions);
                if (episodes is null || episodes.Any(episode => !Valid(episode.Id, episode.Title)
                    || episode.SeriesId != value.Id || episode.SeasonNumber < 0))
                    return Invalid<ProviderCatalogSnapshot>(episodeResponse);
                items.AddRange(episodes.Select(episode => new CatalogItemSnapshot(
                    $"episode:{episode.Id}", CatalogItemKinds.Episode, $"series:{value.Id}", episode.Title!,
                    value.Year > 0 ? value.Year : null, episode.SeasonNumber, null, episode.Monitored,
                    episode.HasFile, "unknown", episode.ReleaseDate, value.Added,
                    ExternalIds(("tvdb", episode.TvdbId)), Data(("overview", episode.Overview)))));
            }
            return ProviderCallResult<ProviderCatalogSnapshot>.Succeeded(
                new ProviderCatalogSnapshot(timeProvider.GetUtcNow(), items), seriesResponse.RateLimit, 200);
        }
        catch (JsonException)
        {
            return Invalid<ProviderCatalogSnapshot>(seriesResponse);
        }
    }

    private static ProviderCallResult<ProviderCatalogSnapshot> ReadWhisparrMovies(
        ProviderTransportResponse response,
        TimeProvider timeProvider)
    {
        try
        {
            var movies = JsonSerializer.Deserialize<WhisparrMovie[]>(response.Body, JsonOptions);
            if (movies is null || movies.Any(value => !Valid(value.Id, value.Title)))
                return Invalid<ProviderCatalogSnapshot>(response);
            var items = movies.Select(value => new CatalogItemSnapshot(
                $"movie:{value.Id}", CatalogItemKinds.Movie, null, value.Title!, PositiveOrNull(value.Year),
                null, null, value.Monitored, value.HasFile,
                NormalizeStatus(value.Status, "tba", "announced", "released", "deleted"),
                value.ReleaseDate, value.Added,
                ExternalIds(("tmdb", value.TmdbId), ("imdb", value.ImdbId), ("stash", value.StashId)),
                Data(("overview", value.Overview), ("studio", value.StudioTitle), ("upstreamStatus", value.Status))))
                .ToArray();
            return ProviderCallResult<ProviderCatalogSnapshot>.Succeeded(
                new ProviderCatalogSnapshot(timeProvider.GetUtcNow(), items), response.RateLimit, response.StatusCode);
        }
        catch (JsonException)
        {
            return Invalid<ProviderCatalogSnapshot>(response);
        }
    }

    private static bool Valid(int id, string? title) => id > 0 && !string.IsNullOrWhiteSpace(title);

    private static int? PositiveOrNull(int value) => value > 0 ? value : null;

    private static bool? Completion(SonarrStatistics? statistics) =>
        statistics is null || statistics.EpisodeCount <= 0
            ? null
            : statistics.EpisodeFileCount >= statistics.EpisodeCount;

    private static bool? Completion(FileStatistics? statistics) =>
        statistics is null || statistics.TotalCount <= 0
            ? null
            : statistics.FileCount >= statistics.TotalCount;

    private static string NormalizeStatus(string? value, params string[] known)
    {
        var normalized = value?.Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized is not null && known.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : "unknown";
    }

    private static IReadOnlyDictionary<string, string> ExternalIds(
        params (string Key, object? Value)[] values) =>
        values.Where(value => value.Value switch
            {
                int number => number > 0,
                string text => !string.IsNullOrWhiteSpace(text),
                _ => false,
            })
            .ToDictionary(
                value => value.Key,
                value => Convert.ToString(value.Value, CultureInfo.InvariantCulture)!,
                StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, object?> Data(
        params (string Key, object? Value)[] values) =>
        values.Where(value => value.Value is not null)
            .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);

    private static ProviderCallResult<T>? Failure<T>(ProviderTransportResponse response)
    {
        if (response.StatusCode == 200)
        {
            return null;
        }

        var code = response.StatusCode switch
        {
            401 => ProviderErrorCodes.Unauthorized,
            403 => ProviderErrorCodes.Forbidden,
            404 => ProviderErrorCodes.NotFound,
            409 => ProviderErrorCodes.UpstreamConflict,
            429 => ProviderErrorCodes.RateLimited,
            _ => ProviderErrorCodes.Unknown,
        };
        return ProviderCallResult<T>.Failed(code, response.RateLimit, response.StatusCode);
    }

    private static ProviderCallResult<T> Invalid<T>(ProviderTransportResponse response) =>
        ProviderCallResult<T>.Failed(
            ProviderErrorCodes.InvalidResponse,
            response.RateLimit,
            response.StatusCode);

    private sealed class SonarrSeries
    {
        public int Id { get; init; }
        public string? Title { get; init; }
        public string? TitleSlug { get; init; }
        public string? Status { get; init; }
        public string? Overview { get; init; }
        public int Year { get; init; }
        public bool Monitored { get; init; }
        public int TvdbId { get; init; }
        public DateTimeOffset? Added { get; init; }
        public SonarrSeason[]? Seasons { get; init; }
        public SonarrStatistics? Statistics { get; init; }
    }

    private sealed class SonarrSeason
    {
        public int SeasonNumber { get; init; }
        public bool Monitored { get; init; }
        public SonarrStatistics? Statistics { get; init; }
    }

    private sealed class SonarrStatistics
    {
        public int EpisodeFileCount { get; init; }
        public int EpisodeCount { get; init; }
        public DateTimeOffset? NextAiring { get; init; }
    }

    private sealed class SonarrEpisode
    {
        public int Id { get; init; }
        public int SeriesId { get; init; }
        public int TvdbId { get; init; }
        public int SeasonNumber { get; init; }
        public int EpisodeNumber { get; init; }
        public int? AbsoluteEpisodeNumber { get; init; }
        public string? Title { get; init; }
        public string? Overview { get; init; }
        public DateTimeOffset? AirDateUtc { get; init; }
        public bool Monitored { get; init; }
        public bool HasFile { get; init; }
    }

    private sealed class RadarrMovie
    {
        public int Id { get; init; }
        public int TmdbId { get; init; }
        public string? ImdbId { get; init; }
        public string? Title { get; init; }
        public string? TitleSlug { get; init; }
        public string? Status { get; init; }
        public string? Overview { get; init; }
        public int Year { get; init; }
        public bool Monitored { get; init; }
        public bool? HasFile { get; init; }
        public DateTimeOffset? Added { get; init; }
        public DateTimeOffset? InCinemas { get; init; }
        public DateTimeOffset? DigitalRelease { get; init; }
        public DateTimeOffset? PhysicalRelease { get; init; }
    }

    private sealed class FileStatistics
    {
        public int TrackFileCount { get; init; }
        public int TotalTrackCount { get; init; }
        public int BookFileCount { get; init; }
        public int TotalBookCount { get; init; }
        public int FileCount => Math.Max(TrackFileCount, BookFileCount);
        public int TotalCount => Math.Max(TotalTrackCount, TotalBookCount);
    }

    private sealed class LidarrArtist
    {
        public int Id { get; init; }
        public string? ArtistName { get; init; }
        public string? ForeignArtistId { get; init; }
        public string? Status { get; init; }
        public string? Overview { get; init; }
        public bool Monitored { get; init; }
        public DateTimeOffset? Added { get; init; }
        public FileStatistics? Statistics { get; init; }
    }

    private sealed class LidarrAlbum
    {
        public int Id { get; init; }
        public int ArtistId { get; init; }
        public string? Title { get; init; }
        public string? ForeignAlbumId { get; init; }
        public string? Overview { get; init; }
        public string? AlbumType { get; init; }
        public bool Monitored { get; init; }
        public DateTimeOffset? ReleaseDate { get; init; }
        public FileStatistics? Statistics { get; init; }
    }

    private sealed class ReadarrAuthor
    {
        public int Id { get; init; }
        public string? AuthorName { get; init; }
        public string? ForeignAuthorId { get; init; }
        public string? Status { get; init; }
        public string? Overview { get; init; }
        public bool Monitored { get; init; }
        public DateTimeOffset? Added { get; init; }
        public FileStatistics? Statistics { get; init; }
    }

    private sealed class ReadarrBook
    {
        public int Id { get; init; }
        public int AuthorId { get; init; }
        public int PageCount { get; init; }
        public string? Title { get; init; }
        public string? ForeignBookId { get; init; }
        public string? Overview { get; init; }
        public bool Monitored { get; init; }
        public DateTimeOffset? ReleaseDate { get; init; }
        public DateTimeOffset? Added { get; init; }
        public FileStatistics? Statistics { get; init; }
    }

    private sealed class WhisparrMovie
    {
        public int Id { get; init; }
        public int TmdbId { get; init; }
        public string? ImdbId { get; init; }
        public string? StashId { get; init; }
        public string? Title { get; init; }
        public string? Status { get; init; }
        public string? Overview { get; init; }
        public string? StudioTitle { get; init; }
        public int Year { get; init; }
        public bool Monitored { get; init; }
        public bool HasFile { get; init; }
        public DateTimeOffset? ReleaseDate { get; init; }
        public DateTimeOffset? Added { get; init; }
    }

    private sealed class WhisparrSeries
    {
        public int Id { get; init; }
        public int TvdbId { get; init; }
        public string? Title { get; init; }
        public string? Status { get; init; }
        public string? Overview { get; init; }
        public int Year { get; init; }
        public bool Monitored { get; init; }
        public DateTimeOffset? NextAiring { get; init; }
        public DateTimeOffset? Added { get; init; }
        public SonarrStatistics? Statistics { get; init; }
    }

    private sealed class WhisparrEpisode
    {
        public int Id { get; init; }
        public int SeriesId { get; init; }
        public int TvdbId { get; init; }
        public int SeasonNumber { get; init; }
        public string? Title { get; init; }
        public string? Overview { get; init; }
        public DateTimeOffset? ReleaseDate { get; init; }
        public bool Monitored { get; init; }
        public bool HasFile { get; init; }
    }
}
