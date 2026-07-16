using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArrControl.Application.Activity;
using ArrControl.Application.Connections;
using ArrControl.Application.Providers;

namespace ArrControl.Infrastructure.Providers;

public sealed class QBittorrentClient(IProviderHttpTransport transport, TimeProvider? timeProvider = null)
    : IProviderDownloadClient, IProviderCredentialContract
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 24 };
    public string Kind => "qbittorrent";
    public bool SupportsRetry => false;
    public IReadOnlyList<string> RequiredCredentialPurposes => [CredentialPurposes.Username, CredentialPurposes.Password];

    public async Task<ProviderCallResult<ProviderSystemStatus>> GetSystemStatusAsync(ProviderConnection connection, CancellationToken token)
    {
        var response = await AuthenticatedAsync(connection, HttpMethod.Get, "api/v2/app/version", null, token);
        if (!response.Success) return ProviderCallResult<ProviderSystemStatus>.Failed(response.ErrorCode!);
        using (var providerResponse = response.Response!)
        {
            var text = Encoding.UTF8.GetString(providerResponse.Body).Trim().TrimStart('v');
            if (!Version.TryParse(text, out var version)) return ProviderCallResult<ProviderSystemStatus>.Failed(ProviderErrorCodes.InvalidResponse);
            if (version.Major != 5) return ProviderCallResult<ProviderSystemStatus>.Failed(ProviderErrorCodes.UnsupportedVersion);
            return ProviderCallResult<ProviderSystemStatus>.Succeeded(new("qBittorrent", text, null, null), providerResponse.RateLimit, 200);
        }
    }

    public async Task<ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>> GetHealthAsync(ProviderConnection connection, CancellationToken token)
    {
        var result = await GetTorrentsAsync(connection, token);
        return result.Success ? ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>.Succeeded([], result.RateLimit, 200)
            : ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>.Failed(result.ErrorCode!, result.RateLimit, result.HttpStatusCode);
    }

    public async Task<ProviderCallResult<ProviderActivitySnapshot>> GetActivityAsync(ProviderConnection connection, CancellationToken token)
    {
        var result = await GetTorrentsAsync(connection, token);
        if (!result.Success) return ProviderCallResult<ProviderActivitySnapshot>.Failed(result.ErrorCode!, result.RateLimit, result.HttpStatusCode);
        var values = result.Value!;
        if (values.Length > 10_000 || values.Any(value => value.Hash?.Length != 40
            || !value.Hash.All(char.IsAsciiHexDigit) || string.IsNullOrWhiteSpace(value.Name)
            || value.TotalSize < 0 || value.AmountLeft < 0 || value.AddedOn is < 0 or > 253402300799
            || value.CompletionOn is < 0 or > 253402300799))
            return ProviderCallResult<ProviderActivitySnapshot>.Failed(ProviderErrorCodes.InvalidResponse);
        var queue = values.Where(value => value.CompletionOn <= 0).Select(value => new QueueItemSnapshot(
            $"qbittorrent:{value.Hash!.ToLowerInvariant()}", null, value.Hash, value.Name!, Normalize(value.State),
            "unknown", "unknown", "torrent", Nonnegative(value.TotalSize), Nonnegative(value.AmountLeft),
            Unix(value.AddedOn), value.Eta is > 0 and < 8640000 ? (timeProvider ?? TimeProvider.System).GetUtcNow().AddSeconds(value.Eta) : null,
            "qBittorrent", null)).ToArray();
        var history = values.Where(value => value.CompletionOn > 0).Select(value => new HistoryItemSnapshot(
            $"qbittorrent:{value.Hash!.ToLowerInvariant()}", null, value.Hash, value.Name!, "downloadfolderimported",
            DateTimeOffset.FromUnixTimeSeconds(value.CompletionOn))).ToArray();
        return ProviderCallResult<ProviderActivitySnapshot>.Succeeded(new((timeProvider ?? TimeProvider.System).GetUtcNow(), queue, history), result.RateLimit, 200);
    }

    public Task<ProviderCallResult<bool>> SetPausedAsync(ProviderConnection c, string key, bool paused, CancellationToken t) =>
        MutateAsync(c, key, paused ? "stop" : "start", false, t);
    public Task<ProviderCallResult<bool>> RemoveAsync(ProviderConnection c, string key, bool deleteData, CancellationToken t) =>
        MutateAsync(c, key, "delete", deleteData, t);
    public Task<ProviderCallResult<bool>> RetryAsync(ProviderConnection c, string key, CancellationToken t) =>
        Task.FromResult(ProviderCallResult<bool>.Failed(ProviderErrorCodes.NotFound));

    private async Task<ProviderCallResult<QbitTorrent[]>> GetTorrentsAsync(ProviderConnection connection, CancellationToken token)
    {
        var result = await AuthenticatedAsync(connection, HttpMethod.Get, "api/v2/torrents/info", null, token);
        if (!result.Success) return ProviderCallResult<QbitTorrent[]>.Failed(result.ErrorCode!);
        using var providerResponse = result.Response!;
        try
        {
            var value = JsonSerializer.Deserialize<QbitTorrent[]>(providerResponse.Body, JsonOptions);
            return value is null ? ProviderCallResult<QbitTorrent[]>.Failed(ProviderErrorCodes.InvalidResponse)
                : ProviderCallResult<QbitTorrent[]>.Succeeded(value, providerResponse.RateLimit, 200);
        }
        catch (JsonException) { return ProviderCallResult<QbitTorrent[]>.Failed(ProviderErrorCodes.InvalidResponse); }
    }

    private async Task<ProviderCallResult<bool>> MutateAsync(ProviderConnection connection, string key, string action, bool deleteData, CancellationToken token)
    {
        if (!TryHash(key, out var hash)) return ProviderCallResult<bool>.Failed(ProviderErrorCodes.InvalidResponse);
        var form = action == "delete" ? $"hashes={hash}&deleteFiles={deleteData.ToString().ToLowerInvariant()}" : $"hashes={hash}";
        var body = Encoding.UTF8.GetBytes(form);
        try
        {
            var result = await AuthenticatedAsync(connection, HttpMethod.Post, $"api/v2/torrents/{action}", body, token);
            if (!result.Success) return ProviderCallResult<bool>.Failed(result.ErrorCode!);
            using var providerResponse = result.Response!;
            return ProviderCallResult<bool>.Succeeded(true, providerResponse.RateLimit, 200);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    private async Task<(bool Success, ProviderTransportResponse? Response, string? ErrorCode)> AuthenticatedAsync(
        ProviderConnection connection, HttpMethod method, string path, byte[]? body, CancellationToken token)
    {
        if (!connection.TryGetCredential(CredentialPurposes.Username, out var username)
            || !connection.TryGetCredential(CredentialPurposes.Password, out var password)) return (false, null, ProviderErrorCodes.CredentialMissing);
        var loginBody = Encoding.UTF8.GetBytes($"username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}");
        var origin = connection.BaseUri.GetLeftPart(UriPartial.Authority);
        try
        {
            using var login = await transport.SendAsync(connection, new ProviderHttpRequest(HttpMethod.Post, "api/v2/auth/login",
                headers: new Dictionary<string, string> { ["Origin"] = origin, ["Referer"] = origin + "/" },
                body: loginBody, contentType: "application/x-www-form-urlencoded"), token);
            if (login.StatusCode != 200 || !login.Headers.TryGetValue("Set-Cookie", out var setCookie)
                || !TrySid(setCookie, out var cookie)) return (false, null, ProviderErrorCodes.Unauthorized);
            var response = await transport.SendAsync(connection, new ProviderHttpRequest(method, path,
                headers: new Dictionary<string, string> { ["Cookie"] = cookie, ["Origin"] = origin, ["Referer"] = origin + "/" },
                body: body, contentType: body is null ? null : "application/x-www-form-urlencoded"), token);
            if (response.StatusCode == 200) return (true, response, null);
            var errorCode = response.StatusCode == 403
                ? ProviderErrorCodes.Forbidden
                : response.StatusCode == 401
                    ? ProviderErrorCodes.Unauthorized
                    : ProviderErrorCodes.Unknown;
            response.Dispose();
            return (false, null, errorCode);
        }
        finally { CryptographicOperations.ZeroMemory(loginBody); }
    }

    private static bool TrySid(string value, out string cookie)
    {
        cookie = value.Split(';', 2)[0].Trim();
        return cookie.StartsWith("SID=", StringComparison.Ordinal) && cookie.Length is > 4 and <= 512
            && !cookie.Contains('\r') && !cookie.Contains('\n');
    }
    private static bool TryHash(string key, out string hash)
    { hash = key.StartsWith("qbittorrent:", StringComparison.Ordinal) ? key[12..] : ""; return hash.Length == 40 && hash.All(char.IsAsciiHexDigit); }
    private static long Nonnegative(long value) => Math.Max(0, value);
    private static DateTimeOffset? Unix(long value) => value > 0 ? DateTimeOffset.FromUnixTimeSeconds(value) : null;
    private static string Normalize(string? value) => value?.ToLowerInvariant() switch
    { "downloading" or "forceddl" or "metadl" => "downloading", "pauseddl" or "stoppeddl" => "paused", "queuedldl" => "queued", "error" or "missingfiles" => "failed", _ => "unknown" };
    private sealed class QbitTorrent
    {
        public string? Hash { get; init; } public string? Name { get; init; } public string? State { get; init; }
        [JsonPropertyName("total_size")] public long TotalSize { get; init; }
        [JsonPropertyName("amount_left")] public long AmountLeft { get; init; }
        [JsonPropertyName("added_on")] public long AddedOn { get; init; }
        [JsonPropertyName("completion_on")] public long CompletionOn { get; init; }
        public long Eta { get; init; }
    }
}
