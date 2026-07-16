using System.Net;
using ArrControl.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace ArrControl.IntegrationTests;

public sealed class PendingMigrationsHealthCheckTests(AuthDatabaseFixture database)
    : IClassFixture<AuthDatabaseFixture>
{
    private const string InitialMigration = "20260714221653_InitialFoundation";

    [Fact]
    public async Task Startup_applies_pending_ef_migrations_before_readiness_is_served()
    {
        var connectionString = await CreateSchemaAtInitialMigrationAsync();
        using var factory = new ReadinessApiFactory(connectionString);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var readinessResponse = await client.GetAsync(
            "/health/ready",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, readinessResponse.StatusCode);

        var options = new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var migrationContext = new ArrControlDbContext(options))
        {
            Assert.Empty(await migrationContext.Database.GetPendingMigrationsAsync());
        }
    }

    private async Task<string> CreateSchemaAtInitialMigrationAsync()
    {
        var schema = $"health_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            SearchPath = schema,
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using var migrationContext = new ArrControlDbContext(options);
        await migrationContext.Database.MigrateAsync(InitialMigration);
        return connectionString;
    }

    private sealed class ReadinessApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Database"] = connectionString,
                    ["Bootstrap:AdminEmail"] = string.Empty,
                    ["Bootstrap:AdminPassword"] = string.Empty,
                }));
        }
    }
}
