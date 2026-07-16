using System.Text;
using System.Text.Json;
using ArrControl.Application.Connections;
using ArrControl.Application.Providers;
using ArrControl.Infrastructure.Providers;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class DownloadClientContractTests
{
    private static readonly DateTimeOffset ObservedAt =
        DateTimeOffset.Parse("2026-07-16T13:00:00Z");

    [Theory]
    [InlineData("sabnzbd", "4.5.3")]
    [InlineData("sabnzbd", "5.0.4")]
    [InlineData("nzbget", "25.4")]
    [InlineData("nzbget", "26.2")]
    [InlineData("qbittorrent", "5.1.4")]
    [InlineData("qbittorrent", "5.2.3")]
    [InlineData("transmission", "4.0.6")]
    [InlineData("transmission", "4.1.3")]
    [InlineData("deluge", "2.1.1")]
    [InlineData("deluge", "2.2.0")]
    public async Task Versioned_contract_probes_and_normalizes_download_activity(
        string kind, string version)
    {
        var transport = new FixtureTransport(kind, version);
        var client = CreateClient(kind, transport);
        var connection = Connection(kind);

        var probe = await new ArrProviderConnectionAdapter(client, new FixedTimeProvider(ObservedAt))
            .ProbeAsync(connection, CancellationToken.None);
        var activity = await client.GetActivityAsync(connection, CancellationToken.None);

        Assert.True(probe.Connected);
        Assert.Equal("connected", probe.Outcome);
        Assert.StartsWith(version, probe.ProviderVersion, StringComparison.Ordinal);
        AssertCapability(probe, ProviderCapabilities.DownloadClient, true);
        AssertCapability(probe, ProviderCapabilities.Queue, true);
        AssertCapability(probe, ProviderCapabilities.History, true);
        AssertCapability(probe, ProviderCapabilities.Pause, true);
        AssertCapability(probe, ProviderCapabilities.Remove, true);
        AssertCapability(probe, ProviderCapabilities.Retry, kind is "sabnzbd" or "nzbget");
        AssertCapability(probe, ProviderCapabilities.Library, false);
        Assert.True(activity.Success, activity.ErrorCode);
        Assert.Equal(ObservedAt, activity.Value!.ObservedAt);
        var queue = Assert.Single(activity.Value.Queue);
        var history = Assert.Single(activity.Value.History);
        Assert.StartsWith(kind + ":", queue.ProviderKey, StringComparison.Ordinal);
        Assert.StartsWith(kind + ":", history.ProviderKey, StringComparison.Ordinal);
        Assert.Equal(kind is "sabnzbd" or "nzbget" ? "usenet" : "torrent", queue.Protocol);
        Assert.DoesNotContain("fixture-password-secret", connection.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-password-secret", JsonSerializer.Serialize(connection), StringComparison.Ordinal);
        Assert.All(transport.Requests, request =>
            Assert.DoesNotContain("fixture-password-secret", request.RequestText, StringComparison.Ordinal));
        if (kind == "transmission")
            Assert.Contains(transport.Requests, request => request.Headers.ContainsKey("X-Transmission-Session-Id"));
    }

    [Theory]
    [InlineData("sabnzbd")]
    [InlineData("nzbget")]
    [InlineData("qbittorrent")]
    [InlineData("transmission")]
    [InlineData("deluge")]
    public async Task Mutations_use_contract_evidenced_bounded_identifiers(string kind)
    {
        var version = kind switch
        {
            "sabnzbd" => "5.0.4",
            "nzbget" => "26.2",
            "qbittorrent" => "5.2.3",
            "transmission" => "4.1.3",
            "deluge" => "2.2.0",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var transport = new FixtureTransport(kind, version);
        var client = CreateClient(kind, transport);
        var connection = Connection(kind);
        var key = ProviderKey(kind);

        Assert.True((await client.SetPausedAsync(connection, key, true, CancellationToken.None)).Success);
        Assert.True((await client.SetPausedAsync(connection, key, false, CancellationToken.None)).Success);
        Assert.True((await client.RemoveAsync(connection, key, true, CancellationToken.None)).Success);
        var retry = await client.RetryAsync(connection, key, CancellationToken.None);
        Assert.Equal(client.SupportsRetry, retry.Success);

        var requestCount = transport.Requests.Count;
        var invalid = await client.SetPausedAsync(
            connection, kind + ":../not-a-provider-id", true, CancellationToken.None);
        Assert.False(invalid.Success);
        Assert.Equal(ProviderErrorCodes.InvalidResponse, invalid.ErrorCode);
        Assert.Equal(requestCount, transport.Requests.Count);

        var mutations = transport.Mutations;
        switch (kind)
        {
            case "sabnzbd":
                Assert.Contains(mutations, value => value.Query.GetValueOrDefault("name") == "pause");
                Assert.Contains(mutations, value => value.Query.GetValueOrDefault("name") == "resume");
                Assert.Contains(mutations, value => value.Query.GetValueOrDefault("name") == "delete"
                    && value.Query.GetValueOrDefault("del_files") == "1");
                Assert.Contains(mutations, value => value.Query.GetValueOrDefault("mode") == "retry");
                break;
            case "nzbget":
                AssertCommands(mutations, "GroupPause", "GroupResume", "GroupFinalDelete", "HistoryRedownload");
                break;
            case "qbittorrent":
                Assert.Equal(["api/v2/torrents/stop", "api/v2/torrents/start", "api/v2/torrents/delete"],
                    mutations.Select(value => value.Path));
                Assert.Contains("deleteFiles=true", mutations.Last().Body, StringComparison.Ordinal);
                break;
            case "transmission":
                AssertCommands(mutations, "torrent-stop", "torrent-start", "torrent-remove");
                Assert.Contains("\"delete-local-data\":true", mutations.Last().Body, StringComparison.Ordinal);
                break;
            case "deluge":
                AssertCommands(mutations, "core.pause_torrent", "core.resume_torrent", "core.remove_torrent");
                Assert.Contains("true", mutations.Last().Body, StringComparison.Ordinal);
                break;
        }
    }

    [Theory]
    [InlineData("sabnzbd")]
    [InlineData("nzbget")]
    [InlineData("qbittorrent")]
    [InlineData("transmission")]
    [InlineData("deluge")]
    public async Task Missing_required_credentials_have_a_stable_probe_outcome(string kind)
    {
        var transport = new FixtureTransport(kind, LatestVersion(kind));
        var connection = new ProviderConnection(Guid.CreateVersion7(),
            new Uri($"https://{kind}.example.invalid/{kind}/"), true, false,
            new Dictionary<string, string>());

        var result = await new ArrProviderConnectionAdapter(
            CreateClient(kind, transport), new FixedTimeProvider(ObservedAt))
            .ProbeAsync(connection, CancellationToken.None);

        Assert.Equal(ProviderErrorCodes.CredentialMissing, result.Outcome);
    }

    [Theory]
    [InlineData("sabnzbd")]
    [InlineData("nzbget")]
    [InlineData("qbittorrent")]
    [InlineData("transmission")]
    [InlineData("deluge")]
    public async Task Authentication_failures_have_a_stable_probe_outcome(string kind)
    {
        var transport = new FixedResponseTransport(401);

        var result = await new ArrProviderConnectionAdapter(
            CreateClient(kind, transport), new FixedTimeProvider(ObservedAt))
            .ProbeAsync(Connection(kind), CancellationToken.None);

        Assert.Equal(ProviderErrorCodes.Unauthorized, result.Outcome);
        Assert.All(transport.Requests, request =>
            Assert.DoesNotContain("fixture-password-secret", request, StringComparison.Ordinal));
    }

    private static void AssertCommands(IEnumerable<CapturedRequest> values, params string[] commands)
    {
        var bodies = values.Select(value => value.Body).ToArray();
        foreach (var command in commands)
            Assert.Contains(bodies, body => body.Contains($"\"{command}\"", StringComparison.Ordinal));
    }

    private static void AssertCapability(
        ConnectionProbeObservation probe, string capability, bool supported) =>
        Assert.Contains(probe.Capabilities,
            value => value.Capability == capability && value.Supported == supported);

    private static IProviderDownloadClient CreateClient(string kind, IProviderHttpTransport transport) =>
        kind switch
        {
            "sabnzbd" => new SabnzbdClient(transport, new FixedTimeProvider(ObservedAt)),
            "nzbget" => new NzbGetClient(transport, new FixedTimeProvider(ObservedAt)),
            "qbittorrent" => new QBittorrentClient(transport, new FixedTimeProvider(ObservedAt)),
            "transmission" => new TransmissionClient(transport, new FixedTimeProvider(ObservedAt)),
            "deluge" => new DelugeClient(transport, new FixedTimeProvider(ObservedAt)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static ProviderConnection Connection(string kind) => new(
        Guid.CreateVersion7(), new Uri($"https://{kind}.example.invalid/{kind}/"), true, false,
        new Dictionary<string, string>
        {
            [CredentialPurposes.ApiKey] = "fixture-api-key-secret",
            [CredentialPurposes.Username] = "fixture-user",
            [CredentialPurposes.Password] = "fixture-password-secret",
        });

    private static string ProviderKey(string kind) => kind switch
    {
        "sabnzbd" => "sabnzbd:SABnzbd_nzo_queue1",
        "nzbget" => "nzbget:21",
        "qbittorrent" => "qbittorrent:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "transmission" => "transmission:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "deluge" => "deluge:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string LatestVersion(string kind) => kind switch
    {
        "sabnzbd" => "5.0.4",
        "nzbget" => "26.2",
        "qbittorrent" => "5.2.3",
        "transmission" => "4.1.3",
        "deluge" => "2.2.0",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private sealed class FixtureTransport(string kind, string version) : IProviderHttpTransport
    {
        private readonly string root = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Providers", ProviderDirectory(kind), version);

        public List<CapturedRequest> Requests { get; } = [];

        public IReadOnlyList<CapturedRequest> Mutations => Requests.Where(IsMutation).ToArray();

        public Task<ProviderTransportResponse> SendAsync(
            ProviderConnection connection, ProviderHttpRequest request, CancellationToken cancellationToken)
        {
            var captured = new CapturedRequest(request.Method, request.RelativePath,
                new Dictionary<string, string>(request.Query),
                new Dictionary<string, string>(request.Headers),
                request.Body is null ? string.Empty : Encoding.UTF8.GetString(request.Body),
                request.ToString());
            Requests.Add(captured);
            return Task.FromResult(Route(captured));
        }

        private ProviderTransportResponse Route(CapturedRequest request) => kind switch
        {
            "sabnzbd" => Sab(request),
            "nzbget" => Rpc(request, "version", "version.json", "status", "status.json",
                "listgroups", "queue.json", "history", "history.json"),
            "qbittorrent" => Qbit(request),
            "transmission" => Transmission(request),
            "deluge" => Deluge(request),
            _ => throw new InvalidOperationException($"Unexpected provider '{kind}'."),
        };

        private ProviderTransportResponse Sab(CapturedRequest request)
        {
            var mode = request.Query.GetValueOrDefault("mode");
            if (mode == "version") return File("version.json");
            if (mode == "queue" && request.Query.ContainsKey("limit")) return File("queue.json");
            if (mode == "history" && request.Query.ContainsKey("limit")) return File("history.json");
            if (mode is "queue" or "retry") return Json("{\"status\":true}");
            throw Unexpected(request);
        }

        private ProviderTransportResponse Qbit(CapturedRequest request) => request.Path switch
        {
            "api/v2/auth/login" => Json("Ok.", new Dictionary<string, string>
                { ["Set-Cookie"] = "SID=fixture-session; HttpOnly; SameSite=Strict" }),
            "api/v2/app/version" => new ProviderTransportResponse(
                200, System.IO.File.ReadAllBytes(Path.Combine(root, "version.txt")), null),
            "api/v2/torrents/info" => File("torrents.json"),
            "api/v2/torrents/stop" or "api/v2/torrents/start" or "api/v2/torrents/delete" => Json(string.Empty),
            _ => throw Unexpected(request),
        };

        private ProviderTransportResponse Transmission(CapturedRequest request)
        {
            if (!request.Headers.ContainsKey("X-Transmission-Session-Id"))
                return Json(string.Empty, new Dictionary<string, string>
                    { ["X-Transmission-Session-Id"] = "fixture-csrf-token" }, 409);
            var method = Method(request);
            return method switch
            {
                "session-get" => File("session.json"),
                "torrent-get" => File("torrents.json"),
                "torrent-stop" or "torrent-start" or "torrent-remove" =>
                    Json("{\"arguments\":{},\"result\":\"success\",\"tag\":1}"),
                _ => throw Unexpected(request),
            };
        }

        private ProviderTransportResponse Deluge(CapturedRequest request) => Method(request) switch
        {
            "auth.login" => Json("{\"error\":null,\"id\":1,\"result\":true}",
                new Dictionary<string, string> { ["Set-Cookie"] = "_session_id=fixture-session; HttpOnly" }),
            "web.get_hosts" => Json("{\"error\":null,\"id\":1,\"result\":[[\"fixture-host\",\"127.0.0.1\",58846,\"Online\"]]}"),
            "web.get_host_status" => File("host-status.json"),
            "web.connected" => Json("{\"error\":null,\"id\":1,\"result\":true}"),
            "web.connect" => Json("{\"error\":null,\"id\":1,\"result\":true}"),
            "core.get_torrents_status" => File("torrents.json"),
            "core.pause_torrent" or "core.resume_torrent" or "core.remove_torrent" =>
                Json("{\"error\":null,\"id\":1,\"result\":true}"),
            var method => throw new InvalidOperationException($"Unexpected Deluge method '{method}'."),
        };

        private ProviderTransportResponse Rpc(
            CapturedRequest request,
            string firstMethod, string firstFile,
            string secondMethod, string secondFile,
            string thirdMethod, string thirdFile,
            string fourthMethod, string fourthFile)
        {
            var method = Method(request);
            if (method == firstMethod) return File(firstFile);
            if (method == secondMethod) return File(secondFile);
            if (method == thirdMethod) return File(thirdFile);
            if (method == fourthMethod) return File(fourthFile);
            if (method == "editqueue") return Json("{\"version\":\"1.1\",\"result\":true,\"id\":1}");
            throw Unexpected(request);
        }

        private ProviderTransportResponse File(string name) =>
            new(200, System.IO.File.ReadAllBytes(Path.Combine(root, name)), null);

        private static ProviderTransportResponse Json(
            string value, IReadOnlyDictionary<string, string>? headers = null, int status = 200) =>
            new(status, Encoding.UTF8.GetBytes(value), null, headers);

        private static string Method(CapturedRequest request)
        {
            using var document = JsonDocument.Parse(request.Body);
            return document.RootElement.GetProperty("method").GetString()!;
        }

        private static bool IsMutation(CapturedRequest request)
        {
            if (request.Path.StartsWith("api/v2/torrents/", StringComparison.Ordinal)
                && request.Path is not "api/v2/torrents/info") return true;
            if (request.Path.StartsWith("api/v2/", StringComparison.Ordinal)) return false;
            if (request.Query.GetValueOrDefault("mode") is "retry") return true;
            if (request.Query.GetValueOrDefault("mode") == "queue" && request.Query.ContainsKey("name")) return true;
            if (request.Body.Length == 0) return false;
            var method = Method(request);
            return method is "editqueue" or "torrent-stop" or "torrent-start" or "torrent-remove"
                or "core.pause_torrent" or "core.resume_torrent" or "core.remove_torrent";
        }

        private static InvalidOperationException Unexpected(CapturedRequest request) =>
            new($"Unexpected request '{request.Method} {request.Path}'.");

        private static string ProviderDirectory(string value) => value switch
        {
            "sabnzbd" => "SABnzbd",
            "nzbget" => "NZBGet",
            "qbittorrent" => "qBittorrent",
            "transmission" => "Transmission",
            "deluge" => "Deluge",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Path,
        IReadOnlyDictionary<string, string> Query,
        IReadOnlyDictionary<string, string> Headers,
        string Body,
        string RequestText);

    private sealed class FixedResponseTransport(int statusCode) : IProviderHttpTransport
    {
        public List<string> Requests { get; } = [];

        public Task<ProviderTransportResponse> SendAsync(
            ProviderConnection connection, ProviderHttpRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request.ToString());
            return Task.FromResult(new ProviderTransportResponse(
                statusCode, Encoding.UTF8.GetBytes("not-json"), null));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
