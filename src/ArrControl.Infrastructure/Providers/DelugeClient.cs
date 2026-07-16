using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArrControl.Application.Activity;
using ArrControl.Application.Connections;
using ArrControl.Application.Providers;

namespace ArrControl.Infrastructure.Providers;

public sealed class DelugeClient(IProviderHttpTransport transport, TimeProvider? timeProvider = null)
    : IProviderDownloadClient, IProviderCredentialContract
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    public string Kind => "deluge";
    public bool SupportsRetry => false;
    public IReadOnlyList<string> RequiredCredentialPurposes => [CredentialPurposes.Password];

    public async Task<ProviderCallResult<ProviderSystemStatus>> GetSystemStatusAsync(
        ProviderConnection connection, CancellationToken token)
    {
        var session = await OpenSessionAsync(connection, token);
        if (!session.Success) return ProviderCallResult<ProviderSystemStatus>.Failed(session.ErrorCode!);
        var status = await CallAsync<JsonElement>(connection, session.Cookie!, "web.get_host_status",
            [session.HostId!], token);
        if (!status.Success) return ProviderCallResult<ProviderSystemStatus>.Failed(
            status.ErrorCode!, status.RateLimit, status.HttpStatusCode);
        if (status.Value.ValueKind != JsonValueKind.Array || status.Value.GetArrayLength() < 3
            || status.Value[2].ValueKind != JsonValueKind.String)
            return ProviderCallResult<ProviderSystemStatus>.Failed(ProviderErrorCodes.InvalidResponse);
        var text = status.Value[2].GetString();
        if (!Version.TryParse(text, out var version))
            return ProviderCallResult<ProviderSystemStatus>.Failed(ProviderErrorCodes.InvalidResponse);
        if (version.Major != 2) return ProviderCallResult<ProviderSystemStatus>.Failed(
            ProviderErrorCodes.UnsupportedVersion, status.RateLimit, status.HttpStatusCode);
        return ProviderCallResult<ProviderSystemStatus>.Succeeded(
            new ProviderSystemStatus("Deluge", text!, null, null), status.RateLimit, 200);
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
        if (values.Count > 10_000 || values.Any(value => value.Key.Length != 40
            || !value.Key.All(char.IsAsciiHexDigit) || string.IsNullOrWhiteSpace(value.Value.Name)
            || value.Value.TotalSize < 0 || value.Value.TotalDone < 0
            || !double.IsFinite(value.Value.TimeAdded) || value.Value.TimeAdded is < 0 or > 253402300799
            || !double.IsFinite(value.Value.CompletedTime) || value.Value.CompletedTime is < 0 or > 253402300799))
            return ProviderCallResult<ProviderActivitySnapshot>.Failed(ProviderErrorCodes.InvalidResponse);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var queue = values.Where(value => value.Value.CompletedTime <= 0).Select(value =>
            new QueueItemSnapshot($"deluge:{value.Key.ToLowerInvariant()}", null, value.Key,
                value.Value.Name!, QueueStatus(value.Value.State), "unknown", "unknown", "torrent",
                value.Value.TotalSize, Math.Max(0, value.Value.TotalSize - value.Value.TotalDone),
                Unix(value.Value.TimeAdded), value.Value.Eta is > 0 and < 8_640_000
                    ? now.AddSeconds(value.Value.Eta) : null, "Deluge", null)).ToArray();
        var history = values.Where(value => value.Value.CompletedTime > 0).Select(value =>
            new HistoryItemSnapshot($"deluge:{value.Key.ToLowerInvariant()}", null, value.Key,
                value.Value.Name!, "downloadfolderimported",
                DateTimeOffset.FromUnixTimeSeconds((long)value.Value.CompletedTime))).ToArray();
        return ProviderCallResult<ProviderActivitySnapshot>.Succeeded(
            new ProviderActivitySnapshot(now, queue, history), result.RateLimit, 200);
    }

    public Task<ProviderCallResult<bool>> SetPausedAsync(
        ProviderConnection connection, string providerKey, bool paused, CancellationToken token) =>
        MutateAsync(connection, providerKey, paused ? "core.pause_torrent" : "core.resume_torrent", false, token);
    public Task<ProviderCallResult<bool>> RemoveAsync(
        ProviderConnection connection, string providerKey, bool deleteData, CancellationToken token) =>
        MutateAsync(connection, providerKey, "core.remove_torrent", deleteData, token);
    public Task<ProviderCallResult<bool>> RetryAsync(
        ProviderConnection connection, string providerKey, CancellationToken token) =>
        Task.FromResult(ProviderCallResult<bool>.Failed(ProviderErrorCodes.NotFound));

    private async Task<ProviderCallResult<Dictionary<string, DelugeTorrent>>> GetTorrentsAsync(
        ProviderConnection connection, CancellationToken token)
    {
        var session = await OpenSessionAsync(connection, token);
        if (!session.Success) return ProviderCallResult<Dictionary<string, DelugeTorrent>>.Failed(session.ErrorCode!);
        var connected = await CallAsync<bool>(connection, session.Cookie!, "web.connected", [], token);
        if (!connected.Success) return ProviderCallResult<Dictionary<string, DelugeTorrent>>.Failed(connected.ErrorCode!);
        if (!connected.Value)
        {
            var connect = await CallAsync<JsonElement>(connection, session.Cookie!, "web.connect", [session.HostId!], token);
            if (!connect.Success) return ProviderCallResult<Dictionary<string, DelugeTorrent>>.Failed(connect.ErrorCode!);
        }
        return await CallAsync<Dictionary<string, DelugeTorrent>>(connection, session.Cookie!,
            "core.get_torrents_status", [new Dictionary<string, object>(), new[] { "name", "state", "total_size", "total_done", "time_added", "completed_time", "eta" }], token);
    }

    private async Task<ProviderCallResult<bool>> MutateAsync(
        ProviderConnection connection, string key, string method, bool deleteData, CancellationToken token)
    {
        if (!TryHash(key, out var hash)) return ProviderCallResult<bool>.Failed(ProviderErrorCodes.InvalidResponse);
        var session = await OpenSessionAsync(connection, token);
        if (!session.Success) return ProviderCallResult<bool>.Failed(session.ErrorCode!);
        var connected = await CallAsync<bool>(connection, session.Cookie!, "web.connected", [], token);
        if (!connected.Success) return ProviderCallResult<bool>.Failed(connected.ErrorCode!);
        if (!connected.Value)
        {
            var connect = await CallAsync<JsonElement>(connection, session.Cookie!, "web.connect", [session.HostId!], token);
            if (!connect.Success) return ProviderCallResult<bool>.Failed(connect.ErrorCode!);
        }
        return method == "core.remove_torrent"
            ? await CallAsync<bool>(connection, session.Cookie!, method, [hash, deleteData], token)
            : await CallAsync<bool>(connection, session.Cookie!, method, [new[] { hash }], token);
    }

    private async Task<(bool Success, string? Cookie, string? HostId, string? ErrorCode)> OpenSessionAsync(
        ProviderConnection connection, CancellationToken token)
    {
        if (!connection.TryGetCredential(CredentialPurposes.Password, out var password))
            return (false, null, null, ProviderErrorCodes.CredentialMissing);
        var body = JsonSerializer.SerializeToUtf8Bytes(new { method = "auth.login", @params = new[] { password }, id = 1 });
        try
        {
            using var response = await transport.SendAsync(connection, new ProviderHttpRequest(
                HttpMethod.Post, "json", body: body, contentType: "application/json"), token);
            if (response.StatusCode != 200 || !response.Headers.TryGetValue("Set-Cookie", out var setCookie)
                || !TryCookie(setCookie, out var cookie)) return (false, null, null, ProviderErrorCodes.Unauthorized);
            var login = Deserialize<bool>(response);
            if (!login.Success || !login.Value) return (false, null, null, ProviderErrorCodes.Unauthorized);
            var hosts = await CallAsync<JsonElement>(connection, cookie, "web.get_hosts", [], token);
            if (!hosts.Success || hosts.Value.ValueKind != JsonValueKind.Array || hosts.Value.GetArrayLength() == 0
                || hosts.Value[0].ValueKind != JsonValueKind.Array || hosts.Value[0].GetArrayLength() == 0
                || hosts.Value[0][0].ValueKind != JsonValueKind.String)
                return (false, null, null, ProviderErrorCodes.InvalidResponse);
            return (true, cookie, hosts.Value[0][0].GetString(), null);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    private async Task<ProviderCallResult<T>> CallAsync<T>(
        ProviderConnection connection, string cookie, string method, object[] parameters, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { method, @params = parameters, id = 1 });
        try
        {
            using var response = await transport.SendAsync(connection, new ProviderHttpRequest(
                HttpMethod.Post, "json", headers: new Dictionary<string, string> { ["Cookie"] = cookie },
                body: body, contentType: "application/json"), token);
            var failure = SupportingProviderReader.Failure<T>(response);
            return failure ?? Deserialize<T>(response);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    private static ProviderCallResult<T> Deserialize<T>(ProviderTransportResponse response)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<DelugeEnvelope<T>>(response.Body, JsonOptions);
            if (envelope is null || envelope.Result is null
                || envelope.Error.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
                return ProviderCallResult<T>.Failed(ProviderErrorCodes.UpstreamConflict, response.RateLimit, response.StatusCode);
            return ProviderCallResult<T>.Succeeded(envelope.Result!, response.RateLimit, response.StatusCode);
        }
        catch (JsonException) { return ProviderCallResult<T>.Failed(ProviderErrorCodes.InvalidResponse); }
    }
    private static bool TryCookie(string value, out string cookie)
    {
        cookie = value.Split(';', 2)[0].Trim();
        return cookie.StartsWith("_session_id=", StringComparison.Ordinal) && cookie.Length is > 12 and <= 512
            && !cookie.Contains('\r') && !cookie.Contains('\n');
    }
    private static bool TryHash(string key, out string hash)
    { const string prefix = "deluge:"; hash = key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : ""; return hash.Length == 40 && hash.All(char.IsAsciiHexDigit); }
    private static DateTimeOffset? Unix(double value) => value > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)value) : null;
    private static string QueueStatus(string? value) => value?.ToLowerInvariant() switch
    { "downloading" => "downloading", "queued" => "queued", "paused" => "paused", "checking" => "warning", "error" => "failed", _ => "unknown" };

    private sealed class DelugeEnvelope<T> { public T? Result { get; init; } public JsonElement Error { get; init; } }
    private sealed class DelugeTorrent
    {
        public string? Name { get; init; }
        public string? State { get; init; }
        [JsonPropertyName("total_size")] public long TotalSize { get; init; }
        [JsonPropertyName("total_done")] public long TotalDone { get; init; }
        [JsonPropertyName("time_added")] public double TimeAdded { get; init; }
        [JsonPropertyName("completed_time")] public double CompletedTime { get; init; }
        public long Eta { get; init; }
    }
}
