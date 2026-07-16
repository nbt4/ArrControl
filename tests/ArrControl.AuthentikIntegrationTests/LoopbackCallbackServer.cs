using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace ArrControl.AuthentikIntegrationTests;

public sealed class LoopbackCallbackServer : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private readonly Channel<Uri> callbacks = Channel.CreateUnbounded<Uri>();
    private readonly HttpListener listener;
    private readonly Task listenerLoop;
    private int disposed;

    private LoopbackCallbackServer(HttpListener listener, Uri origin)
    {
        this.listener = listener;
        Origin = origin;
        listenerLoop = ListenAsync(stopping.Token);
    }

    public Uri Origin { get; }

    public Uri AuthorizationCallbackUri => new(Origin, "/auth/oidc/callback");

    public Uri PostLogoutUri => new(Origin, "/auth/oidc/signed-out");

    public static LoopbackCallbackServer Start()
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();

        var origin = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
        var listener = new HttpListener();
        listener.Prefixes.Add(origin.AbsoluteUri);
        listener.Start();
        return new LoopbackCallbackServer(listener, origin);
    }

    public async Task<Uri> WaitForAuthorizationCallbackAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var callback = await callbacks.Reader.ReadAsync(cancellationToken);
            if (string.Equals(
                    callback.AbsolutePath,
                    AuthorizationCallbackUri.AbsolutePath,
                    StringComparison.Ordinal))
            {
                return callback;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await stopping.CancelAsync();
        listener.Close();

        try
        {
            await listenerLoop;
        }
        catch (OperationCanceledException)
        {
            // Expected while the listener is being disposed.
        }

        callbacks.Writer.TryComplete();
        stopping.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (context.Request.Url is { } requestUri)
            {
                callbacks.Writer.TryWrite(requestUri);
            }

            const string body = "<!doctype html><title>ArrControl OIDC test callback</title>Callback received.";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
            context.Response.Close();
        }
    }

    private static class StatusCodes
    {
        internal const int Status200OK = 200;
    }
}
