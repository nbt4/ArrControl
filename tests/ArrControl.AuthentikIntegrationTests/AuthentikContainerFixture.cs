using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Xunit;

namespace ArrControl.AuthentikIntegrationTests;

public sealed class AuthentikContainerFixture : IAsyncLifetime
{
    public const string ApplicationSlug = "arrcontrol-e2e";
    public const string AdministratorGroup = "arrcontrol-e2e-admins";
    public const string AuthentikImage = "ghcr.io/goauthentik/server:2026.5.4";
    public const string PostgreSqlImage = "docker.io/library/postgres:16.10-alpine";

    public static readonly Uri ArrControlPublicOrigin = new("https://arrcontrol.test/");
    public static readonly Uri ArrControlCallbackUri = new(
        ArrControlPublicOrigin,
        "/auth/oidc/callback");
    public static readonly Uri ArrControlPostLogoutUri = new(
        ArrControlPublicOrigin,
        "/auth/oidc/signed-out");

    private const ushort AuthentikHttpPort = 9000;
    private const string DatabaseHost = "postgresql";
    private const string DatabaseName = "authentik";
    private const string DatabaseUser = "authentik";
    private readonly IContainer postgresql;
    private readonly IContainer server;
    private readonly IContainer worker;
    private readonly INetwork network;
    private readonly bool enabled;

    public AuthentikContainerFixture()
    {
        enabled = AuthentikContainerFactAttribute.IsEnabled("ARRCONTROL_RUN_AUTHENTIK_TESTS")
            || AuthentikContainerFactAttribute.IsEnabled("ARRCONTROL_RUN_AUTHENTIK_E2E");
        CallbackServer = LoopbackCallbackServer.Start();
        DatabasePassword = GenerateSecret(36);
        SecretKey = GenerateSecret(64);
        BootstrapToken = GenerateSecret(48);
        ClientId = $"arrcontrol-{GenerateSecret(18)}";
        ClientSecret = GenerateSecret(64);
        UserName = $"arrcontrol-{GenerateSecret(12)}";
        UserEmail = "oidc-user@example.invalid";
        UserPassword = GenerateSecret(48);

        network = new NetworkBuilder().Build();
        postgresql = new ContainerBuilder(PostgreSqlImage)
            .WithNetwork(network)
            .WithNetworkAliases(DatabaseHost)
            .WithEnvironment("POSTGRES_DB", DatabaseName)
            .WithEnvironment("POSTGRES_USER", DatabaseUser)
            .WithEnvironment("POSTGRES_PASSWORD", DatabasePassword)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilCommandIsCompleted(
                    "pg_isready",
                    "-d",
                    DatabaseName,
                    "-U",
                    DatabaseUser))
            .Build();

