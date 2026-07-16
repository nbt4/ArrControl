namespace ArrControl.Application.Automation;

public enum JobExecutionOutcome
{
    Succeeded,
    RetryScheduled,
    Failed,
    LeaseLost,
    Abandoned,
}

public sealed class JobExecutionEngine(
    IJobSchedulerStore store,
    JobSchedulerService scheduler,
    IEnumerable<IScheduledJobHandler> handlers,
    JobSchedulerSettings settings,
    TimeProvider timeProvider)
{
    public async Task<JobExecutionOutcome> ExecuteAsync(
        ClaimedJob job,
        CancellationToken stoppingToken)
    {
        var handler = handlers.SingleOrDefault(value => value.Type == job.Type);
        if (handler is null)
        {
            return await FailAsync(job, "handler_not_registered", CancellationToken.None);
        }

        using var timedOut = new CancellationTokenSource(settings.HandlerTimeout);
        using var leaseLost = new CancellationTokenSource();
        using var renewalStopped = new CancellationTokenSource();
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            timedOut.Token,
            leaseLost.Token);
        var renewal = RenewLeaseAsync(job, leaseLost, renewalStopped.Token, stoppingToken);
        try
        {
            var result = await handler.ExecuteAsync(job, execution.Token);
            renewalStopped.Cancel();
            await IgnoreRenewalCancellationAsync(renewal);
            if (leaseLost.IsCancellationRequested)
            {
                return JobExecutionOutcome.LeaseLost;
            }

            var completed = await store.CompleteAsync(
                job,
                timeProvider.GetUtcNow(),
                result.Checkpoints,
                stoppingToken);
            return completed ? JobExecutionOutcome.Succeeded : JobExecutionOutcome.LeaseLost;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            renewalStopped.Cancel();
            await IgnoreRenewalCancellationAsync(renewal);
            await store.AbandonAsync(job, timeProvider.GetUtcNow(), CancellationToken.None);
            return JobExecutionOutcome.Abandoned;
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
        {
            return JobExecutionOutcome.LeaseLost;
        }
        catch (OperationCanceledException) when (timedOut.IsCancellationRequested)
        {
            return await FailAsync(job, "timeout", stoppingToken);
        }
        catch (ScheduledJobException exception)
        {
            return await FailAsync(job, exception.Code, stoppingToken);
        }
        catch
        {
            return await FailAsync(job, "handler_failed", stoppingToken);
        }
        finally
        {
            renewalStopped.Cancel();
            await IgnoreRenewalCancellationAsync(renewal);
        }
    }

    private async Task<JobExecutionOutcome> FailAsync(
        ClaimedJob job,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var retryAt = scheduler.GetRetryAt(job, now);
        var updated = await store.FailAsync(
            job,
            errorCode,
            now,
            retryAt,
            cancellationToken);
        if (!updated)
        {
            return JobExecutionOutcome.LeaseLost;
        }

        return retryAt is null ? JobExecutionOutcome.Failed : JobExecutionOutcome.RetryScheduled;
    }

    private async Task RenewLeaseAsync(
        ClaimedJob job,
        CancellationTokenSource leaseLost,
        CancellationToken renewalStopped,
        CancellationToken stoppingToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            renewalStopped,
            stoppingToken);
        var interval = TimeSpan.FromTicks(settings.LeaseDuration.Ticks / 3);
        try
        {
            while (true)
            {
                await Task.Delay(interval, timeProvider, cancellation.Token);
                var renewed = await store.RenewAsync(
                    job.JobId,
                    job.LeaseToken,
                    timeProvider.GetUtcNow(),
                    settings.LeaseDuration,
                    cancellation.Token);
                if (!renewed)
                {
                    leaseLost.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch
        {
            leaseLost.Cancel();
        }
    }

    private static async Task IgnoreRenewalCancellationAsync(Task renewal)
    {
        try
        {
            await renewal;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
