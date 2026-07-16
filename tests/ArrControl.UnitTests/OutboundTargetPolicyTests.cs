using System.Net;
using System.Net.Sockets;
using System.Text;
using ArrControl.Application.Connections;
using ArrControl.Infrastructure.Connections;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class OutboundTargetPolicyTests
{
    [Fact]
    public async Task Policy_allows_public_and_explicit_private_targets_but_rejects_mixed_and_special_ranges()
    {
        var resolver = new StubResolver(new Dictionary<string, IReadOnlyList<IPAddress>>
        {
            ["public.example"] = [IPAddress.Parse("8.8.8.8")],
            ["private.example"] = [IPAddress.Parse("192.168.10.20")],
            ["mixed.example"] = [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.4")],
        });
        var policy = new OutboundTargetPolicy(resolver);

        var publicTarget = await policy.ResolveAsync(
            new Uri("https://public.example/sonarr/"),
            false,
            CancellationToken.None);
        Assert.Equal(IPAddress.Parse("8.8.8.8"), Assert.Single(publicTarget.Addresses));
        await Assert.ThrowsAsync<OutboundTargetRejectedException>(() => policy.ResolveAsync(
            new Uri("http://private.example/"),
            false,
            CancellationToken.None));
        Assert.Equal(
            IPAddress.Parse("192.168.10.20"),
            Assert.Single((await policy.ResolveAsync(
                new Uri("http://private.example/"),
                true,
                CancellationToken.None)).Addresses));
        await Assert.ThrowsAsync<OutboundTargetRejectedException>(() => policy.ResolveAsync(
            new Uri("https://mixed.example/"),
            false,
            CancellationToken.None));

        foreach (var blocked in new[]
                 {
                     "127.0.0.1",
                     "169.254.169.254",
                     "100.100.100.200",
                     "224.0.0.1",
                     "::1",
                     "fe80::1",
                     "64:ff9b::a00:1",
                 })
        {
            var exception = await Assert.ThrowsAsync<OutboundTargetRejectedException>(() =>
                policy.ResolveAsync(
                    new Uri($"http://[{blocked}]/".Replace("[127.0.0.1]", "127.0.0.1", StringComparison.Ordinal)
                        .Replace("[169.254.169.254]", "169.254.169.254", StringComparison.Ordinal)
                        .Replace("[100.100.100.200]", "100.100.100.200", StringComparison.Ordinal)
                        .Replace("[224.0.0.1]", "224.0.0.1", StringComparison.Ordinal)),
                    true,
                    CancellationToken.None));
            Assert.Equal("outbound_address_blocked", exception.Code);
        }
    }

    [Fact]
    public async Task Probe_connects_only_to_the_prevalidated_address_and_does_not_follow_redirects()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeRedirectAsync(listener);
        var target = new ResolvedOutboundTarget(
            new Uri($"http://provider.example:{port}/"),
            [IPAddress.Loopback]);
        var transport = new SafeConnectionProbeTransport(TimeProvider.System);

        var result = await transport.ProbeAsync(target, true, CancellationToken.None);

        Assert.True(result.Connected);
        Assert.Equal("connected", result.Outcome);
        Assert.Equal(302, result.HttpStatusCode);
        Assert.Equal(ProviderCapabilities.Probe, Assert.Single(result.Capabilities).Capability);
        await server;
    }

    private static async Task ServeRedirectAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var buffer = new byte[1024];
        _ = await stream.ReadAsync(buffer);
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1/blocked\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response);
    }

    private sealed class StubResolver(
        IReadOnlyDictionary<string, IReadOnlyList<IPAddress>> addresses) : IHostAddressResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken) =>
            Task.FromResult(addresses.TryGetValue(host, out var result)
                ? result
                : (IReadOnlyList<IPAddress>)[]);
    }
}
