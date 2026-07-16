using ArrControl.Infrastructure.Operations;

namespace ArrControl.Api.Operations;

public static class DatabaseMigrationCommand
{
    public static bool IsRequested(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0
            || !string.Equals(arguments[0], "database", StringComparison.Ordinal))
        {
            return false;
        }

        if (arguments.Count != 2
            || !string.Equals(arguments[1], "migrate", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Usage: ArrControl.Api database migrate");
        }

        return true;
    }

    public static async Task<int> RunAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<DatabaseMigrationRunner>()
                .RunAsync(cancellationToken);
            logger.LogInformation(
                "Database migration completed. Applied {AppliedCount} migration(s); {ExistingCount} were already present.",
                result.AppliedMigrations.Count,
                result.PreviouslyAppliedMigrations.Count);
            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                "Database migration failed with error type {ErrorType}.",
                exception.GetType().Name);
            return 1;
        }
    }
}
