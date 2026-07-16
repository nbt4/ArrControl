using System.Text.Json;
using System.Text.Json.Serialization;
using ArrControl.Application.Connections;
using ArrControl.Application.Providers;

namespace ArrControl.Infrastructure.Providers;

public sealed class PlexClient(IProviderHttpTransport transport, TimeProvider? timeProvider = null)
    : IProviderMediaServerClient, IProviderCredentialContract
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    public string Kind => "plex";
    public IReadOnlyList<string> RequiredCredentialPurposes => [CredentialPurposes.ApiKey];

    public async Task<ProviderCallResult<ProviderSystemStatus>> GetSystemStatusAsync(
        ProviderConnection connection, CancellationToken cancellationToken)
    {
        if (!TryHeaders(connection, out var headers))
            return ProviderCallResult<ProviderSystemStatus>.Failed(ProviderErrorCodes.CredentialMissing);
        using var response = await transport.SendAsync(connection,
            new ProviderHttpRequest(HttpMethod.Get, "identity", headers: headers), cancellationToken);
        var failure = SupportingProviderReader.Failure<ProviderSystemStatus>(response);
        if (failure is not null) return failure;
        try
        {
            var identity = JsonSerializer.Deserialize<PlexEnvelope<PlexIdentity>>(response.Body, JsonOptions)?.MediaContainer;
            var semantic = identity?.Version?.Split('-', 2)[0];
            if (!Version.TryParse(semantic, out var version) || string.IsNullOrWhiteSpace(identity?.MachineIdentifier))
                return SupportingProviderReader.Invalid<ProviderSystemStatus>(response);
            if (version.Major != 1 || version.Minor is < 41 or > 43)
                return ProviderCallResult<ProviderSystemStatus>.Failed(
                    ProviderErrorCodes.UnsupportedVersion, response.RateLimit, response.StatusCode);
            return ProviderCallResult<ProviderSystemStatus>.Succeeded(
                new ProviderSystemStatus("Plex Media Server", identity.Version!, null, null),
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
        using var librariesResponse = await transport.SendAsync(connection,
            new ProviderHttpRequest(HttpMethod.Get, "library/sections", headers: headers), cancellationToken);
        var failure = SupportingProviderReader.Failure<ProviderMediaServerSnapshot>(librariesResponse);
        if (failure is not null) return failure;
        using var sessionsResponse = await transport.SendAsync(connection,
            new ProviderHttpRequest(HttpMethod.Get, "status/sessions", headers: headers), cancellationToken);
        failure = SupportingProviderReader.Failure<ProviderMediaServerSnapshot>(sessionsResponse);
        if (failure is not null) return failure;
        try
        {
            var libraryContainer = JsonSerializer.Deserialize<PlexEnvelope<PlexLibraries>>(
                librariesResponse.Body, JsonOptions)?.MediaContainer;
            var sessionContainer = JsonSerializer.Deserialize<PlexEnvelope<PlexSessions>>(
                sessionsResponse.Body, JsonOptions)?.MediaContainer;
            var directories = libraryContainer?.Directory ?? [];
            var sessions = sessionContainer?.Metadata ?? [];
            if (libraryContainer is null || sessionContainer is null
                || libraryContainer.Size != directories.Length || sessionContainer.Size != sessions.Length
                || directories.Length > 10_000 || sessions.Length > 1_000
                || directories.Any(value => string.IsNullOrWhiteSpace(value.Key)
                    || string.IsNullOrWhiteSpace(value.Type)))
                return SupportingProviderReader.Invalid<ProviderMediaServerSnapshot>(sessionsResponse);
            var libraries = new ProviderMediaLibrarySummary(
                directories.Length,
                Count(directories, "movie"),
                Count(directories, "show"),
                Count(directories, "artist"),
                Count(directories, "photo"),
                directories.Count(value => value.Type?.ToLowerInvariant() is not "movie" and not "show" and not "artist" and not "photo"),
                null);
            var playback = Playback(sessions);
            return ProviderCallResult<ProviderMediaServerSnapshot>.Succeeded(
                new ProviderMediaServerSnapshot((timeProvider ?? TimeProvider.System).GetUtcNow(), libraries, playback),
                sessionsResponse.RateLimit ?? librariesResponse.RateLimit, 200);
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
        headers = new Dictionary<string, string>
        {
            ["X-Plex-Token"] = token,
            ["X-Plex-Client-Identifier"] = $"arrcontrol-{connection.InstanceId:N}",
            ["X-Plex-Product"] = "ArrControl",
            ["X-Plex-Version"] = "1.0",
            ["X-Plex-Pms-Api-Version"] = "1.0.0",
        };
        return true;
    }

    private static int Count(IEnumerable<PlexDirectory> values, string type) =>
        values.Count(value => string.Equals(value.Type, type, StringComparison.OrdinalIgnoreCase));

    private static ProviderPlaybackSummary Playback(PlexSession[] values)
    {
        var playing = 0;
        var paused = 0;
        var buffering = 0;
        var transcode = 0;
        var directStream = 0;
        var directPlay = 0;
        var unknown = 0;
        foreach (var value in values)
        {
            switch (value.Player?.State?.ToLowerInvariant())
            {
                case "playing": playing++; break;
                case "paused": paused++; break;
                case "buffering": buffering++; break;
            }
            var decisions = new[] { value.TranscodeSession?.VideoDecision, value.TranscodeSession?.AudioDecision }
                .Concat(value.Media?.SelectMany(media => media.Part ?? [])
                    .Select(part => part.Decision) ?? []);
            if (decisions.Any(decision => string.Equals(decision, "transcode", StringComparison.OrdinalIgnoreCase)))
                transcode++;
            else if (decisions.Any(decision => string.Equals(decision, "copy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(decision, "directstream", StringComparison.OrdinalIgnoreCase)))
                directStream++;
            else if (decisions.Any(decision => string.Equals(decision, "directplay", StringComparison.OrdinalIgnoreCase)))
                directPlay++;
            else
                unknown++;
        }
        return new ProviderPlaybackSummary(values.Length, playing, paused, buffering,
            transcode, directStream, directPlay, unknown);
    }

    private sealed class PlexEnvelope<T> { public T? MediaContainer { get; init; } }
    private sealed class PlexIdentity { public string? Version { get; init; } public string? MachineIdentifier { get; init; } }
    private sealed class PlexLibraries
    {
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int Size { get; init; }
        public PlexDirectory[]? Directory { get; init; }
    }
    private sealed class PlexDirectory { public string? Key { get; init; } public string? Type { get; init; } }
    private sealed class PlexSessions
    {
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int Size { get; init; }
        public PlexSession[]? Metadata { get; init; }
    }
    private sealed class PlexSession
    {
        public PlexPlayer? Player { get; init; }
        public PlexTranscode? TranscodeSession { get; init; }
        public PlexMedia[]? Media { get; init; }
    }
    private sealed class PlexPlayer { public string? State { get; init; } }
    private sealed class PlexTranscode { public string? VideoDecision { get; init; } public string? AudioDecision { get; init; } }
    private sealed class PlexMedia { public PlexPart[]? Part { get; init; } }
    private sealed class PlexPart { public string? Decision { get; init; } }
}
