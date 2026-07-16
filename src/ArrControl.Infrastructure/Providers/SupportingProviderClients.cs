using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArrControl.Application.Activity;
using ArrControl.Application.Providers;

namespace ArrControl.Infrastructure.Providers;

public sealed class ProwlarrClient(IProviderApiTransport transport, TimeProvider? timeProvider = null)
    : ArrV3ProviderClient(transport, "prowlarr", "Prowlarr", "v1", new HashSet<int> { 2 }),
        IProviderIndexerClient, IProviderActivityClient
{
    public Task<ProviderCallResult<IReadOnlyList<ProviderIndexer>>> GetIndexersAsync(
        ProviderConnection connection, CancellationToken cancellationToken) =>
        SupportingProviderReader.ReadProwlarrIndexersAsync(Transport, connection, cancellationToken);

    public Task<ProviderCallResult<ProviderActivitySnapshot>> GetActivityAsync(
        ProviderConnection connection, CancellationToken cancellationToken) =>
        SupportingProviderReader.ReadProwlarrActivityAsync(
            Transport, connection, timeProvider ?? TimeProvider.System, cancellationToken);
}

public sealed class BazarrClient(IProviderApiTransport transport, TimeProvider? timeProvider = null)
    : IArrProviderClient, IProviderSubtitleActivityClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };

    public string Kind => "bazarr";

    public async Task<ProviderCallResult<ProviderSystemStatus>> GetSystemStatusAsync(
        ProviderConnection connection, CancellationToken cancellationToken)
    {
        using var response = await transport.GetAsync(connection, "api/system/status", cancellationToken);
        var failure = SupportingProviderReader.Failure<ProviderSystemStatus>(response);
        if (failure is not null) return failure;
        try
        {
            var resource = JsonSerializer.Deserialize<BazarrStatusEnvelope>(response.Body, JsonOptions);
            var rawVersion = resource?.Data?.BazarrVersion?.Trim();
            var versionText = rawVersion?.TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(rawVersion) || !Version.TryParse(versionText, out var version))
                return SupportingProviderReader.Invalid<ProviderSystemStatus>(response);
            if (version.Major != 1)
                return ProviderCallResult<ProviderSystemStatus>.Failed(
                    ProviderErrorCodes.UnsupportedVersion, response.RateLimit, response.StatusCode);
            return ProviderCallResult<ProviderSystemStatus>.Succeeded(
                new ProviderSystemStatus("Bazarr", rawVersion, null, null), response.RateLimit, response.StatusCode);
        }
        catch (JsonException)
        {
            return SupportingProviderReader.Invalid<ProviderSystemStatus>(response);
        }
    }

    public async Task<ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>> GetHealthAsync(
        ProviderConnection connection, CancellationToken cancellationToken)
    {
        using var response = await transport.GetAsync(connection, "api/system/health", cancellationToken);
        var failure = SupportingProviderReader.Failure<IReadOnlyList<ProviderHealthIssue>>(response);
        if (failure is not null) return failure;
        try
        {
            var resource = JsonSerializer.Deserialize<BazarrHealthEnvelope>(response.Body, JsonOptions);
            if (resource?.Data is null || resource.Data.Length > 1_000
                || resource.Data.Any(value => string.IsNullOrWhiteSpace(value.Object)
                    || string.IsNullOrWhiteSpace(value.Issue)))
                return SupportingProviderReader.Invalid<IReadOnlyList<ProviderHealthIssue>>(response);
            var issues = resource.Data.Select(value =>
            {
                var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{value.Object}\n{value.Issue}"));
                var id = BinaryPrimitives.ReadInt32BigEndian(digest) & int.MaxValue;
                if (id == 0) id = 1;
                var source = $"bazarr.health.{Convert.ToHexStringLower(digest.AsSpan(0, 8))}";
                return new ProviderHealthIssue(id, source, "warning", value.Issue!.Trim(), null);
            }).ToArray();
            return ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>.Succeeded(
                issues, response.RateLimit, response.StatusCode);
        }
        catch (JsonException)
        {
            return SupportingProviderReader.Invalid<IReadOnlyList<ProviderHealthIssue>>(response);
        }
    }

    public Task<ProviderCallResult<ProviderSubtitleActivitySnapshot>> GetSubtitleActivityAsync(
        ProviderConnection connection, CancellationToken cancellationToken) =>
        SupportingProviderReader.ReadBazarrActivityAsync(
            transport, connection, timeProvider ?? TimeProvider.System, cancellationToken);

    private sealed class BazarrStatusEnvelope { public BazarrStatus? Data { get; init; } }
    private sealed class BazarrStatus
    {
        [JsonPropertyName("bazarr_version")]
        public string? BazarrVersion { get; init; }
    }
    private sealed class BazarrHealthEnvelope { public BazarrHealth[]? Data { get; init; } }
    private sealed class BazarrHealth
    {
        public string? Object { get; init; }
        public string? Issue { get; init; }
    }
}

