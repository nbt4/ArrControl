using System.Security.Cryptography;
using System.Text.Json.Serialization;
using ArrControl.Application.Connections;

namespace ArrControl.Application.Providers;

public static class ProviderErrorCodes
{
    public const string Unreachable = "unreachable";
    public const string TlsError = "tls_error";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string RateLimited = "rate_limited";
    public const string UnsupportedVersion = "unsupported_version";
    public const string InvalidResponse = "invalid_response";
    public const string UpstreamConflict = "upstream_conflict";
    public const string NotFound = "not_found";
    public const string Timeout = "timeout";
    public const string Unknown = "unknown";
    public const string CredentialMissing = "credential_missing";
}

public sealed record ProviderRateLimitMetadata(
    int? Limit,
    int? Remaining,
    DateTimeOffset? ResetAt,
    TimeSpan? RetryAfter);

public sealed record ProviderHealthIssue(
    int Id,
    string Source,
    string Severity,
    string? Message,
    Uri? WikiUrl);

public sealed class ProviderConnection(
    Guid instanceId,
    Uri baseUri,
    bool tlsVerificationEnabled,
    bool allowPrivateNetworkAccess,
    string apiKey)
{
    private readonly IReadOnlyDictionary<string, string> credentials =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CredentialPurposes.ApiKey] = apiKey,
        };

    public ProviderConnection(
        Guid instanceId,
        Uri baseUri,
        bool tlsVerificationEnabled,
        bool allowPrivateNetworkAccess,
        IReadOnlyDictionary<string, string> credentials)
        : this(instanceId, baseUri, tlsVerificationEnabled, allowPrivateNetworkAccess,
            credentials.GetValueOrDefault(CredentialPurposes.ApiKey) ?? string.Empty)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        this.credentials = new Dictionary<string, string>(credentials, StringComparer.Ordinal);
    }

    public Guid InstanceId { get; } = instanceId;

    public Uri BaseUri { get; } = baseUri;

    public bool TlsVerificationEnabled { get; } = tlsVerificationEnabled;

    public bool AllowPrivateNetworkAccess { get; } = allowPrivateNetworkAccess;

    [JsonIgnore]
    public string ApiKey { get; } = apiKey;

    public bool TryGetCredential(string purpose, out string value) =>
        credentials.TryGetValue(purpose, out value!);

    public override string ToString() => "ProviderConnection { [REDACTED] }";
}

public sealed class ProviderTransportResponse(
    int statusCode,
    byte[] body,
    ProviderRateLimitMetadata? rateLimit,
    IReadOnlyDictionary<string, string>? headers = null) : IDisposable
{
    public int StatusCode { get; } = statusCode;

    [JsonIgnore]
    public byte[] Body { get; } = body;

    public ProviderRateLimitMetadata? RateLimit { get; } = rateLimit;

    [JsonIgnore]
    public IReadOnlyDictionary<string, string> Headers { get; } =
        headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public void Dispose() => CryptographicOperations.ZeroMemory(Body);

    public override string ToString() =>
        $"ProviderTransportResponse {{ StatusCode = {StatusCode}, Body = [REDACTED] }}";
}

public sealed class ProviderHttpRequest(
    HttpMethod method,
    string relativePath,
    IReadOnlyDictionary<string, string>? query = null,
    IReadOnlyDictionary<string, string>? headers = null,
    byte[]? body = null,
    string? contentType = null)
{
    public HttpMethod Method { get; } = method;
    public string RelativePath { get; } = relativePath;
    [JsonIgnore] public IReadOnlyDictionary<string, string> Query { get; } =
        query ?? new Dictionary<string, string>();
    [JsonIgnore] public IReadOnlyDictionary<string, string> Headers { get; } =
        headers ?? new Dictionary<string, string>();
    [JsonIgnore] public byte[]? Body { get; } = body;
    public string? ContentType { get; } = contentType;
    public override string ToString() =>
        $"ProviderHttpRequest {{ Method = {Method}, [REDACTED] }}";
}

public interface IProviderHttpTransport
{
    Task<ProviderTransportResponse> SendAsync(
        ProviderConnection connection,
        ProviderHttpRequest request,
        CancellationToken cancellationToken);
}

public sealed class ProviderTransportException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public interface IProviderApiTransport
{
    Task<ProviderTransportResponse> GetAsync(
        ProviderConnection connection,
        string relativePath,
        CancellationToken cancellationToken);

    Task<ProviderTransportResponse> GetAsync(
        ProviderConnection connection,
        string relativePath,
        IReadOnlyDictionary<string, string> query,
        CancellationToken cancellationToken) =>
        query.Count == 0
            ? GetAsync(connection, relativePath, cancellationToken)
            : throw new ProviderTransportException(ProviderErrorCodes.Unknown);

    Task<ProviderTransportResponse> PostJsonAsync(
        ProviderConnection connection,
        string relativePath,
        byte[] body,
        CancellationToken cancellationToken) =>
        throw new ProviderTransportException(ProviderErrorCodes.Unknown);
}

public sealed record ProviderSystemStatus(
    string AppName,
    string Version,
    string? InstanceName,
    string? Branch);

public sealed record ProviderCallResult<T>(
    bool Success,
    T? Value,
    string? ErrorCode,
    ProviderRateLimitMetadata? RateLimit,
    int? HttpStatusCode)
{
    public static ProviderCallResult<T> Succeeded(
        T value,
        ProviderRateLimitMetadata? rateLimit,
        int httpStatusCode) =>
        new(true, value, null, rateLimit, httpStatusCode);

    public static ProviderCallResult<T> Failed(
        string errorCode,
        ProviderRateLimitMetadata? rateLimit = null,
        int? httpStatusCode = null) =>
        new(false, default, errorCode, rateLimit, httpStatusCode);
}

public interface IArrProviderClient
{
    string Kind { get; }

    Task<ProviderCallResult<ProviderSystemStatus>> GetSystemStatusAsync(
        ProviderConnection connection,
        CancellationToken cancellationToken);

    Task<ProviderCallResult<IReadOnlyList<ProviderHealthIssue>>> GetHealthAsync(
        ProviderConnection connection,
        CancellationToken cancellationToken);
}

public interface IProviderConnectionAdapter
{
    string Kind { get; }

    IReadOnlyList<string> RequiredCredentialPurposes => [CredentialPurposes.ApiKey];

    Task<ConnectionProbeObservation> ProbeAsync(
        ProviderConnection connection,
        CancellationToken cancellationToken);
}
