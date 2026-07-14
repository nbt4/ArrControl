using ArrControl.Domain;

namespace ArrControl.Application.Providers;

public interface IServiceProviderAdapter
{
    ServiceKind Kind { get; }
    ProviderCapabilities Capabilities { get; }
    Task<ProviderProbeResult> ProbeAsync(ProviderConnection connection, CancellationToken cancellationToken);
}

public sealed record ProviderConnection(Uri BaseUri, string SecretReference);
public sealed record ProviderProbeResult(bool IsHealthy, string? Version, string? ErrorCode);
public sealed record ProviderCapabilities(bool Library, bool Missing, bool Queue, bool Search, bool Health, bool Tasks, bool History);
