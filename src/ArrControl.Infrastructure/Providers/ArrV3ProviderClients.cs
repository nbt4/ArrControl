using System.Text.Json;
using ArrControl.Application.Catalog;
using ArrControl.Application.Activity;
using ArrControl.Application.Search;
using ArrControl.Application.Providers;

namespace ArrControl.Infrastructure.Providers;

public sealed class SonarrClient(IProviderApiTransport transport, TimeProvider? timeProvider = null)
    : ArrV3ProviderClient(transport, "sonarr", "Sonarr", "v3", new HashSet<int> { 4 }),
        IProviderCatalogClient
        , IProviderActivityClient, IProviderSearchClient
{
    public Task<ProviderCallResult<ProviderCatalogSnapshot>> GetCatalogAsync(
        ProviderConnection connection,
        CancellationToken cancellationToken) =>
        ArrCatalogReader.ReadSonarrAsync(
            Transport,
            connection,
            timeProvider ?? TimeProvider.System,
            cancellationToken);

    public Task<ProviderCallResult<ProviderActivitySnapshot>> GetActivityAsync(
        ProviderConnection connection,
        CancellationToken cancellationToken) =>
        ArrActivityReader.ReadAsync(
            Transport,
            connection,
            "sonarr",
            "v3",
            timeProvider ?? TimeProvider.System,
            cancellationToken);

    public Task<ProviderCallResult<ProviderSearchResult>> SearchAsync(
        ProviderConnection connection,
        IReadOnlyList<string> providerKeys,
        CancellationToken cancellationToken) =>
        ArrSearchCommander.SearchAsync(Transport, connection, "sonarr", "v3", providerKeys, cancellationToken);
}

public sealed class RadarrClient(IProviderApiTransport transport, TimeProvider? timeProvider = null)
    : ArrV3ProviderClient(transport, "radarr", "Radarr", "v3", new HashSet<int> { 5, 6 }),
        IProviderCatalogClient
        , IProviderActivityClient, IProviderSearchClient
{
    public Task<ProviderCallResult<ProviderCatalogSnapshot>> GetCatalogAsync(
        ProviderConnection connection,
        CancellationToken cancellationToken) =>
        ArrCatalogReader.ReadRadarrAsync(
            Transport,
            connection,
            timeProvider ?? TimeProvider.System,
            cancellationToken);

    public Task<ProviderCallResult<ProviderActivitySnapshot>> GetActivityAsync(
        ProviderConnection connection,
        CancellationToken cancellationToken) =>
        ArrActivityReader.ReadAsync(
            Transport,
            connection,
            "radarr",
            "v3",
            timeProvider ?? TimeProvider.System,
            cancellationToken);

    public Task<ProviderCallResult<ProviderSearchResult>> SearchAsync(
        ProviderConnection connection,
        IReadOnlyList<string> providerKeys,
        CancellationToken cancellationToken) =>
        ArrSearchCommander.SearchAsync(Transport, connection, "radarr", "v3", providerKeys, cancellationToken);
}

public sealed class LidarrClient(IProviderApiTransport transport, TimeProvider? timeProvider = null)
    : ArrV3ProviderClient(transport, "lidarr", "Lidarr", "v1", new HashSet<int> { 2, 3 }),
        IProviderCatalogClient, IProviderActivityClient, IProviderSearchClient
{
    public Task<ProviderCallResult<ProviderCatalogSnapshot>> GetCatalogAsync(
        ProviderConnection connection, CancellationToken cancellationToken) =>
        ArrCatalogReader.ReadLidarrAsync(
            Transport, connection, timeProvider ?? TimeProvider.System, cancellationToken);

    public Task<ProviderCallResult<ProviderActivitySnapshot>> GetActivityAsync(
        ProviderConnection connection, CancellationToken cancellationToken) =>
        ArrActivityReader.ReadAsync(
            Transport, connection, "lidarr", "v1", timeProvider ?? TimeProvider.System, cancellationToken);

    public Task<ProviderCallResult<ProviderSearchResult>> SearchAsync(
        ProviderConnection connection, IReadOnlyList<string> providerKeys, CancellationToken cancellationToken) =>
        ArrSearchCommander.SearchAsync(
            Transport, connection, "lidarr", "v1", providerKeys, cancellationToken);
}

public sealed class ReadarrClient(IProviderApiTransport transport, TimeProvider? timeProvider = null)
    : ArrV3ProviderClient(transport, "readarr", "Readarr", "v1", new HashSet<int> { 0 }),
        IProviderCatalogClient, IProviderActivityClient, IProviderSearchClient
{
    public Task<ProviderCallResult<ProviderCatalogSnapshot>> GetCatalogAsync(
        ProviderConnection connection, CancellationToken cancellationToken) =>
        ArrCatalogReader.ReadReadarrAsync(
            Transport, connection, timeProvider ?? TimeProvider.System, cancellationToken);

    public Task<ProviderCallResult<ProviderActivitySnapshot>> GetActivityAsync(
        ProviderConnection connection, CancellationToken cancellationToken) =>
        ArrActivityReader.ReadAsync(
            Transport, connection, "readarr", "v1", timeProvider ?? TimeProvider.System, cancellationToken);

    public Task<ProviderCallResult<ProviderSearchResult>> SearchAsync(
        ProviderConnection connection, IReadOnlyList<string> providerKeys, CancellationToken cancellationToken) =>
        ArrSearchCommander.SearchAsync(
            Transport, connection, "readarr", "v1", providerKeys, cancellationToken);
}

