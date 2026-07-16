using ArrControl.Application.Activity;
using ArrControl.Application.Catalog;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Connections;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Automation;

public sealed class EfActivityScheduleProvisioner(
    ArrControlDbContext dbContext,
    TimeProvider timeProvider) : IActivityScheduleProvisioner
{
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        var ids = await dbContext.Set<InstanceEntity>().AsNoTracking()
            .Where(value => value.Enabled && (value.Kind == "sonarr" || value.Kind == "radarr"
                || value.Kind == "lidarr" || value.Kind == "readarr" || value.Kind == "whisparr"
                || value.Kind == "prowlarr" || value.Kind == "sabnzbd" || value.Kind == "nzbget"
                || value.Kind == "qbittorrent" || value.Kind == "transmission" || value.Kind == "deluge"))
            .Select(value => value.Id).ToArrayAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var changed = 0;
        foreach (var id in ids)
        {
            changed += await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO schedules (id, type, cron, time_zone, scope_json, scope_key, enabled, created_at, updated_at)
                VALUES ({Guid.CreateVersion7()}, {ActivityJobTypes.Sync}, {"*/30 * * * * *"}, {"UTC"},
                        {CatalogJobScope.Create(id)}::jsonb, {id.ToString("D")}, TRUE, {now}, {now})
                ON CONFLICT (type, scope_key) WHERE scope_key IS NOT NULL
                DO UPDATE SET cron = EXCLUDED.cron, time_zone = EXCLUDED.time_zone,
                    scope_json = EXCLUDED.scope_json, enabled = TRUE, updated_at = EXCLUDED.updated_at
                WHERE schedules.enabled = FALSE OR schedules.cron <> EXCLUDED.cron
                   OR schedules.time_zone <> EXCLUDED.time_zone OR schedules.scope_json <> EXCLUDED.scope_json
                """, cancellationToken);
        }

        changed += await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE schedules AS schedule SET enabled = FALSE, updated_at = {now}
            WHERE schedule.type = {ActivityJobTypes.Sync} AND schedule.scope_key IS NOT NULL
              AND schedule.enabled = TRUE AND NOT EXISTS (
                SELECT 1 FROM service_instances AS instance
                WHERE instance.id::text = schedule.scope_key AND instance.enabled = TRUE
                  AND instance.kind IN ('sonarr', 'radarr', 'lidarr', 'readarr', 'whisparr', 'prowlarr', 'sabnzbd', 'nzbget', 'qbittorrent', 'transmission', 'deluge'))
            """, cancellationToken);
        return changed;
    }
}
