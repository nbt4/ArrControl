using ArrControl.Application.Automation;
using ArrControl.Api.Authorization;
using ArrControl.Api.Identity;
using ArrControl.Application.Authorization;
using ArrControl.Infrastructure.Automation;
using Microsoft.AspNetCore.Mvc;

namespace ArrControl.Api.Automation;

public static class AutomationSchedulerApi
{
    public static IServiceCollection AddAutomationScheduler(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var defaults = JobSchedulerSettings.Default;
        var settings = new JobSchedulerSettings(
            ReadTimeSpan(configuration, "Automation:Scheduler:PollInterval", defaults.PollInterval),
            ReadTimeSpan(configuration, "Automation:Scheduler:PlanningInterval", defaults.PlanningInterval),
            ReadTimeSpan(configuration, "Automation:Scheduler:MaterializationHorizon", defaults.MaterializationHorizon),
            ReadTimeSpan(configuration, "Automation:Scheduler:LeaseDuration", defaults.LeaseDuration),
            ReadTimeSpan(configuration, "Automation:Scheduler:HandlerTimeout", defaults.HandlerTimeout),
            ReadTimeSpan(configuration, "Automation:Scheduler:InitialRetryDelay", defaults.InitialRetryDelay),
            ReadTimeSpan(configuration, "Automation:Scheduler:MaximumRetryDelay", defaults.MaximumRetryDelay),
            ReadTimeSpan(configuration, "Automation:Scheduler:MaximumJitter", defaults.MaximumJitter),
            ReadInt(configuration, "Automation:Scheduler:MaximumConcurrency", defaults.MaximumConcurrency),
            ReadInt(configuration, "Automation:Scheduler:MaximumAttempts", defaults.MaximumAttempts),
            ReadInt(configuration, "Automation:Scheduler:ClaimBatchSize", defaults.ClaimBatchSize));
        settings.Validate();

        services.AddSingleton(settings);
        services.AddSingleton<ICronScheduleCalculator, CronosScheduleCalculator>();
        services.AddScoped<IJobSchedulerStore, EfJobSchedulerStore>();
        services.AddScoped<IJobControlStore>(provider =>
            (EfJobSchedulerStore)provider.GetRequiredService<IJobSchedulerStore>());
        services.AddScoped<JobSchedulerService>();
        services.AddScoped<JobControlService>();
        services.AddScoped<JobExecutionEngine>();
        services.AddHostedService<AutomationSchedulerHostedService>();
        return services;
    }

    public static IEndpointRouteBuilder MapAutomationJobs(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/automation/jobs").WithTags("Automation")
            .RequireAuthorization(RbacPolicyNames.Global(RbacPermissions.TasksExecute));
        group.MapGet("", ListAsync)
            .WithName("listAutomationJobs")
            .Produces<JobScheduleDetails[]>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        group.MapPost("/{scheduleId:guid}/start", StartAsync)
            .WithName("startAutomationJob")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(0))
            .Produces<ManualJobStartResult>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        JobControlService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListAsync(cancellationToken));

    private static async Task<IResult> StartAsync(
        Guid scheduleId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        HttpContext context,
        JobControlService service,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetActor(context, out var actor))
            return Problem(context, StatusCodes.Status401Unauthorized, "authentication_required");
        try
        {
            var result = await service.StartAsync(scheduleId, idempotencyKey ?? string.Empty, actor, cancellationToken);
            return result is null
                ? Problem(context, StatusCodes.Status404NotFound, "automation_schedule_not_found")
                : Results.Accepted($"/api/v1/automation/jobs/{scheduleId}", result);
        }
        catch (ArgumentException)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "automation_job_request_invalid");
        }
    }

    private static IResult Problem(HttpContext context, int status, string code) =>
        AuthApiProblem.Create(context, status, "The automation job request could not be completed.", code);

    private static TimeSpan ReadTimeSpan(
        IConfiguration configuration,
        string key,
        TimeSpan fallback)
    {
        var value = configuration[key];
        if (value is null)
        {
            return fallback;
        }

        if (!TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException($"Configuration setting '{key}' is not a valid duration.");
        }

        return parsed;
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback)
    {
        var value = configuration[key];
        if (value is null)
        {
            return fallback;
        }

        if (!int.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new InvalidOperationException($"Configuration setting '{key}' is not a valid integer.");
        }

        return parsed;
    }
}

public sealed class AutomationSchedulerHostedService(
    IServiceScopeFactory scopeFactory,
    JobSchedulerSettings settings,
    TimeProvider timeProvider,
    ILogger<AutomationSchedulerHostedService> logger) : BackgroundService
{
    private readonly string leaseOwner = $"worker-{Guid.CreateVersion7():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new HashSet<Task>();
        var nextPlanningAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var completed in running.Where(task => task.IsCompleted).ToArray())
            {
                running.Remove(completed);
                await ObserveAsync(completed);
            }

            var now = timeProvider.GetUtcNow();
            if (now >= nextPlanningAt)
            {
                await TryMaterializeAsync(now, stoppingToken);
                nextPlanningAt = now + settings.PlanningInterval;
            }

            var capacity = settings.MaximumConcurrency - running.Count;
            if (capacity > 0)
            {
                var jobs = await TryClaimAsync(now, Math.Min(capacity, settings.ClaimBatchSize), stoppingToken);
                foreach (var job in jobs)
                {
                    running.Add(ExecuteJobAsync(job, stoppingToken));
                }
            }

            try
            {
                if (running.Count == 0)
                {
                    await Task.Delay(settings.PollInterval, timeProvider, stoppingToken);
                }
                else
                {
                    await Task.WhenAny(
                        Task.WhenAny(running),
                        Task.Delay(settings.PollInterval, timeProvider, stoppingToken));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        await Task.WhenAll(running.Select(ObserveAsync));
    }

    private async Task TryMaterializeAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var count = await scope.ServiceProvider.GetRequiredService<JobSchedulerService>()
                .MaterializeAsync(now, cancellationToken);
            if (count > 0)
            {
                logger.LogInformation("Automation scheduler materialized {JobCount} job run(s).", count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Automation schedule planning failed with error type {ErrorType}.",
                exception.GetType().Name);
        }
    }

    private async Task<IReadOnlyList<ClaimedJob>> TryClaimAsync(
        DateTimeOffset now,
        int capacity,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IJobSchedulerStore>().ClaimAsync(
                leaseOwner,
                now,
                settings.LeaseDuration,
                settings.MaximumAttempts,
                capacity,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Automation job claim failed with error type {ErrorType}.",
                exception.GetType().Name);
            return [];
        }
    }

    private async Task ExecuteJobAsync(ClaimedJob job, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<JobExecutionEngine>()
                .ExecuteAsync(job, stoppingToken);
            logger.LogInformation(
                "Automation job {JobId} attempt {Attempt} completed with outcome {Outcome}.",
                job.JobId,
                job.Attempt,
                outcome);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Automation job {JobId} ended with unhandled error type {ErrorType}.",
                job.JobId,
                exception.GetType().Name);
        }
    }

    private async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Automation worker task ended with error type {ErrorType}.",
                exception.GetType().Name);
        }
    }
}
