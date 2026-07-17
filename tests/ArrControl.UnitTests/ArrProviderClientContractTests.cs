using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ArrControl.Application.Connections;
using ArrControl.Application.Catalog;
using ArrControl.Application.Activity;
using ArrControl.Application.Search;
using ArrControl.Application.Providers;
using ArrControl.Infrastructure.Providers;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class ArrProviderClientContractTests
{
    [Theory]
    [InlineData("sonarr", "4.0.1.1168", 1)]
    [InlineData("sonarr", "4.0.19.2979", 2)]
    [InlineData("radarr", "5.3.1.8438", 1)]
    [InlineData("radarr", "6.3.0.10514", 1)]
    [InlineData("lidarr", "2.9.6.4552", 1)]
    [InlineData("lidarr", "3.1.3.4975", 1)]
    [InlineData("readarr", "0.4.17.2801", 1)]
    [InlineData("readarr", "0.4.18.2805", 1)]
    [InlineData("whisparr", "2.2.0.108", 1)]
    [InlineData("whisparr", "3.1.0.2116", 1)]
    public async Task Versioned_status_and_health_fixtures_satisfy_the_typed_contract(
        string kind,
        string version,
        int healthIssueCount)
    {
        var transport = FixtureTransport(kind, version);
        var client = CreateClient(kind, transport);
        var observedAt = DateTimeOffset.Parse("2026-07-16T13:00:00Z");
        var adapter = new ArrProviderConnectionAdapter(
            client,
            new FixedTimeProvider(observedAt));
        var connection = Connection(kind);

        var result = await adapter.ProbeAsync(connection, CancellationToken.None);

        Assert.True(result.Connected);
        Assert.Equal("connected", result.Outcome);
        Assert.Equal(version, result.ProviderVersion);
        Assert.Equal(healthIssueCount, result.HealthIssues?.Count);
        Assert.Contains(result.Capabilities, value =>
            value.Capability == ProviderCapabilities.Probe && value.Supported);
        Assert.Contains(result.Capabilities, value =>
            value.Capability == ProviderCapabilities.Health && value.Supported);
        Assert.Contains(result.Capabilities, value =>
            value.Capability == ProviderCapabilities.Library && value.Supported);
        Assert.Contains(result.Capabilities, value =>
            value.Capability == ProviderCapabilities.Missing && value.Supported);
        Assert.Contains(result.Capabilities, value =>
            value.Capability == ProviderCapabilities.Queue && value.Supported);
        Assert.Contains(result.Capabilities, value =>
            value.Capability == ProviderCapabilities.History && value.Supported);
        Assert.Contains(result.Capabilities, value =>
            value.Capability == ProviderCapabilities.Search && value.Supported);
        Assert.All(result.Capabilities, value => Assert.Equal(observedAt, value.ObservedAt));
        var apiVersion = kind is "lidarr" or "readarr" ? "v1" : "v3";
        Assert.Equal(
            [$"api/{apiVersion}/system/status", $"api/{apiVersion}/health"],
            transport.RequestedPaths);
        Assert.DoesNotContain(connection.ApiKey, connection.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            connection.ApiKey,
            JsonSerializer.Serialize(connection),
            StringComparison.Ordinal);
        if (kind == "sonarr" && version == "4.0.19.2979")
        {
            Assert.Contains(result.HealthIssues!, issue => issue.Severity == "unknown");
        }
    }

    [Theory]
    [InlineData("sonarr", "4.0.1.1168", 5)]
    [InlineData("sonarr", "4.0.19.2979", 5)]
    [InlineData("radarr", "5.3.1.8438", 2)]
    [InlineData("radarr", "6.3.0.10514", 2)]
    [InlineData("lidarr", "2.9.6.4552", 3)]
    [InlineData("lidarr", "3.1.3.4975", 3)]
    [InlineData("readarr", "0.4.17.2801", 3)]
    [InlineData("readarr", "0.4.18.2805", 3)]
    [InlineData("whisparr", "2.2.0.108", 3)]
    [InlineData("whisparr", "3.1.0.2116", 2)]
    public async Task Versioned_catalog_fixtures_normalize_without_leaking_provider_dtos(
        string kind,
        string version,
        int expectedCount)
    {
        var transport = FixtureTransport(kind, version);
        var observedAt = DateTimeOffset.Parse("2026-07-16T13:00:00Z");
        var client = (IProviderCatalogClient)CreateClient(kind, transport, observedAt);

        var result = await client.GetCatalogAsync(Connection(kind), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal(observedAt, result.Value.ObservedAt);
        Assert.Equal(expectedCount, result.Value.Items.Count);
        Assert.All(result.Value.Items, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.ProviderKey));
            Assert.False(string.IsNullOrWhiteSpace(item.Title));
        });
        Assert.Contains(result.Value.Items, item => item.Monitored && item.HasFile == false);
        if (version is "4.0.19.2979" or "6.3.0.10514" or "3.1.3.4975" or "0.4.18.2805" or "3.1.0.2116")
        {
            Assert.Contains(result.Value.Items, item => item.Status == "unknown");
        }
    }

    [Theory]
    [InlineData("sonarr", "4.0.1.1168")]
    [InlineData("sonarr", "4.0.19.2979")]
    [InlineData("radarr", "5.3.1.8438")]
    [InlineData("radarr", "6.3.0.10514")]
    [InlineData("lidarr", "2.9.6.4552")]
    [InlineData("lidarr", "3.1.3.4975")]
    [InlineData("readarr", "0.4.17.2801")]
    [InlineData("readarr", "0.4.18.2805")]
    [InlineData("whisparr", "2.2.0.108")]
    [InlineData("whisparr", "3.1.0.2116")]
    public async Task Versioned_activity_fixtures_normalize_queue_history_and_correlation_keys(
        string kind,
        string version)
    {
        var transport = FixtureTransport(kind, version);
        var observedAt = DateTimeOffset.Parse("2026-07-16T13:00:00Z");
        var client = (IProviderActivityClient)CreateClient(kind, transport, observedAt);

        var result = await client.GetActivityAsync(Connection(kind), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(observedAt, result.Value?.ObservedAt);
        var queue = Assert.Single(result.Value!.Queue);
        var history = Assert.Single(result.Value.History);
        Assert.Equal(queue.DownloadId, history.DownloadId, ignoreCase: true);
        Assert.NotNull(queue.MediaProviderKey);
        Assert.NotNull(history.MediaProviderKey);
        if (version is "4.0.19.2979" or "6.3.0.10514" or "3.1.3.4975" or "0.4.18.2805" or "3.1.0.2116")
        {
            Assert.True(queue.Status == "unknown" || queue.TrackedState == "unknown");
        }
    }

    [Theory]
    [InlineData("sonarr", "episode:102", "EpisodeSearch", "episodeIds")]
    [InlineData("radarr", "movie:12", "MoviesSearch", "movieIds")]
    [InlineData("lidarr", "album:12", "AlbumSearch", "albumIds")]
    [InlineData("readarr", "book:12", "BookSearch", "bookIds")]
    [InlineData("whisparr", "episode:12", "EpisodeSearch", "episodeIds")]
    [InlineData("whisparr", "movie:12", "MoviesSearch", "movieIds")]
    public async Task Search_commands_use_only_contract_evidenced_bounded_provider_ids(
        string kind,
        string providerKey,
        string commandName,
        string idsProperty)
    {
        var transport = new StubTransport((_, _) => Response(201, "{\"id\":77}"));
        var client = (IProviderSearchClient)CreateClient(kind, transport);

        var result = await client.SearchAsync(Connection(kind), [providerKey], CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("77", result.Value?.CommandId);
        using var body = JsonDocument.Parse(Assert.Single(transport.PostedBodies));
        Assert.Equal(commandName, body.RootElement.GetProperty("name").GetString());
        Assert.Equal(int.Parse(providerKey.Split(':')[1]),
            body.RootElement.GetProperty(idsProperty)[0].GetInt32());
    }

    [Fact]
    public async Task Authentication_rate_limit_version_and_app_mismatches_use_stable_errors()
    {
        var rateLimit = new ProviderRateLimitMetadata(
            10,
            0,
            DateTimeOffset.Parse("2026-07-16T13:05:00Z"),
            TimeSpan.FromSeconds(30));
        var unauthorizedTransport = new StubTransport((_, _) =>
            new ProviderTransportResponse(401, Encoding.UTF8.GetBytes("not-json"), rateLimit));
        var sonarr = new SonarrClient(unauthorizedTransport);

        var unauthorized = await sonarr.GetSystemStatusAsync(
            Connection("sonarr"),
            CancellationToken.None);

        Assert.False(unauthorized.Success);
        Assert.Equal(ProviderErrorCodes.Unauthorized, unauthorized.ErrorCode);
        Assert.Equal(401, unauthorized.HttpStatusCode);
        Assert.Same(rateLimit, unauthorized.RateLimit);

        var wrongApp = new StubTransport((_, _) => Response(
            200,
            """{"appName":"Radarr","version":"4.0.19.2979"}"""));
        Assert.Equal(
            ProviderErrorCodes.InvalidResponse,
            (await new SonarrClient(wrongApp).GetSystemStatusAsync(
                Connection("sonarr"),
                CancellationToken.None)).ErrorCode);

        var futureMajor = new StubTransport((_, _) => Response(
            200,
            """{"appName":"Sonarr","version":"5.0.0.1"}"""));
        Assert.Equal(
            ProviderErrorCodes.UnsupportedVersion,
            (await new SonarrClient(futureMajor).GetSystemStatusAsync(
                Connection("sonarr"),
                CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Safe_transport_uses_header_auth_query_free_policy_uri_pinned_address_and_rate_metadata()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var policy = new StubOutboundPolicy(IPAddress.Loopback);
        var server = ServeRateLimitedAsync(listener);
        var transport = new SafeProviderApiTransport(policy);
        var connection = new ProviderConnection(
            Guid.CreateVersion7(),
            new Uri($"http://provider.example:{port}/sonarr/"),
            true,
            false,
            "test-api-key-value");

        using var response = await transport.GetAsync(
            connection,
            "api/v3/system/status",
            new Dictionary<string, string> { ["page"] = "1" },
            CancellationToken.None);
        var requestText = await server;

        Assert.Equal(429, response.StatusCode);
        Assert.Equal(100, response.RateLimit?.Limit);
        Assert.Equal(0, response.RateLimit?.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(20), response.RateLimit?.RetryAfter);
        Assert.Contains("GET /sonarr/api/v3/system/status?page=1 HTTP/1.1", requestText, StringComparison.Ordinal);
        Assert.Contains("X-Api-Key: test-api-key-value", requestText, StringComparison.Ordinal);
        Assert.DoesNotContain("apikey=", requestText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, policy.ResolutionCount);
        Assert.Equal("", policy.LastUri?.Query);
    }

    private static IArrProviderClient CreateClient(
        string kind, IProviderApiTransport transport, DateTimeOffset? observedAt = null)
    {
        var time = observedAt is null ? null : new FixedTimeProvider(observedAt.Value);
        return kind switch
        {
            "sonarr" => new SonarrClient(transport, time),
            "radarr" => new RadarrClient(transport, time),
            "lidarr" => new LidarrClient(transport, time),
            "readarr" => new ReadarrClient(transport, time),
            "whisparr" => new WhisparrClient(transport, time),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static StubTransport FixtureTransport(string kind, string version)
    {
        var providerName = char.ToUpperInvariant(kind[0]) + kind[1..];
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Providers",
            providerName,
            version);
        return new StubTransport((path, _) =>
        {
            var fileName = path switch
            {
                "api/v3/health" => "health.json",
                "api/v3/system/status" => "system-status.json",
                "api/v1/health" => "health.json",
                "api/v1/system/status" => "system-status.json",
                "api/v3/series" => "series.json",
                "api/v3/episode?seriesId=42" => "episodes-42.json",
                "api/v3/movie" => "movies.json",
                "api/v1/artist" => "artists.json",
                "api/v1/album" => "albums.json",
                "api/v1/author" => "authors.json",
                "api/v1/book" => "books.json",
                _ when path.StartsWith("api/v1/queue?", StringComparison.Ordinal) => "queue.json",
                _ when path.StartsWith("api/v1/history?", StringComparison.Ordinal) => "history.json",
                _ when path.StartsWith("api/v3/queue?", StringComparison.Ordinal) => "queue.json",
                _ when path.StartsWith("api/v3/history?", StringComparison.Ordinal) => "history.json",
                _ => throw new InvalidOperationException($"Unexpected fixture path '{path}'."),
            };
            if (kind == "whisparr" && version.StartsWith("2.", StringComparison.Ordinal)
                && path == "api/v3/movie")
                return Response(404, "{}");
            return new ProviderTransportResponse(
                200,
                File.ReadAllBytes(Path.Combine(root, fileName)),
                null);
        });
    }

    private static ProviderConnection Connection(string kind) =>
        new(
            Guid.CreateVersion7(),
            new Uri($"https://{kind}.example.invalid/{kind}/"),
            true,
            false,
            "fixture-api-key-value");

    private static ProviderTransportResponse Response(int statusCode, string body) =>
        new(statusCode, Encoding.UTF8.GetBytes(body), null);

    private static async Task<string> ServeRateLimitedAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer);
        var request = Encoding.ASCII.GetString(buffer, 0, read);
        var body = "{\"error\":\"rate limited\"}";
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 429 Too Many Requests\r\n"
            + "Content-Type: application/json\r\n"
            + $"Content-Length: {Encoding.ASCII.GetByteCount(body)}\r\n"
            + "X-RateLimit-Limit: 100\r\n"
            + "X-RateLimit-Remaining: 0\r\n"
            + "Retry-After: 20\r\n"
            + "Connection: close\r\n\r\n"
            + body);
        await stream.WriteAsync(response);
        return request;
    }

    private sealed class StubTransport(
        Func<string, ProviderConnection, ProviderTransportResponse> responseFactory)
        : IProviderApiTransport
    {
        public List<string> RequestedPaths { get; } = [];
        public List<string> PostedBodies { get; } = [];

        public Task<ProviderTransportResponse> GetAsync(
            ProviderConnection connection,
            string relativePath,
            CancellationToken cancellationToken)
        {
            RequestedPaths.Add(relativePath);
            return Task.FromResult(responseFactory(relativePath, connection));
        }

        public Task<ProviderTransportResponse> GetAsync(
            ProviderConnection connection,
            string relativePath,
            IReadOnlyDictionary<string, string> query,
            CancellationToken cancellationToken)
        {
            var path = query.Count == 0
                ? relativePath
                : $"{relativePath}?{string.Join('&', query.OrderBy(value => value.Key).Select(value => $"{value.Key}={value.Value}"))}";
            RequestedPaths.Add(path);
            return Task.FromResult(responseFactory(path, connection));
        }

        public Task<ProviderTransportResponse> PostJsonAsync(
            ProviderConnection connection,
            string relativePath,
            byte[] body,
            CancellationToken cancellationToken)
        {
            RequestedPaths.Add($"POST {relativePath}");
            PostedBodies.Add(Encoding.UTF8.GetString(body));
            return Task.FromResult(responseFactory($"POST {relativePath}", connection));
        }
    }

    private sealed class StubOutboundPolicy(IPAddress address) : IOutboundTargetPolicy
    {
        public int ResolutionCount { get; private set; }
        public Uri? LastUri { get; private set; }

        public Task<ResolvedOutboundTarget> ResolveAsync(
            Uri uri,
            bool allowPrivateNetworkAccess,
            CancellationToken cancellationToken)
        {
            ResolutionCount++;
            LastUri = uri;
            return Task.FromResult(new ResolvedOutboundTarget(uri, [address]));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
