using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArrControl.Application.Providers;

namespace ArrControl.Infrastructure.Providers;

public sealed class GenericWebhookNotificationProvider(IProviderHttpTransport transport)
    : HttpNotificationProvider(transport)
{
    public override string Kind => "webhook";
    public override IReadOnlyList<string> RequiredSettings => [];
    public override IReadOnlyList<string> RequiredSecrets => [];

    protected override async Task<NotificationSendResult> SendCoreAsync(
        NotificationTarget target, NotificationMessage message, CancellationToken cancellationToken)
    {
        var payload = Serialize(new
        {
            version = 1,
            eventId = message.EventId,
            title = message.Title,
            message = message.Body,
            severity = message.Severity,
            occurredAt = message.OccurredAt,
            actionUrl = message.ActionUrl,
        });
        try
        {
            var headers = new Dictionary<string, string>
            {
                ["X-ArrControl-Event-Id"] = message.EventId.ToString("D"),
            };
            if (target.TryGetSecret("signing-secret", out var signingSecret))
            {
                var key = Encoding.UTF8.GetBytes(signingSecret);
                try
                {
                    headers["X-ArrControl-Signature-256"] =
                        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(key, payload));
                }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
            return await SendJsonAsync(target, target.Endpoint, payload, headers, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }
}

public sealed class DiscordNotificationProvider(IProviderHttpTransport transport)
    : SimpleWebhookNotificationProvider(transport, "discord")
{
    protected override object Payload(NotificationMessage message) => new
    {
        content = Format(message, 2_000),
        allowed_mentions = new { parse = Array.Empty<string>() },
    };
    protected override bool Success(ProviderTransportResponse response) => response.StatusCode is 200 or 204;
}

public sealed class SlackNotificationProvider(IProviderHttpTransport transport)
    : SimpleWebhookNotificationProvider(transport, "slack")
{
    protected override object Payload(NotificationMessage message) => new { text = Format(message, 4_000) };
}

public sealed class TeamsNotificationProvider(IProviderHttpTransport transport)
    : SimpleWebhookNotificationProvider(transport, "teams")
{
    protected override object Payload(NotificationMessage message) => new
    {
        type = "message",
        attachments = new[]
        {
            new
            {
                contentType = "application/vnd.microsoft.card.adaptive",
                contentUrl = (string?)null,
                content = new Dictionary<string, object?>
                {
                    ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                    ["type"] = "AdaptiveCard",
                    ["version"] = "1.4",
                    ["body"] = new object[]
                    {
                        new { type = "TextBlock", text = message.Title, weight = "Bolder", wrap = true },
                        new { type = "TextBlock", text = message.Body, wrap = true },
                    },
                },
            },
        },
    };
    protected override bool Success(ProviderTransportResponse response) => response.StatusCode is 200 or 202;
}

public sealed class TelegramNotificationProvider(IProviderHttpTransport transport)
    : HttpNotificationProvider(transport)
{
    public override string Kind => "telegram";
    public override IReadOnlyList<string> RequiredSettings => ["chat-id"];
    public override IReadOnlyList<string> RequiredSecrets => ["bot-token"];
    protected override async Task<NotificationSendResult> SendCoreAsync(
        NotificationTarget target, NotificationMessage message, CancellationToken cancellationToken)
    {
        if (!target.TryGetSetting("chat-id", out var chatId)
            || !target.TryGetSecret("bot-token", out var token)
            || !ValidToken(token) || chatId.Length is 0 or > 128)
            return Invalid();
        if (target.Endpoint.AbsolutePath != "/" || target.Endpoint.Query.Length != 0)
            return Invalid();
        var endpoint = new Uri(target.Endpoint, $"./bot{token}/sendMessage");
        var payload = Serialize(new { chat_id = chatId, text = Format(message, 4_096), disable_web_page_preview = true });
        try
        {
            return await SendJsonAsync(target, endpoint, payload, null, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }
    protected override bool Success(ProviderTransportResponse response) =>
        base.Success(response) && ResponseHasSuccessFlag(response.Body, "ok");
    private static bool ValidToken(string value) => value.Length is >= 20 and <= 256
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is ':' or '_' or '-');
}

public sealed class NtfyNotificationProvider(IProviderHttpTransport transport)
    : HttpNotificationProvider(transport)
{
    public override string Kind => "ntfy";
    public override IReadOnlyList<string> RequiredSettings => [];
    public override IReadOnlyList<string> RequiredSecrets => [];
    protected override async Task<NotificationSendResult> SendCoreAsync(
        NotificationTarget target, NotificationMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Topic(target.Endpoint))) return Invalid();
        var payload = Serialize(new
        {
            topic = Topic(target.Endpoint),
            title = message.Title,
            message = message.Body,
            priority = message.Severity is "error" ? 5 : message.Severity is "warning" ? 4 : 3,
            click = message.ActionUrl,
        });
        try
        {
            var headers = new Dictionary<string, string>();
            if (target.TryGetSecret("access-token", out var token)) headers["Authorization"] = $"Bearer {token}";
            var root = new Uri(target.Endpoint.GetLeftPart(UriPartial.Authority) + "/");
            return await SendJsonAsync(target, root, payload, headers, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }
    private static string Topic(Uri endpoint) => endpoint.AbsolutePath.Trim('/');
}

public sealed class GotifyNotificationProvider(IProviderHttpTransport transport)
    : HttpNotificationProvider(transport)
{
    public override string Kind => "gotify";
    public override IReadOnlyList<string> RequiredSettings => [];
    public override IReadOnlyList<string> RequiredSecrets => ["app-token"];
    protected override async Task<NotificationSendResult> SendCoreAsync(
        NotificationTarget target, NotificationMessage message, CancellationToken cancellationToken)
    {
        if (!target.TryGetSecret("app-token", out var token) || token.Length is 0 or > 512) return Invalid();
        var endpoint = AppendPath(target.Endpoint, "message");
        var payload = Serialize(new
        {
            title = message.Title,
            message = message.Body,
            priority = message.Severity is "error" ? 10 : message.Severity is "warning" ? 5 : 0,
        });
        try
        {
            return await SendJsonAsync(target, endpoint, payload,
                new Dictionary<string, string> { ["X-Gotify-Key"] = token }, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }
}

public sealed class PushoverNotificationProvider(IProviderHttpTransport transport)
    : HttpNotificationProvider(transport)
{
    public override string Kind => "pushover";
    public override IReadOnlyList<string> RequiredSettings => [];
    public override IReadOnlyList<string> RequiredSecrets => ["app-token", "user-key"];
    protected override async Task<NotificationSendResult> SendCoreAsync(
        NotificationTarget target, NotificationMessage message, CancellationToken cancellationToken)
    {
        if (!target.TryGetSecret("app-token", out var token)
            || !target.TryGetSecret("user-key", out var user)
            || token.Length is 0 or > 128 || user.Length is 0 or > 256
            || message.Title.Length > 250 || message.Body.Length > 1_024)
            return Invalid();
        var payload = Serialize(new
        {
            token,
            user,
            title = message.Title,
            message = message.Body,
            url = message.ActionUrl?.ToString(),
        });
        try
        {
            return await SendJsonAsync(target, target.Endpoint, payload, null, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }
    protected override bool Success(ProviderTransportResponse response) =>
        base.Success(response) && ResponseHasInteger(response.Body, "status", 1);
}

public abstract class SimpleWebhookNotificationProvider(
    IProviderHttpTransport transport, string kind) : HttpNotificationProvider(transport)
{
    public override string Kind => kind;
    public override IReadOnlyList<string> RequiredSettings => [];
    public override IReadOnlyList<string> RequiredSecrets => [];
    protected abstract object Payload(NotificationMessage message);
    protected override async Task<NotificationSendResult> SendCoreAsync(
        NotificationTarget target, NotificationMessage message, CancellationToken cancellationToken)
    {
        var payload = Serialize(Payload(message));
        try { return await SendJsonAsync(target, target.Endpoint, payload, null, cancellationToken); }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }
}

public abstract class HttpNotificationProvider(IProviderHttpTransport transport) : INotificationProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };
    public abstract string Kind { get; }
    public abstract IReadOnlyList<string> RequiredSettings { get; }
    public abstract IReadOnlyList<string> RequiredSecrets { get; }

    public async Task<NotificationSendResult> SendAsync(
        NotificationTarget target, NotificationMessage message, CancellationToken cancellationToken)
    {
        if (target is null || !Valid(message) || !ValidEndpoint(target.Endpoint)
            || RequiredSettings.Any(value => !target.TryGetSetting(value, out var setting) || string.IsNullOrWhiteSpace(setting))
            || RequiredSecrets.Any(value => !target.TryGetSecret(value, out var secret) || string.IsNullOrWhiteSpace(secret)))
            return Invalid();
        try
        {
            return await SendCoreAsync(target, message, cancellationToken);
        }
        catch (ProviderTransportException exception)
        {
            return new NotificationSendResult(false, exception.Code);
        }
        catch (ArrControl.Application.Connections.OutboundTargetRejectedException)
        {
            return new NotificationSendResult(false, ProviderErrorCodes.Unreachable);
        }
    }

    protected abstract Task<NotificationSendResult> SendCoreAsync(
        NotificationTarget target, NotificationMessage message, CancellationToken cancellationToken);

    protected async Task<NotificationSendResult> SendJsonAsync(
        NotificationTarget target, Uri endpoint, byte[] payload,
        IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        if (!TrySplitEndpoint(endpoint, out var baseUri, out var path, out var query)) return Invalid();
        var connection = new ProviderConnection(Guid.Empty, baseUri,
            target.TlsVerificationEnabled, target.AllowPrivateNetworkAccess, string.Empty);
        using var response = await transport.SendAsync(connection,
            new ProviderHttpRequest(HttpMethod.Post, path, query, headers, payload, "application/json"),
            cancellationToken);
        if (response.Body.Length > 64 * 1024) return Invalid(response.StatusCode);
        return Success(response)
            ? new NotificationSendResult(true, null, response.StatusCode)
            : new NotificationSendResult(false,
                response.StatusCode is >= 200 and < 300
                    ? ProviderErrorCodes.InvalidResponse : Error(response.StatusCode), response.StatusCode);
    }

    protected virtual bool Success(ProviderTransportResponse response) => response.StatusCode is >= 200 and < 300;
    protected static byte[] Serialize<T>(T payload) => JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
    protected static Uri AppendPath(Uri endpoint, string segment)
    {
        var builder = new UriBuilder(endpoint) { Query = string.Empty, Fragment = string.Empty };
        builder.Path = builder.Path.TrimEnd('/') + "/" + segment;
        return builder.Uri;
    }
    protected static NotificationSendResult Invalid(int? status = null) =>
        new(false, ProviderErrorCodes.InvalidResponse, status);
    protected static string Format(NotificationMessage message, int maximum)
    {
        var value = $"{message.Title}\n{message.Body}";
        if (message.ActionUrl is not null) value += $"\n{message.ActionUrl}";
        return value.Length <= maximum ? value : value[..maximum];
    }
    protected static bool ResponseHasSuccessFlag(byte[] body, string name) => Read(body, root =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True);
    protected static bool ResponseHasInteger(byte[] body, string name, int expected) => Read(body, root =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var actual) && actual == expected);
    private static bool Read(byte[] body, Func<JsonElement, bool> read)
    {
        try { using var document = JsonDocument.Parse(body); return read(document.RootElement); }
        catch (JsonException) { return false; }
    }
    private static bool Valid(NotificationMessage message) => message is not null
        && message.EventId != Guid.Empty && message.Title.Length is > 0 and <= 250
        && message.Body.Length is > 0 and <= 4_000
        && message.Severity is "info" or "warning" or "error"
        && message.OccurredAt != default
        && (message.ActionUrl is null || message.ActionUrl.IsAbsoluteUri
            && message.ActionUrl.Scheme is "http" or "https" && message.ActionUrl.ToString().Length <= 512);
    private static bool ValidEndpoint(Uri endpoint) => endpoint.IsAbsoluteUri
        && endpoint.Scheme is "http" or "https" && string.IsNullOrEmpty(endpoint.UserInfo)
        && string.IsNullOrEmpty(endpoint.Fragment) && endpoint.ToString().Length <= 4_096;
    private static bool TrySplitEndpoint(Uri endpoint, out Uri baseUri, out string path,
        out IReadOnlyDictionary<string, string> query)
    {
        baseUri = new Uri(endpoint.GetLeftPart(UriPartial.Authority) + "/");
        path = endpoint.AbsolutePath.TrimStart('/');
        if (path.Length == 0) path = ".";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in endpoint.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            if (!values.TryAdd(key, Uri.UnescapeDataString(parts.Length == 2 ? parts[1] : string.Empty)))
            { query = values; return false; }
        }
        query = values;
        return true;
    }
    private static string Error(int status) => status switch
    {
        400 => ProviderErrorCodes.InvalidResponse,
        401 => ProviderErrorCodes.Unauthorized,
        403 => ProviderErrorCodes.Forbidden,
        404 => ProviderErrorCodes.NotFound,
        409 => ProviderErrorCodes.UpstreamConflict,
        429 => ProviderErrorCodes.RateLimited,
        _ => ProviderErrorCodes.Unknown,
    };
}
