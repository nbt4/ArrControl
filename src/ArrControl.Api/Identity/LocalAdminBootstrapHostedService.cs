using ArrControl.Application.Identity;

namespace ArrControl.Api.Identity;

public sealed class LocalAdminBootstrapHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<LocalAdminBootstrapHostedService> logger) : IHostedService
{
    private const string PlaceholderPassword = "CHANGE_ME_TO_A_LONG_RANDOM_ADMIN_PASSWORD";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var email = configuration["Bootstrap:AdminEmail"];
        var password = configuration["Bootstrap:AdminPassword"];
        var hasEmail = !string.IsNullOrWhiteSpace(email);
        var hasPassword = !string.IsNullOrWhiteSpace(password);

        if (!hasEmail && !hasPassword)
        {
            return;
        }

        if (!hasEmail || !hasPassword)
        {
            throw new InvalidOperationException(
                "Local administrator bootstrap requires both the email and password settings.");
        }

        if (string.Equals(password, PlaceholderPassword, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Local administrator bootstrap rejected the documented placeholder password.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var identityService = scope.ServiceProvider.GetRequiredService<LocalIdentityService>();
        var status = await identityService.BootstrapAsync(
            email!,
            password!,
            new AuthenticationRequestContext("startup-bootstrap", null),
            cancellationToken);

        if (status == BootstrapStatus.Created)
        {
            logger.LogInformation("Local administrator bootstrap completed.");
        }
        else if (status == BootstrapStatus.Updated)
        {
            logger.LogInformation("Local administrator bootstrap credentials synchronized.");
        }
        else
        {
            logger.LogInformation("Local administrator bootstrap is disabled for this installation.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
