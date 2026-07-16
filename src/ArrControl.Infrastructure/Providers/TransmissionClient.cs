using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArrControl.Application.Activity;
using ArrControl.Application.Connections;
using ArrControl.Application.Providers;

namespace ArrControl.Infrastructure.Providers;

public sealed class TransmissionClient(IProviderHttpTransport transport, TimeProvider? timeProvider = null)
    : IProviderDownloadClient, IProviderCredentialContract
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 24 };
    public string Kind => "transmission";
    public bool SupportsRetry => false;
    public IReadOnlyList<string> RequiredCredentialPurposes => [CredentialPurposes.Username, CredentialPurposes.Password];

    public async Task<ProviderCallResult<ProviderSystemStatus>> GetSystemStatusAsync(
        ProviderConnection connection, CancellationToken token)
    {
        var result = await RpcAsync<TransmissionSession>(connection, "session-get",
            new { fields = new[] { "version" } }, token);
        if (!result.Success) return ProviderCallResult<ProviderSystemStatus>.Failed(
            result.ErrorCode!, result.RateLimit, result.HttpStatusCode);
        var text = result.Value?.Version?.Split(' ', 2)[0];
        if (!Version.TryParse(text, out var version))
            return ProviderCallResult<ProviderSystemStatus>.Failed(ProviderErrorCodes.InvalidResponse);
        if (version.Major != 4) return ProviderCallResult<ProviderSystemStatus>.Failed(
            ProviderErrorCodes.UnsupportedVersion, result.RateLimit, result.HttpStatusCode);
        return ProviderCallResult<ProviderSystemStatus>.Succeeded(
            new ProviderSystemStatus("Transmission", result.Value!.Version!, null, null), result.RateLimit, 200);
    }

    public async Task<ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>> GetHealthAsync(
        ProviderConnection connection, CancellationToken token)
    {
        var result = await GetTorrentsAsync(connection, token);
        return result.Success
            ? ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>.Succeeded([], result.RateLimit, 200)
            : ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>.Failed(
                result.ErrorCode!, result.RateLimit, result.HttpStatusCode);
    }

    public async Task<ProviderCallResult<ProviderActivitySnapshot>> GetActivityAsync(
        ProviderConnection connection, CancellationToken token)
    {
        var result = await GetTorrentsAsync(connection, token);
        if (!result.Success) return ProviderCallResult<ProviderActivitySnapshot>.Failed(
            result.ErrorCode!, result.RateLimit, result.HttpStatusCode);
        var values = result.Value!;
        if (values.Length > 10_000 || values.Any(value => value.HashString?.Length != 40
            || !value.HashString.All(char.IsAsciiHexDigit) || string.IsNullOrWhiteSpace(value.Name)
            || value.TotalSize < 0 || value.LeftUntilDone < 0
            || value.AddedDate is < 0 or > 253402300799
            || value.DoneDate is < 0 or > 253402300799))
            return ProviderCallResult<ProviderActivitySnapshot>.Failed(ProviderErrorCodes.InvalidResponse);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var queue = values.Where(value => value.DoneDate <= 0).Select(value => new QueueItemSnapshot(
            $"transmission:{value.HashString!.ToLowerInvariant()}", null, value.HashString, value.Name!,
            QueueStatus(value.Status), "unknown", "unknown", "torrent", value.TotalSize,
            Math.Min(value.TotalSize, value.LeftUntilDone), Unix(value.AddedDate),
            value.Eta is >= 0 and < 8_640_000 ? now.AddSeconds(value.Eta) : null,
            "Transmission", null)).ToArray();
        var history = values.Where(value => value.DoneDate > 0).Select(value => new HistoryItemSnapshot(
            $"transmission:{value.HashString!.ToLowerInvariant()}", null, value.HashString, value.Name!,
            "downloadfolderimported", DateTimeOffset.FromUnixTimeSeconds(value.DoneDate))).ToArray();
        return ProviderCallResult<ProviderActivitySnapshot>.Succeeded(
            new ProviderActivitySnapshot(now, queue, history), result.RateLimit, 200);
    }

    public Task<ProviderCallResult<bool>> SetPausedAsync(
        ProviderConnection connection, string providerKey, bool paused, CancellationToken token) =>
        MutateAsync(connection, providerKey, paused ? "torrent-stop" : "torrent-start", false, token);

    public Task<ProviderCallResult<bool>> RemoveAsync(
        ProviderConnection connection, string providerKey, bool deleteData, CancellationToken token) =>
        MutateAsync(connection, providerKey, "torrent-remove", deleteData, token);

    public Task<ProviderCallResult<bool>> RetryAsync(
        ProviderConnection connection, string providerKey, CancellationToken token) =>
        Task.FromResult(ProviderCallResult<bool>.Failed(ProviderErrorCodes.NotFound));

    private async Task<ProviderCallResult<TransmissionTorrent[]>> GetTorrentsAsync(
        ProviderConnection connection, CancellationToken token)
    {
        var result = await RpcAsync<TransmissionTorrents>(connection, "torrent-get", new
        {
            fields = new[] { "hashString", "name", "totalSize", "leftUntilDone", "addedDate", "doneDate", "eta", "status" },
        }, token);
        return !result.Success ? ProviderCallResult<TransmissionTorrent[]>.Failed(
                result.ErrorCode!, result.RateLimit, result.HttpStatusCode)
            : result.Value?.Torrents is null ? ProviderCallResult<TransmissionTorrent[]>.Failed(
                ProviderErrorCodes.InvalidResponse)
            : ProviderCallResult<TransmissionTorrent[]>.Succeeded(result.Value.Torrents, result.RateLimit, 200);
    }

    private Task<ProviderCallResult<bool>> MutateAsync(
        ProviderConnection connection, string providerKey, string method, bool deleteData, CancellationToken token)
    {
        if (!TryHash(providerKey, out var hash))
            return Task.FromResult(ProviderCallResult<bool>.Failed(ProviderErrorCodes.InvalidResponse));
        var arguments = new Dictionary<string, object> { ["ids"] = new[] { hash } };
        if (method == "torrent-remove") arguments["delete-local-data"] = deleteData;
        return MutateRpcAsync(connection, method, arguments, token);
    }

    private async Task<ProviderCallResult<bool>> MutateRpcAsync(
        ProviderConnection connection, string method, object arguments, CancellationToken token)
    {
        var result = await RpcAsync<JsonElement>(connection, method, arguments, token);
        return result.Success ? ProviderCallResult<bool>.Succeeded(true, result.RateLimit, 200)
            : ProviderCallResult<bool>.Failed(result.ErrorCode!, result.RateLimit, result.HttpStatusCode);
    }

    private async Task<ProviderCallResult<T>> RpcAsync<T>(
        ProviderConnection connection, string method, object arguments, CancellationToken token)
    {
        if (!connection.TryGetCredential(CredentialPurposes.Username, out var username)
            || !connection.TryGetCredential(CredentialPurposes.Password, out var password))
            return ProviderCallResult<T>.Failed(ProviderErrorCodes.CredentialMissing);
        var body = JsonSerializer.SerializeToUtf8Bytes(new { method, arguments, tag = 1 });
        try
        {
            var basicBytes = Encoding.UTF8.GetBytes($"{username}:{password}");
            var basic = Convert.ToBase64String(basicBytes);
            CryptographicOperations.ZeroMemory(basicBytes);
            var headers = new Dictionary<string, string> { ["Authorization"] = $"Basic {basic}" };
            var response = await SendAsync(connection, headers, body, token);
            if (response.StatusCode == 409 && response.Headers.TryGetValue(
                    "X-Transmission-Session-Id", out var sessionId) && sessionId.Length <= 512)
            {
                response.Dispose();
                headers["X-Transmission-Session-Id"] = sessionId;
                response = await SendAsync(connection, headers, body, token);
            }
            using (response)
            {
                var failure = SupportingProviderReader.Failure<T>(response);
                if (failure is not null) return failure;
                try
                {
                    var envelope = JsonSerializer.Deserialize<TransmissionEnvelope<T>>(response.Body, JsonOptions);
                    if (envelope is null || !string.Equals(envelope.Result, "success", StringComparison.OrdinalIgnoreCase))
                        return ProviderCallResult<T>.Failed(ProviderErrorCodes.UpstreamConflict,
                            response.RateLimit, response.StatusCode);
                    return ProviderCallResult<T>.Succeeded(envelope.Arguments!, response.RateLimit, 200);
                }
                catch (JsonException)
                {
                    return ProviderCallResult<T>.Failed(ProviderErrorCodes.InvalidResponse,
                        response.RateLimit, response.StatusCode);
                }
            }
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    private Task<ProviderTransportResponse> SendAsync(
        ProviderConnection connection, IReadOnlyDictionary<string, string> headers, byte[] body,
        CancellationToken token) => transport.SendAsync(connection,
        new ProviderHttpRequest(HttpMethod.Post, "transmission/rpc", headers: headers,
            body: body, contentType: "application/json"), token);

    private static bool TryHash(string key, out string hash)
    {
        const string prefix = "transmission:";
        hash = key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : string.Empty;
        return hash.Length == 40 && hash.All(char.IsAsciiHexDigit);
    }
    private static DateTimeOffset? Unix(long value) => value > 0 ? DateTimeOffset.FromUnixTimeSeconds(value) : null;
    private static string QueueStatus(int value) => value switch
    { 0 => "paused", 3 or 5 => "queued", 4 => "downloading", 1 or 2 => "warning", _ => "unknown" };

    private sealed class TransmissionEnvelope<T>
    {
        public string? Result { get; init; }
        public T? Arguments { get; init; }
    }
    private sealed class TransmissionSession { public string? Version { get; init; } }
    private sealed class TransmissionTorrents { public TransmissionTorrent[]? Torrents { get; init; } }
    private sealed class TransmissionTorrent
    {
        public string? HashString { get; init; }
        public string? Name { get; init; }
        public long TotalSize { get; init; }
        public long LeftUntilDone { get; init; }
        public long AddedDate { get; init; }
        public long DoneDate { get; init; }
        public long Eta { get; init; }
        public int Status { get; init; }
    }
}
