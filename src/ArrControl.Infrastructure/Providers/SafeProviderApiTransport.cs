using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using ArrControl.Application.Connections;
using ArrControl.Application.Providers;

namespace ArrControl.Infrastructure.Providers;

public sealed class SafeProviderApiTransport(IOutboundTargetPolicy outboundTargetPolicy)
    : IProviderApiTransport, IProviderHttpTransport
{
    private const int MaximumResponseBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public async Task<ProviderTransportResponse> GetAsync(
        ProviderConnection connection,
        string relativePath,
        CancellationToken cancellationToken) =>
        await SendCoreAsync(connection, relativePath, EmptyQuery, EmptyHeaders, HttpMethod.Get, null, null, true, cancellationToken);

    private static readonly IReadOnlyDictionary<string, string> EmptyQuery =
        new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>();

    public async Task<ProviderTransportResponse> GetAsync(
        ProviderConnection connection,
        string relativePath,
        IReadOnlyDictionary<string, string> query,
        CancellationToken cancellationToken) =>
        await SendCoreAsync(connection, relativePath, query, EmptyHeaders, HttpMethod.Get, null, null, true, cancellationToken);

    public async Task<ProviderTransportResponse> PostJsonAsync(
        ProviderConnection connection,
        string relativePath,
        byte[] body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length is 0 or > 1024 * 1024)
        {
            throw new ProviderTransportException(ProviderErrorCodes.InvalidResponse);
        }

        return await SendCoreAsync(
            connection,
            relativePath,
            EmptyQuery,
            EmptyHeaders,
            HttpMethod.Post,
            body,
            "application/json",
            true,
            cancellationToken);
    }

    public Task<ProviderTransportResponse> SendAsync(
        ProviderConnection connection,
        ProviderHttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendCoreAsync(connection, request.RelativePath, request.Query, request.Headers,
            request.Method, request.Body, request.ContentType, false, cancellationToken);
    }

    private async Task<ProviderTransportResponse> SendCoreAsync(
        ProviderConnection connection,
        string relativePath,
        IReadOnlyDictionary<string, string> query,
        IReadOnlyDictionary<string, string> headers,
        HttpMethod method,
        byte[]? body,
        string? contentType,
        bool addApiKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(headers);
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.StartsWith('/')
            || relativePath.Contains('?')
            || relativePath.Contains('#')
            || !Uri.TryCreate(connection.BaseUri, relativePath, out var requestUri))
        {
            throw new ProviderTransportException(ProviderErrorCodes.Unknown);
        }

        if (query.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                              || pair.Key.Any(character => !char.IsAsciiLetterOrDigit(character)
                                  && character is not '-' and not '_')))
        {
            throw new ProviderTransportException(ProviderErrorCodes.Unknown);
        }
        if (headers.Count > 16 || headers.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                || pair.Key.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')
                || pair.Value.Length > 8192 || pair.Value.Contains('\r') || pair.Value.Contains('\n')))
            throw new ProviderTransportException(ProviderErrorCodes.Unknown);
        if (body is { Length: > 1024 * 1024 })
            throw new ProviderTransportException(ProviderErrorCodes.InvalidResponse);

        var target = await outboundTargetPolicy.ResolveAsync(
            requestUri,
            connection.AllowPrivateNetworkAccess,
            cancellationToken);
        if (query.Count > 0)
        {
            var builder = new UriBuilder(target.Uri)
            {
                Query = string.Join(
                    "&",
                    query.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
            };
            requestUri = builder.Uri;
        }
        else
        {
            requestUri = target.Uri;
        }
        using var handler = CreateHandler(target, connection.TlsVerificationEnabled);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(method, requestUri)
        {
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                contentType ?? "application/octet-stream");
        }
        try
        {
            if (addApiKey)
                request.Headers.Add("X-Api-Key", connection.ApiKey);
            foreach (var header in headers)
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    throw new FormatException();
        }
        catch (FormatException)
        {
            throw new ProviderTransportException(ProviderErrorCodes.Unauthorized);
        }

        request.Headers.Accept.ParseAdd("application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            var rateLimit = ReadRateLimit(response);
            var responseBody = await ReadBoundedBodyAsync(response.Content, timeout.Token);
            return new ProviderTransportResponse(
                (int)response.StatusCode, responseBody, rateLimit, ReadSelectedHeaders(response));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderTransportException(ProviderErrorCodes.Timeout);
        }
        catch (HttpRequestException exception)
        {
            throw new ProviderTransportException(
                exception.HttpRequestError == HttpRequestError.SecureConnectionError
                    ? ProviderErrorCodes.TlsError
                    : ProviderErrorCodes.Unreachable);
        }
        catch (IOException)
        {
            throw new ProviderTransportException(ProviderErrorCodes.Unreachable);
        }
    }

    private static IReadOnlyDictionary<string, string> ReadSelectedHeaders(HttpResponseMessage response)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "Set-Cookie", "X-Transmission-Session-Id" })
            if (response.Headers.TryGetValues(name, out var values))
            {
                var value = values.FirstOrDefault();
                if (!string.IsNullOrEmpty(value) && value.Length <= 8192) result[name] = value;
            }
        return result;
    }

    private static SocketsHttpHandler CreateHandler(
        ResolvedOutboundTarget target,
        bool tlsVerificationEnabled)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = RequestTimeout,
            MaxResponseHeadersLength = 64,
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

        return handler;
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new ProviderTransportException(ProviderErrorCodes.InvalidResponse);
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return destination.ToArray();
            }

            if (destination.Length + read > MaximumResponseBytes)
            {
                throw new ProviderTransportException(ProviderErrorCodes.InvalidResponse);
            }

            destination.Write(buffer, 0, read);
        }
    }

    private static ProviderRateLimitMetadata? ReadRateLimit(HttpResponseMessage response)
    {
        var limit = ReadIntegerHeader(response, "X-RateLimit-Limit");
        var remaining = ReadIntegerHeader(response, "X-RateLimit-Remaining");
        DateTimeOffset? resetAt = null;
        if (TryReadHeader(response, "X-RateLimit-Reset", out var resetValue)
            && long.TryParse(resetValue, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            try
            {
                resetAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                resetAt = null;
            }
        }

        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is DateTimeOffset date)
        {
            retryAfter = date - DateTimeOffset.UtcNow;
            if (retryAfter < TimeSpan.Zero)
            {
                retryAfter = TimeSpan.Zero;
            }
        }

        return limit is null && remaining is null && resetAt is null && retryAfter is null
            ? null
            : new ProviderRateLimitMetadata(limit, remaining, resetAt, retryAfter);
    }

    private static int? ReadIntegerHeader(HttpResponseMessage response, string name) =>
        TryReadHeader(response, name, out var value)
        && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static bool TryReadHeader(
        HttpResponseMessage response,
        string name,
        out string value)
    {
        value = string.Empty;
        if (!response.Headers.TryGetValues(name, out var values))
        {
            return false;
        }

        value = values.FirstOrDefault() ?? string.Empty;
        return value.Length > 0;
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
