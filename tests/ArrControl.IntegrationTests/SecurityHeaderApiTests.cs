using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ArrControl.IntegrationTests;

public sealed class SecurityHeaderApiTests(AuthApiDatabaseFixture fixture)
    : IClassFixture<AuthApiDatabaseFixture>
{
    [Fact]
    public async Task Browser_responses_apply_fail_closed_security_headers()
    {
        var connectionString = await fixture.CreateMigratedSchemaAsync();
        using var factory = new AuthApiFactory(connectionString, null, null);
        using var https = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://arrcontrol.example.invalid"),
        });
        using var response = await https.GetAsync("/api/v1/system/status");
        response.EnsureSuccessStatusCode();

        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
        Assert.Contains("camera=()", Header(response, "Permissions-Policy"), StringComparison.Ordinal);
        Assert.Equal("same-origin", Header(response, "Cross-Origin-Opener-Policy"));
        Assert.Equal("same-origin", Header(response, "Cross-Origin-Resource-Policy"));
        var csp = Header(response, "Content-Security-Policy");
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("upgrade-insecure-requests", csp, StringComparison.Ordinal);
        Assert.StartsWith("max-age=", Header(response, "Strict-Transport-Security"), StringComparison.Ordinal);
    }

    private static string Header(HttpResponseMessage response, string name)
    {
        Assert.True(response.Headers.TryGetValues(name, out var values), $"Missing {name}.");
        return Assert.Single(values);
    }
}
