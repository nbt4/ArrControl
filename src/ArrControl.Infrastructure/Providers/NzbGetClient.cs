using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArrControl.Application.Activity;
using ArrControl.Application.Connections;
using ArrControl.Application.Providers;

namespace ArrControl.Infrastructure.Providers;

public sealed class NzbGetClient(IProviderHttpTransport transport, TimeProvider? timeProvider = null)
    : IProviderDownloadClient, IProviderCredentialContract
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    public string Kind => "nzbget";
    public bool SupportsRetry => true;
    public IReadOnlyList<string> RequiredCredentialPurposes =>
        [CredentialPurposes.Username, CredentialPurposes.Password];

    public async Task<ProviderCallResult<ProviderSystemStatus>> GetSystemStatusAsync(
        ProviderConnection connection, CancellationToken cancellationToken)
    {
        var result = await RpcAsync<string>(connection, "version", [], cancellationToken);
        if (!result.Success) return ProviderCallResult<ProviderSystemStatus>.Failed(
            result.ErrorCode!, result.RateLimit, result.HttpStatusCode);
        if (!Version.TryParse(result.Value, out var version))
            return ProviderCallResult<ProviderSystemStatus>.Failed(ProviderErrorCodes.InvalidResponse);
        if (version.Major is not 25 and not 26) return ProviderCallResult<ProviderSystemStatus>.Failed(
            ProviderErrorCodes.UnsupportedVersion, result.RateLimit, result.HttpStatusCode);
        return ProviderCallResult<ProviderSystemStatus>.Succeeded(
            new ProviderSystemStatus("NZBGet", result.Value!, null, null), result.RateLimit, 200);
    }

    public async Task<ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>> GetHealthAsync(
        ProviderConnection connection, CancellationToken cancellationToken)
    {
        var result = await RpcAsync<NzbStatus>(connection, "status", [], cancellationToken);
        return !result.Success ? ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>.Failed(
                result.ErrorCode!, result.RateLimit, result.HttpStatusCode)
            : result.Value is null ? ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>.Failed(
                ProviderErrorCodes.InvalidResponse)
            : ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>.Succeeded([], result.RateLimit, 200);
    }

    public async Task<ProviderCallResult<ProviderActivitySnapshot>> GetActivityAsync(
        ProviderConnection connection, CancellationToken cancellationToken)
    {
        var queue = await RpcAsync<NzbQueue[]>(connection, "listgroups", [0], cancellationToken);
        if (!queue.Success) return FailActivity(queue);
        var history = await RpcAsync<NzbHistory[]>(connection, "history", [false], cancellationToken);
        if (!history.Success) return FailActivity(history);
        if (queue.Value is null || history.Value is null || queue.Value.Length > 10_000
            || history.Value.Length > 10_000 || queue.Value.Any(value => value.NzbId <= 0
                || string.IsNullOrWhiteSpace(value.NzbName)) || history.Value.Any(value => value.NzbId <= 0
                || string.IsNullOrWhiteSpace(value.Name) || value.HistoryTime is <= 0 or > 253402300799))
            return ProviderCallResult<ProviderActivitySnapshot>.Failed(ProviderErrorCodes.InvalidResponse);
        var queueItems = queue.Value.Select(value => new QueueItemSnapshot(
            $"nzbget:{value.NzbId}", null, value.NzbId.ToString(CultureInfo.InvariantCulture), value.NzbName!,
            NormalizeQueue(value.Status), "unknown", "unknown", "usenet",
            Combine(value.FileSizeHi, value.FileSizeLo), Combine(value.RemainingSizeHi, value.RemainingSizeLo),
            null, null, "NZBGet", null)).ToArray();
        var historyItems = history.Value.Select(value => new HistoryItemSnapshot(
            $"nzbget:{value.NzbId}", null, value.NzbId.ToString(CultureInfo.InvariantCulture), value.Name!,
            NormalizeHistory(value.Status), DateTimeOffset.FromUnixTimeSeconds(value.HistoryTime))).ToArray();
        return ProviderCallResult<ProviderActivitySnapshot>.Succeeded(
            new ProviderActivitySnapshot((timeProvider ?? TimeProvider.System).GetUtcNow(), queueItems, historyItems),
            history.RateLimit ?? queue.RateLimit, 200);
    }

    public Task<ProviderCallResult<bool>> SetPausedAsync(
        ProviderConnection connection, string providerKey, bool paused, CancellationToken cancellationToken) =>
        EditAsync(connection, providerKey, paused ? "GroupPause" : "GroupResume", cancellationToken);
    public Task<ProviderCallResult<bool>> RemoveAsync(
        ProviderConnection connection, string providerKey, bool deleteData, CancellationToken cancellationToken) =>
        EditAsync(connection, providerKey, deleteData ? "GroupFinalDelete" : "GroupDelete", cancellationToken);
    public Task<ProviderCallResult<bool>> RetryAsync(
        ProviderConnection connection, string providerKey, CancellationToken cancellationToken) =>
        EditAsync(connection, providerKey, "HistoryRedownload", cancellationToken);

    private Task<ProviderCallResult<bool>> EditAsync(
        ProviderConnection connection, string providerKey, string command, CancellationToken token)
    {
        if (!TryId(providerKey, out var id))
            return Task.FromResult(ProviderCallResult<bool>.Failed(ProviderErrorCodes.InvalidResponse));
        return RpcAsync<bool>(connection, "editqueue", [command, "", new[] { id }], token);
    }

    private async Task<ProviderCallResult<T>> RpcAsync<T>(
        ProviderConnection connection, string method, object[] parameters, CancellationToken token)
    {
        if (!connection.TryGetCredential(CredentialPurposes.Username, out var username)
            || !connection.TryGetCredential(CredentialPurposes.Password, out var password))
            return ProviderCallResult<T>.Failed(ProviderErrorCodes.CredentialMissing);
        var body = JsonSerializer.SerializeToUtf8Bytes(new { version = "1.1", method, @params = parameters, id = 1 });
        try
        {
            var basicBytes = Encoding.UTF8.GetBytes($"{username}:{password}");
            var basic = Convert.ToBase64String(basicBytes);
            CryptographicOperations.ZeroMemory(basicBytes);
            using var response = await transport.SendAsync(connection, new ProviderHttpRequest(
                HttpMethod.Post, "jsonrpc", headers: new Dictionary<string, string>
                { ["Authorization"] = $"Basic {basic}" }, body: body, contentType: "application/json"), token);
            var failure = SupportingProviderReader.Failure<T>(response);
            if (failure is not null) return failure;
            try
            {
                var envelope = JsonSerializer.Deserialize<RpcEnvelope<T>>(response.Body, JsonOptions);
                if (envelope is null || envelope.Error.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
                    return ProviderCallResult<T>.Failed(ProviderErrorCodes.UpstreamConflict,
                        response.RateLimit, response.StatusCode);
                return ProviderCallResult<T>.Succeeded(envelope.Result!, response.RateLimit, 200);
            }
            catch (JsonException)
            {
                return ProviderCallResult<T>.Failed(ProviderErrorCodes.InvalidResponse,
                    response.RateLimit, response.StatusCode);
            }
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    private static ProviderCallResult<ProviderActivitySnapshot> FailActivity<T>(ProviderCallResult<T> value) =>
        ProviderCallResult<ProviderActivitySnapshot>.Failed(value.ErrorCode!, value.RateLimit, value.HttpStatusCode);
    private static long Combine(long hi, long lo) => hi < 0 || lo < 0 ? 0
        : hi > uint.MaxValue || lo > uint.MaxValue ? long.MaxValue
        : (long)Math.Min((ulong)long.MaxValue, ((ulong)hi << 32) | (uint)lo);
    private static string NormalizeQueue(string? value) => value?.ToUpperInvariant() switch
    { "PAUSED" => "paused", "DOWNLOADING" => "downloading", "QUEUED" => "queued", _ => "unknown" };
    private static string NormalizeHistory(string? value) => value?.Split('/')[0].ToUpperInvariant() switch
    { "SUCCESS" => "downloadfolderimported", "FAILURE" => "downloadfailed", "WARNING" => "warning", "DELETED" => "downloadignored", _ => "unknown" };
    private static bool TryId(string key, out int id)
    {
        id = 0;
        return key.StartsWith("nzbget:", StringComparison.Ordinal)
            && int.TryParse(key[7..], NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    private sealed class RpcEnvelope<T>
    {
        public T? Result { get; init; }
        public JsonElement Error { get; init; }
    }
    private sealed class NzbStatus { public bool DownloadPaused { get; init; } }
    private sealed class NzbQueue
    {
        public int NzbId { get; init; }
        public string? NzbName { get; init; }
        public string? Status { get; init; }
        public long FileSizeHi { get; init; }
        public long FileSizeLo { get; init; }
        public long RemainingSizeHi { get; init; }
        public long RemainingSizeLo { get; init; }
    }
    private sealed class NzbHistory
    {
        public int NzbId { get; init; }
        public string? Name { get; init; }
        public string? Status { get; init; }
        public long HistoryTime { get; init; }
    }
}
