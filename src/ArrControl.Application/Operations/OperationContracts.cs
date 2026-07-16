using ArrControl.Application.Authorization;

namespace ArrControl.Application.Operations;

public static class OperationStates
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public static class OperationTargetStates
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Skipped = "skipped";
}

public sealed record OperationTargetInput(Guid InstanceId, string TargetKey);

public sealed record CreateOperationCommand(
    RbacActorContext Actor,
    string Type,
    string Route,
    string IdempotencyKey,
    string RequestHash,
    bool DryRun,
    IReadOnlyList<OperationTargetInput> Targets);

public sealed record OperationTarget(
    Guid InstanceId,
    string TargetKey,
    string State,
    string? ErrorCode,
    string? ResultJson,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record OperationDetails(
    Guid Id,
    string Type,
    string State,
    bool DryRun,
    bool CancellationRequested,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<OperationTarget> Targets);

public enum CreateOperationStatus { Created, Replayed, IdempotencyConflict, Invalid }

public sealed record CreateOperationResult(CreateOperationStatus Status, OperationDetails? Operation = null);

public interface IOperationStore
{
    Task<CreateOperationResult> CreateAsync(CreateOperationCommand command, CancellationToken cancellationToken);
    Task<OperationDetails?> GetAsync(Guid actorUserId, Guid operationId, CancellationToken cancellationToken);
    Task<bool> RequestCancellationAsync(RbacActorContext actor, Guid operationId, CancellationToken cancellationToken);
    Task<bool> TryStartAsync(Guid operationId, CancellationToken cancellationToken);
    Task<bool> CompleteTargetAsync(
        Guid operationId,
        Guid instanceId,
        string targetKey,
        bool succeeded,
        string? errorCode,
        string? resultJson,
        CancellationToken cancellationToken);
    Task<bool> CompleteAsync(Guid operationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> ListPendingAsync(string type, int maximumCount, CancellationToken cancellationToken);
    Task<OperationDetails?> GetForExecutionAsync(Guid operationId, CancellationToken cancellationToken);
}

public sealed class OperationService(IOperationStore store)
{
    public Task<OperationDetails?> GetAsync(Guid userId, Guid operationId, CancellationToken token) =>
        store.GetAsync(userId, operationId, token);

    public Task<bool> CancelAsync(RbacActorContext actor, Guid operationId, CancellationToken token) =>
        store.RequestCancellationAsync(actor, operationId, token);

    public Task<CreateOperationResult> CreateAsync(
        CreateOperationCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Type) || command.Type.Length > 64
            || string.IsNullOrWhiteSpace(command.Route) || command.Route.Length > 200
            || string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 128
            || command.IdempotencyKey.Any(char.IsControl)
            || command.RequestHash.Length != 64 || command.RequestHash.Any(value => !char.IsAsciiHexDigit(value))
            || command.Targets.Count is < 1 or > 10_000
            || command.Targets.Any(value => value.InstanceId == Guid.Empty
                || string.IsNullOrWhiteSpace(value.TargetKey) || value.TargetKey.Length > 200)
            || command.Targets.DistinctBy(value => (value.InstanceId, value.TargetKey)).Count() != command.Targets.Count)
        {
            return Task.FromResult(new CreateOperationResult(CreateOperationStatus.Invalid));
        }

        return store.CreateAsync(command with
        {
            Type = command.Type.Trim(),
            Route = command.Route.Trim(),
            IdempotencyKey = command.IdempotencyKey.Trim(),
        }, cancellationToken);
    }
}
