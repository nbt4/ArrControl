using ArrControl.Api.Authorization;
using ArrControl.Api.Identity;
using ArrControl.Application.Authorization;
using ArrControl.Application.Automation;
using ArrControl.Application.Health;
using ArrControl.Application.Providers;
using ArrControl.Infrastructure.Automation;
using ArrControl.Infrastructure.Health;
using Microsoft.AspNetCore.Mvc;

namespace ArrControl.Api.Health;

public sealed record SetHealthAcknowledgementRequest(bool Acknowledged);

public sealed record SetHealthSnoozeRequest(DateTimeOffset? SnoozedUntil);

public static class HealthIncidentApi
{
    public static IServiceCollection AddHealthIncidents(this IServiceCollection services)
    {
        services.AddSingleton<IArrProviderClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.SonarrClient>());
        services.AddSingleton<IArrProviderClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.RadarrClient>());
        services.AddSingleton<IArrProviderClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.LidarrClient>());
        services.AddSingleton<IArrProviderClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.ReadarrClient>());
        services.AddSingleton<IArrProviderClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.WhisparrClient>());
        services.AddSingleton<IArrProviderClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.ProwlarrClient>());
        services.AddSingleton<IArrProviderClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.BazarrClient>());
        services.AddSingleton<IArrProviderClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.SabnzbdClient>());
        services.AddSingleton<IArrProviderClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.NzbGetClient>());
        services.AddSingleton<IArrProviderClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.QBittorrentClient>());
        services.AddSingleton<IArrProviderClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.TransmissionClient>());
        services.AddSingleton<IArrProviderClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.DelugeClient>());
        services.AddSingleton<IArrProviderClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.PlexClient>());
        services.AddSingleton<IArrProviderClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.JellyfinClient>());
        services.AddSingleton<IArrProviderClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.EmbyClient>());
        services.AddSingleton<IArrProviderClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.OverseerrClient>());
        services.AddSingleton<IArrProviderClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.JellyseerrClient>());
        services.AddSingleton<IArrProviderClient>(provider =>
            provider.GetRequiredService<ArrControl.Infrastructure.Providers.OmbiClient>());
        services.AddScoped<EfHealthIncidentStore>();
        services.AddScoped<IHealthIncidentSnapshotStore>(provider =>
            provider.GetRequiredService<EfHealthIncidentStore>());
        services.AddScoped<IHealthIncidentStore>(provider =>
            provider.GetRequiredService<EfHealthIncidentStore>());
        services.AddScoped<HealthIncidentService>();
        services.AddScoped<IHealthScheduleProvisioner, EfHealthScheduleProvisioner>();
        services.AddScoped<IScheduledJobHandler, HealthSyncJobHandler>();
        services.AddHostedService<HealthScheduleHostedService>();
        return services;
    }

    public static IEndpointRouteBuilder MapHealthIncidents(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/health/incidents").WithTags("Health");
        group.MapGet("", QueryAsync)
            .WithName("listHealthIncidents")
            .RequireAuthorization(RbacPolicyNames.AnyScope(RbacPermissions.InstancesRead))
            .Produces<HealthIncidentDetails[]>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        group.MapPut("/{incidentId:guid}/acknowledgement", SetAcknowledgementAsync)
            .WithName("setHealthIncidentAcknowledgement")
            .RequireAuthorization(RbacPolicyNames.AnyScope(RbacPermissions.TasksExecute))
            .Accepts<SetHealthAcknowledgementRequest>("application/json")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(4096))
            .Produces<HealthIncidentDetails>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPut("/{incidentId:guid}/snooze", SetSnoozeAsync)
            .WithName("setHealthIncidentSnooze")
            .RequireAuthorization(RbacPolicyNames.AnyScope(RbacPermissions.TasksExecute))
            .Accepts<SetHealthSnoozeRequest>("application/json")
            .AddEndpointFilter<RequireCsrfTokenFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(4096))
            .Produces<HealthIncidentDetails>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return endpoints;
    }

    private static async Task<IResult> QueryAsync(
        HttpContext context,
        HealthIncidentService service,
        [FromQuery(Name = "instanceId")] Guid[]? instanceIds = null,
        bool includeResolved = false,
        CancellationToken cancellationToken = default)
    {
        context.Response.Headers.CacheControl = "private, no-cache";
        if (!RbacHttpContext.TryGetIdentity(context, out var identity))
            return Problem(context, StatusCodes.Status401Unauthorized, "authentication_required");
        try
        {
            var result = await service.QueryAsync(
                identity.UserId, identity.SessionId, instanceIds ?? [], includeResolved, cancellationToken);
            return result is null
                ? Problem(context, StatusCodes.Status403Forbidden, "access_denied")
                : Results.Ok(result);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "health_filter_invalid");
        }
    }

    private static async Task<IResult> SetAcknowledgementAsync(
        Guid incidentId,
        SetHealthAcknowledgementRequest request,
        HttpContext context,
        HealthIncidentService service,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetActor(context, out var actor))
            return Problem(context, StatusCodes.Status401Unauthorized, "authentication_required");
        var result = await service.SetAcknowledgementAsync(
            actor, incidentId, request.Acknowledged, cancellationToken);
        return MutationResult(context, result);
    }

    private static async Task<IResult> SetSnoozeAsync(
        Guid incidentId,
        SetHealthSnoozeRequest request,
        HttpContext context,
        HealthIncidentService service,
        CancellationToken cancellationToken)
    {
        if (!RbacHttpContext.TryGetActor(context, out var actor))
            return Problem(context, StatusCodes.Status401Unauthorized, "authentication_required");
        var result = await service.SetSnoozeAsync(
            actor, incidentId, request.SnoozedUntil, cancellationToken);
        return MutationResult(context, result);
    }

    private static IResult MutationResult(HttpContext context, HealthIncidentMutationResult result) =>
        result.Status switch
        {
            HealthIncidentMutationStatus.Updated => Results.Ok(result.Incident),
            HealthIncidentMutationStatus.NotFound => Problem(context, 404, "health_incident_not_found"),
            HealthIncidentMutationStatus.Forbidden => Problem(context, 403, "access_denied"),
            _ => Problem(context, 400, "health_incident_mutation_invalid"),
        };

    private static IResult Problem(HttpContext context, int status, string code) =>
        AuthApiProblem.Create(context, status, "The health incident request could not be completed.", code);
}

public sealed class HealthScheduleHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<HealthScheduleHostedService> logger) : BackgroundService
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
            var count = await scope.ServiceProvider.GetRequiredService<IHealthScheduleProvisioner>()
                .ReconcileAsync(token);
            if (count > 0)
                logger.LogInformation("Health schedule reconciliation changed {ScheduleCount} schedule(s).", count);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Health schedule reconciliation failed with error type {ErrorType}.",
                exception.GetType().Name);
        }
    }
}
