namespace ArrControl.Application.Automation;

public static class JobRunStates
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Retry = "retry";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public sealed record JobSchedulerSettings(
    TimeSpan PollInterval,
    TimeSpan PlanningInterval,
    TimeSpan MaterializationHorizon,
    TimeSpan LeaseDuration,
    TimeSpan HandlerTimeout,
    TimeSpan InitialRetryDelay,
    TimeSpan MaximumRetryDelay,
    TimeSpan MaximumJitter,
    int MaximumConcurrency,
    int MaximumAttempts,
    int ClaimBatchSize)
{
    public static JobSchedulerSettings Default { get; } = new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromSeconds(10),
        4,
        5,
        8);

    public void Validate()
    {
        if (PollInterval < TimeSpan.FromMilliseconds(100) || PollInterval > TimeSpan.FromMinutes(1)
            || PlanningInterval < TimeSpan.FromSeconds(1) || PlanningInterval > TimeSpan.FromMinutes(10)
            || MaterializationHorizon < TimeSpan.FromMinutes(1) || MaterializationHorizon > TimeSpan.FromDays(1)
            || LeaseDuration < TimeSpan.FromSeconds(5) || LeaseDuration > TimeSpan.FromMinutes(10)
            || HandlerTimeout < TimeSpan.FromSeconds(1) || HandlerTimeout > TimeSpan.FromHours(1)
            || InitialRetryDelay < TimeSpan.FromSeconds(1) || InitialRetryDelay > TimeSpan.FromMinutes(10)
            || MaximumRetryDelay < InitialRetryDelay
            || MaximumRetryDelay > TimeSpan.FromHours(6)
            || MaximumJitter < TimeSpan.Zero
            || MaximumJitter > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException("Automation scheduler duration settings are outside safe bounds.");
        }

        if (MaximumConcurrency is < 1 or > 64
            || MaximumAttempts is < 1 or > 100
            || ClaimBatchSize is < 1 or > 128
            || ClaimBatchSize < MaximumConcurrency)
        {
            throw new InvalidOperationException("Automation scheduler count settings are outside safe bounds.");
        }
    }
}

public sealed record SchedulePlanningState(
    Guid ScheduleId,
    string Type,
    string Cron,
    string TimeZone,
    DateTimeOffset? LastEnqueuedAt);

public sealed record ScheduledOccurrence(
    DateTimeOffset ScheduledFor,
    DateTimeOffset AvailableAt);

public sealed record ClaimedJob(
    Guid JobId,
    Guid ScheduleId,
    string Type,
    string ScopeJson,
    int Attempt,
    Guid LeaseToken,
    DateTimeOffset LeaseUntil);

public sealed record SyncCheckpointUpdate(
    Guid InstanceId,
    string Stream,
    string? Cursor);

public sealed record JobHandlerResult(IReadOnlyList<SyncCheckpointUpdate> Checkpoints)
{
    public static JobHandlerResult Completed { get; } = new([]);
}

public interface IScheduledJobHandler
{
    string Type { get; }

    Task<JobHandlerResult> ExecuteAsync(
        ClaimedJob job,
        CancellationToken cancellationToken);
}

public interface ICronScheduleCalculator
{
    DateTimeOffset? GetNextOccurrence(
        string expression,
        string timeZone,
        DateTimeOffset after);
}

public interface IJobSchedulerStore
{
    Task<IReadOnlyList<SchedulePlanningState>> ListEnabledSchedulesAsync(
        CancellationToken cancellationToken);

    Task<bool> TryEnqueueAsync(
        Guid scheduleId,
        DateTimeOffset? expectedLastEnqueuedAt,
        IReadOnlyList<ScheduledOccurrence> occurrences,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ClaimedJob>> ClaimAsync(
        string leaseOwner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        int maximumAttempts,
        int maximumCount,
        CancellationToken cancellationToken);

    Task<bool> RenewAsync(
        Guid jobId,
        Guid leaseToken,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> CompleteAsync(
        ClaimedJob job,
        DateTimeOffset completedAt,
        IReadOnlyList<SyncCheckpointUpdate> checkpoints,
        CancellationToken cancellationToken);

    Task<bool> FailAsync(
        ClaimedJob job,
        string errorCode,
        DateTimeOffset failedAt,
        DateTimeOffset? retryAt,
        CancellationToken cancellationToken);

    Task<bool> AbandonAsync(
        ClaimedJob job,
        DateTimeOffset availableAt,
        CancellationToken cancellationToken);
}

public sealed class ScheduledJobException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
