using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArrControl.Application.Connections;
using ArrControl.Application.Providers;

namespace ArrControl.Infrastructure.Providers;

public sealed class OverseerrClient(IProviderHttpTransport transport, TimeProvider? timeProvider = null)
    : SeerrLikeRequestClient(transport, timeProvider, "overseerr", "Overseerr", 1, 34, 35);

public sealed class JellyseerrClient(IProviderHttpTransport transport, TimeProvider? timeProvider = null)
    : SeerrLikeRequestClient(transport, timeProvider, "jellyseerr", "Jellyseerr/Seerr", 2, 7, 3, 3);

public abstract class SeerrLikeRequestClient : IProviderRequestClient, IProviderCredentialContract
{
    private const int MaximumRecords = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    private readonly IProviderHttpTransport transport;
    private readonly TimeProvider timeProvider;
    private readonly string product;
    private readonly IReadOnlyList<(int Major, int MinimumMinor, int MaximumMinor)> supportedVersions;

    protected SeerrLikeRequestClient(
        IProviderHttpTransport transport,
        TimeProvider? timeProvider,
        string kind,
        string product,
        int supportedMajor,
        int minimumMinor,
        int maximumMinor)
        : this(transport, timeProvider, kind, product,
            [(supportedMajor, minimumMinor, maximumMinor)]) { }

    protected SeerrLikeRequestClient(
        IProviderHttpTransport transport,
        TimeProvider? timeProvider,
        string kind,
        string product,
        int firstMajor,
        int firstMinimumMinor,
        int secondMajor,
        int secondMaximumMinor)
        : this(transport, timeProvider, kind, product,
            [(firstMajor, firstMinimumMinor, int.MaxValue), (secondMajor, 0, secondMaximumMinor)]) { }

    private SeerrLikeRequestClient(
        IProviderHttpTransport transport,
        TimeProvider? timeProvider,
        string kind,
        string product,
        IReadOnlyList<(int Major, int MinimumMinor, int MaximumMinor)> supportedVersions)
    {
        this.transport = transport;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.product = product;
        this.supportedVersions = supportedVersions;
        Kind = kind;
    }

    public string Kind { get; }
    public IReadOnlyList<string> RequiredCredentialPurposes => [CredentialPurposes.ApiKey];

