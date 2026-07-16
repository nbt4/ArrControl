using System.Text.Json.Serialization;

namespace ArrControl.Application.Providers;

public static class NotificationProviderKinds
{
    public static IReadOnlyList<string> All { get; } =
        ["email", "webhook", "discord", "slack", "teams", "telegram", "ntfy", "gotify", "pushover"];
}

public sealed record NotificationMessage(
    Guid EventId,
    string Title,
    string Body,
    string Severity,
    DateTimeOffset OccurredAt,
    Uri? ActionUrl = null);

public sealed class NotificationTarget(
    Uri endpoint,
    bool tlsVerificationEnabled,
    bool allowPrivateNetworkAccess,
    IReadOnlyDictionary<string, string>? settings = null,
    IReadOnlyDictionary<string, string>? secrets = null)
{
    private readonly IReadOnlyDictionary<string, string> settings =
        settings ?? new Dictionary<string, string>();
    private readonly IReadOnlyDictionary<string, string> secrets =
        secrets ?? new Dictionary<string, string>();

    [JsonIgnore] public Uri Endpoint { get; } = endpoint;
    public bool TlsVerificationEnabled { get; } = tlsVerificationEnabled;
    public bool AllowPrivateNetworkAccess { get; } = allowPrivateNetworkAccess;
    public bool TryGetSetting(string name, out string value) => settings.TryGetValue(name, out value!);
    public bool TryGetSecret(string name, out string value) => secrets.TryGetValue(name, out value!);
    public override string ToString() => "NotificationTarget { [REDACTED] }";
}

public sealed record NotificationSendResult(
    bool Success,
    string? ErrorCode,
    int? HttpStatusCode = null);

public interface INotificationProvider
{
    string Kind { get; }
    IReadOnlyList<string> RequiredSettings { get; }
    IReadOnlyList<string> RequiredSecrets { get; }
    Task<NotificationSendResult> SendAsync(
        NotificationTarget target,
        NotificationMessage message,
        CancellationToken cancellationToken);
}

public sealed record SmtpNotificationRequest(
    string Host,
    int Port,
    bool AllowPrivateNetworkAccess,
    string Username,
    [property: JsonIgnore] string Password,
    string From,
    string To,
    string Subject,
    string Body)
{
    public override string ToString() =>
        $"SmtpNotificationRequest {{ Host = [REDACTED], Port = {Port}, Username = [REDACTED], Password = [REDACTED], From = [REDACTED], To = [REDACTED] }}";
}

public interface ISmtpNotificationTransport
{
    Task<NotificationSendResult> SendAsync(
        SmtpNotificationRequest request,
        CancellationToken cancellationToken);
}
