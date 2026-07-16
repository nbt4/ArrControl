using ArrControl.Application.Connections;
using ArrControl.Application.Activity;
using ArrControl.Application.Catalog;
using ArrControl.Application.Providers;
using ArrControl.Application.Search;

namespace ArrControl.Infrastructure.Providers;

public sealed class ArrProviderConnectionAdapter(
    IArrProviderClient client,
    TimeProvider timeProvider) : IProviderConnectionAdapter
{
    public string Kind => client.Kind;

    public IReadOnlyList<string> RequiredCredentialPurposes =>
        client is IProviderCredentialContract contract
            ? contract.RequiredCredentialPurposes
            : [CredentialPurposes.ApiKey];

    public async Task<ConnectionProbeObservation> ProbeAsync(
        ProviderConnection connection,
        CancellationToken cancellationToken)
    {
        ProviderCallResult<ProviderSystemStatus> status;
        try
        {
            status = await client.GetSystemStatusAsync(connection, cancellationToken);
        }
        catch (ProviderTransportException exception)
        {
            return Failure(exception.Code, connected: false, null, null);
        }

        if (!status.Success)
        {
            return Failure(
                status.ErrorCode ?? ProviderErrorCodes.Unknown,
                status.HttpStatusCode is not null,
                status.HttpStatusCode,
                status.RateLimit);
        }

        ProviderCallResult<IReadOnlyList<ProviderHealthIssue>> health;
        try
        {
            health = await client.GetHealthAsync(connection, cancellationToken);
        }
        catch (ProviderTransportException exception)
        {
            return Failure(
                exception.Code,
                connected: true,
                null,
                null,
                status.Value!.Version,
                includeHealth: false);
        }

        var observedAt = timeProvider.GetUtcNow();
        if (!health.Success)
        {
            return new ConnectionProbeObservation(
                true,
                health.ErrorCode ?? ProviderErrorCodes.Unknown,
                health.HttpStatusCode,
                observedAt,
                Capabilities(observedAt, healthSupported: false),
                status.Value!.Version,
                null,
                health.RateLimit);
        }

        return new ConnectionProbeObservation(
            true,
            "connected",
            status.HttpStatusCode,
            observedAt,
            Capabilities(observedAt, healthSupported: true),
            status.Value!.Version,
            health.Value,
            health.RateLimit ?? status.RateLimit);
    }

    private ConnectionProbeObservation Failure(
        string outcome,
        bool connected,
        int? httpStatusCode,
        ProviderRateLimitMetadata? rateLimit,
        string? providerVersion = null,
        bool includeHealth = false)
    {
        var observedAt = timeProvider.GetUtcNow();
        return new ConnectionProbeObservation(
            connected,
            outcome,
            httpStatusCode,
            observedAt,
            includeHealth
                ? Capabilities(observedAt, healthSupported: false)
                : [new ProviderCapabilityObservation(ProviderCapabilities.Probe, true, observedAt)],
            providerVersion,
            null,
            rateLimit);
    }

    private IReadOnlyList<ProviderCapabilityObservation> Capabilities(
        DateTimeOffset observedAt,
        bool healthSupported)
    {
        var capabilities = new List<ProviderCapabilityObservation>
        {
            new(ProviderCapabilities.Probe, true, observedAt),
            new(ProviderCapabilities.Library, client is IProviderCatalogClient, observedAt),
            new(ProviderCapabilities.Missing, client is IProviderCatalogClient, observedAt),
            new(ProviderCapabilities.Queue, client is IProviderActivityClient, observedAt),
            new(ProviderCapabilities.History, client is IProviderActivityClient, observedAt),
            new(ProviderCapabilities.Search, client is IProviderSearchClient, observedAt),
            new(ProviderCapabilities.Health, healthSupported, observedAt),
        };
        if (client is IProviderIndexerClient)
            capabilities.Add(new(ProviderCapabilities.Indexer, true, observedAt));
        if (client is IProviderSubtitleActivityClient)
            capabilities.Add(new(ProviderCapabilities.SubtitleActivity, true, observedAt));
        if (client is IProviderMediaServerClient)
            capabilities.Add(new(ProviderCapabilities.MediaServer, true, observedAt));
        if (client is IProviderRequestClient)
            capabilities.Add(new(ProviderCapabilities.Requests, true, observedAt));
        if (client is IProviderDownloadClient downloadClient)
        {
            capabilities.Add(new(ProviderCapabilities.DownloadClient, true, observedAt));
            capabilities.Add(new(ProviderCapabilities.Pause, true, observedAt));
            capabilities.Add(new(ProviderCapabilities.Remove, true, observedAt));
            capabilities.Add(new(ProviderCapabilities.Retry, downloadClient.SupportsRetry, observedAt));
        }
        return capabilities;
    }
}