internal static class SupportingProviderReader
{
    private const int MaximumRecords = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };

    public static async Task<ProviderCallResult<IReadOnlyList<ProviderIndexer>>> ReadProwlarrIndexersAsync(
        IProviderApiTransport transport, ProviderConnection connection, CancellationToken cancellationToken)
    {
        using var response = await transport.GetAsync(connection, "api/v1/indexer", cancellationToken);
        var failure = Failure<IReadOnlyList<ProviderIndexer>>(response);
        if (failure is not null) return failure;
        try
        {
            var resources = JsonSerializer.Deserialize<ProwlarrIndexer[]>(response.Body, JsonOptions);
            if (resources is null || resources.Length > MaximumRecords
                || resources.Any(value => value.Id <= 0 || string.IsNullOrWhiteSpace(value.Name)))
                return Invalid<IReadOnlyList<ProviderIndexer>>(response);
            return ProviderCallResult<IReadOnlyList<ProviderIndexer>>.Succeeded(resources.Select(value =>
                new ProviderIndexer(value.Id, value.Name!.Trim(), value.Enable, value.SupportsRss,
                    value.SupportsSearch, NormalizeProtocol(value.Protocol), value.Priority,
                    value.Status?.DisabledTill)).ToArray(), response.RateLimit, response.StatusCode);
        }
        catch (JsonException)
        {
            return Invalid<IReadOnlyList<ProviderIndexer>>(response);
        }
    }

    public static async Task<ProviderCallResult<ProviderActivitySnapshot>> ReadProwlarrActivityAsync(
        IProviderApiTransport transport, ProviderConnection connection, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var indexers = await ReadProwlarrIndexersAsync(transport, connection, cancellationToken);
        if (!indexers.Success) return ProviderCallResult<ProviderActivitySnapshot>.Failed(
            indexers.ErrorCode!, indexers.RateLimit, indexers.HttpStatusCode);
        using var response = await transport.GetAsync(connection, "api/v1/history",
            new Dictionary<string, string>
            {
                ["page"] = "1", ["pageSize"] = MaximumRecords.ToString(CultureInfo.InvariantCulture),
                ["sortDirection"] = "descending", ["sortKey"] = "date",
            }, cancellationToken);
        var failure = Failure<ProviderActivitySnapshot>(response);
        if (failure is not null) return failure;
        try
        {
            var page = JsonSerializer.Deserialize<ProwlarrHistoryPage>(response.Body, JsonOptions);
            if (page?.Records is null || page.TotalRecords < 0 || page.TotalRecords > MaximumRecords
                || page.Records.Length != page.TotalRecords
                || page.Records.Any(value => value.Id <= 0 || value.IndexerId <= 0 || value.Date == default))
                return Invalid<ProviderActivitySnapshot>(response);
            var names = indexers.Value!.ToDictionary(value => value.Id, value => value.Name);
            var history = page.Records.Select(value => new HistoryItemSnapshot(
                $"history:{value.Id}", null, Clean(value.DownloadId),
                names.TryGetValue(value.IndexerId, out var name) ? name : $"Indexer {value.IndexerId}",
                NormalizeEvent(value.EventType, value.Successful), value.Date)).ToArray();
            return ProviderCallResult<ProviderActivitySnapshot>.Succeeded(
                new ProviderActivitySnapshot(timeProvider.GetUtcNow(), [], history),
                response.RateLimit ?? indexers.RateLimit, response.StatusCode);
        }
        catch (JsonException)
        {
            return Invalid<ProviderActivitySnapshot>(response);
        }
    }

    public static async Task<ProviderCallResult<ProviderSubtitleActivitySnapshot>> ReadBazarrActivityAsync(
        IProviderApiTransport transport, ProviderConnection connection, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using var response = await transport.GetAsync(connection, "api/history/stats",
            new Dictionary<string, string> { ["timeFrame"] = "year" }, cancellationToken);
        var failure = Failure<ProviderSubtitleActivitySnapshot>(response);
        if (failure is not null) return failure;
        try
        {
            var resource = JsonSerializer.Deserialize<BazarrStats>(response.Body, JsonOptions);
            if (resource?.Series is null || resource.Movies is null
                || resource.Series.Length > 366 || resource.Movies.Length > 366)
                return Invalid<ProviderSubtitleActivitySnapshot>(response);
            var days = new Dictionary<DateOnly, (int Series, int Movies)>();
            if (!Add(resource.Series, true, days) || !Add(resource.Movies, false, days))
                return Invalid<ProviderSubtitleActivitySnapshot>(response);
            return ProviderCallResult<ProviderSubtitleActivitySnapshot>.Succeeded(
                new ProviderSubtitleActivitySnapshot(timeProvider.GetUtcNow(), days.OrderBy(value => value.Key)
                    .Select(value => new ProviderSubtitleActivityDay(
                        value.Key, value.Value.Series, value.Value.Movies)).ToArray()),
                response.RateLimit, response.StatusCode);
        }
        catch (JsonException)
        {
            return Invalid<ProviderSubtitleActivitySnapshot>(response);
        }
    }

    private static bool Add(BazarrStatsDay[] values, bool series,
        Dictionary<DateOnly, (int Series, int Movies)> result)
    {
        foreach (var value in values)
        {
            if (value.Count < 0 || !DateOnly.TryParseExact(value.Date, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return false;
            var current = result.GetValueOrDefault(date);
            result[date] = series ? (value.Count, current.Movies) : (current.Series, value.Count);
        }
        return true;
    }

    internal static ProviderCallResult<T>? Failure<T>(ProviderTransportResponse response) =>
        response.StatusCode == 200 ? null : ProviderCallResult<T>.Failed(response.StatusCode switch
        {
            401 => ProviderErrorCodes.Unauthorized,
            403 => ProviderErrorCodes.Forbidden,
            404 => ProviderErrorCodes.NotFound,
            409 => ProviderErrorCodes.UpstreamConflict,
            429 => ProviderErrorCodes.RateLimited,
            _ => ProviderErrorCodes.Unknown,
        }, response.RateLimit, response.StatusCode);

    internal static ProviderCallResult<T> Invalid<T>(ProviderTransportResponse response) =>
        ProviderCallResult<T>.Failed(ProviderErrorCodes.InvalidResponse, response.RateLimit, response.StatusCode);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string NormalizeProtocol(string? value) => value?.ToLowerInvariant() is "usenet" or "torrent"
        ? value.ToLowerInvariant() : "unknown";
    private static string NormalizeEvent(string? value, bool successful)
    {
        var normalized = value?.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized is "releasegrabbed" or "indexerquery" or "indexerrss" or "indexerauth" or "indexerinfo"
            ? successful ? normalized : $"{normalized}failed"
            : "unknown";
    }

    private sealed class ProwlarrIndexer
    {
        public int Id { get; init; }
        public string? Name { get; init; }
        public bool Enable { get; init; }
        public bool SupportsRss { get; init; }
        public bool SupportsSearch { get; init; }
        public string? Protocol { get; init; }
        public int Priority { get; init; }
        public ProwlarrIndexerStatus? Status { get; init; }
    }
    private sealed class ProwlarrIndexerStatus { public DateTimeOffset? DisabledTill { get; init; } }
    private sealed class ProwlarrHistoryPage
    {
        public int TotalRecords { get; init; }
        public ProwlarrHistory[]? Records { get; init; }
    }
    private sealed class ProwlarrHistory
    {
        public int Id { get; init; }
        public int IndexerId { get; init; }
        public DateTimeOffset Date { get; init; }
        public string? DownloadId { get; init; }
        public bool Successful { get; init; }
        public string? EventType { get; init; }
    }
    private sealed class BazarrStats
    {
        public BazarrStatsDay[]? Series { get; init; }
        public BazarrStatsDay[]? Movies { get; init; }
    }
    private sealed class BazarrStatsDay
    {
        public string? Date { get; init; }
        public int Count { get; init; }
    }
}
