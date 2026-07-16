using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArrControl.Application.Activity;
using ArrControl.Application.Connections;
using ArrControl.Application.Providers;

namespace ArrControl.Infrastructure.Providers;

public sealed class SabnzbdClient(IProviderHttpTransport transport, TimeProvider? timeProvider = null)
    : IProviderDownloadClient, IProviderCredentialContract
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 24 };
    public string Kind => "sabnzbd";
    public bool SupportsRetry => true;
    public IReadOnlyList<string> RequiredCredentialPurposes => [CredentialPurposes.ApiKey];

    public async Task<ProviderCallResult<ProviderSystemStatus>> GetSystemStatusAsync(
        ProviderConnection connection, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(connection,
            new Dictionary<string, string> { ["mode"] = "version", ["output"] = "json" }, cancellationToken);
        var failure = SupportingProviderReader.Failure<ProviderSystemStatus>(response);
        if (failure is not null) return failure;
        try
        {
            var value = JsonSerializer.Deserialize<SabVersion>(response.Body, JsonOptions)?.Version;
            if (!Version.TryParse(value, out var version)) return Invalid<ProviderSystemStatus>(response);
            if (version.Major is not 4 and not 5) return ProviderCallResult<ProviderSystemStatus>.Failed(
                ProviderErrorCodes.UnsupportedVersion, response.RateLimit, response.StatusCode);
            return ProviderCallResult<ProviderSystemStatus>.Succeeded(
                new ProviderSystemStatus("SABnzbd", value!, null, null), response.RateLimit, 200);
        }
        catch (JsonException) { return Invalid<ProviderSystemStatus>(response); }
    }

    public async Task<ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>> GetHealthAsync(
        ProviderConnection connection, CancellationToken cancellationToken)
    {
        using var response = await SendAuthenticatedAsync(connection,
            new Dictionary<string, string> { ["mode"] = "queue", ["limit"] = "0", ["output"] = "json" },
            cancellationToken);
        var failure = SupportingProviderReader.Failure<IReadOnlyList<ProviderHealthIssue>>(response);
        if (failure is not null) return failure;
        try
        {
            var value = JsonSerializer.Deserialize<SabQueueEnvelope>(response.Body, JsonOptions);
            return value?.Queue is null
                ? Invalid<IReadOnlyList<ProviderHealthIssue>>(response)
                : ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>.Succeeded([], response.RateLimit, 200);
        }
        catch (JsonException) { return Invalid<IReadOnlyList<ProviderHealthIssue>>(response); }
    }

    public async Task<ProviderCallResult<ProviderActivitySnapshot>> GetActivityAsync(
        ProviderConnection connection, CancellationToken cancellationToken)
    {
        using var queueResponse = await SendAuthenticatedAsync(connection,
            new Dictionary<string, string> { ["mode"] = "queue", ["limit"] = "10000", ["output"] = "json" },
            cancellationToken);
        var failure = SupportingProviderReader.Failure<ProviderActivitySnapshot>(queueResponse);
        if (failure is not null) return failure;
        using var historyResponse = await SendAuthenticatedAsync(connection,
            new Dictionary<string, string> { ["mode"] = "history", ["limit"] = "10000", ["output"] = "json" },
            cancellationToken);
        failure = SupportingProviderReader.Failure<ProviderActivitySnapshot>(historyResponse);
        if (failure is not null) return failure;
        try
        {
            var queue = JsonSerializer.Deserialize<SabQueueEnvelope>(queueResponse.Body, JsonOptions)?.Queue;
            var history = JsonSerializer.Deserialize<SabHistoryEnvelope>(historyResponse.Body, JsonOptions)?.History;
            if (queue?.Slots is null || history?.Slots is null || queue.Slots.Length > 10_000
                || history.Slots.Length > 10_000 || queue.Slots.Any(value => !Valid(value.NzoId, value.Filename))
                || history.Slots.Any(value => !Valid(value.NzoId, value.Name)
                    || value.Completed is <= 0 or > 253402300799))
                return Invalid<ProviderActivitySnapshot>(historyResponse);
            var queueItems = queue.Slots.Select(value => new QueueItemSnapshot(
                $"sabnzbd:{value.NzoId}", null, value.NzoId, value.Filename!,
                NormalizeQueueStatus(value.Status), "unknown", "unknown", "usenet",
                Mib(value.Mb), Mib(value.MbLeft), Unix(value.TimeAdded), null, "SABnzbd", null)).ToArray();
            var historyItems = history.Slots.Select(value => new HistoryItemSnapshot(
                $"sabnzbd:{value.NzoId}", null, value.NzoId, value.Name!,
                NormalizeHistoryStatus(value.Status), Unix(value.Completed)!.Value)).ToArray();
            return ProviderCallResult<ProviderActivitySnapshot>.Succeeded(
                new ProviderActivitySnapshot((timeProvider ?? TimeProvider.System).GetUtcNow(), queueItems, historyItems),
                historyResponse.RateLimit ?? queueResponse.RateLimit, 200);
        }
        catch (JsonException) { return Invalid<ProviderActivitySnapshot>(historyResponse); }
    }

    public Task<ProviderCallResult<bool>> SetPausedAsync(
        ProviderConnection connection, string providerKey, bool paused, CancellationToken cancellationToken) =>
        MutateAsync(connection, providerKey, "queue", paused ? "pause" : "resume", false, cancellationToken);

    public Task<ProviderCallResult<bool>> RemoveAsync(
        ProviderConnection connection, string providerKey, bool deleteData, CancellationToken cancellationToken) =>
        MutateAsync(connection, providerKey, "queue", "delete", deleteData, cancellationToken);

    public Task<ProviderCallResult<bool>> RetryAsync(
        ProviderConnection connection, string providerKey, CancellationToken cancellationToken) =>
        MutateAsync(connection, providerKey, "retry", null, false, cancellationToken);

    private async Task<ProviderCallResult<bool>> MutateAsync(
        ProviderConnection connection, string providerKey, string mode, string? name, bool deleteData,
        CancellationToken cancellationToken)
    {
        if (!TryId(providerKey, out var id)) return ProviderCallResult<bool>.Failed(ProviderErrorCodes.InvalidResponse);
        var query = new Dictionary<string, string>
        {
            ["mode"] = mode, ["value"] = id, ["output"] = "json",
        };
        if (name is not null) query["name"] = name;
        if (deleteData) query["del_files"] = "1";
        using var response = await SendAuthenticatedAsync(connection, query, cancellationToken);
        var failure = SupportingProviderReader.Failure<bool>(response);
        if (failure is not null) return failure;
        try
        {
            var result = JsonSerializer.Deserialize<SabMutation>(response.Body, JsonOptions);
            return result is null ? Invalid<bool>(response)
                : result.Status ? ProviderCallResult<bool>.Succeeded(true, response.RateLimit, 200)
                : ProviderCallResult<bool>.Failed(ProviderErrorCodes.UpstreamConflict, response.RateLimit, 200);
        }
        catch (JsonException) { return Invalid<bool>(response); }
    }

    private Task<ProviderTransportResponse> SendAuthenticatedAsync(
        ProviderConnection connection, Dictionary<string, string> query, CancellationToken token)
    {
        if (!connection.TryGetCredential(CredentialPurposes.ApiKey, out var key))
            throw new ProviderTransportException(ProviderErrorCodes.CredentialMissing);
        query["apikey"] = key;
        return SendAsync(connection, query, token);
    }

    private Task<ProviderTransportResponse> SendAsync(
        ProviderConnection connection, Dictionary<string, string> query, CancellationToken token) =>
        transport.SendAsync(connection, new ProviderHttpRequest(HttpMethod.Get, "api", query), token);

    private static bool TryId(string key, out string id)
    {
        const string prefix = "sabnzbd:";
        id = key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : string.Empty;
        return id.Length is > 0 and <= 200 && id.All(value => char.IsAsciiLetterOrDigit(value) || value is '_' or '-');
    }
    private static bool Valid(string? id, string? title) =>
        id is { Length: > 0 and <= 200 } && !string.IsNullOrWhiteSpace(title);
    private static long Mib(string? value)
    {
        if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            || result <= 0) return 0;
        return result >= long.MaxValue / 1048576m ? long.MaxValue : (long)(result * 1048576m);
    }
    private static DateTimeOffset? Unix(long value) => value > 0 ? DateTimeOffset.FromUnixTimeSeconds(value) : null;
    private static string NormalizeQueueStatus(string? value) => value?.ToLowerInvariant() switch
    { "downloading" => "downloading", "queued" => "queued", "paused" => "paused", "fetching" => "downloading", _ => "unknown" };
    private static string NormalizeHistoryStatus(string? value) => value?.ToLowerInvariant() switch
    { "completed" => "downloadfolderimported", "failed" => "downloadfailed", _ => "unknown" };
    private static ProviderCallResult<T> Invalid<T>(ProviderTransportResponse response) =>
        SupportingProviderReader.Invalid<T>(response);

    private sealed class SabVersion { public string? Version { get; init; } }
    private sealed class SabQueueEnvelope { public SabQueue? Queue { get; init; } }
    private sealed class SabQueue { public SabQueueSlot[]? Slots { get; init; } }
    private sealed class SabQueueSlot
    {
        [JsonPropertyName("nzo_id")]
        public string? NzoId { get; init; }
        public string? Filename { get; init; }
        public string? Status { get; init; }
        public string? Mb { get; init; }
        [JsonPropertyName("mbleft")]
        public string? MbLeft { get; init; }
        [JsonPropertyName("time_added")]
        public long TimeAdded { get; init; }
    }
    private sealed class SabHistoryEnvelope { public SabHistory? History { get; init; } }
    private sealed class SabHistory { public SabHistorySlot[]? Slots { get; init; } }
    private sealed class SabHistorySlot
    {
        [JsonPropertyName("nzo_id")]
        public string? NzoId { get; init; }
        public string? Name { get; init; }
        public string? Status { get; init; }
        public long Completed { get; init; }
    }
    private sealed class SabMutation { public bool Status { get; init; } }
}
