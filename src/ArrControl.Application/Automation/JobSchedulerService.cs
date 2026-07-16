using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ArrControl.Application.Automation;

public sealed class JobSchedulerService(
    IJobSchedulerStore store,
    ICronScheduleCalculator cronCalculator,
    JobSchedulerSettings settings)
{
    private const int MaximumOccurrencesPerSchedule = 2_000;

    public async Task<int> MaterializeAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var schedules = await store.ListEnabledSchedulesAsync(cancellationToken);
        var horizon = now + settings.MaterializationHorizon;
        var created = 0;
        foreach (var schedule in schedules)
        {
            var cursor = schedule.LastEnqueuedAt ?? now;
            var occurrences = new List<ScheduledOccurrence>();
            while (occurrences.Count < MaximumOccurrencesPerSchedule)
            {
                var next = cronCalculator.GetNextOccurrence(
                    schedule.Cron,
                    schedule.TimeZone,
                    cursor);
                if (next is null || next > horizon)
                {
                    break;
                }

                if (next <= cursor)
                {
                    throw new InvalidOperationException("Cron calculator did not advance the schedule cursor.");
                }

                occurrences.Add(new ScheduledOccurrence(
                    next.Value,
                    next.Value + DeterministicJitter(schedule.ScheduleId, next.Value, settings.MaximumJitter)));
                cursor = next.Value;
            }

            if (occurrences.Count == MaximumOccurrencesPerSchedule)
            {
                throw new InvalidOperationException("Schedule materialization exceeded its occurrence safety bound.");
            }

            if (occurrences.Count > 0
                && await store.TryEnqueueAsync(
                    schedule.ScheduleId,
                    schedule.LastEnqueuedAt,
                    occurrences,
                    cancellationToken))
            {
                created += occurrences.Count;
            }
        }

        return created;
    }

    public DateTimeOffset? GetRetryAt(ClaimedJob job, DateTimeOffset now)
    {
        if (job.Attempt >= settings.MaximumAttempts)
        {
            return null;
        }

        var exponent = Math.Min(job.Attempt - 1, 30);
        var multiplier = 1L << exponent;
        var ticks = settings.InitialRetryDelay.Ticks > settings.MaximumRetryDelay.Ticks / multiplier
            ? settings.MaximumRetryDelay.Ticks
            : settings.InitialRetryDelay.Ticks * multiplier;
        var delay = TimeSpan.FromTicks(Math.Min(ticks, settings.MaximumRetryDelay.Ticks));
        return now + delay + DeterministicJitter(job.JobId, now, settings.MaximumJitter);
    }

    private static TimeSpan DeterministicJitter(
        Guid key,
        DateTimeOffset instant,
        TimeSpan maximum)
    {
        if (maximum == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        Span<byte> input = stackalloc byte[24];
        key.TryWriteBytes(input[..16]);
        BinaryPrimitives.WriteInt64BigEndian(input[16..], instant.UtcTicks);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        var sample = BinaryPrimitives.ReadUInt64BigEndian(digest);
        var ticks = (long)(sample % ((ulong)maximum.Ticks + 1UL));
        return TimeSpan.FromTicks(ticks);
    }
}