    public async Task<ProviderCallResult<ProviderSystemStatus>> GetSystemStatusAsync(
        ProviderConnection connection, CancellationToken cancellationToken)
    {
        if (!TryHeaders(connection, out var headers))
            return ProviderCallResult<ProviderSystemStatus>.Failed(ProviderErrorCodes.CredentialMissing);
        using var response = await transport.SendAsync(connection,
            new ProviderHttpRequest(HttpMethod.Get, "api/v1/status", headers: headers), cancellationToken);
        var failure = SupportingProviderReader.Failure<ProviderSystemStatus>(response);
        if (failure is not null) return failure;
        try
        {
            var status = JsonSerializer.Deserialize<SeerrStatus>(response.Body, JsonOptions);
            var versionText = status?.Version?.Trim().TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(versionText) || !Version.TryParse(versionText, out var version))
                return SupportingProviderReader.Invalid<ProviderSystemStatus>(response);
            if (!supportedVersions.Any(value => version.Major == value.Major
                    && version.Minor >= value.MinimumMinor && version.Minor <= value.MaximumMinor))
                return ProviderCallResult<ProviderSystemStatus>.Failed(
                    ProviderErrorCodes.UnsupportedVersion, response.RateLimit, response.StatusCode);
            return ProviderCallResult<ProviderSystemStatus>.Succeeded(
                new ProviderSystemStatus(product, status!.Version!.Trim(), null, null),
                response.RateLimit, response.StatusCode);
        }
        catch (JsonException)
        {
            return SupportingProviderReader.Invalid<ProviderSystemStatus>(response);
        }
    }

    public async Task<ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>> GetHealthAsync(
        ProviderConnection connection, CancellationToken cancellationToken)
    {
        var requests = await GetRequestsAsync(connection, cancellationToken);
        return requests.Success
            ? ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>.Succeeded([], requests.RateLimit, 200)
            : ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>.Failed(
                requests.ErrorCode!, requests.RateLimit, requests.HttpStatusCode);
    }

    public async Task<ProviderCallResult<ProviderRequestSnapshot>> GetRequestsAsync(
        ProviderConnection connection, CancellationToken cancellationToken)
    {
        if (!TryHeaders(connection, out var headers))
            return ProviderCallResult<ProviderRequestSnapshot>.Failed(ProviderErrorCodes.CredentialMissing);
        using var response = await transport.SendAsync(connection, new ProviderHttpRequest(
            HttpMethod.Get, "api/v1/request",
            new Dictionary<string, string>
            {
                ["skip"] = "0",
                ["take"] = MaximumRecords.ToString(CultureInfo.InvariantCulture),
            }, headers), cancellationToken);
        var failure = SupportingProviderReader.Failure<ProviderRequestSnapshot>(response);
        if (failure is not null) return failure;
        try
        {
            var page = JsonSerializer.Deserialize<SeerrRequestPage>(response.Body, JsonOptions);
            if (page?.PageInfo is null || page.Results is null
                || page.PageInfo.Results < 0 || page.PageInfo.Results > MaximumRecords
                || page.Results.Length != page.PageInfo.Results
                || page.Results.Any(value => !value.Valid()))
                return SupportingProviderReader.Invalid<ProviderRequestSnapshot>(response);
            var requests = page.Results.Select(value => new ProviderMediaRequest(
                $"{Kind}:{value.Id}", NormalizeMediaType(value.Type ?? value.Media!.MediaType),
                NormalizeSeerrStatus(value.Status), value.CreatedAt,
                Positive(value.Media!.TmdbId), Positive(value.Media.TvdbId), Clean(value.Media.ImdbId))).ToArray();
            return ProviderCallResult<ProviderRequestSnapshot>.Succeeded(
                new ProviderRequestSnapshot(timeProvider.GetUtcNow(), requests),
                response.RateLimit, response.StatusCode);
        }
        catch (JsonException)
        {
            return SupportingProviderReader.Invalid<ProviderRequestSnapshot>(response);
        }
    }

    private static bool TryHeaders(ProviderConnection connection, out IReadOnlyDictionary<string, string> headers)
    {
        headers = new Dictionary<string, string>();
        if (!connection.TryGetCredential(CredentialPurposes.ApiKey, out var token)
            || string.IsNullOrWhiteSpace(token)) return false;
        headers = new Dictionary<string, string> { ["X-API-Key"] = token };
        return true;
    }

    private static string NormalizeSeerrStatus(int status) => status switch
    {
        1 => "pending", 2 => "approved", 3 => "declined", 4 => "failed", 5 => "completed", _ => "unknown",
    };
    private static string NormalizeMediaType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "movie" => "movie", "tv" => "series", _ => "unknown",
    };
    private static string? Positive(int? value) => value > 0 ? value.Value.ToString(CultureInfo.InvariantCulture) : null;
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class SeerrStatus { public string? Version { get; init; } }
    private sealed class SeerrRequestPage
    {
        public SeerrPageInfo? PageInfo { get; init; }
        public SeerrRequest[]? Results { get; init; }
    }
    private sealed class SeerrPageInfo { public int Results { get; init; } }
    private sealed class SeerrRequest
    {
        public int Id { get; init; }
        public int Status { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public string? Type { get; init; }
        public SeerrMedia? Media { get; init; }
        public bool Valid() => Id > 0 && CreatedAt != default && Media is not null
            && Media.TmdbId > 0 && NormalizeMediaType(Type ?? Media.MediaType) != "unknown";
    }
    private sealed class SeerrMedia
    {
        public string? MediaType { get; init; }
        public int TmdbId { get; init; }
        public int? TvdbId { get; init; }
        public string? ImdbId { get; init; }
    }
}

