using ArrControl.Application.Automation;
using ArrControl.Infrastructure.Automation;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class JobSchedulerServiceTests
{
    [Fact]
    public void Cron_calculator_supports_second_precision_for_queue_polling()
    {
        var calculator = new ArrControl.Infrastructure.Automation.CronosScheduleCalculator();
        var after = DateTimeOffset.Parse("2026-07-16T13:00:00Z");

        Assert.Equal(
            after.AddSeconds(30),
            calculator.GetNextOccurrence("*/30 * * * * *", "UTC", after));
    }

    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly JobSchedulerSettings Settings = JobSchedulerSettings.Default with
    {
        MaterializationHorizon = TimeSpan.FromMinutes(3),
        MaximumJitter = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public async Task Materialization_is_bounded_deterministic_and_advances_from_the_durable_cursor()
    {
        var scheduleId = Guid.Parse("01981f91-3b80-7000-8000-000000000001");
        var schedule = new SchedulePlanningState(
            scheduleId, "test.poll", "ignored", "UTC", Now);
        var firstStore = new RecordingStore(schedule);
        var secondStore = new RecordingStore(schedule);
        var first = new JobSchedulerService(firstStore, new MinuteCron(), Settings);
        var second = new JobSchedulerService(secondStore, new MinuteCron(), Settings);

        Assert.Equal(3, await first.MaterializeAsync(Now, CancellationToken.None));
        Assert.Equal(3, await second.MaterializeAsync(Now, CancellationToken.None));
        Assert.Equal(firstStore.Occurrences, secondStore.Occurrences);
        Assert.Equal(
            [Now.AddMinutes(1), Now.AddMinutes(2), Now.AddMinutes(3)],
            firstStore.Occurrences.Select(value => value.ScheduledFor));
        Assert.All(firstStore.Occurrences, value =>
            Assert.InRange(value.AvailableAt - value.ScheduledFor, TimeSpan.Zero, Settings.MaximumJitter));
    }

    [Fact]
    public void Retry_uses_capped_exponential_backoff_and_stops_at_the_attempt_limit()
    {
        var scheduler = new JobSchedulerService(new RecordingStore(), new MinuteCron(), Settings);
        var job = Job(attempt: 1);
        var first = scheduler.GetRetryAt(job, Now);
        var fourth = scheduler.GetRetryAt(job with { Attempt = 4 }, Now);

        Assert.NotNull(first);
        Assert.InRange(first.Value - Now, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        Assert.NotNull(fourth);
        Assert.InRange(fourth.Value - Now, TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(45));
        Assert.Null(scheduler.GetRetryAt(job with { Attempt = Settings.MaximumAttempts }, Now));
    }

    [Fact]
    public async Task Execution_completes_only_through_the_fenced_store_boundary()
    {
        var store = new RecordingStore();
        var clock = new FixedTimeProvider(Now);
        var engine = new JobExecutionEngine(
            store,
            new JobSchedulerService(store, new MinuteCron(), Settings),
            [new SuccessfulHandler()],
            Settings,
            clock);

        var outcome = await engine.ExecuteAsync(Job(), CancellationToken.None);

        Assert.Equal(JobExecutionOutcome.Succeeded, outcome);
        Assert.True(store.Completed);
        Assert.Equal("catalog", Assert.Single(store.Checkpoints).Stream);
    }

    [Fact]
    public void Cron_calculation_respects_the_schedule_timezone()
    {
        var next = new CronosScheduleCalculator().GetNextOccurrence(
            "0 9 * * *",
            "Europe/Berlin",
            Now);

        Assert.Equal(new DateTimeOffset(2026, 7, 17, 7, 0, 0, TimeSpan.Zero), next);
    }

    private static ClaimedJob Job(int attempt = 1) => new(
        Guid.Parse("01981f91-3b80-7000-8000-000000000010"),
        Guid.Parse("01981f91-3b80-7000-8000-000000000011"),
        "test.poll",
        "{}",
        attempt,
        Guid.Parse("01981f91-3b80-7000-8000-000000000012"),
        Now.AddMinutes(1));

    private sealed class MinuteCron : ICronScheduleCalculator
    {
        public DateTimeOffset? GetNextOccurrence(
            string expression,
            string timeZone,
            DateTimeOffset after) => after.AddMinutes(1);
    }

    private sealed class SuccessfulHandler : IScheduledJobHandler
    {
        public string Type => "test.poll";

        public Task<JobHandlerResult> ExecuteAsync(
            ClaimedJob job,
            CancellationToken cancellationToken) =>
            Task.FromResult(new JobHandlerResult([
                new SyncCheckpointUpdate(
                    Guid.Parse("01981f91-3b80-7000-8000-000000000020"),
                    "catalog",
                    "cursor-1"),
            ]));
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class RecordingStore(params SchedulePlanningState[] schedules)
        : IJobSchedulerStore
    {
        public IReadOnlyList<ScheduledOccurrence> Occurrences { get; private set; } = [];
        public bool Completed { get; private set; }
        public IReadOnlyList<SyncCheckpointUpdate> Checkpoints { get; private set; } = [];

        public Task<IReadOnlyList<SchedulePlanningState>> ListEnabledSchedulesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SchedulePlanningState>>(schedules);

        public Task<bool> TryEnqueueAsync(
            Guid scheduleId,
            DateTimeOffset? expectedLastEnqueuedAt,
            IReadOnlyList<ScheduledOccurrence> occurrences,
            CancellationToken cancellationToken)
        {
            Occurrences = occurrences;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<ClaimedJob>> ClaimAsync(
            string leaseOwner,
            DateTimeOffset now,
            TimeSpan leaseDuration,
            int maximumAttempts,
            int maximumCount,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClaimedJob>>([]);

        public Task<bool> RenewAsync(
            Guid jobId,
            Guid leaseToken,
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> CompleteAsync(
            ClaimedJob job,
            DateTimeOffset completedAt,
            IReadOnlyList<SyncCheckpointUpdate> checkpoints,
            CancellationToken cancellationToken)
        {
            Completed = true;
            Checkpoints = checkpoints;
            return Task.FromResult(true);
        }

        public Task<bool> FailAsync(
            ClaimedJob job,
            string errorCode,
            DateTimeOffset failedAt,
            DateTimeOffset? retryAt,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> AbandonAsync(
            ClaimedJob job,
            DateTimeOffset availableAt,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
