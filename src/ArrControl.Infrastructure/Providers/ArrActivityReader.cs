using System.Globalization;
using System.Text.Json;
using ArrControl.Application.Activity;
using ArrControl.Application.Providers;

namespace ArrControl.Infrastructure.Providers;

internal static class ArrActivityReader
{
    private const int PageSize = 1000;
    private const int MaximumRecords = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
    };

    public static async Task<ProviderCallResult<ProviderActivitySnapshot>> ReadAsync(
        IProviderApiTransport transport,
        ProviderConnection connection,
        string kind,
        string apiVersion,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var queue = await ReadPagesAsync<QueueResource>(
            transport,
            connection,
            $"api/{apiVersion}/queue",
            "added",
            cancellationToken);
        if (!queue.Success)
        {
            return ProviderCallResult<ProviderActivitySnapshot>.Failed(
                queue.ErrorCode!, queue.RateLimit, queue.HttpStatusCode);
        }

        var history = await ReadPagesAsync<HistoryResource>(
            transport,
            connection,
            $"api/{apiVersion}/history",
            "date",
            cancellationToken);
        if (!history.Success)
        {
            return ProviderCallResult<ProviderActivitySnapshot>.Failed(
                history.ErrorCode!, history.RateLimit, history.HttpStatusCode);
        }

        var queueRecords = queue.Value!;
        var historyRecords = history.Value!;
        if (queueRecords.Any(value => value.Id <= 0 || string.IsNullOrWhiteSpace(value.Title))
            || historyRecords.Any(value => value.Id <= 0
                || string.IsNullOrWhiteSpace(value.SourceTitle)
                || value.Date == default))
        {
            return ProviderCallResult<ProviderActivitySnapshot>.Failed(ProviderErrorCodes.InvalidResponse);
        }

        var queueItems = queueRecords.Select(value => new QueueItemSnapshot(
                $"queue:{value.Id}",
                MediaKey(kind, value),
                Clean(value.DownloadId),
                value.Title!,
                Normalize(value.Status, QueueStatuses),
                Normalize(value.TrackedDownloadStatus, TrackedStatuses),
                Normalize(value.TrackedDownloadState, TrackedStates),
                Normalize(value.Protocol, Protocols),
                Nonnegative(value.Size),
                Nonnegative(value.Sizeleft),
                value.Added,
                value.EstimatedCompletionTime,
                Clean(value.DownloadClient),
                Clean(value.Indexer)))
            .ToArray();
        var historyItems = historyRecords.Select(value => new HistoryItemSnapshot(
                $"history:{value.Id}",
                MediaKey(kind, value),
                Clean(value.DownloadId),
                value.SourceTitle!,
                Normalize(value.EventType, HistoryEvents),
                value.Date))
            .ToArray();
        return ProviderCallResult<ProviderActivitySnapshot>.Succeeded(
            new ProviderActivitySnapshot(timeProvider.GetUtcNow(), queueItems, historyItems),
            history.RateLimit ?? queue.RateLimit,
            200);
    }

    private static async Task<ProviderCallResult<IReadOnlyList<T>>> ReadPagesAsync<T>(
        IProviderApiTransport transport,
        ProviderConnection connection,
        string path,
        string sortKey,
        CancellationToken cancellationToken)
    {
        var records = new List<T>();
        for (var page = 1; page <= MaximumRecords / PageSize; page++)
        {
            using var response = await transport.GetAsync(
                connection,
                path,
                new Dictionary<string, string>
                {
                    ["page"] = page.ToString(CultureInfo.InvariantCulture),
                    ["pageSize"] = PageSize.ToString(CultureInfo.InvariantCulture),
                    ["sortDirection"] = "descending",
                    ["sortKey"] = sortKey,
                },
                cancellationToken);
            var failure = Failure<IReadOnlyList<T>>(response);
            if (failure is not null)
            {
                return failure;
            }

            try
            {
                var resource = JsonSerializer.Deserialize<PagingResource<T>>(response.Body, JsonOptions);
                if (resource?.Records is null
                    || resource.TotalRecords < 0
                    || resource.TotalRecords > MaximumRecords)
                {
                    return Invalid<IReadOnlyList<T>>(response);
                }

                records.AddRange(resource.Records);
                if (records.Count >= resource.TotalRecords)
                {
                    return ProviderCallResult<IReadOnlyList<T>>.Succeeded(
                        records,
                        response.RateLimit,
                        response.StatusCode);
                }
            }
            catch (JsonException)
            {
                return Invalid<IReadOnlyList<T>>(response);
            }
        }

        return ProviderCallResult<IReadOnlyList<T>>.Failed(ProviderErrorCodes.InvalidResponse);
    }

    private static string? MediaKey(string kind, ActivityResource value) => kind switch
    {
        "radarr" when value.MovieId > 0 => $"movie:{value.MovieId}",
        "sonarr" when value.EpisodeId > 0 => $"episode:{value.EpisodeId}",
        "lidarr" when value.AlbumId > 0 => $"album:{value.AlbumId}",
        "readarr" when value.BookId > 0 => $"book:{value.BookId}",
        "whisparr" when value.MovieId > 0 => $"movie:{value.MovieId}",
        "whisparr" when value.EpisodeId > 0 => $"episode:{value.EpisodeId}",
        _ => null,
    };

    private static double Nonnegative(double value) =>
        double.IsFinite(value) && value > 0 ? value : 0;

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Normalize(string? value, IReadOnlySet<string> known)
    {
        var normalized = value?.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized is not null && known.Contains(normalized) ? normalized : "unknown";
    }

    private static readonly HashSet<string> QueueStatuses =
        ["unknown", "queued", "paused", "downloading", "completed", "failed", "warning", "delay", "downloadclientunavailable", "fallback"];
    private static readonly HashSet<string> TrackedStatuses = ["ok", "warning", "error"];
    private static readonly HashSet<string> TrackedStates =
        ["downloading", "importblocked", "importpending", "importing", "imported", "failedpending", "failed", "ignored"];
    private static readonly HashSet<string> Protocols = ["usenet", "torrent", "unknown"];
    private static readonly HashSet<string> HistoryEvents =
        ["unknown", "grabbed", "seriesfolderimported", "downloadfolderimported", "downloadfailed", "episodefiledeleted", "episodefilerenamed", "moviefolderimported", "moviefiledeleted", "moviefilerenamed", "downloadignored", "artistfolderimported", "trackfileimported", "trackfiledeleted", "trackfilerenamed", "albumimportincomplete", "downloadimported", "trackfileretagged", "bookfileimported", "bookfiledeleted", "bookfilerenamed", "bookimportincomplete", "bookfileretagged"];

    private static ProviderCallResult<T>? Failure<T>(ProviderTransportResponse response) =>
        response.StatusCode == 200
            ? null
            : ProviderCallResult<T>.Failed(
                response.StatusCode switch
                {
                    401 => ProviderErrorCodes.Unauthorized,
                    403 => ProviderErrorCodes.Forbidden,
                    429 => ProviderErrorCodes.RateLimited,
                    _ => ProviderErrorCodes.Unknown,
                },
                response.RateLimit,
                response.StatusCode);

    private static ProviderCallResult<T> Invalid<T>(ProviderTransportResponse response) =>
        ProviderCallResult<T>.Failed(
            ProviderErrorCodes.InvalidResponse,
            response.RateLimit,
            response.StatusCode);

    private sealed class PagingResource<T>
    {
        public int TotalRecords { get; init; }
        public T[]? Records { get; init; }
    }

    private abstract class ActivityResource
    {
        public int? MovieId { get; init; }
        public int? EpisodeId { get; init; }
        public int? AlbumId { get; init; }
        public int? BookId { get; init; }
    }

    private sealed class QueueResource : ActivityResource
    {
        public int Id { get; init; }
        public string? Title { get; init; }
        public string? DownloadId { get; init; }
        public string? Status { get; init; }
        public string? TrackedDownloadStatus { get; init; }
        public string? TrackedDownloadState { get; init; }
        public string? Protocol { get; init; }
        public double Size { get; init; }
        public double Sizeleft { get; init; }
        public DateTimeOffset? Added { get; init; }
        public DateTimeOffset? EstimatedCompletionTime { get; init; }
        public string? DownloadClient { get; init; }
        public string? Indexer { get; init; }
    }

    private sealed class HistoryResource : ActivityResource
    {
        public int Id { get; init; }
        public string? SourceTitle { get; init; }
        public string? DownloadId { get; init; }
        public string? EventType { get; init; }
        public DateTimeOffset Date { get; init; }
    }
}
