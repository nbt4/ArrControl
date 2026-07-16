using ArrControl.Application.Automation;
using ArrControl.Application.Catalog;
using ArrControl.Infrastructure.Automation;
using ArrControl.Infrastructure.Catalog;

namespace ArrControl.Api.Catalog;

public static class CatalogSyncApi
{
    public static IServiceCollection AddCatalogSynchronization(this IServiceCollection services)
    {
        services.AddSingleton<IProviderCatalogClient>(serviceProvider =>
            serviceProvider.GetRequiredService<ArrControl.Infrastructure.Providers.SonarrClient>());
        services.AddSingleton<IProviderCatalogClient>(serviceProvider =>
            serviceProvider.GetRequiredService<ArrControl.Infrastructure.Providers.RadarrClient>());
        services.AddSingleton<IProviderCatalogClient>(serviceProvider =>
            serviceProvider.GetRequiredService<ArrControl.Infrastructure.Providers.LidarrClient>());
        services.AddSingleton<IProviderCatalogClient>(serviceProvider =>
            serviceProvider.GetRequiredService<ArrControl.Infrastructure.Providers.ReadarrClient>());
        services.AddSingleton<IProviderCatalogClient>(serviceProvider =>
            serviceProvider.GetRequiredService<ArrControl.Infrastructure.Providers.WhisparrClient>());
        services.AddScoped<ICatalogSyncTargetResolver, EfCatalogSyncTargetResolver>();
        services.AddScoped<ICatalogSnapshotStore, EfCatalogSnapshotStore>();
        services.AddScoped<IMissingQueryStore, EfMissingQueryStore>();
        services.AddScoped<IMissingSavedViewStore, EfMissingSavedViewStore>();
        services.AddScoped<MissingQueryService>();
        services.AddScoped<MissingSavedViewService>();
        services.AddSingleton<IMissingCursorCodec, DataProtectionMissingCursorCodec>();
        services.AddScoped<ICatalogScheduleProvisioner, EfCatalogScheduleProvisioner>();
        services.AddScoped<IScheduledJobHandler, CatalogSyncJobHandler>();
        services.AddHostedService<CatalogScheduleHostedService>();
        return services;
    }
}

public sealed class CatalogScheduleHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<CatalogScheduleHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ReconcileAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var count = await scope.ServiceProvider
                .GetRequiredService<ICatalogScheduleProvisioner>()
                .ReconcileAsync(cancellationToken);
            if (count > 0)
            {
                logger.LogInformation("Catalog schedule reconciliation changed {ScheduleCount} schedule(s).", count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Catalog schedule reconciliation failed with error type {ErrorType}.",
                exception.GetType().Name);
        }
    }
}
