using System.Net;
using ArrControl.Application.Authorization;
using ArrControl.Application.Providers;

namespace ArrControl.Application.Connections;

public static class InstanceKinds
{
    public static IReadOnlyList<string> All { get; } =
    [
        "bazarr",
        "deluge",
        "emby",
        "jellyseerr",
        "jellyfin",
        "lidarr",
        "nzbget",
        "ombi",
        "overseerr",
        "plex",
        "prowlarr",
        "qbittorrent",
        "radarr",
        "readarr",
        "sabnzbd",
        "sonarr",
        "transmission",
        "whisparr",
    ];

    public static bool IsKnown(string? kind) =>
        kind is not null && All.Contains(kind, StringComparer.Ordinal);
}

public static class ProviderCapabilities
{
    public const string Probe = "probe";
    public const string Library = "library";
    public const string Missing = "missing";
    public const string Queue = "queue";
    public const string History = "history";
    public const string Search = "search";
    public const string Health = "health";
    public const string Indexer = "indexer";
    public const string SubtitleActivity = "subtitle-activity";
    public const string DownloadClient = "download-client";
    public const string Pause = "pause";
    public const string Remove = "remove";
    public const string Retry = "retry";
    public const string MediaServer = "media-server";
    public const string Requests = "requests";
    public const string Tasks = "tasks";
    public const string Notifications = "notifications";

    public static IReadOnlyList<string> All { get; } =
    [
        Probe,
        Library,
        Missing,
        Queue,
        History,
        Search,
        "grab",
        Tasks,
        Health,
        DownloadClient,
        Pause,
        Remove,
        Retry,
        MediaServer,
        Requests,
        Notifications,
        Indexer,
        SubtitleActivity,
    ];

    public static bool IsKnown(string? capability) =>
        capability is not null && All.Contains(capability, StringComparer.Ordinal);
}

public static class InstanceLimits
{
    public const int MaximumNameLength = 120;
    public const int MaximumBaseUrlLength = 2048;
}

public sealed record ProviderCapabilityObservation(
    string Capability,
    bool Supported,
    DateTimeOffset ObservedAt);

public sealed record InstanceDetails(
    Guid Id,
    string Name,
    string Kind,
    string BaseUrl,
    bool Enabled,
    Guid? InstanceGroupId,
    bool TlsVerificationEnabled,
    bool AllowPrivateNetworkAccess,
    IReadOnlyList<ProviderCapabilityObservation> Capabilities,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record InstanceGroupDetails(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ValidatedInstanceInput(
    string Name,
    string Kind,
    Uri BaseUri,
    bool Enabled,
    Guid? InstanceGroupId,
    bool TlsVerificationEnabled,
    bool AllowPrivateNetworkAccess);

public sealed record InstanceScope(bool Exists, Guid? InstanceGroupId);

public enum InstanceWriteStatus
{
    Created,
    Updated,
    NotFound,
    GroupNotFound,
    NameConflict,
    Forbidden,
}

public sealed record InstanceWriteResult(
    InstanceWriteStatus Status,
    InstanceDetails? Instance = null);

public enum InstanceDeleteStatus
{
    Deleted,
    NotFound,
    Forbidden,
}

public enum InstanceGroupWriteStatus
{
    Created,
    Updated,
    NotFound,
    NameConflict,
}

public sealed record InstanceGroupWriteResult(
    InstanceGroupWriteStatus Status,
    InstanceGroupDetails? Group = null);

public enum InstanceGroupDeleteStatus
{
    Deleted,
    NotFound,
    InUse,
}

public sealed record ResolvedOutboundTarget(
    Uri Uri,
    IReadOnlyList<IPAddress> Addresses);

public sealed record ConnectionProbeObservation(
    bool Connected,
    string Outcome,
    int? HttpStatusCode,
    DateTimeOffset ObservedAt,
    IReadOnlyList<ProviderCapabilityObservation> Capabilities,
    string? ProviderVersion = null,
    IReadOnlyList<ProviderHealthIssue>? HealthIssues = null,
    ProviderRateLimitMetadata? RateLimit = null);

public enum InstanceProbeStatus
{
    Completed,
    NotFound,
    Forbidden,
}

public sealed record InstanceProbeResult(
    InstanceProbeStatus Status,
    ConnectionProbeObservation? Probe = null);

public interface IOutboundTargetPolicy
{
    Task<ResolvedOutboundTarget> ResolveAsync(
        Uri uri,
        bool allowPrivateNetworkAccess,
        CancellationToken cancellationToken);
}

public interface IConnectionProbeTransport
{
    Task<ConnectionProbeObservation> ProbeAsync(
        ResolvedOutboundTarget target,
        bool tlsVerificationEnabled,
        CancellationToken cancellationToken);
}

public interface IInstanceManagementStore
{
    Task<InstanceScope> GetScopeAsync(Guid instanceId, CancellationToken cancellationToken);

    Task<bool> InstanceGroupExistsAsync(Guid instanceGroupId, CancellationToken cancellationToken);

    Task<InstanceDetails?> GetAsync(Guid instanceId, CancellationToken cancellationToken);

    Task<InstanceWriteResult> CreateAsync(
        RbacActorContext actor,
        Guid instanceId,
        ValidatedInstanceInput input,
        CancellationToken cancellationToken);

    Task<InstanceWriteResult> UpdateAsync(
        RbacActorContext actor,
        Guid instanceId,
        ValidatedInstanceInput input,
        CancellationToken cancellationToken);

    Task<InstanceDeleteStatus> DeleteAsync(
        RbacActorContext actor,
        Guid instanceId,
        CancellationToken cancellationToken);

    Task SaveProbeAsync(
        RbacActorContext actor,
        Guid instanceId,
        ConnectionProbeObservation observation,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InstanceGroupDetails>> ListGroupsAsync(CancellationToken cancellationToken);

    Task<InstanceGroupWriteResult> UpsertGroupAsync(
        RbacActorContext actor,
        Guid instanceGroupId,
        string name,
        CancellationToken cancellationToken);

    Task<InstanceGroupDeleteStatus> DeleteGroupAsync(
        RbacActorContext actor,
        Guid instanceGroupId,
        CancellationToken cancellationToken);
}

public sealed class InstanceValidationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class OutboundTargetRejectedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