        var commonEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AUTHENTIK_POSTGRESQL__HOST"] = DatabaseHost,
            ["AUTHENTIK_POSTGRESQL__NAME"] = DatabaseName,
            ["AUTHENTIK_POSTGRESQL__USER"] = DatabaseUser,
            ["AUTHENTIK_POSTGRESQL__PASSWORD"] = DatabasePassword,
            ["AUTHENTIK_SECRET_KEY"] = SecretKey,
            ["AUTHENTIK_ERROR_REPORTING__ENABLED"] = "false",
            ["AUTHENTIK_LOG_LEVEL"] = "warning",
        };

        var serverBuilder = new ContainerBuilder(AuthentikImage)
            .WithCommand("server")
            .WithNetwork(network)
            .WithPortBinding(AuthentikHttpPort, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(request => request
                        .ForPort(AuthentikHttpPort)
                        .ForPath("/-/health/ready/")));
        foreach (var item in commonEnvironment)
        {
            serverBuilder = serverBuilder.WithEnvironment(item.Key, item.Value);
        }

        server = serverBuilder.Build();

        var blueprintPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "arrcontrol-e2e-blueprint.yaml");
        var workerBuilder = new ContainerBuilder(AuthentikImage)
            .WithCommand("worker")
            .WithNetwork(network)
            .WithBindMount(
                blueprintPath,
                "/blueprints/arrcontrol-e2e.yaml",
                AccessMode.ReadOnly)
            .WithEnvironment("AUTHENTIK_BOOTSTRAP_TOKEN", BootstrapToken)
            .WithEnvironment("AUTHENTIK_BOOTSTRAP_EMAIL", "akadmin@example.invalid")
            .WithEnvironment("ARRCONTROL_E2E_USERNAME", UserName)
            .WithEnvironment("ARRCONTROL_E2E_USER_EMAIL", UserEmail)
            .WithEnvironment("ARRCONTROL_E2E_USER_PASSWORD", UserPassword)
            .WithEnvironment("ARRCONTROL_E2E_CLIENT_ID", ClientId)
            .WithEnvironment("ARRCONTROL_E2E_CLIENT_SECRET", ClientSecret)
            .WithEnvironment(
                "ARRCONTROL_E2E_CALLBACK_URI",
                CallbackServer.AuthorizationCallbackUri.AbsoluteUri)
            .WithEnvironment(
                "ARRCONTROL_E2E_POST_LOGOUT_URI",
                CallbackServer.PostLogoutUri.AbsoluteUri)
            .WithEnvironment(
                "ARRCONTROL_E2E_APP_CALLBACK_URI",
                ArrControlCallbackUri.AbsoluteUri)
            .WithEnvironment(
                "ARRCONTROL_E2E_APP_POST_LOGOUT_URI",
                ArrControlPostLogoutUri.AbsoluteUri);
        foreach (var item in commonEnvironment)
        {
            workerBuilder = workerBuilder.WithEnvironment(item.Key, item.Value);
        }

        worker = workerBuilder.Build();
    }

    public LoopbackCallbackServer CallbackServer { get; }

    public Uri BaseAddress { get; private set; } = null!;

    public Uri Authority => new(BaseAddress, $"/application/o/{ApplicationSlug}/");

    public string BootstrapToken { get; }

    public string ClientId { get; }

    public string ClientSecret { get; }

    public string UserName { get; }

    public string UserEmail { get; }

    public string UserPassword { get; }

    private string DatabasePassword { get; }

    private string SecretKey { get; }

    public async Task InitializeAsync()
    {
        if (!enabled)
        {
            return;
        }

        try
        {
            await network.CreateAsync();
            await postgresql.StartAsync();
            await Task.WhenAll(server.StartAsync(), worker.StartAsync())
                .WaitAsync(TimeSpan.FromMinutes(8));

            BaseAddress = new Uri(
                $"http://{server.Hostname}:{server.GetMappedPublicPort(AuthentikHttpPort)}/",
                UriKind.Absolute);
            await WaitForBlueprintAsync();
        }
        catch
        {
            try
            {
                await DisposeContainersAsync();
            }
            finally
            {
                await CallbackServer.DisposeAsync();
            }

            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (enabled)
        {
            await DisposeContainersAsync();
        }

        await CallbackServer.DisposeAsync();
    }

    public HttpClient CreateAnonymousClient() => new()
    {
        BaseAddress = BaseAddress,
        Timeout = TimeSpan.FromSeconds(30),
    };

    public HttpClient CreateApiClient()
    {
        var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", BootstrapToken);
        return client;
    }

    private async Task WaitForBlueprintAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        Exception? lastFailure = null;

        while (!timeout.IsCancellationRequested)
        {
            try
            {
                using var client = CreateApiClient();
                using var blueprintResponse = await client.GetAsync(
                    "api/v3/managed/blueprints/?page_size=100",
                    timeout.Token);
                if (blueprintResponse.StatusCode == HttpStatusCode.OK)
                {
                    using var blueprintDocument = JsonDocument.Parse(
                        await blueprintResponse.Content.ReadAsStringAsync(timeout.Token));
                    var blueprints = blueprintDocument.RootElement
                        .GetProperty("results")
                        .EnumerateArray()
                        .ToArray();
                    var requiredBlueprints = new[]
                    {
                        "arrcontrol-e2e.yaml",
                        "default/default-brand.yaml",
                        "default/flow-default-authentication-flow.yaml",
                    };
                    var requiredStatuses = requiredBlueprints
                        .Select(required => blueprints.FirstOrDefault(item => item
                            .GetProperty("path")
                            .GetString()?
                            .EndsWith(required, StringComparison.Ordinal) == true))
                        .ToArray();
                    if (requiredStatuses.Any(item =>
                            item.ValueKind != JsonValueKind.Undefined
                            && string.Equals(
                                item.GetProperty("status").GetString(),
                                "error",
                                StringComparison.Ordinal)))
                    {
                        lastFailure = new InvalidOperationException(
                            "Authentik rejected a blueprint required by the ArrControl OIDC test realm.");
                    }
                    else if (requiredStatuses.All(item =>
                                 item.ValueKind != JsonValueKind.Undefined
                                 && string.Equals(
                                     item.GetProperty("status").GetString(),
                                     "successful",
                                     StringComparison.Ordinal)))
                    {
                        using var providerResponse = await client.GetAsync(
                            $"api/v3/providers/oauth2/?client_id={Uri.EscapeDataString(ClientId)}",
                            timeout.Token);
                        if (providerResponse.StatusCode == HttpStatusCode.OK)
                        {
                            var body = await providerResponse.Content.ReadAsStringAsync(
                                timeout.Token);
                            if (body.Contains(ClientId, StringComparison.Ordinal))
                            {
                                return;
                            }
                        }
                    }
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException)
            {
                lastFailure = exception;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException(
            "Authentik started, but its ArrControl test blueprint was not observable through the API within eight minutes.",
            lastFailure);
    }

    private async Task DisposeContainersAsync()
    {
        await worker.DisposeAsync();
        await server.DisposeAsync();
        await postgresql.DisposeAsync();
        await network.DisposeAsync();
    }

    private static string GenerateSecret(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AuthentikContainerCollection : ICollectionFixture<AuthentikContainerFixture>
{
    public const string Name = "real-authentik";
}
