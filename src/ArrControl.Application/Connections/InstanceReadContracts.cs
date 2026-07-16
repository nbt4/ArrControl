namespace ArrControl.Application.Connections;

public sealed record VisibleInstance(
    Guid Id,
    string Name,
    string Kind,
    string BaseUrl,
    bool Enabled,
    Guid? InstanceGroupId,
    bool TlsVerificationEnabled,
    bool AllowPrivateNetworkAccess,
    bool CredentialsConfigured,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface IScopedInstanceReadStore
{
    Task<IReadOnlyList<VisibleInstance>> ListAsync(
        bool includeAll,
        IReadOnlyCollection<Guid> instanceGroupIds,
        CancellationToken cancellationToken);
}