public sealed class OmbiClient(IProviderHttpTransport transport, TimeProvider? timeProvider = null)
    : IProviderRequestClient, IProviderCredentialContract
{
    private const int MaximumRecords = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 64 };
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public string Kind => "ombi";
    public IReadOnlyList<string> RequiredCredentialPurposes => [CredentialPurposes.ApiKey];

    public async Task<ProviderCallResult<ProviderSystemStatus>> GetSystemStatusAsync(
        ProviderConnection connection, CancellationToken cancellationToken)
    {
        if (!TryHeaders(connection, out var headers))
            return ProviderCallResult<ProviderSystemStatus>.Failed(ProviderErrorCodes.CredentialMissing);
        using var response = await transport.SendAsync(connection,
            new ProviderHttpRequest(HttpMethod.Get, "api/v1/Status/info", headers: headers), cancellationToken);
        var failure = SupportingProviderReader.Failure<ProviderSystemStatus>(response);
        if (failure is not null) return failure;
        try
        {
            var raw = JsonSerializer.Deserialize<string>(response.Body, JsonOptions)?.Trim();
            var versionText = raw?.TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(raw) || !Version.TryParse(versionText, out var version))
                return SupportingProviderReader.Invalid<ProviderSystemStatus>(response);
            if (version.Major != 4 || version.Minor is < 47 or > 48)
                return ProviderCallResult<ProviderSystemStatus>.Failed(
                    ProviderErrorCodes.UnsupportedVersion, response.RateLimit, response.StatusCode);
            return ProviderCallResult<ProviderSystemStatus>.Succeeded(
                new ProviderSystemStatus("Ombi", raw, null, null), response.RateLimit, response.StatusCode);
        }
        catch (JsonException)
        {
            return SupportingProviderReader.Invalid<ProviderSystemStatus>(response);
        }
    }

    public async Task<ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>> GetHealthAsync(
        ProviderConnection connection, CancellationToken cancellationToken)
    {
        var requests = await GetRequestsAsync(connection, cancellationToken);
        return requests.Success
            ? ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>.Succeeded([], requests.RateLimit, 200)
            : ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>.Failed(
                requests.ErrorCode!, requests.RateLimit, requests.HttpStatusCode);
    }

    public async Task<ProviderCallResult<ProviderRequestSnapshot>> GetRequestsAsync(
        ProviderConnection connection, CancellationToken cancellationToken)
    {
        if (!TryHeaders(connection, out var headers))
            return ProviderCallResult<ProviderRequestSnapshot>.Failed(ProviderErrorCodes.CredentialMissing);
        using var movieResponse = await transport.SendAsync(connection,
            new ProviderHttpRequest(HttpMethod.Get, "api/v1/Request/movie", headers: headers), cancellationToken);
        var failure = SupportingProviderReader.Failure<ProviderRequestSnapshot>(movieResponse);
        if (failure is not null) return failure;
        using var tvResponse = await transport.SendAsync(connection,
            new ProviderHttpRequest(HttpMethod.Get, "api/v1/Request/tvlite", headers: headers), cancellationToken);
        failure = SupportingProviderReader.Failure<ProviderRequestSnapshot>(tvResponse);
        if (failure is not null) return failure;
        try
        {
            var movies = JsonSerializer.Deserialize<OmbiMovie[]>(movieResponse.Body, JsonOptions);
            var series = JsonSerializer.Deserialize<OmbiSeries[]>(tvResponse.Body, JsonOptions);
            var childCount = series?.Sum(value => value.ChildRequests?.Length ?? 0) ?? 0;
            if (movies is null || series is null || movies.Length + childCount > MaximumRecords
                || movies.Any(value => !value.Valid()) || series.Any(value => !value.Valid()))
                return SupportingProviderReader.Invalid<ProviderRequestSnapshot>(tvResponse);
            var requests = movies.Select(value => new ProviderMediaRequest(
                    $"ombi:movie:{value.Id}", "movie", NormalizeOmbiStatus(value), value.RequestedDate,
                    Positive(value.TheMovieDbId), null, Clean(value.ImdbId)))
                .Concat(series.SelectMany(parent => parent.ChildRequests!.Select(value => new ProviderMediaRequest(
                    $"ombi:series:{value.Id}", "series", NormalizeOmbiStatus(value), value.RequestedDate,
                    Positive(parent.ExternalProviderId), Positive(parent.TvDbId), Clean(parent.ImdbId)))))
                .OrderByDescending(value => value.RequestedAt).ThenBy(value => value.ProviderKey, StringComparer.Ordinal)
                .ToArray();
            return ProviderCallResult<ProviderRequestSnapshot>.Succeeded(
                new ProviderRequestSnapshot(clock.GetUtcNow(), requests),
                tvResponse.RateLimit ?? movieResponse.RateLimit, tvResponse.StatusCode);
        }
        catch (JsonException)
        {
            return SupportingProviderReader.Invalid<ProviderRequestSnapshot>(tvResponse);
        }
    }

    private static bool TryHeaders(ProviderConnection connection, out IReadOnlyDictionary<string, string> headers)
    {
        headers = new Dictionary<string, string>();
        if (!connection.TryGetCredential(CredentialPurposes.ApiKey, out var token)
            || string.IsNullOrWhiteSpace(token)) return false;
        headers = new Dictionary<string, string> { ["ApiKey"] = token };
        return true;
    }

    private static string NormalizeOmbiStatus(OmbiRequest value) => value.Available ? "completed"
        : value.Denied == true ? "declined" : value.Approved ? "approved" : "pending";
    private static string? Positive(int value) => value > 0 ? value.ToString(CultureInfo.InvariantCulture) : null;
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private abstract class OmbiRequest
    {
        public int Id { get; init; }
        public bool Approved { get; init; }
        public DateTimeOffset RequestedDate { get; init; }
        public bool Available { get; init; }
        public bool? Denied { get; init; }
        public bool Valid() => Id > 0 && RequestedDate != default;
    }
    private sealed class OmbiMovie : OmbiRequest
    {
        public int TheMovieDbId { get; init; }
        public string? ImdbId { get; init; }
        public new bool Valid() => base.Valid() && TheMovieDbId > 0;
    }
    private sealed class OmbiSeries
    {
        public int TvDbId { get; init; }
        public int ExternalProviderId { get; init; }
        public string? ImdbId { get; init; }
        public OmbiChild[]? ChildRequests { get; init; }
        public bool Valid() => ExternalProviderId > 0 && ChildRequests is not null
            && ChildRequests.All(value => value.Valid());
    }
    private sealed class OmbiChild : OmbiRequest { }
}
