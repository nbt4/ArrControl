using System.Text;
using ArrControl.Application.Activity;
using ArrControl.Application.Connections;
using ArrControl.Application.Providers;
using ArrControl.Infrastructure.Providers;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class SupportingProviderContractTests
{
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-07-16T13:00:00Z");

    [Theory]
    [InlineData("2.4.0.5397", "torrent", "indexerquery")]
    [InlineData("2.5.1.5464", "usenet", "indexerauthfailed")]
    public async Task Prowlarr_versioned_contract_maps_probe_indexers_and_history(
        string version, string protocol, string eventType)
    {
        var transport = FixtureTransport("Prowlarr", version);
        var client = new ProwlarrClient(transport, new FixedTimeProvider(ObservedAt));
        var probe = await new ArrProviderConnectionAdapter(client, new FixedTimeProvider(ObservedAt))
            .ProbeAsync(Connection("prowlarr"), CancellationToken.None);
        var indexers = await client.GetIndexersAsync(Connection("prowlarr"), CancellationToken.None);
        var activity = await client.GetActivityAsync(Connection("prowlarr"), CancellationToken.None);

        Assert.True(probe.Connected);
        Assert.Equal(version, probe.ProviderVersion);
        AssertCapability(probe, ProviderCapabilities.Indexer, true);
        AssertCapability(probe, ProviderCapabilities.History, true);
        AssertCapability(probe, ProviderCapabilities.Library, false);
        var indexer = Assert.Single(indexers.Value!);
        Assert.Equal(protocol, indexer.Protocol);
        Assert.DoesNotContain("not-copied", indexer.ToString(), StringComparison.Ordinal);
        Assert.Empty(activity.Value!.Queue);
        var history = Assert.Single(activity.Value.History);
        Assert.Equal("Fixture Indexer", history.Title);
        Assert.Equal(eventType, history.EventType);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), history.EventAt);
    }

    [Theory]
    [InlineData("1.5.3", 1)]
    [InlineData("1.6.0", 2)]
    public async Task Bazarr_versioned_contract_maps_redacted_health_and_daily_subtitle_activity(
        string version, int expectedDays)
    {
        var transport = FixtureTransport("Bazarr", version);
        var client = new BazarrClient(transport, new FixedTimeProvider(ObservedAt));
        var probe = await new ArrProviderConnectionAdapter(client, new FixedTimeProvider(ObservedAt))
            .ProbeAsync(Connection("bazarr"), CancellationToken.None);
        var activity = await client.GetSubtitleActivityAsync(Connection("bazarr"), CancellationToken.None);

        Assert.True(probe.Connected);
        Assert.Equal(version == "1.6.0" ? "v1.6.0" : version, probe.ProviderVersion);
        AssertCapability(probe, ProviderCapabilities.SubtitleActivity, true);
        AssertCapability(probe, ProviderCapabilities.History, false);
        var issue = Assert.Single(probe.HealthIssues!);
        Assert.StartsWith("bazarr.health.", issue.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("private", issue.Source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/", issue.Source, StringComparison.Ordinal);
        Assert.Equal(expectedDays, activity.Value!.Days.Count);
        var first = activity.Value.Days[0];
        Assert.Equal(new DateOnly(2026, 1, 1), first.Date);
        Assert.Equal((2, 3), (first.SeriesCount, first.MovieCount));
    }

    [Theory]
    [InlineData("prowlarr")]
    [InlineData("bazarr")]
    public async Task Supporting_provider_authentication_failures_are_stable(string kind)
    {
        var transport = new StubTransport((_, _) => Response(401, "not-json"));
        IArrProviderClient client = kind == "prowlarr"
            ? new ProwlarrClient(transport)
            : new BazarrClient(transport);

        var result = await client.GetSystemStatusAsync(Connection(kind), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ProviderErrorCodes.Unauthorized, result.ErrorCode);
    }

    private static void AssertCapability(
        ConnectionProbeObservation probe, string capability, bool supported) =>
        Assert.Contains(probe.Capabilities,
            value => value.Capability == capability && value.Supported == supported);

    private static StubTransport FixtureTransport(string provider, string version)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Providers", provider, version);
        return new StubTransport((path, _) =>
        {
            var file = path switch
            {
                "api/v1/system/status" or "api/system/status" => "system-status.json",
                "api/v1/health" or "api/system/health" => "health.json",
                "api/v1/indexer" => "indexers.json",
                "api/v1/history?page=1&pageSize=10000&sortDirection=descending&sortKey=date" => "history.json",
                "api/history/stats?timeFrame=year" => "activity.json",
                _ => throw new InvalidOperationException($"Unexpected fixture path '{path}'."),
            };
            return new ProviderTransportResponse(200, File.ReadAllBytes(Path.Combine(root, file)), null);
        });
    }

    private static ProviderConnection Connection(string kind) => new(
        Guid.CreateVersion7(), new Uri($"https://{kind}.example.invalid/{kind}/"),
        true, false, "fixture-api-key-value");

    private static ProviderTransportResponse Response(int status, string body) =>
        new(status, Encoding.UTF8.GetBytes(body), null);

    private sealed class StubTransport(
        Func<string, ProviderConnection, ProviderTransportResponse> responseFactory) : IProviderApiTransport
    {
        public Task<ProviderTransportResponse> GetAsync(
            ProviderConnection connection, string relativePath, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(relativePath, connection));

        public Task<ProviderTransportResponse> GetAsync(
            ProviderConnection connection, string relativePath, IReadOnlyDictionary<string, string> query,
            CancellationToken cancellationToken)
        {
            var path = $"{relativePath}?{string.Join('&', query.OrderBy(value => value.Key)
                .Select(value => $"{value.Key}={value.Value}"))}";
            return Task.FromResult(responseFactory(path, connection));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
