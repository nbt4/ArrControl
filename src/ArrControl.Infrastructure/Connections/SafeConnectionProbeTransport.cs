using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using ArrControl.Application.Connections;

namespace ArrControl.Infrastructure.Connections;

public sealed class SafeConnectionProbeTransport(TimeProvider timeProvider)
    : IConnectionProbeTransport
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    public async Task<ConnectionProbeObservation> ProbeAsync(
        ResolvedOutboundTarget target,
        bool tlsVerificationEnabled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = ProbeTimeout,
            MaxResponseHeadersLength = 32,
            PooledConnectionLifetime = TimeSpan.Zero,
            UseCookies = false,
            UseProxy = false,
            ConnectCallback = (context, token) => ConnectAsync(
                target.Addresses,
                context.DnsEndPoint.Port,
                token),
        };
        if (!tlsVerificationEnabled)
        {
            handler.SslOptions.RemoteCertificateValidationCallback =
                static (_, _, _, _) => true;
        }

        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, target.Uri)
        {
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            return Observation(
                true,
                "connected",
                (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Observation(false, "connection_timeout", null);
        }
        catch (HttpRequestException exception)
        {
            return Observation(
                false,
                exception.HttpRequestError == HttpRequestError.SecureConnectionError
                    ? "tls_validation_failed"
                    : "connection_failed",
                null);
        }
        catch (IOException)
        {
            return Observation(false, "connection_failed", null);
        }
    }

    private ConnectionProbeObservation Observation(
        bool connected,
        string outcome,
        int? httpStatusCode)
    {
        var observedAt = timeProvider.GetUtcNow();
        return new ConnectionProbeObservation(
            connected,
            outcome,
            httpStatusCode,
            observedAt,
            [new ProviderCapabilityObservation(ProviderCapabilities.Probe, true, observedAt)]);
    }

    private static async ValueTask<Stream> ConnectAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (
                exception is SocketException or IOException or OperationCanceledException)
            {
                lastException = exception;
                socket.Dispose();
                if (exception is OperationCanceledException)
                {
                    throw;
                }
            }
        }

        throw lastException ?? new SocketException((int)SocketError.HostUnreachable);
    }
}
