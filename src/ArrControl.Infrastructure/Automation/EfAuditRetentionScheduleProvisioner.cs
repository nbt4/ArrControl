using ArrControl.Application.Audit;
using ArrControl.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Automation;

public sealed class EfAuditRetentionScheduleProvisioner(
    ArrControlDbContext dbContext,
    TimeProvider timeProvider) : IAuditRetentionScheduleProvisioner
{
    public Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO schedules (id, type, cron, time_zone, scope_json, scope_key, enabled, created_at, updated_at)
            VALUES ({Guid.CreateVersion7()}, {AuditJobTypes.Retention}, {"0 15 3 * * *"}, {"UTC"},
                    {"{\"kind\":\"global\"}"}::jsonb, {"global"}, TRUE, {now}, {now})
            ON CONFLICT (type, scope_key) WHERE scope_key IS NOT NULL
            DO UPDATE SET cron = EXCLUDED.cron, time_zone = EXCLUDED.time_zone,
                scope_json = EXCLUDED.scope_json, enabled = TRUE, updated_at = EXCLUDED.updated_at
            WHERE schedules.enabled = FALSE OR schedules.cron <> EXCLUDED.cron
               OR schedules.time_zone <> EXCLUDED.time_zone OR schedules.scope_json <> EXCLUDED.scope_json
            """, cancellationToken);
    }
}
