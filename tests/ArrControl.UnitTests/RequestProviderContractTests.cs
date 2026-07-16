using System.Text;
using System.Text.Json;
using ArrControl.Application.Connections;
using ArrControl.Application.Providers;
using ArrControl.Infrastructure.Providers;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class RequestProviderContractTests
{
    private static readonly DateTimeOffset ObservedAt =
        DateTimeOffset.Parse("2026-07-16T17:00:00Z");

    [Theory]
    [InlineData("overseerr", "1.34.0", "pending", "approved")]
    [InlineData("overseerr", "1.35.0", "completed", "unknown")]
    [InlineData("jellyseerr", "2.7.3", "declined", "failed")]
    [InlineData("jellyseerr", "3.3.0", "approved", "pending")]
    [InlineData("ombi", "4.47.1", "pending", "approved")]
    [InlineData("ombi", "4.48.0", "declined", "completed")]
    public async Task Versioned_contract_probes_and_maps_privacy_preserving_requests(
        string kind, string version, string firstStatus, string secondStatus)
    {
        var transport = new FixtureTransport(kind, version);
        var client = CreateClient(kind, transport);
        var connection = Connection(kind);

        var probe = await new ArrProviderConnectionAdapter(client, new FixedTimeProvider(ObservedAt))
            .ProbeAsync(connection, CancellationToken.None);
        var snapshot = await client.GetRequestsAsync(connection, CancellationToken.None);

        Assert.True(probe.Connected);
        Assert.Equal("connected", probe.Outcome);
        Assert.Equal(version, probe.ProviderVersion);
        AssertCapability(probe, ProviderCapabilities.Requests, true);
        AssertCapability(probe, ProviderCapabilities.Health, true);
        AssertCapability(probe, ProviderCapabilities.Library, false);
        Assert.True(snapshot.Success, snapshot.ErrorCode);
        Assert.Equal(ObservedAt, snapshot.Value!.ObservedAt);
        Assert.Equal(2, snapshot.Value.Requests.Count);
        Assert.Equal(firstStatus, snapshot.Value.Requests[0].Status);
        Assert.Equal(secondStatus, snapshot.Value.Requests[1].Status);
        Assert.All(snapshot.Value.Requests, request =>
        {
            Assert.NotEqual("unknown", request.MediaType);
            Assert.NotNull(request.TmdbId);
            Assert.NotEqual(default, request.RequestedAt);
        });
        Assert.All(transport.Requests, request =>
        {
            var expectedHeader = kind == "ombi" ? "ApiKey" : "X-API-Key";
            Assert.Equal("fixture-api-key-secret", request.Headers[expectedHeader]);
            Assert.DoesNotContain("fixture-api-key-secret", request.RequestText, StringComparison.Ordinal);
        });
        var serialized = JsonSerializer.Serialize(snapshot.Value);
        Assert.DoesNotContain("private", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixture-api-key-secret", serialized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("overseerr")]
    [InlineData("jellyseerr")]
    [InlineData("ombi")]
    public async Task Authentication_and_missing_credentials_have_stable_outcomes(string kind)
    {
        var unauthorized = await new ArrProviderConnectionAdapter(
            CreateClient(kind, new FixedResponseTransport(401)), new FixedTimeProvider(ObservedAt))
            .ProbeAsync(Connection(kind), CancellationToken.None);
        var missing = await new ArrProviderConnectionAdapter(
            CreateClient(kind, new FixedResponseTransport(200)), new FixedTimeProvider(ObservedAt))
            .ProbeAsync(new ProviderConnection(Guid.CreateVersion7(),
                new Uri($"https://{kind}.example.invalid/"), true, false,
                new Dictionary<string, string>()), CancellationToken.None);

        Assert.Equal(ProviderErrorCodes.Unauthorized, unauthorized.Outcome);
        Assert.Equal(ProviderErrorCodes.CredentialMissing, missing.Outcome);
    }

    [Theory]
    [InlineData("overseerr", "{\"version\":\"2.0.0\"}")]
    [InlineData("jellyseerr", "{\"version\":\"4.0.0\"}")]
    [InlineData("ombi", "\"5.0.0\"")]
    public async Task Future_unevidenced_versions_fail_closed(string kind, string body)
    {
        var result = await CreateClient(kind, new FixedResponseTransport(200, body))
            .GetSystemStatusAsync(Connection(kind), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ProviderErrorCodes.UnsupportedVersion, result.ErrorCode);
    }

    private static void AssertCapability(
        ConnectionProbeObservation probe, string capability, bool supported) =>
        Assert.Contains(probe.Capabilities,
            value => value.Capability == capability && value.Supported == supported);

    private static IProviderRequestClient CreateClient(string kind, IProviderHttpTransport transport) =>
        kind switch
        {
            "overseerr" => new OverseerrClient(transport, new FixedTimeProvider(ObservedAt)),
            "jellyseerr" => new JellyseerrClient(transport, new FixedTimeProvider(ObservedAt)),
            "ombi" => new OmbiClient(transport, new FixedTimeProvider(ObservedAt)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static ProviderConnection Connection(string kind) => new(
        Guid.CreateVersion7(), new Uri($"https://{kind}.example.invalid/{kind}/"), true, false,
        new Dictionary<string, string> { [CredentialPurposes.ApiKey] = "fixture-api-key-secret" });

    private sealed class FixtureTransport(string kind, string version) : IProviderHttpTransport
    {
        private readonly string root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Providers",
            kind switch { "overseerr" => "Overseerr", "jellyseerr" => "Jellyseerr", "ombi" => "Ombi", _ => kind },
            version);

        public List<CapturedRequest> Requests { get; } = [];

        public Task<ProviderTransportResponse> SendAsync(
            ProviderConnection connection, ProviderHttpRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(request.RelativePath,
                new Dictionary<string, string>(request.Headers), request.ToString()));
            var file = request.RelativePath switch
            {
                "api/v1/status" or "api/v1/Status/info" => "status.json",
                "api/v1/request" => "requests.json",
                "api/v1/Request/movie" => "movies.json",
                "api/v1/Request/tvlite" => "series.json",
                _ => throw new InvalidOperationException($"Unexpected path '{request.RelativePath}'."),
            };
            if (request.RelativePath == "api/v1/request")
            {
                Assert.Equal("0", request.Query["skip"]);
                Assert.Equal("10000", request.Query["take"]);
            }
            return Task.FromResult(new ProviderTransportResponse(
                200, File.ReadAllBytes(Path.Combine(root, file)), null));
        }
    }

    private sealed class FixedResponseTransport(int statusCode, string body = "not-json") : IProviderHttpTransport
    {
        public Task<ProviderTransportResponse> SendAsync(
            ProviderConnection connection, ProviderHttpRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderTransportResponse(statusCode, Encoding.UTF8.GetBytes(body), null));
    }

    private sealed record CapturedRequest(
        string Path, IReadOnlyDictionary<string, string> Headers, string RequestText);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
