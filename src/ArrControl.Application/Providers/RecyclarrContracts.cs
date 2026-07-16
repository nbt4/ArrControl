namespace ArrControl.Application.Providers;

public sealed record RecyclarrSyncRequest(
    string? Service,
    IReadOnlyList<string> InstanceNames,
    bool Preview);

public sealed record RecyclarrVersionResult(
    bool Success,
    string? Version,
    string? ErrorCode);

public sealed record RecyclarrSyncResult(
    bool Success,
    bool Preview,
    int? ExitCode,
    string? ErrorCode);

public interface IRecyclarrCommandClient
{
    Task<RecyclarrVersionResult> GetVersionAsync(CancellationToken cancellationToken);

    Task<RecyclarrSyncResult> SyncAsync(
        RecyclarrSyncRequest request,
        CancellationToken cancellationToken);
}
