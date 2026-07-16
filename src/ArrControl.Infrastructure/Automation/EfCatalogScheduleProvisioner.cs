using ArrControl.Application.Catalog;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Connections;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Automation;

public sealed class EfCatalogScheduleProvisioner(
    ArrControlDbContext dbContext,
    TimeProvider timeProvider) : ICatalogScheduleProvisioner
{
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        var instances = await dbContext.Set<InstanceEntity>()
            .AsNoTracking()
            .Where(value => value.Enabled && (value.Kind == "sonarr" || value.Kind == "radarr"
                || value.Kind == "lidarr" || value.Kind == "readarr" || value.Kind == "whisparr"))
            .Select(value => value.Id)
            .ToArrayAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var changed = 0;
        foreach (var instanceId in instances)
        {
            changed += await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO schedules (
                    id, type, cron, time_zone, scope_json, scope_key, enabled,
                    created_at, updated_at)
                VALUES (
                    {Guid.CreateVersion7()}, {CatalogJobTypes.Sync}, {"*/15 * * * *"}, {"UTC"},
                    {CatalogJobScope.Create(instanceId)}::jsonb, {instanceId.ToString("D")}, TRUE,
                    {now}, {now})
                ON CONFLICT (type, scope_key) WHERE scope_key IS NOT NULL
                DO UPDATE SET
                    cron = EXCLUDED.cron,
                    time_zone = EXCLUDED.time_zone,
                    scope_json = EXCLUDED.scope_json,
                    enabled = TRUE,
                    updated_at = CASE
                        WHEN schedules.enabled = FALSE
                          OR schedules.cron <> EXCLUDED.cron
                          OR schedules.time_zone <> EXCLUDED.time_zone
                          OR schedules.scope_json <> EXCLUDED.scope_json
                        THEN EXCLUDED.updated_at
                        ELSE schedules.updated_at
                    END
                WHERE schedules.enabled = FALSE
                   OR schedules.cron <> EXCLUDED.cron
                   OR schedules.time_zone <> EXCLUDED.time_zone
                   OR schedules.scope_json <> EXCLUDED.scope_json
                """, cancellationToken);
        }

        changed += await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE schedules AS schedule
            SET enabled = FALSE, updated_at = {now}
            WHERE schedule.type = {CatalogJobTypes.Sync}
              AND schedule.scope_key IS NOT NULL
              AND schedule.enabled = TRUE
              AND NOT EXISTS (
                  SELECT 1
                  FROM service_instances AS instance
                  WHERE instance.id::text = schedule.scope_key
                    AND instance.enabled = TRUE
                    AND instance.kind IN ('sonarr', 'radarr', 'lidarr', 'readarr', 'whisparr'))
            """, cancellationToken);
        return changed;
    }
}
