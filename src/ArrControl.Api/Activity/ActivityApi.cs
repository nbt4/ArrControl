using ArrControl.Api.Authorization;
using ArrControl.Api.Identity;
using ArrControl.Application.Activity;
using ArrControl.Application.Authorization;
using ArrControl.Application.Automation;
using ArrControl.Infrastructure.Activity;
using ArrControl.Infrastructure.Automation;
using Microsoft.AspNetCore.Mvc;

namespace ArrControl.Api.Activity;

public static class ActivityApi
{
    public static IServiceCollection AddActivitySynchronization(this IServiceCollection services)
    {
        services.AddSingleton<IProviderActivityClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.SonarrClient>());
        services.AddSingleton<IProviderActivityClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.RadarrClient>());
        services.AddSingleton<IProviderActivityClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.LidarrClient>());
        services.AddSingleton<IProviderActivityClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.ReadarrClient>());
        services.AddSingleton<IProviderActivityClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.WhisparrClient>());
        services.AddSingleton<IProviderActivityClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.ProwlarrClient>());
        services.AddSingleton<IProviderActivityClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.SabnzbdClient>());
        services.AddSingleton<IProviderActivityClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.NzbGetClient>());
        services.AddSingleton<IProviderActivityClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.QBittorrentClient>());
        services.AddSingleton<IProviderActivityClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.TransmissionClient>());
        services.AddSingleton<IProviderActivityClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.DelugeClient>());
        services.AddScoped<IActivitySnapshotStore, EfActivitySnapshotStore>();
        services.AddScoped<IActivityQueryStore, EfActivityQueryStore>();
        services.AddScoped<ActivityQueryService>();
        services.AddScoped<IActivityScheduleProvisioner, EfActivityScheduleProvisioner>();
        services.AddScoped<IScheduledJobHandler, ActivitySyncJobHandler>();
        services.AddHostedService<ActivityScheduleHostedService>();
        return services;
    }

    public static IEndpointRouteBuilder MapActivity(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/queue", GetQueueAsync)
            .WithName("listQueue").WithTags("Activity")
            .RequireAuthorization(RbacPolicyNames.AnyScope(RbacPermissions.InstancesRead))
            .Produces<AggregatedQueueItem[]>();
        endpoints.MapGet("/api/v1/history", GetHistoryAsync)
            .WithName("listHistory").WithTags("Activity")
            .RequireAuthorization(RbacPolicyNames.AnyScope(RbacPermissions.InstancesRead))
            .Produces<AggregatedHistoryItem[]>();
        return endpoints;
    }

    private static async Task<IResult> GetQueueAsync(
        HttpContext context,
        ActivityQueryService service,
        [FromQuery(Name = "instanceId")] Guid[]? instanceIds = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await QueryAsync(context, service, instanceIds, 1000, cancellationToken);
        return snapshot is null ? Forbidden(context) : Results.Ok(snapshot.Queue);
    }

    private static async Task<IResult> GetHistoryAsync(
        HttpContext context,
        ActivityQueryService service,
        [FromQuery(Name = "instanceId")] Guid[]? instanceIds = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await QueryAsync(context, service, instanceIds, limit, cancellationToken);
            return snapshot is null ? Forbidden(context) : Results.Ok(snapshot.History);
        }
        catch (ArgumentOutOfRangeException)
        {
            return AuthApiProblem.Create(context, 400, "The activity query is invalid.", "activity_filter_invalid");
        }
    }

    private static Task<ActivitySnapshot?> QueryAsync(
        HttpContext context,
        ActivityQueryService service,
        Guid[]? instanceIds,
        int historyLimit,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetIdentity(context, out var identity))
        {
            return Task.FromResult<ActivitySnapshot?>(null);
        }

        context.Response.Headers.CacheControl = "private, no-cache";
        return service.QueryAsync(
            identity.UserId, identity.SessionId, instanceIds ?? [], historyLimit, cancellationToken);
    }

    private static IResult Forbidden(HttpContext context) =>
        AuthApiProblem.Create(context, 403, "Access denied.", "access_denied");
}

public sealed class ActivityScheduleHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ActivityScheduleHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) await ReconcileAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task ReconcileAsync(CancellationToken token)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var count = await scope.ServiceProvider.GetRequiredService<IActivityScheduleProvisioner>()
                .ReconcileAsync(token);
            if (count > 0) logger.LogInformation("Activity schedule reconciliation changed {ScheduleCount} schedule(s).", count);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning("Activity schedule reconciliation failed with error type {ErrorType}.", exception.GetType().Name);
        }
    }
}
