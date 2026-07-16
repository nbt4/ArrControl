using ArrControl.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Operations;

public sealed record DatabaseMigrationResult(
    IReadOnlyList<string> AppliedMigrations,
    IReadOnlyList<string> PreviouslyAppliedMigrations);

public sealed class DatabaseMigrationRunner(ArrControlDbContext dbContext)
{
    private const long MigrationAdvisoryLock = 4703240382486482771;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    public async Task<DatabaseMigrationResult> RunAsync(CancellationToken cancellationToken)
    {
        dbContext.Database.SetCommandTimeout(CommandTimeout);
        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        var lockAcquired = false;
        try
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_lock({MigrationAdvisoryLock})",
                cancellationToken);
            lockAcquired = true;

            var previouslyApplied = (await dbContext.Database
                    .GetAppliedMigrationsAsync(cancellationToken))
                .Order(StringComparer.Ordinal)
                .ToArray();
            var pending = (await dbContext.Database
                    .GetPendingMigrationsAsync(cancellationToken))
                .Order(StringComparer.Ordinal)
                .ToArray();

            await dbContext.Database.MigrateAsync(cancellationToken);
            if ((await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            {
                throw new InvalidOperationException(
                    "Database migration verification found pending migrations.");
            }

            return new DatabaseMigrationResult(pending, previouslyApplied);
        }
        finally
        {
            if (lockAcquired)
            {
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_unlock({MigrationAdvisoryLock})",
                    CancellationToken.None);
            }

            await dbContext.Database.CloseConnectionAsync();
        }
    }
}
