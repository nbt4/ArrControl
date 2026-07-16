using System.Text;
using System.Text.Json;
using ArrControl.Application.Providers;
using ArrControl.Infrastructure.Providers;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class NotificationProviderContractTests
{
    private static readonly NotificationMessage Message = new(
        Guid.Parse("019b1234-1111-7111-8111-111111111111"), "Fixture alert", "Something happened.",
        "warning", DateTimeOffset.Parse("2026-07-16T18:00:00Z"), new Uri("https://arrcontrol.example.invalid/health"));

    [Theory]
    [InlineData("webhook", 200)]
    [InlineData("discord", 204)]
    [InlineData("slack", 200)]
    [InlineData("teams", 202)]
    [InlineData("telegram", 200)]
    [InlineData("ntfy", 200)]
    [InlineData("gotify", 200)]
    [InlineData("pushover", 200)]
    public async Task Http_provider_sends_only_its_contract_payload_and_auth(string kind, int statusCode)
    {
        var responseBody = kind switch
        {
            "telegram" => "{\"ok\":true,\"result\":{\"message_id\":1}}",
            "pushover" => "{\"status\":1,\"request\":\"fixture\"}",
            _ => "{\"ok\":true}",
        };
        var transport = new CaptureTransport(statusCode, responseBody);
        var provider = Provider(kind, transport);
        var target = Target(kind);

        var result = await provider.SendAsync(target, Message, CancellationToken.None);

        Assert.True(result.Success, result.ErrorCode);
        var request = Assert.Single(transport.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("application/json", request.ContentType);
        using var document = JsonDocument.Parse(request.Body!);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.DoesNotContain("fixture-secret", target.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret", request.ToString(), StringComparison.Ordinal);
        switch (kind)
        {
            case "webhook":
                Assert.Contains("X-ArrControl-Signature-256", request.Headers.Keys);
                Assert.Equal(Message.EventId.ToString("D"), request.Headers["X-ArrControl-Event-Id"]);
                break;
            case "discord":
                Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("allowed_mentions")
                    .GetProperty("parse").ValueKind);
                break;
            case "slack":
                Assert.Contains("Fixture alert", document.RootElement.GetProperty("text").GetString());
                break;
            case "teams":
                Assert.Equal("application/vnd.microsoft.card.adaptive", document.RootElement
                    .GetProperty("attachments")[0].GetProperty("contentType").GetString());
                Assert.True(document.RootElement.GetProperty("attachments")[0]
                    .GetProperty("content").TryGetProperty("$schema", out _));
                break;
            case "telegram":
                Assert.Contains("bot1234567890:fixture-secret/sendMessage", request.RelativePath);
                Assert.Equal("fixture-chat", document.RootElement.GetProperty("chat_id").GetString());
                break;
            case "ntfy":
                Assert.Equal("Bearer fixture-secret", request.Headers["Authorization"]);
                Assert.Equal("arrcontrol-alerts", document.RootElement.GetProperty("topic").GetString());
                break;
            case "gotify":
                Assert.Equal("fixture-secret", request.Headers["X-Gotify-Key"]);
                Assert.Equal("message", request.RelativePath);
                break;
            case "pushover":
                Assert.Equal("fixture-secret", document.RootElement.GetProperty("token").GetString());
                Assert.Equal("fixture-user-key", document.RootElement.GetProperty("user").GetString());
                break;
        }
    }

    [Fact]
    public async Task Email_requires_implicit_tls_and_maps_a_redacted_smtp_request()
    {
        var transport = new CaptureSmtpTransport();
        var provider = new EmailNotificationProvider(transport);
        var target = new NotificationTarget(new Uri("smtps://smtp.example.invalid:465/"), true, false,
            new Dictionary<string, string> { ["from"] = "arrcontrol@example.invalid", ["to"] = "ops@example.invalid" },
            new Dictionary<string, string> { ["username"] = "fixture-user", ["password"] = "fixture-secret" });

        var result = await provider.SendAsync(target, Message, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(transport.Request);
        Assert.Equal(465, transport.Request.Port);
        Assert.Contains(Message.ActionUrl!.ToString(), transport.Request.Body);
        Assert.DoesNotContain("fixture-secret", transport.Request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ops@example.invalid", transport.Request.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("telegram")]
    [InlineData("gotify")]
    [InlineData("pushover")]
    public async Task Missing_provider_secrets_fail_before_transport(string kind)
    {
        var transport = new CaptureTransport(200, "{\"ok\":true,\"status\":1}");
        var target = new NotificationTarget(Target(kind).Endpoint, true, false,
            kind == "telegram" ? new Dictionary<string, string> { ["chat-id"] = "chat" } : null);

        var result = await Provider(kind, transport).SendAsync(target, Message, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ProviderErrorCodes.InvalidResponse, result.ErrorCode);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Rate_limit_maps_to_stable_error_without_response_disclosure()
    {
        var transport = new CaptureTransport(429, "private upstream response");
        var result = await Provider("slack", transport)
            .SendAsync(Target("slack"), Message, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ProviderErrorCodes.RateLimited, result.ErrorCode);
        Assert.Equal(429, result.HttpStatusCode);
        Assert.DoesNotContain("private", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static INotificationProvider Provider(string kind, IProviderHttpTransport transport) => kind switch
    {
        "webhook" => new GenericWebhookNotificationProvider(transport),
        "discord" => new DiscordNotificationProvider(transport),
        "slack" => new SlackNotificationProvider(transport),
        "teams" => new TeamsNotificationProvider(transport),
        "telegram" => new TelegramNotificationProvider(transport),
        "ntfy" => new NtfyNotificationProvider(transport),
        "gotify" => new GotifyNotificationProvider(transport),
        "pushover" => new PushoverNotificationProvider(transport),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static NotificationTarget Target(string kind) => kind switch
    {
        "telegram" => new(new Uri("https://api.telegram.org/"), true, false,
            new Dictionary<string, string> { ["chat-id"] = "fixture-chat" },
            new Dictionary<string, string> { ["bot-token"] = "1234567890:fixture-secret" }),
        "ntfy" => new(new Uri("https://ntfy.example.invalid/arrcontrol-alerts"), true, false,
            secrets: new Dictionary<string, string> { ["access-token"] = "fixture-secret" }),
        "gotify" => new(new Uri("https://gotify.example.invalid/"), true, false,
            secrets: new Dictionary<string, string> { ["app-token"] = "fixture-secret" }),
        "pushover" => new(new Uri("https://api.pushover.net/1/messages.json"), true, false,
            secrets: new Dictionary<string, string>
            { ["app-token"] = "fixture-secret", ["user-key"] = "fixture-user-key" }),
        "webhook" => new(new Uri("https://hooks.example.invalid/arrcontrol?tenant=fixture"), true, false,
            secrets: new Dictionary<string, string> { ["signing-secret"] = "fixture-secret" }),
        _ => new(new Uri($"https://hooks.example.invalid/{kind}/fixture-secret"), true, false),
    };

    private sealed class CaptureTransport(int statusCode, string responseBody) : IProviderHttpTransport
    {
        public List<ProviderHttpRequest> Requests { get; } = [];
        public Task<ProviderTransportResponse> SendAsync(
            ProviderConnection connection, ProviderHttpRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(new ProviderHttpRequest(request.Method, request.RelativePath,
                new Dictionary<string, string>(request.Query), new Dictionary<string, string>(request.Headers),
                request.Body?.ToArray(), request.ContentType));
            return Task.FromResult(new ProviderTransportResponse(
                statusCode, Encoding.UTF8.GetBytes(responseBody), null));
        }
    }

    private sealed class CaptureSmtpTransport : ISmtpNotificationTransport
    {
        public SmtpNotificationRequest? Request { get; private set; }
        public Task<NotificationSendResult> SendAsync(
            SmtpNotificationRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new NotificationSendResult(true, null));
        }
    }
}
