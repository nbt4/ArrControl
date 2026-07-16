using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using ArrControl.Application.Connections;
using ArrControl.Application.Providers;

namespace ArrControl.Infrastructure.Providers;

public sealed class EmailNotificationProvider(ISmtpNotificationTransport transport) : INotificationProvider
{
    public string Kind => "email";
    public IReadOnlyList<string> RequiredSettings => ["from", "to"];
    public IReadOnlyList<string> RequiredSecrets => ["username", "password"];

    public Task<NotificationSendResult> SendAsync(
        NotificationTarget target, NotificationMessage message, CancellationToken cancellationToken)
    {
        if (target.Endpoint.Scheme != "smtps" || target.Endpoint.AbsolutePath != "/"
            || !string.IsNullOrEmpty(target.Endpoint.Query) || !string.IsNullOrEmpty(target.Endpoint.Fragment)
            || target.Endpoint.Port <= 0 || !target.TlsVerificationEnabled
            || !target.TryGetSetting("from", out var from) || !MailAddress.TryCreate(from, out _)
            || !target.TryGetSetting("to", out var to) || !MailAddress.TryCreate(to, out _)
            || !target.TryGetSecret("username", out var username) || string.IsNullOrWhiteSpace(username)
            || !target.TryGetSecret("password", out var password) || string.IsNullOrEmpty(password)
            || !Valid(message))
            return Task.FromResult(new NotificationSendResult(false, ProviderErrorCodes.InvalidResponse));
        var body = message.ActionUrl is null ? message.Body : $"{message.Body}\n\n{message.ActionUrl}";
        return transport.SendAsync(new SmtpNotificationRequest(
            target.Endpoint.IdnHost, target.Endpoint.Port, target.AllowPrivateNetworkAccess,
            username, password, from, to, message.Title, body), cancellationToken);
    }

    private static bool Valid(NotificationMessage message) => message is not null
        && message.EventId != Guid.Empty && message.Title.Length is > 0 and <= 250
        && message.Body.Length is > 0 and <= 4_000
        && message.Severity is "info" or "warning" or "error" && message.OccurredAt != default;
}

public sealed class SecureSmtpNotificationTransport(IOutboundTargetPolicy outboundTargetPolicy)
    : ISmtpNotificationTransport
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<NotificationSendResult> SendAsync(
        SmtpNotificationRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            var policyUri = new UriBuilder("https", request.Host, request.Port).Uri;
            var target = await outboundTargetPolicy.ResolveAsync(
                policyUri, request.AllowPrivateNetworkAccess, timeout.Token);
            using var client = await ConnectAsync(target.Addresses, request.Port, timeout.Token);
            await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = request.Host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.Online,
            }, timeout.Token);
            using var reader = new StreamReader(tls, Encoding.ASCII, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(tls, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = true,
            };
            await ExpectAsync(reader, [220], timeout.Token);
            await CommandAsync(writer, reader, "EHLO arrcontrol.invalid", [250], timeout.Token);
            var authBytes = Encoding.UTF8.GetBytes($"\0{request.Username}\0{request.Password}");
            try
            {
                await CommandAsync(writer, reader, "AUTH PLAIN " + Convert.ToBase64String(authBytes),
                    [235], timeout.Token, ProviderErrorCodes.Unauthorized);
            }
            finally { CryptographicOperations.ZeroMemory(authBytes); }
            var from = new MailAddress(request.From).Address;
            var to = new MailAddress(request.To).Address;
            await CommandAsync(writer, reader, $"MAIL FROM:<{from}>", [250], timeout.Token);
            await CommandAsync(writer, reader, $"RCPT TO:<{to}>", [250, 251], timeout.Token);
            await CommandAsync(writer, reader, "DATA", [354], timeout.Token);
            await writer.WriteAsync(Message(from, to, request.Subject, request.Body).AsMemory(), timeout.Token);
            await writer.WriteLineAsync(".").WaitAsync(timeout.Token);
            await ExpectAsync(reader, [250], timeout.Token);
            await writer.WriteLineAsync("QUIT").WaitAsync(timeout.Token);
            return new NotificationSendResult(true, null);
        }
        catch (ProviderTransportException exception)
        {
            return new NotificationSendResult(false, exception.Code);
        }
        catch (OutboundTargetRejectedException)
        {
            return new NotificationSendResult(false, ProviderErrorCodes.Unreachable);
        }
        catch (AuthenticationException)
        {
            return new NotificationSendResult(false, ProviderErrorCodes.TlsError);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new NotificationSendResult(false, ProviderErrorCodes.Timeout);
        }
        catch (IOException)
        {
            return new NotificationSendResult(false, ProviderErrorCodes.Unreachable);
        }
        catch (SocketException)
        {
            return new NotificationSendResult(false, ProviderErrorCodes.Unreachable);
        }
    }

    private static async Task<TcpClient> ConnectAsync(
        IReadOnlyList<IPAddress> addresses, int port, CancellationToken cancellationToken)
    {
        foreach (var address in addresses)
        {
            var client = new TcpClient(address.AddressFamily);
            try
            {
                await client.ConnectAsync(address, port, cancellationToken);
                return client;
            }
            catch (SocketException) { client.Dispose(); }
        }
        throw new ProviderTransportException(ProviderErrorCodes.Unreachable);
    }

    private static async Task CommandAsync(
        StreamWriter writer, StreamReader reader, string command, IReadOnlyCollection<int> expected,
        CancellationToken cancellationToken, string errorCode = ProviderErrorCodes.Unknown)
    {
        await writer.WriteLineAsync(command).WaitAsync(cancellationToken);
        await ExpectAsync(reader, expected, cancellationToken, errorCode);
    }

    private static async Task ExpectAsync(
        StreamReader reader, IReadOnlyCollection<int> expected, CancellationToken cancellationToken,
        string errorCode = ProviderErrorCodes.Unknown)
    {
        for (var lineCount = 0; lineCount < 50; lineCount++)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null || line.Length is < 3 or > 2_048
                || !int.TryParse(line.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var code))
                throw new ProviderTransportException(ProviderErrorCodes.InvalidResponse);
            if (line.Length == 3 || line[3] == ' ')
            {
                if (!expected.Contains(code)) throw new ProviderTransportException(errorCode);
                return;
            }
            if (line[3] != '-') throw new ProviderTransportException(ProviderErrorCodes.InvalidResponse);
        }
        throw new ProviderTransportException(ProviderErrorCodes.InvalidResponse);
    }

    private static string Message(string from, string to, string subject, string body)
    {
        var encodedSubject = Convert.ToBase64String(Encoding.UTF8.GetBytes(subject));
        var normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Split('\n').Select(line => line.StartsWith('.') ? "." + line : line);
        return $"From: <{from}>\r\nTo: <{to}>\r\nSubject: =?UTF-8?B?{encodedSubject}?=\r\n"
            + "MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n"
            + "Content-Transfer-Encoding: 8bit\r\n\r\n"
            + string.Join("\r\n", normalized) + "\r\n";
    }
}