public sealed class WhisparrClient(IProviderApiTransport transport, TimeProvider? timeProvider = null)
    : ArrV3ProviderClient(transport, "whisparr", "Whisparr", "v3", new HashSet<int> { 2, 3 }),
        IProviderCatalogClient, IProviderActivityClient, IProviderSearchClient
{
    public Task<ProviderCallResult<ProviderCatalogSnapshot>> GetCatalogAsync(
        ProviderConnection connection, CancellationToken cancellationToken) =>
        ArrCatalogReader.ReadWhisparrAsync(
            Transport, connection, timeProvider ?? TimeProvider.System, cancellationToken);

    public Task<ProviderCallResult<ProviderActivitySnapshot>> GetActivityAsync(
        ProviderConnection connection, CancellationToken cancellationToken) =>
        ArrActivityReader.ReadAsync(
            Transport, connection, "whisparr", "v3", timeProvider ?? TimeProvider.System, cancellationToken);

    public Task<ProviderCallResult<ProviderSearchResult>> SearchAsync(
        ProviderConnection connection, IReadOnlyList<string> providerKeys, CancellationToken cancellationToken) =>
        ArrSearchCommander.SearchAsync(
            Transport, connection, "whisparr", "v3", providerKeys, cancellationToken);
}

public abstract class ArrV3ProviderClient(
    IProviderApiTransport transport,
    string kind,
    string expectedAppName,
    string apiVersion,
    IReadOnlySet<int> supportedMajorVersions) : IArrProviderClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
    };

    public string Kind { get; } = kind;

    protected IProviderApiTransport Transport { get; } = transport;

    public async Task<ProviderCallResult<ProviderSystemStatus>> GetSystemStatusAsync(
        ProviderConnection connection,
        CancellationToken cancellationToken)
    {
        using var response = await Transport.GetAsync(
            connection,
            $"api/{apiVersion}/system/status",
            cancellationToken);
        var failure = MapFailure<ProviderSystemStatus>(response);
        if (failure is not null)
        {
            return failure;
        }

        try
        {
            var resource = JsonSerializer.Deserialize<SystemResource>(response.Body, JsonOptions);
            if (resource is null
                || string.IsNullOrWhiteSpace(resource.AppName)
                || !string.Equals(
                    resource.AppName,
                    expectedAppName,
                    StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(resource.Version))
            {
                return ProviderCallResult<ProviderSystemStatus>.Failed(
                    ProviderErrorCodes.InvalidResponse,
                    response.RateLimit,
                    response.StatusCode);
            }

            if (!Version.TryParse(resource.Version, out var version)
                || !supportedMajorVersions.Contains(version.Major))
            {
                return ProviderCallResult<ProviderSystemStatus>.Failed(
                    ProviderErrorCodes.UnsupportedVersion,
                    response.RateLimit,
                    response.StatusCode);
            }

            return ProviderCallResult<ProviderSystemStatus>.Succeeded(
                new ProviderSystemStatus(
                    resource.AppName,
                    resource.Version,
                    resource.InstanceName,
                    resource.Branch),
                response.RateLimit,
                response.StatusCode);
        }
        catch (JsonException)
        {
            return ProviderCallResult<ProviderSystemStatus>.Failed(
                ProviderErrorCodes.InvalidResponse,
                response.RateLimit,
                response.StatusCode);
        }
    }

    public async Task<ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>> GetHealthAsync(
        ProviderConnection connection,
        CancellationToken cancellationToken)
    {
        using var response = await Transport.GetAsync(
            connection,
            $"api/{apiVersion}/health",
            cancellationToken);
        var failure = MapFailure<IReadOnlyList<ProviderHealthIssue>>(response);
        if (failure is not null)
        {
            return failure;
        }

        try
        {
            var resources = JsonSerializer.Deserialize<HealthResource[]>(response.Body, JsonOptions);
            if (resources is null
                || resources.Any(resource => string.IsNullOrWhiteSpace(resource.Source)))
            {
                return ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>.Failed(
                    ProviderErrorCodes.InvalidResponse,
                    response.RateLimit,
                    response.StatusCode);
            }

            var issues = resources
                .Select(resource => new ProviderHealthIssue(
                    resource.Id,
                    resource.Source!,
                    NormalizeSeverity(resource.Type),
                    resource.Message,
                    NormalizeWikiUrl(resource.WikiUrl)))
                .ToArray();
            return ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>.Succeeded(
                issues,
                response.RateLimit,
                response.StatusCode);
        }
        catch (JsonException)
        {
            return ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>.Failed(
                ProviderErrorCodes.InvalidResponse,
                response.RateLimit,
                response.StatusCode);
        }
    }

    private static ProviderCallResult<T>? MapFailure<T>(ProviderTransportResponse response)
    {
        var errorCode = response.StatusCode switch
        {
            200 => null,
            401 => ProviderErrorCodes.Unauthorized,
            403 => ProviderErrorCodes.Forbidden,
            404 => ProviderErrorCodes.NotFound,
            409 => ProviderErrorCodes.UpstreamConflict,
            429 => ProviderErrorCodes.RateLimited,
            _ => ProviderErrorCodes.Unknown,
        };
        return errorCode is null
            ? null
            : ProviderCallResult<T>.Failed(
                errorCode,
                response.RateLimit,
                response.StatusCode);
    }

    private static string NormalizeSeverity(string? severity) =>
        severity?.ToLowerInvariant() switch
        {
            "ok" => "ok",
            "notice" => "notice",
            "warning" => "warning",
            "error" => "error",
            _ => "unknown",
        };

    private static Uri? NormalizeWikiUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
            ? uri
            : null;

    private sealed class SystemResource
    {
        public string? AppName { get; init; }

        public string? InstanceName { get; init; }

        public string? Version { get; init; }

        public string? Branch { get; init; }
    }

    private sealed class HealthResource
    {
        public int Id { get; init; }

        public string? Source { get; init; }

        public string? Type { get; init; }

        public string? Message { get; init; }

        public string? WikiUrl { get; init; }
    }
}
