using System.Text;
using System.Text.Json;
using ArrControl.Application.Connections;
using ArrControl.Application.Providers;
using ArrControl.Infrastructure.Providers;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class MediaServerContractTests
{
    private static readonly DateTimeOffset ObservedAt =
        DateTimeOffset.Parse("2026-07-16T16:00:00Z");

    [Theory]
    [InlineData("plex", "1.41.9.9961", 5, 3)]
    [InlineData("plex", "1.43.2.10687", 4, 4)]
    [InlineData("jellyfin", "10.10.7", -1, 2)]
    [InlineData("jellyfin", "10.11.10", -1, 2)]
    [InlineData("emby", "4.8.11.0", -1, 2)]
    [InlineData("emby", "4.9.3.0", -1, 2)]
    public async Task Versioned_contract_probes_and_returns_privacy_preserving_aggregates(
        string kind, string version, int expectedLibraries, int expectedActive)
    {
        var transport = new FixtureTransport(kind, version);
        var client = CreateClient(kind, transport);
        var connection = Connection(kind);

        var probe = await new ArrProviderConnectionAdapter(client, new FixedTimeProvider(ObservedAt))
            .ProbeAsync(connection, CancellationToken.None);
        var snapshot = await client.GetMediaServerSnapshotAsync(connection, CancellationToken.None);

        Assert.True(probe.Connected);
        Assert.Equal("connected", probe.Outcome);
        Assert.StartsWith(version, probe.ProviderVersion, StringComparison.Ordinal);
        AssertCapability(probe, ProviderCapabilities.MediaServer, true);
        AssertCapability(probe, ProviderCapabilities.Health, true);
        AssertCapability(probe, ProviderCapabilities.Library, false);
        Assert.True(snapshot.Success, snapshot.ErrorCode);
        Assert.Equal(ObservedAt, snapshot.Value!.ObservedAt);
        Assert.Equal(expectedLibraries < 0 ? null : expectedLibraries, snapshot.Value.Libraries.LibraryCount);
        Assert.Equal(expectedActive, snapshot.Value.Playback.Active);
        Assert.Equal(1, snapshot.Value.Playback.Playing);
        Assert.Equal(1, snapshot.Value.Playback.Paused);
        Assert.Equal(1, snapshot.Value.Playback.Transcoding);
        if (kind == "plex")
        {
            Assert.Null(snapshot.Value.Libraries.Items);
            Assert.Equal(1, snapshot.Value.Playback.DirectStreaming);
            Assert.Equal(1, snapshot.Value.Playback.DirectPlaying);
            Assert.All(transport.Requests, request =>
            {
                Assert.Equal("fixture-api-key-secret", request.Headers["X-Plex-Token"]);
                Assert.Equal("1.0.0", request.Headers["X-Plex-Pms-Api-Version"]);
            });
        }
        else
        {
            Assert.NotNull(snapshot.Value.Libraries.Items);
            Assert.True(snapshot.Value.Libraries.Items.Total > 0);
            Assert.All(transport.Requests, request =>
                Assert.Equal("fixture-api-key-secret", request.Headers["X-Emby-Token"]));
        }
        var serialized = JsonSerializer.Serialize(snapshot.Value);
        Assert.DoesNotContain("private", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixture-api-key-secret", serialized, StringComparison.Ordinal);
        Assert.All(transport.Requests, request =>
            Assert.DoesNotContain("fixture-api-key-secret", request.RequestText, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("plex")]
    [InlineData("jellyfin")]
    [InlineData("emby")]
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
    [InlineData("plex", "{\"MediaContainer\":{\"machineIdentifier\":\"fixture\",\"version\":\"1.44.0.1\"}}")]
    [InlineData("jellyfin", "{\"ProductName\":\"Jellyfin Server\",\"Version\":\"10.12.0\"}")]
    [InlineData("emby", "{\"ProductName\":\"Emby Server\",\"Version\":\"4.10.0.0\"}")]
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

    private static IProviderMediaServerClient CreateClient(string kind, IProviderHttpTransport transport) =>
        kind switch
        {
            "plex" => new PlexClient(transport, new FixedTimeProvider(ObservedAt)),
            "jellyfin" => new JellyfinClient(transport, new FixedTimeProvider(ObservedAt)),
            "emby" => new EmbyClient(transport, new FixedTimeProvider(ObservedAt)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static ProviderConnection Connection(string kind) => new(
        Guid.CreateVersion7(), new Uri($"https://{kind}.example.invalid/"), true, false,
        new Dictionary<string, string> { [CredentialPurposes.ApiKey] = "fixture-api-key-secret" });

    private sealed class FixtureTransport(string kind, string version) : IProviderHttpTransport
    {
        private readonly string root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Providers",
            kind switch { "plex" => "Plex", "jellyfin" => "Jellyfin", "emby" => "Emby", _ => kind }, version);

        public List<CapturedRequest> Requests { get; } = [];

        public Task<ProviderTransportResponse> SendAsync(
            ProviderConnection connection, ProviderHttpRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(request.RelativePath,
                new Dictionary<string, string>(request.Headers), request.ToString()));
            var file = request.RelativePath switch
            {
                "identity" or "System/Info" or "emby/System/Info" => "status.json",
                "library/sections" => "libraries.json",
                "Items/Counts" or "emby/Items/Counts" => "counts.json",
                "status/sessions" or "Sessions" or "emby/Sessions" => "sessions.json",
                _ => throw new InvalidOperationException($"Unexpected path '{request.RelativePath}'."),
            };
            if (kind == "plex" && file == "status.json") file = "identity.json";
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
