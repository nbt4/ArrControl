using ArrControl.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ArrControl.Api.Health;

public sealed class PendingMigrationsHealthCheck(ArrControlDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var pendingMigrations = (await dbContext.Database
                .GetPendingMigrationsAsync(cancellationToken))
            .ToArray();

        return pendingMigrations.Length == 0
            ? HealthCheckResult.Healthy("The database schema is current.")
            : HealthCheckResult.Unhealthy(
                $"The database schema has {pendingMigrations.Length} pending migration(s).",
                data: new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["pendingMigrations"] = pendingMigrations,
                });
    }
}
